using System.Net;
using System.Net.Http.Json;
using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;
using System.Xml;
using Dapper;
using Microsoft.Data.Sqlite;
using InfoHubParser.Models;

namespace InfoHubParser;

class Program
{
    public record FeedSource(string Name, string Url);
    public record CategoryConfig(string CategoryName, string[] WebhookUrlEnvVars, List<FeedSource> Feeds);

    static async Task Main(string[] args)
    {
        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Starting RSS to Discord sync bot...");

        // 1. Get database path from environment variable or default to news.db in the repo root/current working directory
        string dbPath = Environment.GetEnvironmentVariable("DATABASE_PATH") ?? "news.db";
        string connectionString = $"Data Source={dbPath}";

        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Database Path: {Path.GetFullPath(dbPath)}");

        // 2. Initialize database
        try
        {
            await InitializeDatabaseAsync(connectionString);
        }
        catch (Exception ex)
        {
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] [FATAL] Failed to initialize database: {ex.Message}");
            Environment.Exit(1);
        }

        // 3. Setup configuration mapping supporting exact user names, standard uppercase secrets, and legacy env vars
        var categories = new List<CategoryConfig>
        {
            new CategoryConfig(
                "Daily Digests",
                new[] { "discrod_webhook_daily-digests", "DISCORD_WEBHOOK_DAILY_DIGESTS", "DISCROD_WEBHOOK_DAILY_DIGESTS", "DISCORD_WEBHOOK_A" },
                new List<FeedSource>
                {
                    new FeedSource("Chris Alcock Morning Brew", "https://blog.cwa.me.uk/feed/"),
                    new FeedSource("Hacker News", "https://hnrss.org/frontpage"),
                    new FeedSource("TLDR", "https://tldr.tech/rss")
                }
            ),
            new CategoryConfig(
                ".NET Deep Dive",
                new[] { "discrod_webhook_csharp-dotnet", "DISCORD_WEBHOOK_CSHARP_DOTNET", "DISCROD_WEBHOOK_CSHARP_DOTNET", "DISCORD_WEBHOOK_B" },
                new List<FeedSource>
                {
                    new FeedSource("Andrew Lock", "https://andrewlock.net/rss.xml"),
                    new FeedSource("Docker", "https://www.docker.com/blog/feed/")
                }
            ),
            new CategoryConfig(
                "Architecture",
                new[] { "discrod_webhook_architecture-systems", "DISCORD_WEBHOOK_ARCHITECTURE_SYSTEMS", "DISCROD_WEBHOOK_ARCHITECTURE_SYSTEMS", "DISCORD_WEBHOOK_C" },
                new List<FeedSource>
                {
                    new FeedSource("Netflix TechBlog", "https://netflixtechblog.com/feed"),
                    new FeedSource("Uber", "https://www.uber.com/blog/engineering/rss"),
                    new FeedSource("Cloudflare", "https://blog.cloudflare.com/rss/"),
                    new FeedSource("ByteByteGo", "https://blog.bytebytego.com/feed")
                }
            )
        };

        // 4. Initialize HTTP client with proper User-Agent and timeouts
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64) RSS-to-Discord Bot");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/xml, text/xml, */*");

        int totalProcessed = 0;
        int totalSent = 0;
        int totalFailed = 0;

        // Process categories sequentially to prevent webhook rate limits overlap
        foreach (var category in categories)
        {
            try
            {
                string? webhookUrl = ResolveWebhookUrl(category.WebhookUrlEnvVars);
                if (string.IsNullOrWhiteSpace(webhookUrl))
                {
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] [WARNING] Webhook URL for category '{category.CategoryName}' is not set. Checked env vars: [{string.Join(", ", category.WebhookUrlEnvVars)}]. Skipping category.");
                    continue;
                }

                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Processing Category: {category.CategoryName}");

                foreach (var feedSource in category.Feeds)
                {
                    try
                    {
                        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]   Fetching Feed: {feedSource.Name} ({feedSource.Url})");
                        var items = await FetchFeedItemsWithRetryAsync(httpClient, feedSource.Url);
                        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     Found {items.Count} items.");

                        // Process items from oldest to newest to post in chronological order
                        foreach (var item in items.AsEnumerable().Reverse())
                        {
                            string? articleUrl = item.Links.FirstOrDefault()?.Uri?.AbsoluteUri ?? item.Id;
                            if (string.IsNullOrWhiteSpace(articleUrl))
                            {
                                continue;
                            }

                            articleUrl = articleUrl.Trim();
                            totalProcessed++;

                            // Check if already sent
                            bool alreadySent = await CheckIfSentAsync(connectionString, articleUrl);
                            if (alreadySent)
                            {
                                continue;
                            }

                            // Send to Discord
                            string titleToLog = item.Title?.Text ?? articleUrl;
                            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     Syncing: {titleToLog}");
                            bool sentSuccessfully = await SendToDiscordWithRetryAsync(httpClient, webhookUrl, category.CategoryName, feedSource.Name, item);

                            if (sentSuccessfully)
                            {
                                // Save to SQLite
                                await SaveSentArticleAsync(connectionString, articleUrl);
                                totalSent++;

                                // Polite delay to respect Discord rate limits
                                await Task.Delay(1000);
                            }
                            else
                            {
                                totalFailed++;
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]   [ERROR] Error processing feed '{feedSource.Name}': {ex.Message}");
                    }
                }
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] [ERROR] Unhandled error processing category '{category.CategoryName}': {ex.Message}");
            }
        }

        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC] Sync completed. Processed: {totalProcessed}, Sent: {totalSent}, Failed: {totalFailed}");
    }

    private static string? ResolveWebhookUrl(string[] candidates)
    {
        foreach (var candidate in candidates)
        {
            var val = Environment.GetEnvironmentVariable(candidate);
            if (!string.IsNullOrWhiteSpace(val)) return val.Trim();

            // Try uppercase with underscores
            var upperVal = Environment.GetEnvironmentVariable(candidate.ToUpperInvariant().Replace("-", "_"));
            if (!string.IsNullOrWhiteSpace(upperVal)) return upperVal.Trim();

            // Try lowercase
            var lowerVal = Environment.GetEnvironmentVariable(candidate.ToLowerInvariant());
            if (!string.IsNullOrWhiteSpace(lowerVal)) return lowerVal.Trim();
        }
        return null;
    }

    private static async Task InitializeDatabaseAsync(string connectionString)
    {
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        string createTableSql = @"
            CREATE TABLE IF NOT EXISTS SentArticles (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                ArticleUrl TEXT NOT NULL UNIQUE,
                SentAt TEXT DEFAULT CURRENT_TIMESTAMP
            );
            CREATE UNIQUE INDEX IF NOT EXISTS IX_SentArticles_ArticleUrl ON SentArticles(ArticleUrl);
        ";

        await connection.ExecuteAsync(createTableSql);
    }

    private static async Task<List<SyndicationItem>> FetchFeedItemsWithRetryAsync(HttpClient httpClient, string feedUrl)
    {
        int maxRetries = 2;
        for (int attempt = 1; attempt <= maxRetries + 1; attempt++)
        {
            try
            {
                using var response = await httpClient.GetAsync(feedUrl);
                response.EnsureSuccessStatusCode();

                using var stream = await response.Content.ReadAsStreamAsync();
                
                var readerSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    IgnoreComments = true,
                    IgnoreWhitespace = true
                };

                using var reader = XmlReader.Create(stream, readerSettings);
                var feed = SyndicationFeed.Load(reader);

                return feed?.Items?.ToList() ?? new List<SyndicationItem>();
            }
            catch (Exception ex) when (attempt <= maxRetries && (ex is HttpRequestException || ex is TaskCanceledException || ex is XmlException))
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     [WARNING] Transient failure fetching feed (Attempt {attempt}/{maxRetries + 1}): {ex.Message}. Retrying in 2 seconds...");
                await Task.Delay(2000 * attempt);
            }
        }
        return new List<SyndicationItem>();
    }

    private static async Task<bool> CheckIfSentAsync(string connectionString, string articleUrl)
    {
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        string query = "SELECT EXISTS(SELECT 1 FROM SentArticles WHERE ArticleUrl = @Url)";
        return await connection.ExecuteScalarAsync<bool>(query, new { Url = articleUrl });
    }

    private static async Task SaveSentArticleAsync(string connectionString, string articleUrl)
    {
        using var connection = new SqliteConnection(connectionString);
        await connection.OpenAsync();

        string insertSql = "INSERT OR IGNORE INTO SentArticles (ArticleUrl) VALUES (@Url)";
        await connection.ExecuteAsync(insertSql, new { Url = articleUrl });
    }

    private static async Task<bool> SendToDiscordWithRetryAsync(
        HttpClient httpClient,
        string webhookUrl,
        string categoryName,
        string sourceName,
        SyndicationItem item)
    {
        string? title = item.Title?.Text ?? "New Article";
        if (title.Length > 250)
        {
            title = title.Substring(0, 247) + "...";
        }

        string? url = item.Links.FirstOrDefault()?.Uri?.AbsoluteUri ?? item.Id;

        // Handle publication date. Fallback to current time if unavailable
        DateTimeOffset pubDate = item.PublishDate != default ? item.PublishDate : (item.LastUpdatedTime != default ? item.LastUpdatedTime : DateTimeOffset.UtcNow);

        // Get clean text description if available
        string? description = item.Summary?.Text;
        if (string.IsNullOrEmpty(description) && item.Content is TextSyndicationContent textContent)
        {
            description = textContent.Text;
        }

        // Clean HTML tags from description if present
        if (!string.IsNullOrEmpty(description))
        {
            description = Regex.Replace(description, "<.*?>", string.Empty).Trim();
            if (description.Length > 1900)
            {
                description = description.Substring(0, 1897) + "...";
            }
        }

        // Distinct colors for each category
        int color = categoryName switch
        {
            "Daily Digests" => 0x3498DB, // Blue
            ".NET Deep Dive" => 0x9B59B6, // Purple
            "Architecture" => 0xE67E22, // Orange
            _ => 0x5865F2 // Discord Blurple
        };

        var payload = new DiscordWebhookPayload
        {
            Username = $"{sourceName} Sync",
            Embeds = new List<DiscordEmbed>
            {
                new DiscordEmbed
                {
                    Title = title,
                    Description = string.IsNullOrWhiteSpace(description) ? null : description,
                    Url = url,
                    Timestamp = pubDate.ToString("yyyy-MM-ddTHH:mm:ssK"),
                    Color = color,
                    Footer = new DiscordFooter
                    {
                        Text = $"{sourceName} • {categoryName}"
                    }
                }
            }
        };

        int maxRetries = 3;
        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            try
            {
                using var response = await httpClient.PostAsJsonAsync(webhookUrl, payload);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                string errorContent = await response.Content.ReadAsStringAsync();
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     [ERROR] Discord Webhook returned status code {response.StatusCode} (Attempt {attempt}/{maxRetries}): {errorContent}");

                // Check for rate limit specifically
                if (response.StatusCode == HttpStatusCode.TooManyRequests && attempt < maxRetries)
                {
                    int delayMs = 5000;
                    if (response.Headers.TryGetValues("Retry-After", out var values) && double.TryParse(values.FirstOrDefault(), out double retrySeconds))
                    {
                        delayMs = Math.Max(1000, (int)(retrySeconds * 1000) + 500);
                    }
                    Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     [WARNING] Hit rate limit (429). Waiting {delayMs}ms before retrying...");
                    await Task.Delay(delayMs);
                    continue;
                }

                // Retry on 5xx server errors
                if ((int)response.StatusCode >= 500 && attempt < maxRetries)
                {
                    await Task.Delay(2000 * attempt);
                    continue;
                }

                return false;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     [ERROR] Failed to send to Discord (Attempt {attempt}/{maxRetries}): {ex.Message}");
                if (attempt < maxRetries)
                {
                    await Task.Delay(2000 * attempt);
                }
            }
        }

        return false;
    }
}
