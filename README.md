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
| **Daily Digests** | Tech digests and daily newsletters (Hacker News, TLDR, morning brews) | `DISCORD_WEBHOOK_DAILY_DIGESTS`<br>`discord_webhook_daily-digests` |
| **.NET Deep Dive** | C# and .NET deep-dives (Andrew Lock, Docker, etc.) | `DISCORD_WEBHOOK_CSHARP_DOTNET`<br>`discord_webhook_csharp-dotnet` |
| **Architecture** | System architecture and scale blogs (Netflix TechBlog, Uber, Cloudflare, etc.) | `DISCORD_WEBHOOK_ARCHITECTURE_SYSTEMS`<br>`discord_webhook_architecture-systems` |

---

## Getting Started

### Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)
- SQLite (optional, for viewing the database locally)

### Running Locally

1. Clone this repository.
2. Define the webhook environment variables in your local shell or your IDE launch configuration:
   ```bash
   # Windows PowerShell
   $env:DISCORD_WEBHOOK_DAILY_DIGESTS="https://discord.com/api/webhooks/..."
   $env:DISCORD_WEBHOOK_CSHARP_DOTNET="https://discord.com/api/webhooks/..."
   $env:DISCORD_WEBHOOK_ARCHITECTURE_SYSTEMS="https://discord.com/api/webhooks/..."
   
   # Linux/macOS
   export DISCORD_WEBHOOK_DAILY_DIGESTS="https://discord.com/api/webhooks/..."
   export DISCORD_WEBHOOK_CSHARP_DOTNET="https://discord.com/api/webhooks/..."
   export DISCORD_WEBHOOK_ARCHITECTURE_SYSTEMS="https://discord.com/api/webhooks/..."
   ```
3. Run the application:
   ```bash
   dotnet run --project InfoHubParser/InfoHubParser.csproj
   ```

---

## Production Deployment on GitHub Actions

The repository includes a GitHub Actions workflow (`.github/workflows/sync.yml`) that runs every 2 hours to execute the bot and commit state changes back to the repository.

### Configuration Steps

1. In your GitHub repository, navigate to **Settings** > **Secrets and variables** > **Actions**.
2. Add the following **Repository Secrets**:
   - `DISCORD_WEBHOOK_DAILY_DIGESTS` — URL of your Discord channel webhook for daily digests.
   - `DISCORD_WEBHOOK_CSHARP_DOTNET` — URL of your Discord channel webhook for C# / .NET feed updates.
   - `DISCORD_WEBHOOK_ARCHITECTURE_SYSTEMS` — URL of your Discord channel webhook for system architecture updates.
3. Under **Settings** > **Actions** > **General** > **Workflow permissions**, ensure that **Read and write permissions** is selected (this allows the action to commit updates back to `news.db` in your repository).

---

## License

This project is licensed under the MIT License.
