namespace Lodestone.Models;

public sealed class LodestoneScanDiagnostics
{
    public DateTime StartedAtUtc { get; set; }
    public DateTime? FinishedAtUtc { get; set; }
    public bool Force { get; set; }
    public bool UsedFreshCache { get; set; }
    public int CachedEntries { get; set; }
    public int SourceCount { get; set; }
    public int PagesFetched { get; set; }
    public int FeedImages { get; set; }
    public int IndexEntries { get; set; }
    public int FilteredEntries { get; set; }
    public int EnrichedEntries { get; set; }
    public int CacheHits { get; set; }
    public int PrunedCacheEntries { get; set; }
    public int DeveloperPostEntries { get; set; }
    public int IcyVeinsEntries { get; set; }
    public int IcyVeinsGuideEntries { get; set; }
    public int ExternalEntries => DeveloperPostEntries + IcyVeinsEntries + IcyVeinsGuideEntries;
    public int ImageUrlsKept { get; set; }
    public int ImageUrlsRejected { get; set; }
    public int Errors { get; set; }
    public string Status { get; set; } = "No scan has run yet.";
    public string LastError { get; set; } = string.Empty;
    public List<string> SourceSummaries { get; set; } = [];
    public List<string> ParserNotes { get; set; } = [];

    public TimeSpan? Duration => FinishedAtUtc.HasValue
        ? FinishedAtUtc.Value - StartedAtUtc
        : null;
}
