using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Lodestone.Models;

namespace Lodestone.Services;

public sealed partial class LodestoneClient : IDisposable
{
    private const string DefaultHomeUrl = "https://na.finalfantasyxiv.com/lodestone/";
    private const string DefaultNewsHeroImage = ImageCache.AssetScheme + "default-news-hero.png";
    private const string DefaultMaintenanceHeroImage = ImageCache.AssetScheme + "default-maintenance-hero.png";
    private const string ProducerLiveHeroImage = ImageCache.AssetScheme + "producer-live-hero.png";
    private const string EternalBondingRestrictedHeroImage = ImageCache.AssetScheme + "eternal-bonding-restricted-hero.png";
    private const string SpecialImageStopMarker = "https://lds-img.finalfantasyxiv.com/h/L/EbtcXqPUGzsVYdi23FpUR25oH4.png";
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly HttpClient httpClient = new();
    private readonly FileInfo cacheFile;

    public LodestoneClient(DirectoryInfo configDirectory)
    {
        cacheFile = new FileInfo(Path.Combine(configDirectory.FullName, "lodestone-cache-v6.json"));
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LodestoneDalamudPlugin/0.1");
    }

    public async Task<IReadOnlyList<LodestoneEntry>> LoadCachedAsync()
    {
        try
        {
            if (!cacheFile.Exists)
                return [];

            await using var stream = cacheFile.OpenRead();
            return await JsonSerializer.DeserializeAsync<List<LodestoneEntry>>(stream, JsonOptions) ?? [];
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to load Lodestone cache.");
            return [];
        }
    }

    public void ClearCache()
    {
        try
        {
            if (cacheFile.Exists)
                cacheFile.Delete();
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Unable to clear Lodestone cache.");
        }
    }

    public (bool Exists, long Length, DateTime? LastWriteTimeUtc) GetCacheInfo()
    {
        cacheFile.Refresh();
        return cacheFile.Exists
            ? (true, cacheFile.Length, cacheFile.LastWriteTimeUtc)
            : (false, 0, null);
    }

    public async Task<IReadOnlyList<LodestoneEntry>> RefreshAsync(Configuration configuration, bool force)
    {
        var cached = (await LoadCachedAsync()).ToDictionary(e => e.Id, e => e);
        if (!force && cached.Count > 0)
        {
            var newestFetch = cached.Values.Max(e => e.FetchedAt);
            if (DateTime.UtcNow - newestFetch < TimeSpan.FromMinutes(Math.Max(15, configuration.RefreshMinutes)))
                return cached.Values.OrderBy(e => e.StartsAt).ToArray();
        }

        var feedImages = await FetchFeedImagesAsync(configuration);
        var indexEntries = await FetchIndexEntriesAsync(configuration);
        foreach (var entry in indexEntries)
        {
            if (!feedImages.TryGetValue(CanonicalContentUrl(entry.Url), out var imageUrl))
                continue;

            var normalizedImage = NormalizeHeroImage(imageUrl, entry.Kind);
            if (IsProducerLiveEntry(entry))
                normalizedImage = ProducerLiveHeroImage;
            else if (IsEternalBondingRestrictionEntry(entry))
                normalizedImage = EternalBondingRestrictedHeroImage;

            entry.HeroImageUrl = normalizedImage;
            if (!entry.ImageUrls.Contains(normalizedImage))
                entry.ImageUrls.Add(normalizedImage);
        }

        var stubs = indexEntries
            .Where(e => ShouldInclude(e.Kind, configuration))
            .GroupBy(e => e.Id)
            .Select(g => g.OrderByDescending(e => e.StartsAt).First())
            .OrderByDescending(e => e.StartsAt)
            .Take(Math.Clamp(configuration.MaxEntriesToScan, 5, 250))
            .ToList();

        var results = new List<LodestoneEntry>();
        foreach (var stub in stubs)
        {
            if (cached.TryGetValue(stub.Id, out var existing) && !force && !string.IsNullOrEmpty(existing.Summary))
            {
                results.Add(existing);
                continue;
            }

            results.Add(await EnrichEntryAsync(stub));
            await Task.Delay(250);
        }

        await SaveCacheAsync(results);
        return results.OrderBy(e => e.StartsAt).ToArray();
    }

    private async Task<List<LodestoneEntry>> FetchIndexEntriesAsync(Configuration configuration)
    {
        var entries = new List<LodestoneEntry>();
        var maxPages = Math.Clamp(configuration.MaxPagesPerSource, 1, 20);
        var sources = BuildSources(configuration).ToArray();

        foreach (var source in sources)
        {
            for (var page = 1; page <= maxPages; page++)
            {
                var url = PageUrl(source.Url, page);
                string html;
                try
                {
                    html = await httpClient.GetStringAsync(url);
                }
                catch (Exception ex)
                {
                    Plugin.Log.Warning(ex, "Failed to fetch Lodestone index {Url}", url);
                    break;
                }

                var pageEntries = ParseIndex(html, source.Kind, source.Url).ToArray();
                entries.AddRange(pageEntries);

                if (pageEntries.Length == 0 || !HasNextPage(html, page))
                    break;

                await Task.Delay(150);
            }
        }

        return entries;
    }

    private async Task<Dictionary<string, string>> FetchFeedImagesAsync(Configuration configuration)
    {
        var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var baseUrl = RegionBaseUrl(configuration);
        foreach (var feedUrl in new[]
                 {
                     $"{baseUrl}lodestone/news/news.xml",
                     $"{baseUrl}lodestone/news/topics.xml"
                 })
        {
            try
            {
                var xml = await httpClient.GetStringAsync(feedUrl);
                foreach (Match match in FeedEntryRegex().Matches(xml))
                {
                    var entryXml = match.Groups["entry"].Value;
                    var link = FeedAlternateLinkRegex().Match(entryXml);
                    var image = FeedImageLinkRegex().Match(entryXml);
                    if (!link.Success || !image.Success)
                        continue;

                    images[CanonicalContentUrl(WebUtility.HtmlDecode(link.Groups["url"].Value))] = WebUtility.HtmlDecode(image.Groups["url"].Value);
                }
            }
            catch (Exception ex)
            {
                Plugin.Log.Warning(ex, "Failed to fetch Lodestone feed {FeedUrl}", feedUrl);
            }
        }

        return images;
    }

    private async Task SaveCacheAsync(IReadOnlyCollection<LodestoneEntry> entries)
    {
        cacheFile.Directory?.Create();
        await using var stream = cacheFile.Create();
        await JsonSerializer.SerializeAsync(stream, entries.OrderBy(e => e.StartsAt).ToArray(), JsonOptions);
    }

    private async Task<LodestoneEntry> EnrichEntryAsync(LodestoneEntry stub)
    {
        try
        {
            var html = await httpClient.GetStringAsync(stub.Url);
            var entry = stub.Url.Contains("/lodestone/special/", StringComparison.OrdinalIgnoreCase)
                ? ParseSpecialPage(stub, html, await FetchSpecialStylesAsync(html))
                : ParseNewsPage(stub, html);

            entry.FetchedAt = DateTime.UtcNow;
            return entry;
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to enrich Lodestone entry {Title}", stub.Title);
            stub.FetchedAt = DateTime.UtcNow;
            return stub;
        }
    }

    private async Task<string> FetchSpecialStylesAsync(string html)
    {
        var builder = new StringBuilder();
        var stylesheetUrls = StylesheetRegex().Matches(html)
            .Select(m => AbsoluteUrl(WebUtility.HtmlDecode(m.Groups["url"].Value)))
            .Where(IsLodestoneStaticCss)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToArray();

        foreach (var stylesheetUrl in stylesheetUrls)
        {
            try
            {
                builder.AppendLine(await httpClient.GetStringAsync(stylesheetUrl));
            }
            catch (Exception ex)
            {
                Plugin.Log.Debug(ex, "Failed to fetch Lodestone special stylesheet {StylesheetUrl}", stylesheetUrl);
            }
        }

        return builder.ToString();
    }

    private static IEnumerable<(string Url, LodestoneEntryKind? Kind)> BuildSources(Configuration configuration)
    {
        var baseUrl = RegionBaseUrl(configuration);

        yield return ($"{baseUrl}lodestone/", null);

        if (configuration.ShowEvents || configuration.ShowTopics)
            yield return ($"{baseUrl}lodestone/topics/", LodestoneEntryKind.Topic);
        if (configuration.ShowNotices)
            yield return ($"{baseUrl}lodestone/news/category/1/", LodestoneEntryKind.Notice);
        if (configuration.ShowMaintenance)
            yield return ($"{baseUrl}lodestone/news/category/2/", LodestoneEntryKind.Maintenance);
        if (configuration.ShowUpdates)
            yield return ($"{baseUrl}lodestone/news/category/3/", LodestoneEntryKind.Update);
        if (configuration.ShowStatus || configuration.ShowRecovery)
            yield return ($"{baseUrl}lodestone/news/category/4/", LodestoneEntryKind.Status);
    }

    internal static IReadOnlyList<LodestoneEntry> ParseIndex(string html, LodestoneEntryKind? forcedKind = null, string baseUrl = DefaultHomeUrl)
    {
        var entries = new List<LodestoneEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in IndexLinkRegex().Matches(html))
        {
            var href = CanonicalContentUrl(AbsoluteUrl(WebUtility.HtmlDecode(match.Groups["href"].Value), baseUrl));
            var rawText = CleanText(match.Groups["text"].Value);
            var special = href.Contains("/lodestone/special/", StringComparison.OrdinalIgnoreCase);
            if ((!special && string.IsNullOrWhiteSpace(rawText)) || !IsLodestoneContentUrl(href))
                continue;

            var contextStart = Math.Max(0, match.Index - 800);
            var contextLength = Math.Min(html.Length - contextStart, match.Length + 1600);
            var context = html.Substring(contextStart, contextLength);
            var timestamp = ExtractTimestamp(match.Value) ?? ExtractTimestamp(context) ?? DateTime.UtcNow;
            AddIndexEntry(entries, seen, href, rawText, timestamp, forcedKind);
        }

        foreach (Match match in RawSpecialUrlRegex().Matches(html))
        {
            var href = CanonicalContentUrl(AbsoluteUrl(WebUtility.HtmlDecode(match.Groups["url"].Value), baseUrl));
            if (!IsLodestoneContentUrl(href))
                continue;

            var contextStart = Math.Max(0, match.Index - 800);
            var contextLength = Math.Min(html.Length - contextStart, match.Length + 1600);
            var context = html.Substring(contextStart, contextLength);
            AddIndexEntry(entries, seen, href, string.Empty, ExtractTimestamp(context) ?? DateTime.UtcNow, forcedKind);
        }

        return entries;
    }

    private static void AddIndexEntry(List<LodestoneEntry> entries, HashSet<string> seen, string href, string rawText, DateTime timestamp, LodestoneEntryKind? forcedKind)
    {
        var id = StableId(href);
        if (!seen.Add(id))
            return;

        var special = href.Contains("/lodestone/special/", StringComparison.OrdinalIgnoreCase);
        var inferredKind = KindFromTextAndUrl(rawText, href);
        var kind = forcedKind.HasValue && inferredKind == LodestoneEntryKind.Topic
            ? forcedKind.Value
            : inferredKind;
        if (special)
            kind = LodestoneEntryKind.SpecialEvent;

        entries.Add(new LodestoneEntry
        {
            Id = id,
            Title = string.IsNullOrWhiteSpace(rawText) ? TitleFromSpecialUrl(href) : StripDateScript(rawText),
            Url = href,
            Kind = kind,
            StartsAt = timestamp
        });
    }

    private static LodestoneEntry ParseSpecialPage(LodestoneEntry entry, string html, string styles)
    {
        var text = CleanText(html);
        var schedule = SpecialScheduleRegex().Match(text);
        if (schedule.Success)
        {
            entry.StartsAt = ParseSpecialDate(schedule.Groups["start"].Value);
            entry.EndsAt = ParseSpecialDate(schedule.Groups["end"].Value);
        }

        entry.Kind = LodestoneEntryKind.SpecialEvent;
        var pageTitle = ExtractPageTitle(html);
        if (!string.IsNullOrWhiteSpace(pageTitle))
            entry.Title = pageTitle;

        var questTitle = CleanText(FirstMatch(html, "<h2[^>]*content__event-info__quest--title[^>]*>(?<value>.*?)</h2>"));
        var questText = CleanText(FirstMatch(html, "<p[^>]*content__event-info__quest--text[^>]*>(?<value>.*?)</p>"));
        entry.Summary = string.Join("\n", new[] { questTitle, questText }.Where(s => !string.IsNullOrWhiteSpace(s)));

        var mapAlt = WebUtility.HtmlDecode(FirstMatch(html, "<img[^>]*content__event-info__map[^>]*alt=[\"'](?<value>[^\"']+)[\"'][^>]*>"));
        ApplyMapAlt(entry, mapAlt);
        entry.Requirements = RequirementRegex().Matches(text).Select(m => m.Groups["req"].Value.Trim()).Where(s => s.Length > 0).Distinct().ToList();

        var images = TrimAfterStopMarker(ExtractSpecialImages(html, styles)).ToList();
        entry.ImageUrls = images;
        entry.HeroImageUrl = NormalizeHeroImage(SelectSpecialHeroImage(images) ?? string.Empty, entry.Kind);
        if (!entry.ImageUrls.Contains(entry.HeroImageUrl))
            entry.ImageUrls.Insert(0, entry.HeroImageUrl);
        entry.Rewards = ExtractRewards(text, images);
        return entry;
    }

    private static LodestoneEntry ParseNewsPage(LodestoneEntry entry, string html)
    {
        var articleHtml = ExtractNewsArticleHtml(html);
        var text = CleanText(string.IsNullOrWhiteSpace(articleHtml) ? html : articleHtml);
        var times = MaintenanceWindowRegex().Match(text);
        if (times.Success)
        {
            var timezone = times.Groups["tz"].Value;
            entry.StartsAt = ParseMaintenanceDate(times.Groups["start"].Value, timezone);
            entry.EndsAt = ParseMaintenanceDate(times.Groups["end"].Value, timezone);
        }

        var articleSummary = CleanArticleText(articleHtml);
        entry.Summary = Shorten(string.IsNullOrWhiteSpace(articleSummary) ? text : articleSummary, 2000);
        var imageSourceHtml = string.IsNullOrWhiteSpace(articleHtml) ? html : articleHtml;
        var images = TrimAfterStopMarker(ExtractImages(imageSourceHtml))
            .Where(IsContentImage)
            .Select(url => NormalizeHeroImage(url, entry.Kind))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
        foreach (var existingImage in entry.ImageUrls)
        {
            var normalizedExistingImage = NormalizeHeroImage(existingImage, entry.Kind);
            if (!images.Contains(normalizedExistingImage))
                images.Insert(0, normalizedExistingImage);
        }

        entry.ImageUrls = images;
        entry.HeroImageUrl = SelectNewsHeroImage(entry, images);
        if (IsProducerLiveEntry(entry))
            entry.HeroImageUrl = ProducerLiveHeroImage;
        else if (IsEternalBondingRestrictionEntry(entry))
            entry.HeroImageUrl = EternalBondingRestrictedHeroImage;

        if (!entry.ImageUrls.Contains(entry.HeroImageUrl))
            entry.ImageUrls.Insert(0, entry.HeroImageUrl);
        return entry;
    }

    private static List<LodestoneReward> ExtractRewards(string text, IReadOnlyList<string> images)
    {
        var kinds = new[] { "Minion", "Fashion Accessory", "Furnishings", "Emote", "Orchestrion Roll" };
        var rewards = new List<LodestoneReward>();
        var itemImages = images.Where(i => i.Contains("/itemicon/", StringComparison.OrdinalIgnoreCase)).ToList();

        for (var i = 0; i < kinds.Length; i++)
        {
            if (!text.Contains(kinds[i], StringComparison.OrdinalIgnoreCase))
                continue;

            rewards.Add(new LodestoneReward
            {
                Kind = kinds[i],
                ImageUrl = itemImages.ElementAtOrDefault(rewards.Count) ?? string.Empty
            });
        }

        return rewards;
    }

    private static IEnumerable<string> ExtractImages(string html)
    {
        return ImageRegex().Matches(html)
            .Select(m => WebUtility.HtmlDecode(m.Groups["url"].Value))
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
            .Distinct();
    }

    private static string ExtractNewsArticleHtml(string html)
    {
        var wrapper = FirstMatch(html, "<div[^>]+class=[\"'][^\"']*news__detail__wrapper[^\"']*[\"'][^>]*>(?<value>.*?)</div>\\s*<div[^>]+class=[\"'][^\"']*news__detail__social");
        if (!string.IsNullOrWhiteSpace(wrapper))
            return wrapper;

        return FirstMatch(html, "<article[^>]+class=[\"'][^\"']*news__detail[^\"']*[\"'][^>]*>(?<value>.*?)</article>");
    }

    private static string CleanArticleText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = ScriptRegex().Replace(html, " ");
        text = Regex.Replace(text, "<style.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<img[^>]*>", "\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<h[1-6][^>]*>", "\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "</h[1-6]>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<li[^>]*>", "\n- ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "</li>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</p>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</div>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<a[^>]*>(?<value>.*?)</a>", m => CleanText(m.Groups["value"].Value), RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);

        var lines = text
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => WhitespaceRegex().Replace(line, " ").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line))
            .ToArray();

        return string.Join("\n", lines);
    }

    private static IEnumerable<string> ExtractSpecialImages(string html, string styles)
    {
        return ExtractSpecialHeroCandidates(styles)
            .Concat(ExtractImages(html).Where(IsContentImage))
            .Concat(ExtractMetaImages(html))
            .Concat(ExtractCssImages(styles))
            .Where(IsContentImage)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> TrimAfterStopMarker(IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            yield return url;
            if (url.Equals(SpecialImageStopMarker, StringComparison.OrdinalIgnoreCase))
                yield break;
        }
    }

    private static IEnumerable<string> ExtractSpecialHeroCandidates(string styles)
    {
        foreach (Match rule in SpecialHeroRuleRegex().Matches(styles))
        {
            foreach (var image in ExtractCssImages(rule.Groups["body"].Value))
                yield return image;
        }
    }

    private static IEnumerable<string> ExtractMetaImages(string html)
    {
        return MetaImageRegex().Matches(html)
            .Select(m => WebUtility.HtmlDecode(m.Groups["url"].Value))
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _));
    }

    private static IEnumerable<string> ExtractCssImages(string styles)
    {
        foreach (Match match in CssImageRegex().Matches(styles))
        {
            var image = WebUtility.HtmlDecode(match.Groups["url"].Value).Trim();
            if (image.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
                continue;

            if (image.StartsWith("//", StringComparison.Ordinal))
                image = $"https:{image}";
            else if (image.StartsWith("/", StringComparison.Ordinal))
                image = $"https://lds-img.finalfantasyxiv.com{image}";

            if (Uri.TryCreate(image, UriKind.Absolute, out _))
                yield return image;
        }
    }

    private static string? SelectSpecialHeroImage(IReadOnlyList<string> images)
    {
        return images
            .OrderByDescending(SpecialImageScore)
            .FirstOrDefault();
    }

    private static int SpecialImageScore(string url)
    {
        var score = 0;
        if (url.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || url.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase))
            score += 40;
        if (url.Contains("/itemicon/", StringComparison.OrdinalIgnoreCase))
            score -= 100;
        if (url.Contains("U2uGfVX4GdZgU1jASO0m9h_xLg", StringComparison.OrdinalIgnoreCase))
            score -= 80;
        if (url.Contains("/pc/", StringComparison.OrdinalIgnoreCase))
            score -= 50;
        if (IsDecorativeLodestoneImage(url))
            score -= 100;

        return score;
    }

    private static string NormalizeHeroImage(string url, LodestoneEntryKind kind)
        => IsDecorativeLodestoneImage(url) ? DefaultHeroImage(kind) : url;

    private static string SelectNewsHeroImage(LodestoneEntry entry, IReadOnlyList<string> images)
    {
        var fallback = DefaultHeroImage(entry.Kind);
        if (UsesMaintenanceFallback(entry.Kind))
            return fallback;

        var hero = images
            .Concat(new[] { entry.HeroImageUrl })
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Select(url => NormalizeHeroImage(url, entry.Kind))
            .FirstOrDefault(url => !string.IsNullOrWhiteSpace(url) && url != fallback);

        return hero ?? fallback;
    }

    private static string DefaultHeroImage(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.Maintenance => DefaultMaintenanceHeroImage,
        LodestoneEntryKind.Status => DefaultMaintenanceHeroImage,
        LodestoneEntryKind.Recovery => DefaultMaintenanceHeroImage,
        _ => DefaultNewsHeroImage
    };

    private static bool IsProducerLiveEntry(LodestoneEntry entry)
        => entry.Title.Contains("Letter from the Producer", StringComparison.OrdinalIgnoreCase)
           || entry.Title.Contains("Producer LIVE", StringComparison.OrdinalIgnoreCase);

    private static bool IsEternalBondingRestrictionEntry(LodestoneEntry entry)
    {
        var text = $"{entry.Title} {entry.Summary}";
        return text.Contains("Eternal Bonding", StringComparison.OrdinalIgnoreCase)
               && (text.Contains("Reservation", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("Restriction", StringComparison.OrdinalIgnoreCase)
                   || text.Contains("Restricted", StringComparison.OrdinalIgnoreCase));
    }

    private static bool UsesMaintenanceFallback(LodestoneEntryKind kind)
    {
        return kind is LodestoneEntryKind.Maintenance or LodestoneEntryKind.Status or LodestoneEntryKind.Recovery;
    }

    private static bool IsDecorativeLodestoneImage(string url)
    {
        return url.Contains("1LbK-2Cqoku3zorQFR0VQ6jP0Y", StringComparison.OrdinalIgnoreCase)
               || url.Contains("6PLTZ82M99GJ7tKOee1RSwvNrQ", StringComparison.OrdinalIgnoreCase)
               || url.Contains("U2uGfVX4GdZgU1jASO0m9h_xLg", StringComparison.OrdinalIgnoreCase)
               || url.Contains("/pc/global/", StringComparison.OrdinalIgnoreCase)
               || url.Contains("favicon", StringComparison.OrdinalIgnoreCase);
    }

    private static void ApplyMapAlt(LodestoneEntry entry, string mapAlt)
    {
        if (string.IsNullOrWhiteSpace(mapAlt))
            return;

        var coord = Regex.Match(mapAlt, @"(?<loc>.+?\s+X:\d+(?:\.\d+)?\s+Y:\d+(?:\.\d+)?)(?:\s+(?<npc>.+))?$");
        if (!coord.Success)
        {
            entry.StartingLocation = mapAlt.Trim();
            return;
        }

        entry.StartingLocation = coord.Groups["loc"].Value.Trim();
        entry.StartingNpc = coord.Groups["npc"].Value.Trim();
    }

    private static bool IsContentImage(string url)
    {
        return (url.Contains("lds-img.finalfantasyxiv.com", StringComparison.OrdinalIgnoreCase)
                || url.Contains("img.finalfantasyxiv.com/t/", StringComparison.OrdinalIgnoreCase))
               && !url.EndsWith(".css", StringComparison.OrdinalIgnoreCase)
               && !url.EndsWith(".js", StringComparison.OrdinalIgnoreCase)
               && !url.Contains("/pc/global/", StringComparison.OrdinalIgnoreCase)
               && !url.Contains("favicon", StringComparison.OrdinalIgnoreCase)
               && !IsDecorativeLodestoneImage(url);
    }

    private static bool IsLodestoneStaticCss(string url)
    {
        return url.Contains("lds-img.finalfantasyxiv.com", StringComparison.OrdinalIgnoreCase)
               && url.EndsWith(".css", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ShouldInclude(LodestoneEntryKind kind, Configuration config) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => config.ShowEvents,
        LodestoneEntryKind.Topic => config.ShowTopics,
        LodestoneEntryKind.Notice => config.ShowNotices,
        LodestoneEntryKind.Maintenance => config.ShowMaintenance,
        LodestoneEntryKind.Update => config.ShowUpdates,
        LodestoneEntryKind.Status => config.ShowStatus,
        LodestoneEntryKind.Recovery => config.ShowRecovery,
        _ => true
    };

    private static LodestoneEntryKind KindFromTextAndUrl(string title, string url)
    {
        if (url.Contains("/lodestone/special/", StringComparison.OrdinalIgnoreCase))
            return LodestoneEntryKind.SpecialEvent;
        if (title.Contains("[Maintenance]", StringComparison.OrdinalIgnoreCase) || url.Contains("/news/detail/", StringComparison.OrdinalIgnoreCase) && title.Contains("Maintenance", StringComparison.OrdinalIgnoreCase))
            return LodestoneEntryKind.Maintenance;
        if (title.Contains("[Recovery]", StringComparison.OrdinalIgnoreCase))
            return LodestoneEntryKind.Recovery;
        if (title.Contains("[Important]", StringComparison.OrdinalIgnoreCase))
            return LodestoneEntryKind.Notice;
        if (title.Contains("Updated", StringComparison.OrdinalIgnoreCase))
            return LodestoneEntryKind.Update;
        return LodestoneEntryKind.Topic;
    }

    private static DateTime? ExtractTimestamp(string html)
    {
        var match = TimestampRegex().Match(html);
        if (!match.Success || !long.TryParse(match.Groups["ts"].Value, out var timestamp))
            return null;

        return DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime;
    }

    private static DateTime ParseSpecialDate(string value)
    {
        var normalized = NormalizeMeridiem(value.Replace(" at ", " ", StringComparison.OrdinalIgnoreCase)).Trim();
        var formats = new[] { "dddd, MMMM d, yyyy h:mm tt", "MMMM d, yyyy h:mm tt" };
        foreach (var format in formats)
        {
            if (DateTime.TryParseExact(normalized, format, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed))
                return parsed;
        }

        return DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var fallback)
            ? fallback
            : DateTime.Now;
    }

    private static DateTime ParseLodestoneMonthDate(string value, int year)
    {
        var normalized = value.Replace(".", string.Empty).Trim();
        return DateTime.TryParse($"{normalized} {year}", CultureInfo.InvariantCulture, DateTimeStyles.AssumeLocal, out var parsed)
            ? parsed
            : DateTime.Now;
    }

    private static DateTime ParseMaintenanceDate(string value, string timezone)
    {
        var normalized = NormalizeMeridiem(value)
            .Replace("Jan.", "Jan", StringComparison.OrdinalIgnoreCase)
            .Replace("Feb.", "Feb", StringComparison.OrdinalIgnoreCase)
            .Replace("Mar.", "Mar", StringComparison.OrdinalIgnoreCase)
            .Replace("Apr.", "Apr", StringComparison.OrdinalIgnoreCase)
            .Replace("Jun.", "Jun", StringComparison.OrdinalIgnoreCase)
            .Replace("Jul.", "Jul", StringComparison.OrdinalIgnoreCase)
            .Replace("Aug.", "Aug", StringComparison.OrdinalIgnoreCase)
            .Replace("Sep.", "Sep", StringComparison.OrdinalIgnoreCase)
            .Replace("Oct.", "Oct", StringComparison.OrdinalIgnoreCase)
            .Replace("Nov.", "Nov", StringComparison.OrdinalIgnoreCase)
            .Replace("Dec.", "Dec", StringComparison.OrdinalIgnoreCase)
            .Trim();

        if (!DateTime.TryParseExact(normalized, "MMM d, yyyy h:mm tt", CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return DateTime.Now;

        return ConvertSourceTimeToLocal(parsed, timezone);
    }

    private static DateTime ConvertSourceTimeToLocal(DateTime sourceTime, string timezone)
    {
        var sourceZone = ResolveTimeZone(timezone);
        if (sourceZone == null)
            return DateTime.SpecifyKind(sourceTime, DateTimeKind.Local);

        try
        {
            return TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(sourceTime, DateTimeKind.Unspecified), sourceZone).ToLocalTime();
        }
        catch
        {
            return DateTime.SpecifyKind(sourceTime, DateTimeKind.Local);
        }
    }

    private static TimeZoneInfo? ResolveTimeZone(string timezone)
    {
        var normalized = timezone.Trim();
        if (normalized.Contains("PDT", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("PST", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Pacific", StringComparison.OrdinalIgnoreCase))
            return FindTimeZone("Pacific Standard Time", "America/Los_Angeles");

        if (normalized.Contains("UTC", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("GMT", StringComparison.OrdinalIgnoreCase))
            return TimeZoneInfo.Utc;

        return null;
    }

    private static TimeZoneInfo? FindTimeZone(params string[] ids)
    {
        foreach (var id in ids)
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch
            {
            }
        }

        return null;
    }

    private static string NormalizeMeridiem(string value)
        => value.Replace("a.m.", "AM", StringComparison.OrdinalIgnoreCase)
            .Replace("p.m.", "PM", StringComparison.OrdinalIgnoreCase)
            .Replace("a.m", "AM", StringComparison.OrdinalIgnoreCase)
            .Replace("p.m", "PM", StringComparison.OrdinalIgnoreCase);

    private static string CleanText(string html)
    {
        var noScripts = ScriptRegex().Replace(html, " ");
        var noTags = TagRegex().Replace(noScripts, " ");
        return WebUtility.HtmlDecode(WhitespaceRegex().Replace(noTags, " ")).Trim();
    }

    private static string StripDateScript(string text)
    {
        var idx = text.IndexOf("document.getElementById", StringComparison.OrdinalIgnoreCase);
        return (idx >= 0 ? text[..idx] : text).Trim(' ', '-', '\t', '\r', '\n');
    }

    private static string ExtractBetween(string text, string start, string end)
    {
        var startIndex = text.IndexOf(start, StringComparison.OrdinalIgnoreCase);
        if (startIndex < 0)
            startIndex = 0;
        var endIndex = text.IndexOf(end, startIndex, StringComparison.OrdinalIgnoreCase);
        return endIndex > startIndex ? text[startIndex..endIndex] : Shorten(text, 500);
    }

    private static string FirstMatch(string text, string pattern)
    {
        var match = Regex.Match(text, pattern, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success ? match.Groups["value"].Value.Trim() : string.Empty;
    }

    private static string Shorten(string text, int max) => text.Length <= max ? text : text[..max].Trim() + "...";
    private static string PageUrl(string sourceUrl, int page)
    {
        if (page <= 1)
            return sourceUrl;

        return sourceUrl.Contains('?', StringComparison.Ordinal)
            ? $"{sourceUrl}&page={page}"
            : $"{sourceUrl.TrimEnd('/')}/?page={page}";
    }

    private static bool HasNextPage(string html, int currentPage)
        => html.Contains($"page={currentPage + 1}", StringComparison.OrdinalIgnoreCase);

    private static string RegionBaseUrl(Configuration configuration)
    {
        var region = configuration.Region.ToLowerInvariant() switch
        {
            "eu" => "eu",
            "fr" => "fr",
            "de" => "de",
            "jp" => "jp",
            _ => "na"
        };

        return $"https://{region}.finalfantasyxiv.com/";
    }

    private static string AbsoluteUrl(string href, string baseUrl = DefaultHomeUrl) => href.StartsWith("http", StringComparison.OrdinalIgnoreCase) ? href : new Uri(new Uri(baseUrl), href).ToString();
    private static string CanonicalContentUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return url;

        if (!uri.Host.Equals("na.finalfantasyxiv.com", StringComparison.OrdinalIgnoreCase))
            return url;

        var builder = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty };
        return builder.Uri.ToString().TrimEnd('/');
    }

    private static bool IsLodestoneContentUrl(string url) => url.Contains("/lodestone/news/detail/", StringComparison.OrdinalIgnoreCase) || url.Contains("/lodestone/topics/detail/", StringComparison.OrdinalIgnoreCase) || SpecialEventUrlRegex().IsMatch(url);
    private static string StableId(string url) => Convert.ToHexString(System.Security.Cryptography.SHA1.HashData(System.Text.Encoding.UTF8.GetBytes(url)));

    private static string ExtractPageTitle(string html)
    {
        var og = FirstMatch(html, "<meta[^>]+property=[\"']og:title[\"'][^>]+content=[\"'](?<value>[^\"']+)[\"'][^>]*>");
        var title = string.IsNullOrWhiteSpace(og)
            ? FirstMatch(html, "<title>(?<value>.*?)</title>")
            : og;

        return WebUtility.HtmlDecode(title)
            .Replace(" | FINAL FANTASY XIV, The Lodestone", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    private static string TitleFromSpecialUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return "Special Event";

        var parts = uri.AbsolutePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var slug = parts.Reverse().FirstOrDefault(p => !p.All(char.IsDigit) && p.Length > 8) ?? "special event";
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(slug.Replace('_', ' ').Replace('-', ' '));
    }

    public void Dispose() => httpClient.Dispose();

    [GeneratedRegex("<a[^>]+href=[\"'](?<href>[^\"']+)[\"'][^>]*>(?<text>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IndexLinkRegex();
    [GeneratedRegex("<img[^>]+src=[\"'](?<url>https://[^\"']+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex ImageRegex();
    [GeneratedRegex("<meta[^>]+(?:property|name)=[\"'](?:og:image|twitter:image)[\"'][^>]+content=[\"'](?<url>https://[^\"']+)[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex MetaImageRegex();
    [GeneratedRegex("<link[^>]+href=[\"'](?<url>[^\"']+\\.css)[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex StylesheetRegex();
    [GeneratedRegex("url\\([\"']?(?<url>[^\\)\"']+)[\"']?\\)", RegexOptions.IgnoreCase)]
    private static partial Regex CssImageRegex();
    [GeneratedRegex("[^{]*(?:main-art|mainvisual|main-visual|keyvisual|hero)[^{]*\\{(?<body>[^{}]+)\\}", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialHeroRuleRegex();
    [GeneratedRegex("ldst_strftime\\((?<ts>\\d+),", RegexOptions.IgnoreCase)]
    private static partial Regex TimestampRegex();
    [GeneratedRegex("(?:From\\s+)?(?<start>(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),\\s+[A-Z][a-z]+\\s+\\d{1,2},\\s+\\d{4}\\s+at\\s+\\d{1,2}:\\d{2}\\s+[ap]\\.m\\.)\\s+to\\s+(?<end>(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),\\s+[A-Z][a-z]+\\s+\\d{1,2},\\s+\\d{4}\\s+at\\s+\\d{1,2}:\\d{2}\\s+[ap]\\.m\\.)", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialScheduleRegex();
    [GeneratedRegex("Level\\s+(?<req>\\d+)|Players must first complete the quest\\s+\"(?<req>[^\"]+)\"", RegexOptions.IgnoreCase)]
    private static partial Regex RequirementRegex();
    [GeneratedRegex("(?<start>[A-Z][a-z]{2}\\.\\s+\\d{1,2},\\s+\\d{4}\\s+\\d{1,2}:\\d{2}\\s+[ap]\\.m\\.)\\s+to\\s+(?<end>[A-Z][a-z]{2}\\.\\s+\\d{1,2},\\s+\\d{4}\\s+\\d{1,2}:\\d{2}\\s+[ap]\\.m\\.)\\s+\\((?<tz>[^)]+)\\)", RegexOptions.IgnoreCase)]
    private static partial Regex MaintenanceWindowRegex();
    [GeneratedRegex("<script.*?</script>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex ScriptRegex();
    [GeneratedRegex("<[^>]+>", RegexOptions.Singleline)]
    private static partial Regex TagRegex();
    [GeneratedRegex("\\s+")]
    private static partial Regex WhitespaceRegex();
    [GeneratedRegex("<entry>(?<entry>.*?)</entry>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FeedEntryRegex();
    [GeneratedRegex("<link\\s+rel=[\"']alternate[\"'][^>]*href=[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FeedAlternateLinkRegex();
    [GeneratedRegex("<link\\s+rel=[\"']enclosure[\"'][^>]*href=[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex FeedImageLinkRegex();
    [GeneratedRegex("/lodestone/special/20\\d{2}/", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialEventUrlRegex();
    [GeneratedRegex("(?<url>(?:https://[^\"'<>\\s]+)?/lodestone/special/20\\d{2}/[^\"'<>\\s?]+/[^\"'<>\\s?]+)(?:\\?[^\"'<>\\s]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex RawSpecialUrlRegex();
}
