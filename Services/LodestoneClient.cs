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
    private const string IcyVeinsUrl = "https://www.icy-veins.com/ffxiv/";
    private const string OfficialBlogUrl = "https://na.finalfantasyxiv.com/blog/";
    private const int CurrentArticleFormatVersion = 3;
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };
    private readonly HttpClient httpClient = new();
    private readonly FileInfo cacheFile;

    public LodestoneScanDiagnostics LastDiagnostics { get; private set; } = new();
    public ScanProgress CurrentProgress { get; private set; } = ScanProgress.Idle;

    public LodestoneClient(DirectoryInfo configDirectory)
    {
        cacheFile = new FileInfo(Path.Combine(configDirectory.FullName, "lodestone-cache-v6.json"));
        httpClient.Timeout = TimeSpan.FromSeconds(30);
        httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LodestoneDalamudPlugin/0.1");
    }

    public async Task<IReadOnlyList<LodestoneEntry>> LoadCachedAsync(Configuration? configuration = null)
    {
        try
        {
            if (!cacheFile.Exists)
                return [];

            List<LodestoneEntry> entries;
            await using (var stream = cacheFile.OpenRead())
                entries = await JsonSerializer.DeserializeAsync<List<LodestoneEntry>>(stream, JsonOptions) ?? [];

            if (configuration == null)
                return entries;

            var pruned = ApplyCacheRetention(entries, configuration, out var removed);
            if (removed > 0)
                await SaveCacheAsync(pruned);
            return pruned;
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
        var diagnostics = new LodestoneScanDiagnostics
        {
            StartedAtUtc = DateTime.UtcNow,
            Force = force,
            Status = force ? "Forced refresh started." : "Refresh started."
        };
        SetProgress("Refresh", "Cache", 0, 4, diagnostics.Status);

        var cachedEntries = (await LoadCachedAsync()).ToList();
        diagnostics.CachedEntries = cachedEntries.Count;
        var retainedCachedEntries = ApplyCacheRetention(cachedEntries, configuration, out var prunedFromCache);
        diagnostics.PrunedCacheEntries = prunedFromCache;
        if (prunedFromCache > 0)
            await SaveCacheAsync(retainedCachedEntries);

        var cached = retainedCachedEntries.ToDictionary(e => e.Id, e => e);
        if (!force && cached.Count > 0)
        {
            var newestFetch = cached.Values.Max(e => e.FetchedAt);
            if (DateTime.UtcNow - newestFetch < TimeSpan.FromMinutes(Math.Max(15, configuration.RefreshMinutes)) && CacheFormatReady(cached.Values, configuration))
            {
                diagnostics.UsedFreshCache = true;
                diagnostics.Status = $"Used fresh cache with {cached.Count} entries.";
                CompleteDiagnostics(diagnostics);
                return cached.Values.OrderBy(e => e.StartsAt).ToArray();
            }
        }

        SetProgress("Refresh", "Feeds", 1, 4, "Fetching Lodestone feed images.");
        var feedImages = await FetchFeedImagesAsync(configuration, diagnostics);
        SetProgress("Refresh", "Indexes", 2, 4, "Fetching source indexes.");
        var indexEntries = await FetchIndexEntriesAsync(configuration, diagnostics);
        var externalEntries = await FetchExternalEntriesAsync(configuration, diagnostics);
        indexEntries.AddRange(externalEntries);
        diagnostics.IndexEntries = indexEntries.Count;
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
            .Where(e => ShouldInclude(e.Kind, configuration) || ShouldProbeTopicForEvents(e, configuration))
            .GroupBy(e => e.Id)
            .Select(g => g.OrderByDescending(e => e.StartsAt).First())
            .OrderByDescending(e => e.StartsAt)
            .Take(Math.Clamp(configuration.MaxEntriesToScan, 5, 500))
            .ToList();
        diagnostics.FilteredEntries = stubs.Count;

        var results = new List<LodestoneEntry>();
        foreach (var stub in stubs)
        {
            SetProgress("Refresh", SourceLabel(stub), results.Count, stubs.Count, $"Loading {stub.Title}");
            LodestoneEntry entry;
            if (cached.TryGetValue(stub.Id, out var existing) && !force && CanUseCachedEntry(stub, existing))
            {
                diagnostics.CacheHits++;
                entry = existing;
            }
            else
            {
                entry = await EnrichEntryAsync(stub, diagnostics);
                if (!IsExternalEntry(stub))
                    await Task.Delay(250);
            }

            if (ShouldInclude(entry.Kind, configuration))
                results.Add(entry);
        }

        SetProgress("Refresh", "Cache", stubs.Count, stubs.Count, "Saving refreshed calendar data.");
        var retainedResults = ApplyCacheRetention(results, configuration, out var prunedFromResults);
        diagnostics.PrunedCacheEntries += prunedFromResults;
        await SaveCacheAsync(retainedResults);
        diagnostics.Status = $"Refresh complete. {retainedResults.Count} entries available.";
        CompleteDiagnostics(diagnostics);
        return retainedResults.OrderBy(e => e.StartsAt).ToArray();
    }

    private async Task<List<LodestoneEntry>> FetchIndexEntriesAsync(Configuration configuration, LodestoneScanDiagnostics diagnostics)
    {
        var entries = new List<LodestoneEntry>();
        var maxPages = Math.Clamp(configuration.MaxPagesPerSource, 1, 20);
        var sources = BuildSources(configuration).ToArray();
        diagnostics.SourceCount = sources.Length;

        foreach (var source in sources)
        {
            SetProgress("Refresh", SourceLabel(source.Kind), entries.Count, 0, $"Scanning {source.Url}");
            var beforeSource = entries.Count;
            var pagesForSource = 0;
            for (var page = 1; page <= maxPages; page++)
            {
                var url = PageUrl(source.Url, page);
                string html;
                try
                {
                    html = await httpClient.GetStringAsync(url);
                    diagnostics.PagesFetched++;
                    pagesForSource++;
                }
                catch (Exception ex)
                {
                    diagnostics.Errors++;
                    diagnostics.LastError = $"Failed index: {url}";
                    Plugin.Log.Warning(ex, "Failed to fetch Lodestone index {Url}", url);
                    break;
                }

                var pageEntries = ParseIndex(html, source.Kind, source.Url).ToArray();
                entries.AddRange(pageEntries);

                if (pageEntries.Length == 0 || !HasNextPage(html, page))
                    break;

                await Task.Delay(150);
            }

            diagnostics.SourceSummaries.Add($"{source.Url} - {entries.Count - beforeSource} entries across {pagesForSource} page{(pagesForSource == 1 ? string.Empty : "s")}");
        }

        return entries;
    }

    private async Task<List<LodestoneEntry>> FetchExternalEntriesAsync(Configuration configuration, LodestoneScanDiagnostics diagnostics)
    {
        var entries = new List<LodestoneEntry>();
        var sourceCount = (configuration.ShowDeveloperPosts ? 1 : 0)
                          + (configuration.ShowIcyVeins ? 1 : 0)
                          + (configuration.ShowIcyVeins && configuration.ShowIcyVeinsGuides ? 1 : 0);
        var sourceIndex = 0;

        if (configuration.ShowDeveloperPosts)
        {
            SetProgress("Refresh", "Developer Posts", sourceIndex++, sourceCount, "Fetching official developer posts.");
            var developerPosts = await FetchDeveloperPostEntriesAsync(configuration, diagnostics);
            entries.AddRange(developerPosts);
            diagnostics.DeveloperPostEntries = developerPosts.Count;
        }

        if (configuration.ShowIcyVeins)
        {
            SetProgress("Refresh", "Icy Veins", sourceIndex++, sourceCount, "Fetching Icy Veins FFXIV articles.");
            var icyVeins = await FetchIcyVeinsEntriesAsync(diagnostics);
            entries.AddRange(icyVeins);
            diagnostics.IcyVeinsEntries = icyVeins.Count;
        }

        if (configuration.ShowIcyVeins && configuration.ShowIcyVeinsGuides)
        {
            SetProgress("Refresh", "Icy Veins Guides", sourceIndex, sourceCount, "Fetching Icy Veins FFXIV guides.");
            var icyVeinsGuides = await FetchIcyVeinsGuideEntriesAsync(diagnostics);
            entries.AddRange(icyVeinsGuides);
            diagnostics.IcyVeinsGuideEntries = icyVeinsGuides.Count;
        }

        return entries;
    }

    private async Task<List<LodestoneEntry>> FetchDeveloperPostEntriesAsync(Configuration configuration, LodestoneScanDiagnostics diagnostics)
    {
        var entries = new List<LodestoneEntry>();

        try
        {
            var html = await httpClient.GetStringAsync(OfficialBlogUrl);
            entries.AddRange(ParseOfficialBlogIndex(html, OfficialBlogUrl).Take(20));

            diagnostics.SourceCount++;
            diagnostics.SourceSummaries.Add($"{OfficialBlogUrl} - {entries.Count} official blog posts");
        }
        catch (Exception ex)
        {
            diagnostics.Errors++;
            diagnostics.LastError = "Failed developer posts.";
            Plugin.Log.Warning(ex, "Failed to fetch official FFXIV blog posts.");
        }

        return entries;
    }

    private static IEnumerable<LodestoneEntry> ParseOfficialBlogIndex(string html, string baseUrl)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in OfficialBlogCardRegex().Matches(html))
        {
            var card = match.Groups["card"].Value;
            var postUrl = AbsoluteUrl(WebUtility.HtmlDecode(FirstMatch(card, "<a[^>]+href=[\"'](?<value>[^\"']+)[\"'][^>]*>")), baseUrl);
            if (string.IsNullOrWhiteSpace(postUrl) || postUrl.Contains("<%", StringComparison.Ordinal) || !seen.Add(postUrl))
                continue;

            var title = CleanText(FirstMatch(card, "<div[^>]+class=[\"'][^\"']*blog-entry__title[^\"']*[\"'][^>]*>\\s*<p>(?<value>.*?)</p>"));
            if (string.IsNullOrWhiteSpace(title))
                continue;

            var publishText = WebUtility.HtmlDecode(FirstMatch(card, "data-publish_dt=[\"'](?<value>[^\"']+)[\"']"));
            var startsAt = DateTimeOffset.TryParse(publishText, CultureInfo.InvariantCulture, DateTimeStyles.None, out var publishDate)
                ? publishDate.LocalDateTime
                : DateTime.Now;

            var category = CleanText(FirstMatch(card, "<div[^>]+class=[\"'][^\"']*blog-entry__status[^\"']*[\"'][^>]*>\\s*<p>(?<value>.*?)</p>"));
            var hero = WebUtility.HtmlDecode(FirstMatch(card, "data-thumbnail_big=[\"'](?<value>[^\"']+)[\"']"));
            if (string.IsNullOrWhiteSpace(hero))
                hero = FirstMatch(card, "background:url\\([\"'](?<value>[^\"']+)[\"']\\)");
            hero = NormalizeExternalImageUrl(hero, baseUrl);
            if (string.IsNullOrWhiteSpace(hero))
                hero = DefaultNewsHeroImage;

            yield return new LodestoneEntry
            {
                Id = $"dev:{StableId(postUrl)}",
                Title = WebUtility.HtmlDecode(title),
                Url = postUrl,
                Kind = LodestoneEntryKind.DeveloperPost,
                SourceName = "FFXIV Official Blog",
                StartsAt = startsAt,
                SourceTimeText = string.IsNullOrWhiteSpace(publishText) ? string.Empty : publishText,
                Summary = string.IsNullOrWhiteSpace(category) ? "Official FFXIV blog post." : category,
                HeroImageUrl = hero,
                ImageUrls = [hero],
                FetchedAt = DateTime.UtcNow
            };
        }
    }

    private async Task<List<LodestoneEntry>> FetchIcyVeinsEntriesAsync(LodestoneScanDiagnostics diagnostics)
    {
        var entries = new List<LodestoneEntry>();

        try
        {
            var html = await httpClient.GetStringAsync(IcyVeinsUrl);
            foreach (Match match in IcyVeinsCardRegex().Matches(html).Take(20))
            {
                var card = match.Groups["card"].Value;
                var titleMatch = IcyVeinsTitleRegex().Match(card);
                var timeMatch = IcyVeinsTimeRegex().Match(card);
                if (!titleMatch.Success || !timeMatch.Success)
                    continue;

                var url = WebUtility.HtmlDecode(titleMatch.Groups["url"].Value);
                var title = CleanText(titleMatch.Groups["title"].Value);
                if (string.IsNullOrWhiteSpace(url) || string.IsNullOrWhiteSpace(title) || !long.TryParse(timeMatch.Groups["ts"].Value, out var unixTime))
                    continue;

                var image = IcyVeinsImageRegex().Match(card);
                var summary = CleanText(IcyVeinsSummaryRegex().Match(card).Groups["summary"].Value);
                var author = CleanText(IcyVeinsAuthorRegex().Match(card).Groups["author"].Value);
                var hero = image.Success ? NormalizeExternalImageUrl(WebUtility.HtmlDecode(image.Groups["url"].Value), IcyVeinsUrl) : DefaultNewsHeroImage;

                entries.Add(new LodestoneEntry
                {
                    Id = $"icy:{StableId(url)}",
                    Title = title,
                    Url = url,
                    Kind = LodestoneEntryKind.IcyVeins,
                    SourceName = "Icy Veins",
                    Author = author,
                    StartsAt = DateTimeOffset.FromUnixTimeSeconds(unixTime).LocalDateTime,
                    Summary = string.IsNullOrWhiteSpace(author) ? summary : $"{summary}\n\nBy {author}",
                    HeroImageUrl = string.IsNullOrWhiteSpace(hero) ? DefaultNewsHeroImage : hero,
                    ImageUrls = string.IsNullOrWhiteSpace(hero) ? [DefaultNewsHeroImage] : [hero],
                    FetchedAt = DateTime.UtcNow
                });
            }

            diagnostics.SourceCount++;
            diagnostics.SourceSummaries.Add($"{IcyVeinsUrl} - {entries.Count} Icy Veins articles");
        }
        catch (Exception ex)
        {
            diagnostics.Errors++;
            diagnostics.LastError = "Failed Icy Veins.";
            Plugin.Log.Warning(ex, "Failed to fetch Icy Veins FFXIV articles.");
        }

        return entries;
    }

    private async Task<List<LodestoneEntry>> FetchIcyVeinsGuideEntriesAsync(LodestoneScanDiagnostics diagnostics)
    {
        var entries = new List<LodestoneEntry>();

        try
        {
            var html = await httpClient.GetStringAsync(IcyVeinsUrl);
            var guideLinks = ExtractIcyVeinsGuideLinks(html).Take(12).ToArray();
            for (var i = 0; i < guideLinks.Length; i++)
            {
                var guide = guideLinks[i];
                SetProgress("Refresh", "Icy Veins Guides", i, guideLinks.Length, $"Loading {guide.Title}");
                try
                {
                    var guideHtml = await httpClient.GetStringAsync(guide.Url);
                    var entry = ParseIcyVeinsGuidePage(
                        new LodestoneEntry
                        {
                            Id = $"icy-guide:{StableId(guide.Url)}",
                            Title = guide.Title,
                            Url = guide.Url,
                            Kind = LodestoneEntryKind.IcyVeinsGuide,
                            SourceName = "Icy Veins Guide",
                            StartsAt = DateTime.Now,
                            HeroImageUrl = DefaultNewsHeroImage,
                            ImageUrls = [DefaultNewsHeroImage],
                            FetchedAt = DateTime.UtcNow
                        },
                        guideHtml);

                    entries.Add(entry);
                }
                catch (Exception ex)
                {
                    diagnostics.Errors++;
                    diagnostics.LastError = $"Failed Icy Veins guide: {guide.Title}";
                    Plugin.Log.Warning(ex, "Failed to fetch Icy Veins guide {GuideUrl}", guide.Url);
                }

                await Task.Delay(125);
            }

            diagnostics.SourceCount++;
            diagnostics.SourceSummaries.Add($"{IcyVeinsUrl} - {entries.Count} Icy Veins guides");
        }
        catch (Exception ex)
        {
            diagnostics.Errors++;
            diagnostics.LastError = "Failed Icy Veins guides.";
            Plugin.Log.Warning(ex, "Failed to fetch Icy Veins FFXIV guide links.");
        }

        return entries;
    }

    private async Task<Dictionary<string, string>> FetchFeedImagesAsync(Configuration configuration, LodestoneScanDiagnostics diagnostics)
    {
        var images = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var baseUrl = RegionBaseUrl(configuration);
        var feedUrls = new[]
        {
            $"{baseUrl}lodestone/news/news.xml",
            $"{baseUrl}lodestone/news/topics.xml"
        };
        for (var i = 0; i < feedUrls.Length; i++)
        {
            var feedUrl = feedUrls[i];
            SetProgress("Refresh", "Feeds", i, feedUrls.Length, $"Fetching feed image map {i + 1}/{feedUrls.Length}.");
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
                diagnostics.Errors++;
                diagnostics.LastError = $"Failed feed: {feedUrl}";
                Plugin.Log.Warning(ex, "Failed to fetch Lodestone feed {FeedUrl}", feedUrl);
            }
        }

        diagnostics.FeedImages = images.Count;
        return images;
    }

    private async Task SaveCacheAsync(IReadOnlyCollection<LodestoneEntry> entries)
    {
        cacheFile.Directory?.Create();
        await using var stream = cacheFile.Create();
        await JsonSerializer.SerializeAsync(stream, entries.OrderBy(e => e.StartsAt).ToArray(), JsonOptions);
    }

    private static List<LodestoneEntry> ApplyCacheRetention(IEnumerable<LodestoneEntry> entries, Configuration configuration, out int removed)
    {
        var entryList = entries.ToList();
        var retentionDays = configuration.AutoClearCacheEntriesDays;
        if (retentionDays <= 0)
        {
            removed = 0;
            return entryList;
        }

        var cutoff = DateTime.Now.Date.AddDays(-retentionDays);
        var retained = entryList
            .Where(entry => entry.EffectiveEnd.Date >= cutoff)
            .ToList();
        removed = entryList.Count - retained.Count;
        return retained;
    }

    private async Task<LodestoneEntry> EnrichEntryAsync(LodestoneEntry stub, LodestoneScanDiagnostics diagnostics)
    {
        if (IsExternalEntry(stub))
        {
            if (stub.FullArticleParsed && stub.ArticleFormatVersion >= CurrentArticleFormatVersion)
            {
                diagnostics.EnrichedEntries++;
                diagnostics.ImageUrlsKept += stub.ImageUrls.Count;
                stub.FetchedAt = DateTime.UtcNow;
                return stub;
            }

            return await EnrichExternalEntryAsync(stub, diagnostics);
        }

        try
        {
            var html = await httpClient.GetStringAsync(stub.Url);
            var entry = stub.Url.Contains("/lodestone/special/", StringComparison.OrdinalIgnoreCase)
                ? ParseSpecialPage(stub, html, await FetchSpecialStylesAsync(html))
                : ParseNewsPage(stub, html);

            diagnostics.EnrichedEntries++;
            diagnostics.ImageUrlsKept += entry.ImageUrls.Count;
            diagnostics.ImageUrlsRejected += CountRejectedImageCandidates(html);
            entry.FetchedAt = DateTime.UtcNow;
            return entry;
        }
        catch (Exception ex)
        {
            diagnostics.Errors++;
            diagnostics.LastError = $"Failed detail: {stub.Title}";
            Plugin.Log.Warning(ex, "Failed to enrich Lodestone entry {Title}", stub.Title);
            stub.FetchedAt = DateTime.UtcNow;
            return stub;
        }
    }

    private async Task<LodestoneEntry> EnrichExternalEntryAsync(LodestoneEntry stub, LodestoneScanDiagnostics diagnostics)
    {
        try
        {
            var html = await httpClient.GetStringAsync(stub.Url);
            var entry = stub.Kind switch
            {
                LodestoneEntryKind.DeveloperPost => ParseDeveloperBlogPage(stub, html),
                LodestoneEntryKind.IcyVeins => ParseIcyVeinsArticlePage(stub, html),
                LodestoneEntryKind.IcyVeinsGuide => ParseIcyVeinsGuidePage(stub, html),
                _ => stub
            };

            diagnostics.EnrichedEntries++;
            diagnostics.ImageUrlsKept += entry.ImageUrls.Count;
            entry.FetchedAt = DateTime.UtcNow;
            return entry;
        }
        catch (Exception ex)
        {
            diagnostics.Errors++;
            diagnostics.LastError = $"Failed external detail: {stub.Title}";
            Plugin.Log.Warning(ex, "Failed to enrich external entry {Title}", stub.Title);
            stub.FetchedAt = DateTime.UtcNow;
            return stub;
        }
    }

    private static LodestoneEntry ParseIcyVeinsArticlePage(LodestoneEntry entry, string html)
    {
        var articleHtml = FirstMatch(html, "<article[^>]+class=[\"'][^\"']*article-content[^\"']*[\"'][^>]*>(?<value>.*?)</article>");
        var fullText = CleanArticleText(articleHtml);
        if (!string.IsNullOrWhiteSpace(fullText))
        {
            entry.Summary = string.IsNullOrWhiteSpace(entry.Author)
                ? fullText
                : $"By {entry.Author}\n\n{fullText}";
            entry.FullArticleParsed = true;
            entry.ArticleFormatVersion = CurrentArticleFormatVersion;
        }

        var pageHero = FirstMatch(html, "<img[^>]+class=[\"'][^\"']*news-image[^\"']*[\"'][^>]+src=[\"'](?<value>[^\"']+)[\"'][^>]*>");
        var images = (string.IsNullOrWhiteSpace(pageHero) ? Enumerable.Empty<string>() : new[] { pageHero })
            .Concat(ExtractImages(articleHtml))
            .Concat(string.IsNullOrWhiteSpace(entry.HeroImageUrl) ? Enumerable.Empty<string>() : new[] { entry.HeroImageUrl })
            .Select(url => NormalizeExternalImageUrl(WebUtility.HtmlDecode(url), entry.Url))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();

        if (images.Count > 0)
        {
            entry.ImageUrls = images;
            entry.HeroImageUrl = images[0];
        }

        return entry;
    }

    private static LodestoneEntry ParseIcyVeinsGuidePage(LodestoneEntry entry, string html)
    {
        var title = FirstMatch(html, "<meta[^>]+property=[\"']og:title[\"'][^>]+content=[\"'](?<value>[^\"']+)[\"'][^>]*>");
        if (!string.IsNullOrWhiteSpace(title))
            entry.Title = WebUtility.HtmlDecode(title).Trim();

        var author = ExtractIcyVeinsAuthor(html);
        if (!string.IsNullOrWhiteSpace(author))
            entry.Author = author;

        var startsAt = ExtractIcyVeinsDate(html, "dateModified")
                       ?? ExtractIcyVeinsDate(html, "datePublished")
                       ?? ExtractIcyVeinsTimestamp(html)
                       ?? entry.StartsAt;
        entry.StartsAt = startsAt;
        entry.SourceTimeText = $"Last updated {startsAt:g}";

        var articleHtml = ExtractIcyVeinsGuideContentHtml(html);
        var fullText = CleanArticleText(articleHtml);
        if (!string.IsNullOrWhiteSpace(fullText))
        {
            entry.Summary = string.IsNullOrWhiteSpace(entry.Author)
                ? fullText
                : $"By {entry.Author}\n\n{fullText}";
            entry.FullArticleParsed = true;
            entry.ArticleFormatVersion = CurrentArticleFormatVersion;
        }

        var pageHero = ExtractIcyVeinsGuideHero(html);
        var images = (string.IsNullOrWhiteSpace(pageHero) ? Enumerable.Empty<string>() : new[] { pageHero })
            .Concat(ExtractExternalImages(articleHtml, entry.Url))
            .Concat(string.IsNullOrWhiteSpace(entry.HeroImageUrl) ? Enumerable.Empty<string>() : new[] { entry.HeroImageUrl })
            .Select(url => NormalizeExternalImageUrl(WebUtility.HtmlDecode(url), entry.Url))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();

        if (images.Count > 0)
        {
            entry.ImageUrls = images;
            entry.HeroImageUrl = images[0];
        }

        return entry;
    }

    private static LodestoneEntry ParseDeveloperBlogPage(LodestoneEntry entry, string html)
    {
        var pageTitle = ExtractPageTitle(html);
        if (!string.IsNullOrWhiteSpace(pageTitle))
            entry.Title = pageTitle;

        var articleHtml = FirstMatch(html, "<div[^>]+class=[\"'][^\"']*blog-entry-detail__body[^\"']*[\"'][^>]*>(?<value>.*?)</div>\\s*<div[^>]+class=[\"'][^\"']*blog-entry-detail__footer");
        var fullText = CleanArticleText(articleHtml);
        if (!string.IsNullOrWhiteSpace(fullText))
        {
            entry.Summary = fullText;
            entry.FullArticleParsed = true;
            entry.ArticleFormatVersion = CurrentArticleFormatVersion;
        }

        var images = ExtractMetaImages(html)
            .Concat(ExtractImages(articleHtml))
            .Select(url => NormalizeHeroImage(url, entry.Kind))
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Where(url => !url.Contains("/blog/s/global/", StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(16)
            .ToList();
        if (images.Count > 0)
        {
            entry.ImageUrls = images;
            entry.HeroImageUrl = images[0];
        }

        return entry;
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
            SourceName = "Lodestone",
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
        var questText = CleanArticleText(FirstMatch(html, "<p[^>]*content__event-info__quest--text[^>]*>(?<value>.*?)</p>"));
        entry.Summary = string.Join("\n\n", new[]
        {
            string.IsNullOrWhiteSpace(questTitle) ? string.Empty : $"## {questTitle}",
            questText
        }.Where(s => !string.IsNullOrWhiteSpace(s)));
        entry.ArticleFormatVersion = CurrentArticleFormatVersion;

        var mapAlt = WebUtility.HtmlDecode(FirstMatch(html, "<img[^>]*content__event-info__map[^>]*alt=(?:\"(?<value>[^\"]+)\"|'(?<value>[^']+)')[^>]*>"));
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
            var startsAt = ParseMaintenanceDate(times.Groups["start"].Value, timezone);
            var endsAt = ParseMaintenanceDate(times.Groups["end"].Value, timezone);
            if (startsAt.HasValue && endsAt.HasValue)
            {
                entry.StartsAt = startsAt.Value;
                entry.EndsAt = endsAt.Value;
                entry.SourceTimeZone = timezone.Trim();
                entry.SourceTimeText = $"{times.Groups["start"].Value} to {times.Groups["end"].Value} ({timezone.Trim()})";
            }
            else
            {
                Plugin.Log.Warning("Unable to parse maintenance window for {Title}: {Window}", entry.Title, times.Value);
            }
        }

        var articleSummary = CleanArticleText(articleHtml);
        entry.Summary = Shorten(string.IsNullOrWhiteSpace(articleSummary) ? text : articleSummary, 6000);
        entry.ArticleFormatVersion = CurrentArticleFormatVersion;
        TryApplyEventPeriod(entry, text);
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

    private static IEnumerable<string> ExtractExternalImages(string html, string baseUrl)
    {
        return IcyVeinsImageRegex().Matches(html)
            .Select(m => NormalizeExternalImageUrl(WebUtility.HtmlDecode(m.Groups["url"].Value), baseUrl))
            .Where(u => Uri.TryCreate(u, UriKind.Absolute, out _))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<(string Url, string Title)> ExtractIcyVeinsGuideLinks(string html)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match match in IndexLinkRegex().Matches(html))
        {
            var title = CleanText(match.Groups["text"].Value);
            var rawUrl = WebUtility.HtmlDecode(match.Groups["href"].Value);
            var url = NormalizeExternalImageUrl(rawUrl, IcyVeinsUrl);
            if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
                continue;

            var canonical = new UriBuilder(uri) { Query = string.Empty, Fragment = string.Empty }.Uri.ToString().TrimEnd('/');
            if (!IsIcyVeinsGuideUrl(canonical, title) || !seen.Add(canonical))
                continue;

            yield return (canonical, string.IsNullOrWhiteSpace(title) ? "Icy Veins Guide" : title);
        }
    }

    private static bool IsIcyVeinsGuideUrl(string url, string title)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri))
            return false;

        if (!uri.Host.Contains("icy-veins.com", StringComparison.OrdinalIgnoreCase))
            return false;

        var path = uri.AbsolutePath.TrimEnd('/');
        if (!path.StartsWith("/ffxiv/", StringComparison.OrdinalIgnoreCase) || path.Equals("/ffxiv", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.Contains("/news", StringComparison.OrdinalIgnoreCase))
            return false;

        if (path.Contains("guides-home-page", StringComparison.OrdinalIgnoreCase)
            || path.Contains("guides-for-", StringComparison.OrdinalIgnoreCase)
            || path.Contains("general-guides", StringComparison.OrdinalIgnoreCase)
            || path.Contains("instance-guides", StringComparison.OrdinalIgnoreCase))
            return false;

        return path.Contains("guide", StringComparison.OrdinalIgnoreCase)
               || title.Contains("guide", StringComparison.OrdinalIgnoreCase);
    }

    private static string ExtractIcyVeinsGuideContentHtml(string html)
    {
        var match = Regex.Match(html, "<div[^>]+class=[\"'][^\"']*page_content_container[^\"']*[\"'][^>]*>", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        if (!match.Success)
            return string.Empty;

        var end = html.IndexOf("<div id=\"footer\"", match.Index, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            end = html.IndexOf("<div class=\"footer\"", match.Index, StringComparison.OrdinalIgnoreCase);
        if (end < 0)
            end = Math.Min(html.Length, match.Index + 160_000);

        var content = html[match.Index..end];
        content = Regex.Replace(content, "<nav[^>]*>.*?</nav>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        content = Regex.Replace(content, "<form[^>]*>.*?</form>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        content = Regex.Replace(content, "<div[^>]+class=[\"'][^\"']*(?:comments|related|sidebar|breadcrumb)[^\"']*[\"'][^>]*>.*?</div>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return content;
    }

    private static string ExtractIcyVeinsGuideHero(string html)
    {
        var headerHero = FirstMatch(html, "<div[^>]+class=[\"'][^\"']*page_content_header[^\"']*[\"'][^>]*style=[\"'][^\"']*background-image\\s*:\\s*url\\([\"']?(?<value>[^\\)\"']+)[\"']?\\)");
        if (!string.IsNullOrWhiteSpace(headerHero))
            return headerHero.Trim(' ', '\'', '"');

        return FirstMatch(html, "<meta[^>]+property=[\"']og:image[\"'][^>]+content=[\"'](?<value>[^\"']+)[\"'][^>]*>").Trim(' ', '\'', '"');
    }

    private static DateTime? ExtractIcyVeinsDate(string html, string propertyName)
    {
        var match = Regex.Match(html, $"\"{Regex.Escape(propertyName)}\"\\s*:\\s*\"(?<value>[^\"]+)\"", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        return match.Success && DateTimeOffset.TryParse(match.Groups["value"].Value, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var parsed)
            ? parsed.LocalDateTime
            : null;
    }

    private static DateTime? ExtractIcyVeinsTimestamp(string html)
    {
        var match = IcyVeinsTimeRegex().Match(html);
        return match.Success && long.TryParse(match.Groups["ts"].Value, out var timestamp)
            ? DateTimeOffset.FromUnixTimeSeconds(timestamp).LocalDateTime
            : null;
    }

    private static string ExtractIcyVeinsAuthor(string html)
    {
        var author = FirstMatch(html, "\"author\"\\s*:\\s*\\{.*?\"name\"\\s*:\\s*\"(?<value>[^\"]+)\"");
        if (!string.IsNullOrWhiteSpace(author))
            return WebUtility.HtmlDecode(author).Trim();

        author = FirstMatch(html, "<span[^>]+class=[\"'][^\"']*page_author[^\"']*[\"'][^>]*>.*?by\\s*<span[^>]*>(?<value>.*?)</span>");
        return CleanText(author);
    }

    private static int CountRejectedImageCandidates(string html)
        => ExtractImages(html).Count(url => !IsContentImage(url) || IsDecorativeLodestoneImage(url));

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
        text = Regex.Replace(text, "<img[^>]*>", "\n\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<h([1-6])[^>]*>(?<value>.*?)</h\\1>", m => $"\n\n## {CleanArticleInlineText(m.Groups["value"].Value)}\n\n", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<blockquote[^>]*>(?<value>.*?)</blockquote>", m =>
        {
            var quote = CleanArticleInlineText(m.Groups["value"].Value);
            return string.IsNullOrWhiteSpace(quote)
                ? "\n\n"
                : $"\n\n> {quote.Replace("\n", "\n> ")}\n\n";
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<li[^>]*>(?<value>.*?)</li>", m =>
        {
            var item = CleanArticleInlineText(m.Groups["value"].Value);
            return string.IsNullOrWhiteSpace(item) ? "\n" : $"\n- {item}\n";
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<p[^>]*>(?<value>.*?)</p>", m =>
        {
            var paragraph = CleanArticleInlineText(m.Groups["value"].Value);
            return string.IsNullOrWhiteSpace(paragraph) ? "\n\n" : $"\n\n{paragraph}\n\n";
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "</(?:div|section|article|ul|ol|table|tbody|tr)>", "\n\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<a[^>]*>(?<value>.*?)</a>", m => CleanArticleInlineText(m.Groups["value"].Value), RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);

        var lines = new List<string>();
        foreach (var rawLine in text
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries)
            .Select(line => WhitespaceRegex().Replace(line, " ").Trim()))
        {
            if (string.IsNullOrWhiteSpace(rawLine))
            {
                if (lines.Count > 0 && lines[^1].Length > 0)
                    lines.Add(string.Empty);
                continue;
            }

            lines.Add(rawLine);
        }

        while (lines.Count > 0 && lines[^1].Length == 0)
            lines.RemoveAt(lines.Count - 1);

        return string.Join("\n", lines);
    }

    private static string CleanArticleInlineText(string html)
    {
        if (string.IsNullOrWhiteSpace(html))
            return string.Empty;

        var text = ScriptRegex().Replace(html, " ");
        text = Regex.Replace(text, "<style.*?</style>", " ", RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<br\\s*/?>", "\n", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<a[^>]*>(?<value>.*?)</a>", m => CleanArticleInlineText(m.Groups["value"].Value), RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<(?:strong|b)[^>]*>(?<value>.*?)</(?:strong|b)>", m =>
        {
            var value = CleanArticleInlineText(m.Groups["value"].Value);
            return string.IsNullOrWhiteSpace(value) ? string.Empty : $"**{value}**";
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = Regex.Replace(text, "<(?:em|i)[^>]*>(?<value>.*?)</(?:em|i)>", m =>
        {
            var value = CleanArticleInlineText(m.Groups["value"].Value);
            return string.IsNullOrWhiteSpace(value) ? string.Empty : $"*{value}*";
        }, RegexOptions.IgnoreCase | RegexOptions.Singleline);
        text = TagRegex().Replace(text, " ");
        text = WebUtility.HtmlDecode(text);

        return string.Join("\n", text
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.TrimEntries)
            .Select(line => WhitespaceRegex().Replace(line, " ").Trim())
            .Where(line => !string.IsNullOrWhiteSpace(line)));
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
        LodestoneEntryKind.DeveloperPost => config.ShowDeveloperPosts,
        LodestoneEntryKind.IcyVeins => config.ShowIcyVeins,
        LodestoneEntryKind.IcyVeinsGuide => config.ShowIcyVeins && config.ShowIcyVeinsGuides,
        _ => true
    };

    private static bool IsExternalEntry(LodestoneEntry entry)
        => entry.Kind is LodestoneEntryKind.DeveloperPost or LodestoneEntryKind.IcyVeins or LodestoneEntryKind.IcyVeinsGuide;

    private static bool CanUseCachedEntry(LodestoneEntry stub, LodestoneEntry existing)
        => !string.IsNullOrEmpty(existing.Summary)
           && existing.ArticleFormatVersion >= CurrentArticleFormatVersion
           && (!IsExternalEntry(stub) || existing.FullArticleParsed);

    private static bool CacheFormatReady(IEnumerable<LodestoneEntry> cachedEntries, Configuration configuration)
        => cachedEntries
            .Where(entry => ShouldInclude(entry.Kind, configuration))
            .All(entry => entry.ArticleFormatVersion >= CurrentArticleFormatVersion && (!IsExternalEntry(entry) || entry.FullArticleParsed));

    private static string SourceLabel(LodestoneEntry entry)
        => !string.IsNullOrWhiteSpace(entry.SourceName) && !entry.SourceName.Equals("Lodestone", StringComparison.OrdinalIgnoreCase)
            ? entry.SourceName
            : SourceLabel(entry.Kind);

    private static string SourceLabel(LodestoneEntryKind? kind) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => "Events",
        LodestoneEntryKind.Topic => "Topics",
        LodestoneEntryKind.Notice => "Notices",
        LodestoneEntryKind.Maintenance => "Maintenance",
        LodestoneEntryKind.Update => "Updates",
        LodestoneEntryKind.Status => "Status",
        LodestoneEntryKind.Recovery => "Recovery",
        LodestoneEntryKind.DeveloperPost => "Developer Posts",
        LodestoneEntryKind.IcyVeins => "Icy Veins",
        LodestoneEntryKind.IcyVeinsGuide => "Icy Veins Guides",
        _ => "Lodestone"
    };

    private void SetProgress(string operation, string source, int completed, int total, string status)
    {
        CurrentProgress = new ScanProgress
        {
            IsActive = true,
            Operation = operation,
            Source = source,
            Completed = Math.Max(0, completed),
            Total = Math.Max(0, total),
            Status = status,
            StartedAtUtc = CurrentProgress.IsActive ? CurrentProgress.StartedAtUtc : DateTime.UtcNow,
            UpdatedAtUtc = DateTime.UtcNow
        };
    }

    private void CompleteDiagnostics(LodestoneScanDiagnostics diagnostics)
    {
        diagnostics.FinishedAtUtc = DateTime.UtcNow;
        LastDiagnostics = diagnostics;
        CurrentProgress = new ScanProgress
        {
            IsActive = false,
            Operation = "Refresh",
            Source = string.Empty,
            Status = diagnostics.Status,
            Completed = 1,
            Total = 1,
            StartedAtUtc = diagnostics.StartedAtUtc,
            UpdatedAtUtc = diagnostics.FinishedAtUtc.Value
        };
    }

    private static string GetJsonString(JsonElement item, string propertyName)
        => item.TryGetProperty(propertyName, out var value) && value.ValueKind != JsonValueKind.Null
            ? value.GetString() ?? string.Empty
            : string.Empty;

    private static string CleanArticleSummary(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
            return string.Empty;

        return string.Join("\n", value
            .Replace('\r', '\n')
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(line => WhitespaceRegex().Replace(line, " ").Trim())
            .Where(line => line.Length > 0));
    }

    private static string NormalizeExternalImageUrl(string url, string baseUrl)
    {
        if (string.IsNullOrWhiteSpace(url))
            return string.Empty;

        if (url.StartsWith("//", StringComparison.Ordinal))
            return $"https:{url}";

        return Uri.TryCreate(url, UriKind.Absolute, out _)
            ? url
            : new Uri(new Uri(baseUrl), url).ToString();
    }

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

    private static bool ShouldProbeTopicForEvents(LodestoneEntry entry, Configuration config)
        => config.ShowEvents && entry.Kind == LodestoneEntryKind.Topic;

    private static bool TryApplyEventPeriod(LodestoneEntry entry, string text)
    {
        if (entry.Kind != LodestoneEntryKind.Topic)
            return false;

        var match = EventPeriodRegex().Match(text);
        if (!match.Success)
            return false;

        var timezone = match.Groups["tz"].Value.Trim();
        if (!TryParseEventPeriodDate(match.Groups["start"].Value, timezone, out var startsAt)
            || !TryParseEventPeriodDate(match.Groups["end"].Value, timezone, out var endsAt))
            return false;

        entry.Kind = LodestoneEntryKind.SpecialEvent;
        entry.StartsAt = startsAt;
        entry.EndsAt = endsAt;
        entry.SourceTimeZone = timezone;
        entry.SourceTimeText = string.IsNullOrWhiteSpace(timezone)
            ? $"{match.Groups["start"].Value} to {match.Groups["end"].Value}"
            : $"{match.Groups["start"].Value} to {match.Groups["end"].Value} ({timezone})";
        return true;
    }

    private static bool TryParseEventPeriodDate(string value, string timezone, out DateTime result)
    {
        var normalized = NormalizeMeridiem(value.Replace(" at ", " ", StringComparison.OrdinalIgnoreCase)).Trim();
        var formats = new[]
        {
            "dddd, MMMM d, yyyy h:mm tt",
            "dddd, MMM d, yyyy h:mm tt",
            "MMMM d, yyyy h:mm tt",
            "MMM d, yyyy h:mm tt"
        };

        if (DateTime.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed)
            || DateTime.TryParse(normalized, CultureInfo.InvariantCulture, DateTimeStyles.None, out parsed))
        {
            result = string.IsNullOrWhiteSpace(timezone)
                ? DateTime.SpecifyKind(parsed, DateTimeKind.Local)
                : ConvertSourceTimeToLocal(parsed, timezone);
            return true;
        }

        result = default;
        return false;
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

    private static DateTime? ParseMaintenanceDate(string value, string timezone)
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

        var formats = new[] { "MMM d, yyyy h:mm tt", "MMMM d, yyyy h:mm tt" };
        if (!DateTime.TryParseExact(normalized, formats, CultureInfo.InvariantCulture, DateTimeStyles.None, out var parsed))
            return null;

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

        if (normalized.Contains("EDT", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("EST", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Eastern", StringComparison.OrdinalIgnoreCase))
            return FindTimeZone("Eastern Standard Time", "America/New_York");

        if (normalized.Contains("CDT", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("CST", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Central", StringComparison.OrdinalIgnoreCase))
            return FindTimeZone("Central Standard Time", "America/Chicago");

        if (normalized.Contains("MDT", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("MST", StringComparison.OrdinalIgnoreCase)
            || normalized.Contains("Mountain", StringComparison.OrdinalIgnoreCase))
            return FindTimeZone("Mountain Standard Time", "America/Denver");

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
            .Replace(" | FINAL FANTASY XIV: Official Blog", string.Empty, StringComparison.OrdinalIgnoreCase)
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

    internal static LodestoneEntry ParseNewsFixture(LodestoneEntry entry, string html) => ParseNewsPage(entry, html);
    internal static LodestoneEntry ParseSpecialFixture(LodestoneEntry entry, string html, string styles) => ParseSpecialPage(entry, html, styles);
    internal static LodestoneEntry ParseIcyVeinsFixture(LodestoneEntry entry, string html) => ParseIcyVeinsArticlePage(entry, html);
    internal static LodestoneEntry ParseIcyVeinsGuideFixture(LodestoneEntry entry, string html) => ParseIcyVeinsGuidePage(entry, html);
    internal static LodestoneEntry ParseDeveloperBlogFixture(LodestoneEntry entry, string html) => ParseDeveloperBlogPage(entry, html);
    internal static IReadOnlyList<LodestoneEntry> ParseOfficialBlogIndexFixture(string html) => ParseOfficialBlogIndex(html, OfficialBlogUrl).ToArray();
    internal static DateTime? ParseMaintenanceDateFixture(string value, string timezone) => ParseMaintenanceDate(value, timezone);

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
    [GeneratedRegex("(?<start>[A-Z][a-z]{2,8}\\.?\\s+\\d{1,2},\\s+\\d{4}\\s+\\d{1,2}:\\d{2}\\s+[ap]\\.?m\\.?)\\s+to\\s+(?<end>[A-Z][a-z]{2,8}\\.?\\s+\\d{1,2},\\s+\\d{4}\\s+\\d{1,2}:\\d{2}\\s+[ap]\\.?m\\.?)\\s+\\((?<tz>[^)]+)\\)", RegexOptions.IgnoreCase)]
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
    [GeneratedRegex("<li[^>]+class=[\"'][^\"']*blog-entry__card[^\"']*[\"'][^>]*>(?<card>.*?)</li>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex OfficialBlogCardRegex();
    [GeneratedRegex("/lodestone/special/20\\d{2}/", RegexOptions.IgnoreCase)]
    private static partial Regex SpecialEventUrlRegex();
    [GeneratedRegex("(?<url>(?:https://[^\"'<>\\s]+)?/lodestone/special/20\\d{2}/[^\"'<>\\s?]+/[^\"'<>\\s?]+)(?:\\?[^\"'<>\\s]*)?", RegexOptions.IgnoreCase)]
    private static partial Regex RawSpecialUrlRegex();
    [GeneratedRegex("(?:Event|Campaign|Entry|Sweepstakes|Collaboration)\\s+Period:?\\s*(?<start>(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),\\s+[A-Z][a-z]+\\s+\\d{1,2},\\s+\\d{4}\\s+at\\s+\\d{1,2}:\\d{2}\\s+[ap]\\.m\\.)\\s+to\\s+(?<end>(?:Monday|Tuesday|Wednesday|Thursday|Friday|Saturday|Sunday),\\s+[A-Z][a-z]+\\s+\\d{1,2},\\s+\\d{4}\\s+at\\s+\\d{1,2}:\\d{2}\\s+[ap]\\.m\\.)(?:\\s+\\((?<tz>[^)]+)\\))?", RegexOptions.IgnoreCase)]
    private static partial Regex EventPeriodRegex();
    [GeneratedRegex("<div\\s+id=[\"']news_\\d+[\"'][^>]*>(?<card>.*?)(?=<div\\s+id=[\"']news_\\d+[\"']|<div\\s+id=[\"']news_more|$)", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IcyVeinsCardRegex();
    [GeneratedRegex("<span[^>]+class=[\"'][^\"']*news_title[^\"']*[\"'][^>]*>\\s*<a[^>]+href=[\"'](?<url>[^\"']+)[\"'][^>]*>(?<title>.*?)</a>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IcyVeinsTitleRegex();
    [GeneratedRegex("data-time=[\"'](?<ts>\\d+)[\"']", RegexOptions.IgnoreCase)]
    private static partial Regex IcyVeinsTimeRegex();
    [GeneratedRegex("<img[^>]+src=[\"'](?<url>[^\"']+)[\"']", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IcyVeinsImageRegex();
    [GeneratedRegex("<span[^>]+class=[\"'][^\"']*news_subtitle[^\"']*[\"'][^>]*>(?<summary>.*?)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IcyVeinsSummaryRegex();
    [GeneratedRegex("<span[^>]+class=[\"'][^\"']*news_author[^\"']*[\"'][^>]*>.*?<span[^>]*>(?<author>.*?)</span>", RegexOptions.IgnoreCase | RegexOptions.Singleline)]
    private static partial Regex IcyVeinsAuthorRegex();
}
