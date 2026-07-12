# InfoHubParser - RSS to Discord Bot

A lightweight, reliable, and production-ready .NET 10 console application designed to run on a schedule (e.g., via GitHub Actions) to parse RSS feeds, filter out duplicate articles using a local SQLite database, and publish new updates to Discord via webhooks.

## Features

- **Duplicate Prevention:** Tracks previously processed articles in a local SQLite database (`news.db`).
- **Resilient Feed Fetching:** Includes automatic retries for transient network issues (HTTP 5xx, timeouts) and parses feeds safely by ignoring risky DTDs/comments.
- **Discord Rate-Limit Handling:** Automatically respects Discord API rate limits (`429 Too Many Requests`) by checking the `Retry-After` headers and retrying rather than discarding notifications.
- **Sequential Category Isolation:** Ensures that a failure in one RSS feed or category does not prevent other categories or feeds from being processed.
- **GitHub Actions Optimized:** Designed to run inside GitHub Actions via cron triggers, automatically committing and pushing database state changes back to the repository using rebase-on-retry push resilience.
- **Flexible Webhook Resolving:** Key configurations are case-insensitive and support both standard underscore (`DISCORD_WEBHOOK_...`) and hyphen (`discrod_webhook_...`) spellings to avoid deployment-related environment misconfigurations.

---

## Category Configurations & Webhooks

The bot automatically maps feed categories to corresponding Discord Webhook environment variables:

| Category | Description | Primary Env / Secrets Checked |
|---|---|---|
| **Daily Digests** | Tech digests and daily newsletters (Hacker News, TLDR, Dan Luu, Simon Willison, Martin Fowler) | `DISCORD_WEBHOOK_DAILY_DIGESTS`<br>`discord_webhook_daily-digests` |
| **.NET & Backend** | C# and .NET blogs (Andrew Lock, .NET Blog, Steve Gordon) | `DISCORD_WEBHOOK_CSHARP_DOTNET`<br>`discord_webhook_csharp-dotnet` |
| **Architecture & Distributed Systems** | System architecture and scale blogs (Netflix, Stripe, Cloudflare, ByteByteGo, Uber, Shopify) | `DISCORD_WEBHOOK_ARCHITECTURE_SYSTEMS`<br>`discord_webhook_architecture-systems` |
| **Databases & Data** | Database engineering and SQL performance (PostgreSQL, Brent Ozar, SQLPerformance, Use The Index Luke) | `DISCORD_WEBHOOK_DATABASES`<br>`discord_webhook_databases` |
| **Cloud & Platform** | Infrastructure, Kubernetes & containers (AWS Architecture, Azure Architecture, Docker, Kubernetes, OpenTelemetry) | `DISCORD_WEBHOOK_CLOUD_PLATFORM`<br>`discord_webhook_cloud-platform` |
| **Security** | Security research & OWASP updates (OWASP, Trail of Bits, Auth0) | `DISCORD_WEBHOOK_SECURITY`<br>`discord_webhook_security` |
| **AI Engineering** | AI research & tools (Anthropic News, OpenAI News, Ollama) | `DISCORD_WEBHOOK_AI_ENGINEERING`<br>`discord_webhook_ai-engineering` |
| **Human Systems** | Thinking & productivity systems (Farnam Street, Works in Progress, Astral Codex Ten) | `DISCORD_WEBHOOK_HUMAN_SYSTEMS`<br>`discord_webhook_human-systems` |

---

## Free AI Article Evaluation

For every article published to Discord, the bot automatically assigns structured metrics inside the Discord Embed `Fields`:
- **Importance (1-10):** Significance and impact of the article/news on the tech industry.
- **Career ROI (1-10):** Long-term professional value and skill return-on-investment from reading the article.
- **Timelessness (1-10):** How evergreen the concepts are (`10` = lasting architectural models, `1` = transient release notes).
- **Read Time:** Estimated reading time in minutes (e.g., `4 min`).

### Free AI API Configuration
The bot natively supports multiple free AI evaluation engines. Simply set one of the following environment variables (or GitHub Secrets):
- `GEMINI_API_KEY`: [Google AI Studio (100% Free Tier)](https://aistudio.google.com/) (`gemini-2.5-flash`).
- `GROQ_API_KEY`: [Groq Free Tier](https://console.groq.com/) (`llama-3.3-70b-versatile`).
- `OPENROUTER_API_KEY`: [OpenRouter Free Tier](https://openrouter.ai/) (`google/gemini-2.0-flash-exp:free`).
- `OLLAMA_HOST`: Local Ollama URL (`http://localhost:11434`) for self-hosted evaluation.

> **Note:** If no AI API key is configured (or if an API call transiently fails), the bot seamlessly falls back to an intelligent local heuristic evaluator so that every article always includes calibrated importance, career ROI, timelessness, and read-time metrics without requiring paid APIs or blocking execution.

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- SQLite (optional, for viewing the database locally)

### Running Locally

1. Clone this repository.
2. Define your webhook environment variables and optional free AI API key:
   ```bash
   # Windows PowerShell
   $env:DISCORD_WEBHOOK_DAILY_DIGESTS="https://discord.com/api/webhooks/..."
   $env:DISCORD_WEBHOOK_CSHARP_DOTNET="https://discord.com/api/webhooks/..."
   $env:DISCORD_WEBHOOK_ARCHITECTURE_SYSTEMS="https://discord.com/api/webhooks/..."
   $env:GEMINI_API_KEY="your_free_google_ai_studio_key"
   
   # Linux/macOS
   export DISCORD_WEBHOOK_DAILY_DIGESTS="https://discord.com/api/webhooks/..."
   export DISCORD_WEBHOOK_CSHARP_DOTNET="https://discord.com/api/webhooks/..."
   export DISCORD_WEBHOOK_ARCHITECTURE_SYSTEMS="https://discord.com/api/webhooks/..."
   export GEMINI_API_KEY="your_free_google_ai_studio_key"
   ```
3. Run the application:
   ```bash
   dotnet run --project InfoHubParser/InfoHubParser.csproj
   ```

---

## Production Deployment on GitHub Actions

The repository includes a GitHub Actions workflow (`.github/workflows/prod.yml`) that runs every 6 hours to execute the bot and commit state changes back to the repository.

### Configuration Steps

1. In your GitHub repository, navigate to **Settings** > **Secrets and variables** > **Actions**.
2. Add any or all of the following **Repository Secrets**:
   - Webhooks: `DISCORD_WEBHOOK_DAILY_DIGESTS`, `DISCORD_WEBHOOK_CSHARP_DOTNET`, `DISCORD_WEBHOOK_ARCHITECTURE_SYSTEMS`, `DISCORD_WEBHOOK_DATABASES`, `DISCORD_WEBHOOK_CLOUD_PLATFORM`, `DISCORD_WEBHOOK_SECURITY`, `DISCORD_WEBHOOK_AI_ENGINEERING`, `DISCORD_WEBHOOK_HUMAN_SYSTEMS`
   - Free AI Evaluation (Optional): `GEMINI_API_KEY`, `GROQ_API_KEY`, or `OPENROUTER_API_KEY`
3. Under **Settings** > **Actions** > **General** > **Workflow permissions**, ensure that **Read and write permissions** is selected (this allows the action to commit updates back to `news.db` in your repository).

---

## License

This project is licensed under the MIT License.
