using System.Net;
using System.Net.Http.Json;
using System.ServiceModel.Syndication;
using System.Text.RegularExpressions;
using System.Xml;
using Dapper;
using Microsoft.Data.Sqlite;
using InfoHubParser.Models;
using InfoHubParser.Services;

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
                    new FeedSource("Hacker News", "https://hnrss.org/frontpage"),
                    new FeedSource("TLDR", "https://tldr.tech/rss"),
                    new FeedSource("Dan Luu", "https://danluu.com/atom.xml"),
                    new FeedSource("Simon Willison", "https://simonwillison.net/atom/everything/"),
                    new FeedSource("Martin Fowler", "https://martinfowler.com/feed.atom")
                }
            ),

            new CategoryConfig(
                ".NET & Backend",
                new[] { "discrod_webhook_csharp-dotnet", "DISCORD_WEBHOOK_CSHARP_DOTNET", "DISCROD_WEBHOOK_CSHARP_DOTNET", "DISCORD_WEBHOOK_B" },
                new List<FeedSource>
                {
                    new FeedSource("Andrew Lock", "https://andrewlock.net/rss.xml"),
                    new FeedSource(".NET Blog", "https://devblogs.microsoft.com/dotnet/feed/"),
                    new FeedSource("Steve Gordon", "https://www.stevejgordon.co.uk/feed")
                }
            ),

            new CategoryConfig(
                "Architecture & Distributed Systems",
                new[] { "discrod_webhook_architecture-systems", "DISCORD_WEBHOOK_ARCHITECTURE_SYSTEMS", "DISCROD_WEBHOOK_ARCHITECTURE_SYSTEMS", "DISCORD_WEBHOOK_C" },
                new List<FeedSource>
                {
                    new FeedSource("Netflix TechBlog", "https://netflixtechblog.com/feed"),
                    new FeedSource("Stripe Engineering", "https://github.com/stripe/stripe-dotnet/releases.atom"),
                    new FeedSource("Cloudflare", "https://blog.cloudflare.com/rss/"),
                    new FeedSource("ByteByteGo", "https://blog.bytebytego.com/feed"),
                    new FeedSource("Uber Engineering", "https://github.com/uber/h3/releases.atom"),
                    new FeedSource("Shopify Engineering", "https://github.com/Shopify/flash-list/releases.atom")
                }
            ),

            new CategoryConfig(
                "Databases & Data",
                new[] { "discrod_webhook_databases", "DISCORD_WEBHOOK_DATABASES", "DISCROD_WEBHOOK_DATABASES", "DISCORD_WEBHOOK_D" },
                new List<FeedSource>
                {
                    new FeedSource("PostgreSQL", "https://www.postgresql.org/news.rss"),
                    new FeedSource("Brent Ozar", "https://www.brentozar.com/feed/"),
                    new FeedSource("SQLPerformance", "https://sqlperformance.com/feed"),
                    new FeedSource("Use The Index, Luke", "https://use-the-index-luke.com/blog/feed")
                }
            ),

            new CategoryConfig(
                "Cloud & Platform",
                new[] { "discrod_webhook_cloud-platform", "DISCORD_WEBHOOK_CLOUD_PLATFORM", "DISCROD_WEBHOOK_CLOUD_PLATFORM", "DISCORD_WEBHOOK_E" },
                new List<FeedSource>
                {
                    new FeedSource("AWS Architecture", "https://aws.amazon.com/blogs/architecture/feed/"),
                    new FeedSource("Azure Architecture", "https://azure.microsoft.com/en-us/blog/feed/"),
                    new FeedSource("Docker", "https://www.docker.com/blog/feed/"),
                    new FeedSource("Kubernetes", "https://kubernetes.io/feed.xml"),
                    new FeedSource("OpenTelemetry", "https://github.com/open-telemetry/opentelemetry-specification/releases.atom")
                }
            ),

            new CategoryConfig(
                "Security",
                new[] { "discrod_webhook_security", "DISCORD_WEBHOOK_SECURITY", "DISCORD_WEBHOOK_SECURITY", "DISCORD_WEBHOOK_F" },
                new List<FeedSource>
                {
                    new FeedSource("OWASP", "https://owasp.org/feed.xml"),
                    new FeedSource("Trail of Bits", "https://blog.trailofbits.com/feed/"),
                    new FeedSource("Auth0", "https://auth0.com/blog/rss.xml")
                }
            ),

            new CategoryConfig(
                "AI Engineering",
                new[] { "discrod_webhook_ai-engineering", "DISCORD_WEBHOOK_AI_ENGINEERING", "DISCROD_WEBHOOK_AI_ENGINEERING", "DISCORD_WEBHOOK_G" },
                new List<FeedSource>
                {
                    new FeedSource("Anthropic News", "https://github.com/anthropics/anthropic-sdk-python/releases.atom"),
                    new FeedSource("OpenAI News", "https://openai.com/news/rss.xml"),
                    new FeedSource("Ollama", "https://ollama.com/blog/rss.xml")
                }
            ),

            new CategoryConfig(
                "Human Systems",
                new[] { "discrod_webhook_human-systems", "DISCORD_WEBHOOK_HUMAN_SYSTEMS", "DISCROD_WEBHOOK_HUMAN_SYSTEMS", "DISCORD_WEBHOOK_H" },
                new List<FeedSource>
                {
                    new FeedSource("Farnam Street", "https://fs.blog/feed/"),
                    new FeedSource("Works in Progress", "https://www.worksinprogress.news/feed"),
                    new FeedSource("Astral Codex Ten", "https://www.astralcodexten.com/feed")
                }
            )
        };

        // 4. Initialize HTTP client with proper User-Agent and timeouts
        using var httpClient = new HttpClient
        {
            Timeout = TimeSpan.FromSeconds(30)
        };
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 (Windows NT 10.0; Win64; x64; rv:135.0) Gecko/20100101 Firefox/135.0");
        httpClient.DefaultRequestHeaders.Accept.ParseAdd("application/rss+xml, application/atom+xml, application/xml;q=0.9, text/xml;q=0.8, */*;q=0.7");
        httpClient.DefaultRequestHeaders.AcceptLanguage.ParseAdd("en-US,en;q=0.9");

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

                        // 1. Identify all un-sent items for this feed
                        var unSentItems = new List<(SyndicationItem Item, string Url, DateTimeOffset PubDate)>();
                        foreach (var item in items)
                        {
                            string? articleUrl = item.Links.FirstOrDefault()?.Uri?.AbsoluteUri ?? item.Id;
                            if (string.IsNullOrWhiteSpace(articleUrl)) continue;
                            articleUrl = articleUrl.Trim();

                            bool alreadySent = await CheckIfSentAsync(connectionString, articleUrl);
                            if (!alreadySent)
                            {
                                DateTimeOffset pubDate = item.PublishDate != default ? item.PublishDate : (item.LastUpdatedTime != default ? item.LastUpdatedTime : DateTimeOffset.UtcNow);
                                unSentItems.Add((item, articleUrl, pubDate));
                            }
                        }

                        if (unSentItems.Count == 0) continue;

                        // 2. Filter out historical items older than 14 days so we never flood old archive posts into Discord/AI
                        var recentUnSent = new List<(SyndicationItem Item, string Url, DateTimeOffset PubDate)>();
                        foreach (var entry in unSentItems)
                        {
                            if (DateTimeOffset.UtcNow - entry.PubDate > TimeSpan.FromDays(14))
                            {
                                // Silently mark historical items (>14 days old) as processed in SQLite
                                await SaveSentArticleAsync(connectionString, entry.Url);
                                totalProcessed++;
                            }
                            else
                            {
                                recentUnSent.Add(entry);
                            }
                        }

                        // 3. Cap max new items sent per feed per sync run to 3 to prevent burst notifications / AI 429 errors
                        int maxItemsPerFeed = 3;
                        var itemsToSend = recentUnSent
                            .OrderByDescending(x => x.PubDate)
                            .Take(maxItemsPerFeed)
                            .OrderBy(x => x.PubDate) // Order oldest to newest among top 3 for chronological posting
                            .ToList();

                        // Any recent items beyond the top 3 (e.g. initial feed addition of 15 recent posts) are marked as processed to prevent backlog flooding
                        if (recentUnSent.Count > maxItemsPerFeed)
                        {
                            var skippedRecent = recentUnSent.OrderByDescending(x => x.PubDate).Skip(maxItemsPerFeed);
                            foreach (var skipped in skippedRecent)
                            {
                                await SaveSentArticleAsync(connectionString, skipped.Url);
                                totalProcessed++;
                            }
                            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     [INFO] Feed has {recentUnSent.Count} recent un-sent items. Sending top {maxItemsPerFeed} and marking {recentUnSent.Count - maxItemsPerFeed} as processed to prevent spam.");
                        }

                        foreach (var entry in itemsToSend)
                        {
                            totalProcessed++;
                            string titleToLog = entry.Item.Title?.Text ?? entry.Url;
                            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     Syncing: {titleToLog}");
                            bool sentSuccessfully = await SendToDiscordWithRetryAsync(httpClient, webhookUrl, category.CategoryName, feedSource.Name, entry.Item);

                            if (sentSuccessfully)
                            {
                                await SaveSentArticleAsync(connectionString, entry.Url);
                                totalSent++;
                                await Task.Delay(2500); // Polite 2.5s delay between articles to respect Discord & AI rate limits
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

                string xmlContent = await response.Content.ReadAsStringAsync();

                // Fix non-conformant Atom <author>plain text</author> tags (e.g., OWASP / Jekyll Atom feeds)
                xmlContent = Regex.Replace(xmlContent, @"<author>\s*([^<]+?)\s*</author>", "<author><name>$1</name></author>");

                var readerSettings = new XmlReaderSettings
                {
                    DtdProcessing = DtdProcessing.Ignore,
                    IgnoreComments = true,
                    IgnoreWhitespace = true
                };

                using var reader = XmlReader.Create(new StringReader(xmlContent), readerSettings);
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

        // Perform AI evaluation for exact scores and read time
        var aiEval = await AiEvaluator.EvaluateArticleAsync(
            httpClient,
            title,
            description,
            url ?? title,
            categoryName,
            sourceName);

        Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     [AI Eval] Importance: {aiEval.Importance}/10 | Career ROI: {aiEval.CareerRoi}/10 | Timelessness: {aiEval.Timelessness}/10 | Read Time: {aiEval.ReadTime}");

        // Distinct colors for each category
        int color = categoryName switch
        {
            "Daily Digests" => 0x3498DB, // Blue
            ".NET & Backend" => 0x9B59B6, // Purple
            "Architecture & Distributed Systems" => 0xE67E22, // Orange
            "Databases & Data" => 0x2ECC71, // Green
            "Cloud & Platform" => 0x1ABC9C, // Teal
            "Security" => 0xE74C3C, // Red
            "AI Engineering" => 0xF1C40F, // Gold
            "Human Systems" => 0xE6B0AA, // Rose/Soft Pink
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
                    },
                    Fields = new List<DiscordField>
                    {
                        new DiscordField { Name = "Importance", Value = $"{aiEval.Importance}/10", Inline = true },
                        new DiscordField { Name = "Career ROI", Value = $"{aiEval.CareerRoi}/10", Inline = true },
                        new DiscordField { Name = "Timelessness", Value = $"{aiEval.Timelessness}/10", Inline = true },
                        new DiscordField { Name = "Read Time", Value = aiEval.ReadTime, Inline = true }
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
