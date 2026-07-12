using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace InfoHubParser.Services;

public record ArticleEvaluation(int Importance, int CareerRoi, int Timelessness, string ReadTime);

public static class AiEvaluator
{
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };

    private static DateTime _geminiCooldownUntil = DateTime.MinValue;
    private static DateTime _groqCooldownUntil = DateTime.MinValue;
    private static DateTime _openRouterCooldownUntil = DateTime.MinValue;
    private static DateTime _openAiCooldownUntil = DateTime.MinValue;

    public static async Task<ArticleEvaluation> EvaluateArticleAsync(
        HttpClient httpClient,
        string title,
        string? description,
        string url,
        string categoryName,
        string sourceName)
    {
        string? geminiKey = Environment.GetEnvironmentVariable("GEMINI_API_KEY") ?? Environment.GetEnvironmentVariable("GOOGLE_API_KEY");
        string? groqKey = Environment.GetEnvironmentVariable("GROQ_API_KEY");
        string? openRouterKey = Environment.GetEnvironmentVariable("OPENROUTER_API_KEY");
        string? openAiKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        string? ollamaHost = Environment.GetEnvironmentVariable("OLLAMA_HOST");

        string prompt = BuildPrompt(title, description, url, categoryName, sourceName);

        // 1. Try Google Gemini API (Free tier from Google AI Studio)
        if (!string.IsNullOrWhiteSpace(geminiKey) && DateTime.UtcNow >= _geminiCooldownUntil)
        {
            try
            {
                var eval = await CallGeminiAsync(httpClient, geminiKey.Trim(), prompt);
                if (eval != null) return eval;
            }
            catch (Exception ex)
            {
                HandleApiFailure("Gemini", ex, ref _geminiCooldownUntil);
            }
        }

        // 2. Try Groq API (Free tier)
        if (!string.IsNullOrWhiteSpace(groqKey) && DateTime.UtcNow >= _groqCooldownUntil)
        {
            try
            {
                var eval = await CallOpenAiCompatibleAsync(httpClient, "https://api.groq.com/openai/v1/chat/completions", groqKey.Trim(), "llama-3.3-70b-versatile", prompt);
                if (eval != null) return eval;
            }
            catch (Exception ex)
            {
                HandleApiFailure("Groq", ex, ref _groqCooldownUntil);
            }
        }

        // 3. Try OpenRouter API (Free tier models)
        if (!string.IsNullOrWhiteSpace(openRouterKey) && DateTime.UtcNow >= _openRouterCooldownUntil)
        {
            try
            {
                var eval = await CallOpenAiCompatibleAsync(httpClient, "https://openrouter.ai/api/v1/chat/completions", openRouterKey.Trim(), "google/gemini-2.0-flash-lite-preview-02-05:free", prompt);
                if (eval != null) return eval;
            }
            catch (Exception ex)
            {
                HandleApiFailure("OpenRouter", ex, ref _openRouterCooldownUntil);
            }
        }

        // 4. Try OpenAI API
        if (!string.IsNullOrWhiteSpace(openAiKey) && DateTime.UtcNow >= _openAiCooldownUntil)
        {
            try
            {
                var eval = await CallOpenAiCompatibleAsync(httpClient, "https://api.openai.com/v1/chat/completions", openAiKey.Trim(), "gpt-4o-mini", prompt);
                if (eval != null) return eval;
            }
            catch (Exception ex)
            {
                HandleApiFailure("OpenAI", ex, ref _openAiCooldownUntil);
            }
        }

        // 5. Try local Ollama
        if (!string.IsNullOrWhiteSpace(ollamaHost))
        {
            try
            {
                string endpoint = $"{ollamaHost.TrimEnd('/')}/api/generate";
                var eval = await CallOllamaAsync(httpClient, endpoint, "llama3.2", prompt);
                if (eval != null) return eval;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     [WARNING] Ollama evaluation failed: {ex.Message}. Falling back...");
            }
        }

        // 6. Intelligent local heuristic evaluation (ensures free AI evaluation works out of the box or as fallback)
        return EvaluateLocallyHeuristic(title, description, categoryName);
    }

    private static void HandleApiFailure(string providerName, Exception ex, ref DateTime cooldownUntil)
    {
        string msg = ex.Message;
        if (msg.Contains("429") || msg.Contains("Too Many Requests"))
        {
            cooldownUntil = DateTime.UtcNow.AddMinutes(2);
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     [WARNING] {providerName} API rate-limited (429). Cooldown active for 2 minutes. Falling back to next evaluator...");
        }
        else if (msg.Contains("404") || msg.Contains("Not Found") || msg.Contains("401") || msg.Contains("Unauthorized"))
        {
            cooldownUntil = DateTime.UtcNow.AddMinutes(15);
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     [WARNING] {providerName} API returned error ({msg}). Pausing {providerName} for this run. Falling back to next evaluator...");
        }
        else
        {
            Console.WriteLine($"[{DateTime.UtcNow:yyyy-MM-dd HH:mm:ss} UTC]     [WARNING] {providerName} API evaluation failed: {msg}. Falling back...");
        }
    }

    private static string BuildPrompt(string title, string? description, string url, string categoryName, string sourceName)
    {
        return $@"You are an expert AI technical editor for software engineers. Evaluate the following article:
Title: {title}
Category: {categoryName}
Source: {sourceName}
Summary: {description ?? "No summary available."}
URL: {url}

Analyze the article and assign accurate integers between 1 and 10 and reading time:
1. importance (integer 1-10): How significant, groundbreaking, or critical this article is to software developers and industry architecture.
2. career_roi (integer 1-10): How much long-term professional value and engineering skill boost a developer gains by reading this.
3. timelessness (integer 1-10): How evergreen the topic is (10 = foundational architecture/concepts that last decades, 1 = fleeting release note or minor bug fix).
4. read_time_minutes (integer): Estimated reading time in minutes for the full article (e.g. 4 for standard articles, 8 for deep-dive engineering blogs, 2 for quick news).

Return ONLY valid JSON formatted exactly like this:
{{
  ""importance"": 8,
  ""career_roi"": 7,
  ""timelessness"": 9,
  ""read_time_minutes"": 4
}}";
    }

    private static async Task<ArticleEvaluation?> CallGeminiAsync(HttpClient httpClient, string apiKey, string prompt)
    {
        string model = Environment.GetEnvironmentVariable("AI_MODEL") ?? "gemini-2.5-flash";
        string url = $"https://generativelanguage.googleapis.com/v1beta/models/{model}:generateContent?key={apiKey}";

        var payload = new
        {
            contents = new[]
            {
                new
                {
                    parts = new[]
                    {
                        new { text = prompt }
                    }
                }
            },
            generationConfig = new
            {
                temperature = 0.2,
                responseMimeType = "application/json"
            }
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement
            .GetProperty("candidates")[0]
            .GetProperty("content")
            .GetProperty("parts")[0]
            .GetProperty("text").GetString();

        return string.IsNullOrWhiteSpace(text) ? null : ParseAiResponseJson(text);
    }

    private static async Task<ArticleEvaluation?> CallOpenAiCompatibleAsync(HttpClient httpClient, string url, string apiKey, string defaultModel, string prompt)
    {
        string model = Environment.GetEnvironmentVariable("AI_MODEL") ?? defaultModel;

        var payload = new
        {
            model = model,
            messages = new[]
            {
                new { role = "system", content = "You are an AI article evaluator. Output only JSON." },
                new { role = "user", content = prompt }
            },
            temperature = 0.2
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", apiKey);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement
            .GetProperty("choices")[0]
            .GetProperty("message")
            .GetProperty("content").GetString();

        return string.IsNullOrWhiteSpace(text) ? null : ParseAiResponseJson(text);
    }

    private static async Task<ArticleEvaluation?> CallOllamaAsync(HttpClient httpClient, string url, string defaultModel, string prompt)
    {
        string model = Environment.GetEnvironmentVariable("AI_MODEL") ?? defaultModel;

        var payload = new
        {
            model = model,
            prompt = prompt,
            stream = false,
            format = "json"
        };

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Content = new StringContent(JsonSerializer.Serialize(payload), Encoding.UTF8, "application/json");

        using var response = await httpClient.SendAsync(request);
        response.EnsureSuccessStatusCode();

        string json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        var text = doc.RootElement.GetProperty("response").GetString();

        return string.IsNullOrWhiteSpace(text) ? null : ParseAiResponseJson(text);
    }

    private static ArticleEvaluation? ParseAiResponseJson(string jsonText)
    {
        try
        {
            string cleanJson = jsonText.Trim();
            if (cleanJson.StartsWith("```"))
            {
                int firstBrace = cleanJson.IndexOf('{');
                int lastBrace = cleanJson.LastIndexOf('}');
                if (firstBrace >= 0 && lastBrace >= firstBrace)
                {
                    cleanJson = cleanJson.Substring(firstBrace, lastBrace - firstBrace + 1);
                }
            }

            using var doc = JsonDocument.Parse(cleanJson);
            var root = doc.RootElement;

            int importance = ExtractInt(root, "importance", 7);
            int careerRoi = ExtractInt(root, "career_roi", "careerRoi", 7);
            int timelessness = ExtractInt(root, "timelessness", 7);
            int readTimeMinutes = ExtractInt(root, "read_time_minutes", "readTimeMinutes", 4);

            importance = Math.Clamp(importance, 1, 10);
            careerRoi = Math.Clamp(careerRoi, 1, 10);
            timelessness = Math.Clamp(timelessness, 1, 10);
            readTimeMinutes = Math.Max(1, readTimeMinutes);

            return new ArticleEvaluation(importance, careerRoi, timelessness, $"{readTimeMinutes} min");
        }
        catch
        {
            return null;
        }
    }

    private static int ExtractInt(JsonElement root, string prop1, int fallback)
    {
        if (root.TryGetProperty(prop1, out var elem) && elem.TryGetInt32(out int val))
        {
            return val;
        }
        return fallback;
    }

    private static int ExtractInt(JsonElement root, string prop1, string prop2, int fallback)
    {
        if (root.TryGetProperty(prop1, out var elem1) && elem1.TryGetInt32(out int val1))
        {
            return val1;
        }
        if (root.TryGetProperty(prop2, out var elem2) && elem2.TryGetInt32(out int val2))
        {
            return val2;
        }
        return fallback;
    }

    private static ArticleEvaluation EvaluateLocallyHeuristic(string title, string? description, string categoryName)
    {
        // Compute estimated read time based on text length and complexity
        string combinedText = $"{title} {description}";
        int wordCount = Regex.Matches(combinedText, @"\b\w+\b").Count;
        
        // Since RSS descriptions are usually summaries, scale word count to full article read time
        int readTimeMinutes = wordCount < 60 ? 4 : Math.Max(4, (int)Math.Ceiling(wordCount * 3.5 / 230.0));

        // Baseline scores by category
        int importance = categoryName switch
        {
            "Security" => 8,
            "Architecture & Distributed Systems" => 8,
            "AI Engineering" => 8,
            "Databases & Data" => 7,
            ".NET & Backend" => 7,
            "Cloud & Platform" => 7,
            _ => 6
        };

        int careerRoi = categoryName switch
        {
            "Architecture & Distributed Systems" => 8,
            "Databases & Data" => 8,
            ".NET & Backend" => 8,
            "Security" => 8,
            "AI Engineering" => 7,
            _ => 6
        };

        int timelessness = categoryName switch
        {
            "Databases & Data" => 9,
            "Architecture & Distributed Systems" => 9,
            "Human Systems" => 8,
            ".NET & Backend" => 7,
            "Security" => 6,
            _ => 6
        };

        // Keyword modifiers
        string lowerText = combinedText.ToLowerInvariant();
        if (Regex.IsMatch(lowerText, @"\b(cve|zero-day|vulnerability|critical|breaking|architecture|benchmark|consensus|distributed|engine)\b"))
        {
            importance = Math.Min(10, importance + 2);
        }
        if (Regex.IsMatch(lowerText, @"\b(design|pattern|performance|optimization|deep dive|internals|concurrency|async|sql|algorithm|system)\b"))
        {
            careerRoi = Math.Min(10, careerRoi + 2);
        }
        if (Regex.IsMatch(lowerText, @"\b(fundamental|mental model|principle|btree|raft|paxos|postgres|tcp|http|index|memory)\b"))
        {
            timelessness = Math.Min(10, timelessness + 2);
        }
        if (Regex.IsMatch(lowerText, @"\b(weekly|digest|podcast|minor|release notes|v1\.\d|announce)\b"))
        {
            importance = Math.Max(1, importance - 1);
            timelessness = Math.Max(1, timelessness - 2);
        }

        return new ArticleEvaluation(importance, careerRoi, timelessness, $"{readTimeMinutes} min");
    }
}
