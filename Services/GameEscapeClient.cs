using System.Net;
using System.Text.Json;
using Lodestone.Models;

namespace Lodestone.Services;

public sealed class GameEscapeClient : IDisposable
{
    private const string BaseUrl = "https://ffxiv.gamerescape.com";
    private const string ConsoleGamesWikiBaseUrl = "https://ffxiv.consolegameswiki.com";
    private const int MaxCacheEntries = 80;
    private const int MaxFetchAttempts = 3;
    private readonly HttpClient httpClient = new();
    private readonly string cachePath;
    private readonly object cacheLock = new();
    private Dictionary<string, GameEscapeQuest> questCache = new(StringComparer.OrdinalIgnoreCase);

    public GameEscapeClient(DirectoryInfo configDirectory)
    {
        cachePath = Path.Combine(configDirectory.FullName, "gamerescape-quest-cache.json");
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("Mozilla/5.0 LodestoneDalamudPlugin/0.1");
        LoadCache();
    }

    public async Task<GameEscapeQuest> LookupQuestAsync(string questName, CancellationToken cancellationToken = default, Action<QuestLookupProgress>? progress = null)
    {
        var slug = Slugify(questName);
        var pageUrl = $"{BaseUrl}/wiki/{slug}";
        var consolePageUrl = $"{ConsoleGamesWikiBaseUrl}/wiki/{slug}";
        var cached = GetCachedQuest(slug);
        var errors = new List<string>();
        GameEscapeQuest? locationFallback = null;
        var locationFallbackChecked = false;

        progress?.Invoke(new QuestLookupProgress("Checking local quest cache.", 0.05f));

        var fetchers = new (string Label, float Percent, Func<string, CancellationToken, Task<string>> Fetcher)[]
        {
            ("Trying Gamer Escape parse API.", 0.20f, FetchParseApiAsync),
            ("Trying Gamer Escape raw API.", 0.42f, FetchRawApiAsync),
            ("Trying Gamer Escape page HTML.", 0.62f, FetchWikiPageAsync)
        };

        foreach (var fetcher in fetchers)
        {
            try
            {
                progress?.Invoke(new QuestLookupProgress(fetcher.Label, fetcher.Percent, true));
                var content = await fetcher.Fetcher(slug, cancellationToken);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                var trimmed = content.TrimStart();
                var quest = trimmed.StartsWith('{')
                    ? ParseApiResponse(content, questName, pageUrl)
                    : trimmed.Contains("Markdown Content:", StringComparison.OrdinalIgnoreCase)
                        ? ParseMarkdown(content, questName, pageUrl)
                        : ParseHtml(content, questName, pageUrl);

                quest.SourceName = "Gamer Escape";
                if (IsUsableQuest(quest))
                {
                    if (!quest.HasLocation)
                    {
                        locationFallbackChecked = true;
                        locationFallback = await TryLookupConsoleGamesWikiAsync(slug, questName, consolePageUrl, cancellationToken, errors, progress);
                        if (locationFallback is { HasLocation: true })
                        {
                            progress?.Invoke(new QuestLookupProgress("Loaded ConsoleGamesWiki location fallback.", 0.96f));
                            SetCachedQuest(slug, locationFallback);
                            return locationFallback;
                        }
                    }

                    SetCachedQuest(slug, quest);
                    progress?.Invoke(new QuestLookupProgress("Loaded quest data.", 1f));
                    return quest;
                }
            }
            catch (Exception ex)
            {
                errors.Add(ex.Message);
            }
        }

        if (!locationFallbackChecked)
            locationFallback = await TryLookupConsoleGamesWikiAsync(slug, questName, consolePageUrl, cancellationToken, errors, progress);

        if (locationFallback != null)
        {
            progress?.Invoke(new QuestLookupProgress("Loaded ConsoleGamesWiki fallback.", 0.96f));
            SetCachedQuest(slug, locationFallback);
            return locationFallback;
        }

        if (cached != null && IsUsableCachedQuest(cached))
        {
            progress?.Invoke(new QuestLookupProgress("Using cached quest data.", 1f));
            return cached;
        }

        throw new InvalidOperationException("Look up failed, Try again later.");
    }

    private async Task<GameEscapeQuest?> TryLookupConsoleGamesWikiAsync(string slug, string questName, string pageUrl, CancellationToken cancellationToken, List<string> errors, Action<QuestLookupProgress>? progress)
    {
        try
        {
            progress?.Invoke(new QuestLookupProgress("Trying ConsoleGamesWiki fallback.", 0.82f, true));
            var content = await FetchConsoleRawWikiPageAsync(slug, cancellationToken);
            if (string.IsNullOrWhiteSpace(content))
                return null;

            var quest = ParseConsoleGamesWikiRaw(content, questName, pageUrl);
            return IsUsableQuest(quest) ? quest : null;
        }
        catch (Exception ex)
        {
            errors.Add(ex.Message);
            return null;
        }
    }

    private async Task<string> FetchParseApiAsync(string slug, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/w/api.php?action=parse&page={Uri.EscapeDataString(slug)}&prop=text|displaytitle&format=json&origin=*";
        return await FetchStringWithRetriesAsync(url, cancellationToken);
    }

    private async Task<string> FetchRawApiAsync(string slug, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/w/api.php?action=query&titles={Uri.EscapeDataString(slug)}&prop=revisions&rvprop=content&rvslots=main&format=json&formatversion=2&origin=*";
        return await FetchStringWithRetriesAsync(url, cancellationToken);
    }

    private async Task<string> FetchWikiPageAsync(string slug, CancellationToken cancellationToken)
    {
        var url = $"{BaseUrl}/wiki/{Uri.EscapeDataString(slug)}";
        return await FetchStringWithRetriesAsync(url, cancellationToken);
    }

    private async Task<string> FetchConsoleRawWikiPageAsync(string slug, CancellationToken cancellationToken)
    {
        var url = $"{ConsoleGamesWikiBaseUrl}/wiki/{Uri.EscapeDataString(slug)}?action=raw";
        return await FetchStringWithRetriesAsync(url, cancellationToken, "ConsoleGamesWiki");
    }

    private async Task<string> FetchStringWithRetriesAsync(string url, CancellationToken cancellationToken, string sourceName = "Gamer Escape")
    {
        for (var attempt = 1; attempt <= MaxFetchAttempts; attempt++)
        {
            try
            {
                using var response = await httpClient.GetAsync(url, cancellationToken);
                if (response.IsSuccessStatusCode)
                    return await response.Content.ReadAsStringAsync(cancellationToken);

                var exception = new HttpRequestException($"{sourceName} returned {(int)response.StatusCode} {response.StatusCode}.", null, response.StatusCode);
                if (!ShouldRetry(response.StatusCode) || attempt >= MaxFetchAttempts)
                    throw exception;
            }
            catch (HttpRequestException ex) when (ShouldRetry(ex.StatusCode) && attempt < MaxFetchAttempts)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(350 * attempt * attempt), cancellationToken);
        }

        throw new InvalidOperationException($"{sourceName} did not return data.");
    }

    private static bool IsUsableQuest(GameEscapeQuest quest)
        => quest.HasLocation
           || !string.IsNullOrWhiteSpace(quest.Acquisition)
           || !string.IsNullOrWhiteSpace(quest.Description)
           || quest.Requirements.Count > 0
           || quest.Rewards.Count > 0
           || quest.Objectives.Count > 0;

    private static bool IsUsableCachedQuest(GameEscapeQuest quest)
        => IsUsableQuest(quest)
           && (!quest.MapX.HasValue || !quest.MapY.HasValue || !string.IsNullOrWhiteSpace(quest.Zone) || !string.IsNullOrWhiteSpace(quest.ClosestAetheryte));

    private static bool ShouldRetry(HttpStatusCode? statusCode)
        => statusCode is HttpStatusCode.Forbidden
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private GameEscapeQuest? GetCachedQuest(string slug)
    {
        lock (cacheLock)
            return questCache.TryGetValue(slug, out var cached) ? cached : null;
    }

    private void SetCachedQuest(string slug, GameEscapeQuest quest)
    {
        if (!IsUsableCachedQuest(quest))
            return;

        lock (cacheLock)
        {
            questCache[slug] = quest;
            questCache = questCache
                .OrderByDescending(pair => pair.Value.FetchedAt)
                .Take(MaxCacheEntries)
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            SaveCache();
        }
    }

    private void LoadCache()
    {
        try
        {
            if (!File.Exists(cachePath))
                return;

            var json = File.ReadAllText(cachePath);
            questCache = JsonSerializer.Deserialize<Dictionary<string, GameEscapeQuest>>(json) ?? new Dictionary<string, GameEscapeQuest>(StringComparer.OrdinalIgnoreCase);
            questCache = new Dictionary<string, GameEscapeQuest>(questCache, StringComparer.OrdinalIgnoreCase);
            var cleanedCache = questCache
                .Where(pair => IsUsableCachedQuest(pair.Value))
                .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);
            if (cleanedCache.Count != questCache.Count)
            {
                questCache = cleanedCache;
                SaveCache();
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to load Gamer Escape quest cache.");
            questCache = new Dictionary<string, GameEscapeQuest>(StringComparer.OrdinalIgnoreCase);
        }
    }

    private void SaveCache()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(cachePath) ?? string.Empty);
            var json = JsonSerializer.Serialize(questCache);
            File.WriteAllText(cachePath, json);
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to save Gamer Escape quest cache.");
        }
    }

    private static GameEscapeQuest ParseApiResponse(string json, string query, string pageUrl)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.TryGetProperty("parse", out var parse))
        {
            var title = parse.TryGetProperty("displaytitle", out var displayTitle)
                ? CleanText(displayTitle.GetString() ?? query)
                : query;
            var html = parse.GetProperty("text").GetProperty("*").GetString() ?? string.Empty;
            var quest = ParseHtml(html, query, pageUrl);
            quest.Title = string.IsNullOrWhiteSpace(quest.Title) ? title : quest.Title;
            return quest;
        }

        if (root.TryGetProperty("query", out var queryNode)
            && queryNode.TryGetProperty("pages", out var pages)
            && pages.ValueKind == JsonValueKind.Array)
        {
            foreach (var page in pages.EnumerateArray())
            {
                var title = page.TryGetProperty("title", out var titleNode) ? titleNode.GetString() ?? query : query;
                var content = ExtractRevisionContent(page);
                if (string.IsNullOrWhiteSpace(content))
                    continue;

                var quest = ParseWikitext(content, title, pageUrl);
                quest.Query = query;
                return quest;
            }
        }

        throw new InvalidOperationException("Gamer Escape API did not include parseable quest content.");
    }

    private static string ExtractRevisionContent(JsonElement page)
    {
        if (!page.TryGetProperty("revisions", out var revisions) || revisions.ValueKind != JsonValueKind.Array)
            return string.Empty;

        var revision = revisions.EnumerateArray().FirstOrDefault();
        if (revision.ValueKind == JsonValueKind.Undefined)
            return string.Empty;

        if (revision.TryGetProperty("slots", out var slots)
            && slots.TryGetProperty("main", out var main)
            && main.TryGetProperty("content", out var content))
            return content.GetString() ?? string.Empty;

        if (revision.TryGetProperty("*", out var legacyContent))
            return legacyContent.GetString() ?? string.Empty;

        return string.Empty;
    }

    private static GameEscapeQuest ParseWikitext(string text, string title, string pageUrl)
    {
        var quest = new GameEscapeQuest
        {
            Query = title,
            Title = CleanTitle(title),
            Url = pageUrl,
            SourceName = "Gamer Escape",
            FetchedAt = DateTime.UtcNow
        };

        var fields = ParseConsoleGamesWikiFields(text);

        string Field(params string[] keys)
            => keys.Select(key => fields.TryGetValue(key, out var value) ? value : string.Empty)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

        quest.QuestGiver = Field("Quest Giver", "Issuer", "NPC");
        quest.Zone = Field("Location", "Zone");
        quest.LocationDetail = Field("Coordinates", "Area", "Place");
        quest.ClosestAetheryte = NormalizeAetheryte(Field("Closest Aetheryte", "Aetheryte"));
        quest.Description = Field("Description", "Short Description");

        if (!string.IsNullOrWhiteSpace(quest.Zone) && quest.Zone.Contains(" - ", StringComparison.Ordinal))
            SetZoneAndDetail(quest, quest.Zone);

        if (float.TryParse(Field("X", "X Coordinate", "X"), out var x))
            quest.MapX = x;
        if (float.TryParse(Field("Y", "Y Coordinate", "Y"), out var y))
            quest.MapY = y;

        var level = Field("Level", "Required Level");
        if (!string.IsNullOrWhiteSpace(level))
            quest.Requirements.Add($"Any Class (Level {level})");

        quest.Objectives = ExtractListAfterHeading(text, "Objectives").Select(CleanWikiText).Where(NotBlank).ToList();
        quest.Rewards = fields
            .Where(kvp => kvp.Key.Contains("Reward", StringComparison.OrdinalIgnoreCase)
                          || kvp.Key.Contains("Gil", StringComparison.OrdinalIgnoreCase)
                          || kvp.Key.Contains("Experience", StringComparison.OrdinalIgnoreCase)
                          || kvp.Key.Contains("Achievement", StringComparison.OrdinalIgnoreCase))
            .Select(kvp => kvp.Value)
            .Where(NotBlank)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ComposeAcquisition(quest);
        return quest;
    }

    private static Dictionary<string, string> ParseConsoleGamesWikiFields(string text)
    {
        var fields = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var rawLine in text.Replace("\r", string.Empty, StringComparison.Ordinal).Split('\n'))
        {
            var line = rawLine.Trim();
            if (!line.StartsWith('|'))
                continue;

            var equalIndex = line.IndexOf('=');
            if (equalIndex <= 1)
                continue;

            var key = line[1..equalIndex].Trim();
            var value = line[(equalIndex + 1)..].Trim();
            if (!string.IsNullOrWhiteSpace(key))
                fields[key] = CleanWikiText(value);
        }

        return fields;
    }

    private static GameEscapeQuest ParseHtml(string html, string query, string pageUrl)
    {
        var quest = new GameEscapeQuest
        {
            Query = query,
            Url = pageUrl,
            Title = CleanTitle(FirstMatch(html, "<h1[^>]*>(?<value>.*?)</h1>")),
            SourceName = "Gamer Escape",
            FetchedAt = DateTime.UtcNow
        };
        if (string.IsNullOrWhiteSpace(quest.Title))
            quest.Title = CleanTitle(query);

        var lines = CleanArticleLines(html);
        quest.Acquisition = ExtractSection(lines, "Acquisition").FirstOrDefault() ?? string.Empty;
        quest.Requirements = ExtractSection(lines, "Requirements").ToList();
        quest.Rewards = ExtractSection(lines, "Rewards").ToList();
        quest.Description = string.Join("\n", ExtractSection(lines, "Description"));
        quest.Objectives = ExtractSection(lines, "Objectives")
            .Select(line => line.TrimStart('-', '•', ' '))
            .Where(NotBlank)
            .ToList();

        ParseLocation(quest);
        if (string.IsNullOrWhiteSpace(quest.ClosestAetheryte))
        {
            var closest = lines.FirstOrDefault(line => line.Contains("Closest Aetheryte:", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrWhiteSpace(closest))
                quest.ClosestAetheryte = NormalizeAetheryte(closest.Split(':', 2).Last().Trim());
        }
        else
        {
            quest.ClosestAetheryte = NormalizeAetheryte(quest.ClosestAetheryte);
        }

        ComposeAcquisition(quest);
        return quest;
    }

    private static GameEscapeQuest ParseMarkdown(string markdown, string query, string pageUrl)
    {
        var content = markdown;
        var marker = content.IndexOf("Markdown Content:", StringComparison.OrdinalIgnoreCase);
        if (marker >= 0)
            content = content[(marker + "Markdown Content:".Length)..];

        var quest = new GameEscapeQuest
        {
            Query = query,
            Url = pageUrl,
            Title = CleanTitle(query),
            SourceName = "Gamer Escape",
            FetchedAt = DateTime.UtcNow
        };

        var headers = Regex.Matches(content, @"^#\s+(?<value>.+)$", RegexOptions.Multiline)
            .Select(match => CleanMarkdownText(match.Groups["value"].Value))
            .Where(NotBlank)
            .ToArray();
        quest.Title = headers.FirstOrDefault(header => header.Equals(CleanTitle(query), StringComparison.OrdinalIgnoreCase))
                      ?? headers.FirstOrDefault(header => !header.Contains("Gamer Escape", StringComparison.OrdinalIgnoreCase))
                      ?? quest.Title;

        quest.Acquisition = CleanMarkdownText(ExtractMarkdownBetween(content, @"\*\*Acquisition\*\*", @"Closest Aetheryte:|\*\*Requirements\*\*"));
        quest.ClosestAetheryte = NormalizeAetheryte(CleanMarkdownText(ExtractMarkdownBetween(content, @"Closest Aetheryte:", @"\*\*Requirements\*\*")));
        var requirement = CleanMarkdownText(ExtractMarkdownBetween(content, @"\*\*Requirements\*\*", @"\*\*Rewards\*\*"));
        if (!string.IsNullOrWhiteSpace(requirement))
            quest.Requirements.Add(requirement);

        var rewards = CleanMarkdownText(ExtractMarkdownBetween(content, @"\*\*Rewards\*\*", @"\*\*Description\*\*"), preserveImageAlt: true);
        rewards = CleanRewardText(rewards);
        if (!string.IsNullOrWhiteSpace(rewards))
            quest.Rewards.Add(rewards);

        quest.Description = CleanMarkdownText(ExtractMarkdownBetween(content, @"\*\*Description\*\*", @"\*\*Objectives\*\*"));

        var objectives = ExtractMarkdownBetween(content, @"\*\*Objectives\*\*", @"\*\*NPCs Involved\*\*");
        quest.Objectives = Regex.Matches(RemoveMarkdownImages(objectives), @"\*\s+(?<value>.*?)(?=\s+\*\s+|$)", RegexOptions.Singleline)
            .Select(match => CleanMarkdownText(match.Groups["value"].Value))
            .Where(NotBlank)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ParseLocation(quest);
        ComposeAcquisition(quest);
        return quest;
    }

    private static GameEscapeQuest ParseConsoleGamesWikiRaw(string text, string query, string pageUrl)
    {
        var quest = new GameEscapeQuest
        {
            Query = query,
            Url = pageUrl,
            Title = CleanTitle(query),
            SourceName = "ConsoleGamesWiki",
            FetchedAt = DateTime.UtcNow
        };

        var fields = ParseConsoleGamesWikiFields(text);

        string Field(params string[] keys)
            => keys.Select(key => fields.TryGetValue(key, out var value) ? value : string.Empty)
                .FirstOrDefault(value => !string.IsNullOrWhiteSpace(value)) ?? string.Empty;

        quest.Title = CleanTitle(Field("title"));
        if (string.IsNullOrWhiteSpace(quest.Title))
            quest.Title = CleanTitle(query);

        quest.QuestGiver = Field("quest-giver", "issuer", "npc");
        quest.Zone = Field("location", "zone");
        quest.Description = Field("description");
        quest.ClosestAetheryte = NormalizeAetheryte(Field("closest-aetheryte", "aetheryte"));

        if (float.TryParse(Field("location-x", "x"), out var x))
            quest.MapX = x;
        if (float.TryParse(Field("location-y", "y"), out var y))
            quest.MapY = y;

        var level = Field("level", "required-level");
        if (!string.IsNullOrWhiteSpace(level))
            quest.Requirements.Add($"Any Class (Level {level})");

        var requirements = Field("requirements", "req-quest", "req-items");
        if (!string.IsNullOrWhiteSpace(requirements))
            quest.Requirements.Add(requirements);

        var exp = Field("exp", "experience");
        if (!string.IsNullOrWhiteSpace(exp) && !exp.Equals("0", StringComparison.Ordinal))
            quest.Rewards.Add($"{exp} EXP");

        var gil = Field("gil");
        if (!string.IsNullOrWhiteSpace(gil) && !gil.Equals("0", StringComparison.Ordinal))
            quest.Rewards.Add($"{gil} Gil");

        foreach (var reward in fields
                     .Where(kvp => Regex.IsMatch(kvp.Key, @"^reward\d+$", RegexOptions.IgnoreCase) || kvp.Key.Equals("unlocks", StringComparison.OrdinalIgnoreCase))
                     .Select(kvp => kvp.Value)
                     .Where(NotBlank))
            quest.Rewards.Add(reward);

        quest.Rewards = quest.Rewards
            .Select(CleanRewardText)
            .Where(NotBlank)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        quest.Objectives = ExtractListAfterHeading(text, "Steps")
            .Select(CleanWikiText)
            .Where(NotBlank)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        ComposeAcquisition(quest);
        return quest;
    }

    private static void ParseLocation(GameEscapeQuest quest)
    {
        var acquisition = quest.Acquisition;
        var match = Regex.Match(acquisition, @"^(?<giver>[^:]+):\s*(?<location>.*?)\s*\(x:(?<x>\d+(?:\.\d+)?),\s*y:(?<y>\d+(?:\.\d+)?)\)", RegexOptions.IgnoreCase);
        if (!match.Success)
            return;

        quest.QuestGiver = match.Groups["giver"].Value.Trim();
        SetZoneAndDetail(quest, match.Groups["location"].Value.Trim());
        if (float.TryParse(match.Groups["x"].Value, out var x))
            quest.MapX = x;
        if (float.TryParse(match.Groups["y"].Value, out var y))
            quest.MapY = y;
    }

    private static void SetZoneAndDetail(GameEscapeQuest quest, string location)
    {
        if (string.IsNullOrWhiteSpace(location))
            return;

        var parts = location
            .Split(" - ", StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .ToList();
        if (parts.Count == 0)
            return;

        var zonePartCount = parts[0].Equals("Ul'dah", StringComparison.OrdinalIgnoreCase) && parts.Count >= 2
            ? 2
            : 1;

        quest.Zone = string.Join(" - ", parts.Take(zonePartCount));
        quest.LocationDetail = string.Join(" - ", parts.Skip(zonePartCount));
    }

    private static void ComposeAcquisition(GameEscapeQuest quest)
    {
        if (!string.IsNullOrWhiteSpace(quest.Acquisition))
            return;

        var location = quest.Zone;
        if (!string.IsNullOrWhiteSpace(quest.LocationDetail))
            location = string.IsNullOrWhiteSpace(location) ? quest.LocationDetail : $"{location} - {quest.LocationDetail}";
        if (quest.MapX.HasValue && quest.MapY.HasValue)
            location = $"{location} (x:{quest.MapX:0.0}, y:{quest.MapY:0.0})";
        quest.Acquisition = string.IsNullOrWhiteSpace(quest.QuestGiver)
            ? location
            : $"{quest.QuestGiver}: {location}";
    }

    private static string NormalizeAetheryte(string value)
    {
        value = value.Replace("→", ">", StringComparison.Ordinal).Trim();
        if (value.Contains('>'))
            value = value.Split('>', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).LastOrDefault() ?? value;
        return value.Trim();
    }

    private static IEnumerable<string> ExtractSection(IReadOnlyList<string> lines, string heading)
    {
        var start = Array.FindIndex(lines.ToArray(), line => line.Equals(heading, StringComparison.OrdinalIgnoreCase));
        if (start < 0)
            return [];

        var result = new List<string>();
        for (var i = start + 1; i < lines.Count; i++)
        {
            if (IsHeading(lines[i]))
                break;
            if (!string.IsNullOrWhiteSpace(lines[i]) && !lines[i].StartsWith("Edit ", StringComparison.OrdinalIgnoreCase))
                result.Add(lines[i]);
        }

        return result;
    }

    private static string[] CleanArticleLines(string html)
    {
        var text = Regex.Replace(html, "<script.*?</script>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<style.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<img[^>]*alt=[\"'](?<alt>[^\"']+)[\"'][^>]*>", "\n${alt}\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<h[1-6][^>]*>", "\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "</h[1-6]>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<li[^>]*>", "\n- ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "</li>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</p>|</tr>|</div>|</table>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<a[^>]*>(?<value>.*?)</a>", match => CleanText(match.Groups["value"].Value), RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<[^>]+>", " ", RegexOptions.Singleline);
        text = WebUtility.HtmlDecode(text);
        return text.Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => Regex.Replace(line, @"\s+", " ").Trim())
            .Where(NotBlank)
            .Distinct()
            .ToArray();
    }

    private static IEnumerable<string> ExtractListAfterHeading(string text, string heading)
    {
        var match = Regex.Match(text, $@"==+\s*{Regex.Escape(heading)}\s*==+(?<body>.*?)(?:\n==|\z)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return [];

        return match.Groups["body"].Value
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(line => line.StartsWith("*") || line.StartsWith("#"))
            .Select(line => line.TrimStart('*', '#', ' '));
    }

    private static string ExtractMarkdownBetween(string text, string startPattern, string endPattern)
    {
        var match = Regex.Match(text, $"{startPattern}(?<body>.*?)(?:{endPattern}|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["body"].Value : string.Empty;
    }

    private static bool IsHeading(string line)
    {
        var normalized = line.Trim().TrimEnd(':');
        return normalized is "Details" or "Interactions" or "Journal" or "Dialogue" or "Gallery"
            or "Acquisition" or "Requirements" or "Rewards" or "Description" or "Objectives";
    }

    private static string FirstMatch(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private static string CleanTitle(string value)
        => CleanText(value.Replace("_", " ", StringComparison.Ordinal)).Trim();

    private static string CleanWikiText(string value)
    {
        value = Regex.Replace(value, @"\[\[(?:[^|\]]+\|)?(?<text>[^\]]+)\]\]", "${text}");
        value = Regex.Replace(value, @"\{\{(?:[^\|}]+\|)*(?<text>[^|}]+)\}\}", "${text}");
        value = value.Replace("'''", string.Empty, StringComparison.Ordinal).Replace("''", string.Empty, StringComparison.Ordinal);
        return CleanText(value);
    }

    private static string RemoveMarkdownImages(string value, bool preserveAlt = false)
        => Regex.Replace(value, @"!\[(?<alt>[^\]]*)\]\([^)]+\)", match =>
        {
            if (!preserveAlt)
                return " ";

            var alt = CleanImageAlt(match.Groups["alt"].Value);
            return string.IsNullOrWhiteSpace(alt) ? " " : $" {alt} ";
        }, RegexOptions.Singleline);

    private static string CleanMarkdownText(string value, bool preserveImageAlt = false)
    {
        value = value.Replace("\")-[", "\") - [", StringComparison.Ordinal)
            .Replace(")-[", ") - [", StringComparison.Ordinal);
        value = RemoveMarkdownImages(value, preserveImageAlt);
        value = Regex.Replace(value, @"\[(?<text>[^\]]*)\]\((?<target>[^)]*)\)", match =>
        {
            var text = match.Groups["text"].Value.Trim();
            if (!string.IsNullOrWhiteSpace(text))
                return $" {text} ";

            if (!preserveImageAlt)
                return " ";

            var target = match.Groups["target"].Value;
            var title = Regex.Match(target, "\"(?<title>[^\"]+)\"");
            if (title.Success)
                return $" {title.Groups["title"].Value} ";

            var url = target.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault() ?? string.Empty;
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                return " ";

            return $" {WebUtility.UrlDecode(Path.GetFileName(uri.AbsolutePath)).Replace('_', ' ')} ";
        }, RegexOptions.Singleline);
        value = value.Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("__", string.Empty, StringComparison.Ordinal)
            .Replace("\\_", "_", StringComparison.Ordinal);
        return CleanText(value);
    }

    private static string CleanImageAlt(string alt)
    {
        alt = Regex.Replace(alt, @"^Image\s+\d+:\s*", string.Empty, RegexOptions.IgnoreCase).Trim();
        if (string.IsNullOrWhiteSpace(alt) || Regex.IsMatch(alt, @"^Image\s+\d+$", RegexOptions.IgnoreCase))
            return string.Empty;

        var lower = alt.ToLowerInvariant();
        if (lower.Contains("journal detail")
            || lower.Contains("spacer")
            || lower.Contains("latestpatch")
            || lower.Contains("quest sync")
            || lower.EndsWith(".png", StringComparison.Ordinal)
            || lower.Contains("edit ")
            || lower.Contains("add image")
            || lower.Contains("map33")
            || lower.Contains("player")
            || lower.Contains("refresh"))
            return string.Empty;

        return alt;
    }

    private static string CleanText(string html)
    {
        var noTags = Regex.Replace(html, "<[^>]+>", " ", RegexOptions.Singleline);
        var cleaned = WebUtility.HtmlDecode(Regex.Replace(noTags, @"\s+", " ")).Trim();
        return Regex.Replace(cleaned, @"\s+([.,;:!?])", "$1");
    }

    private static string CleanRewardText(string value)
    {
        value = Regex.Replace(value, @"\s*Edit\s+.*?(?:Reward|Notes)\s*$", string.Empty, RegexOptions.IgnoreCase);
        value = CollapseRepeatedPhrase(value, "Senor Otter Pack");
        value = CollapseRepeatedPhrase(value, "Golden Globe-trotter");
        return value.Trim();
    }

    private static string CollapseRepeatedPhrase(string value, string phrase)
    {
        var escaped = Regex.Escape(phrase);
        return Regex.Replace(value, $@"\b{escaped}(?:\s+{escaped})+\b", phrase, RegexOptions.IgnoreCase);
    }

    private static bool NotBlank(string value) => !string.IsNullOrWhiteSpace(value);

    internal static string Slugify(string questName)
    {
        var normalized = questName.Trim().Replace(' ', '_');
        return Uri.EscapeDataString(normalized).Replace("%5F", "_", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => httpClient.Dispose();
}
