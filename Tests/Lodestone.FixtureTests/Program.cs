using Lodestone.Models;
using Lodestone.Services;
using Lodestone.Windows;

var tests = new (string Name, Action Test)[]
{
    ("maintenance PDT converts to local and preserves source text", TestMaintenancePdt),
    ("invalid maintenance dates do not parse as now", TestInvalidMaintenanceDate),
    ("special event fixture parses schedule, quest, map, and hero", TestSpecialEvent),
    ("topic campaign period is promoted to event", TestTopicCampaignPeriod),
    ("index fixture classifies Lodestone links", TestIndex),
    ("Icy Veins fixture parses full article body", TestIcyVeinsArticle),
    ("Icy Veins guide fixture parses full guide body", TestIcyVeinsGuide),
    ("developer blog fixture parses full article body", TestDeveloperBlogArticle),
    ("official blog index parses real cards", TestOfficialBlogIndex),
    ("quest lookup strips formatted heading markers", TestQuestLookupHeadingCleanup)
};

var failures = 0;
foreach (var test in tests)
{
    try
    {
        test.Test();
        Console.WriteLine($"PASS {test.Name}");
    }
    catch (Exception ex)
    {
        failures++;
        Console.WriteLine($"FAIL {test.Name}");
        Console.WriteLine(ex.Message);
    }
}

if (failures > 0)
    Environment.Exit(1);

static void TestMaintenancePdt()
{
    var html = ReadFixture("maintenance-pdt.html");
    var entry = LodestoneClient.ParseNewsFixture(
        new LodestoneEntry
        {
            Id = "maintenance",
            Title = "[Maintenance] All Worlds Maintenance",
            Url = "https://na.finalfantasyxiv.com/lodestone/news/detail/test",
            Kind = LodestoneEntryKind.Maintenance,
            StartsAt = new DateTime(2026, 1, 1, 0, 0, 0)
        },
        html);

    var pacific = FindTimeZone("Pacific Standard Time", "America/Los_Angeles");
    var expectedStart = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 6, 2, 3, 0, 0), pacific).ToLocalTime();
    var expectedEnd = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 6, 2, 7, 0, 0), pacific).ToLocalTime();
    AssertNear(expectedStart, entry.StartsAt, "start time");
    AssertNear(expectedEnd, entry.EndsAt ?? DateTime.MinValue, "end time");
    AssertEqual("PDT", entry.SourceTimeZone, "source timezone");
    AssertContains(entry.SourceTimeText, "Jun. 2, 2026 3:00 a.m.", "source text");
    AssertContains(entry.Summary, "## [Maintenance] All Worlds Maintenance", "maintenance heading");
    AssertContains(entry.Summary, "We will be performing maintenance", "maintenance paragraph");
    AssertEqual(3, entry.ArticleFormatVersion, "maintenance format version");
}

static void TestInvalidMaintenanceDate()
{
    var parsed = LodestoneClient.ParseMaintenanceDateFixture("Jun. 2, 2026 3:00 banana", "PDT");
    if (parsed.HasValue)
        throw new InvalidOperationException("Invalid maintenance text parsed as a real date.");
}

static void TestSpecialEvent()
{
    var html = ReadFixture("special-event.html");
    var styles = ReadFixture("special-event.css");
    var entry = LodestoneClient.ParseSpecialFixture(
        new LodestoneEntry
        {
            Id = "make-it-rain",
            Title = "The Make It Rain Campaign",
            Url = "https://na.finalfantasyxiv.com/lodestone/special/2026/the_make_it_rain_campaign/NX0yR0viwE",
            Kind = LodestoneEntryKind.Topic,
            StartsAt = new DateTime(2026, 5, 1)
        },
        html,
        styles);

    AssertEqual(LodestoneEntryKind.SpecialEvent, entry.Kind, "kind");
    AssertEqual(new DateTime(2026, 5, 29, 1, 0, 0), entry.StartsAt, "start");
    AssertEqual(new DateTime(2026, 6, 24, 7, 59, 0), entry.EndsAt ?? DateTime.MinValue, "end");
    AssertContains(entry.Title, "Make It Rain", "title");
    AssertContains(entry.Summary, "## You Otter Be There", "summary heading");
    AssertContains(entry.Summary, "Ollier locks eyes", "summary paragraph");
    AssertEqual(3, entry.ArticleFormatVersion, "special format version");
    AssertContains(entry.StartingLocation, "Ul'dah", "starting location");
    AssertEqual("https://lds-img.finalfantasyxiv.com/h/s/NWFaAgSpPh0h2emJx69yeD85nY.jpg", entry.HeroImageUrl, "hero");
}

static void TestTopicCampaignPeriod()
{
    const string html = """
                        <html><body>
                        <div class="news__detail__wrapper">
                        <h1>Enter the FINAL FANTASY XIV x Jollibee Collaboration Sweepstakes!</h1>
                        <p>Campaign Period: Monday, May 25, 2026 at 11:00 a.m. to Sunday, May 31, 2026 at 11:59 p.m. (PDT)</p>
                        <p>Visit the promotion page and complete at least one or more entry methods.</p>
                        <img src="https://lds-img.finalfantasyxiv.com/h/0/LIzzCdAQLNQ17P90_FIvlR_Dvg.jpg">
                        </div>
                        <div class="news__detail__social"></div>
                        </body></html>
                        """;
    var entry = LodestoneClient.ParseNewsFixture(
        new LodestoneEntry
        {
            Id = "campaign",
            Title = "Enter the FINAL FANTASY XIV x Jollibee Collaboration Sweepstakes!",
            Url = "https://na.finalfantasyxiv.com/lodestone/topics/detail/test",
            Kind = LodestoneEntryKind.Topic,
            StartsAt = new DateTime(2026, 5, 25)
        },
        html);

    var pacific = FindTimeZone("Pacific Standard Time", "America/Los_Angeles");
    var expectedStart = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 5, 25, 11, 0, 0), pacific).ToLocalTime();
    var expectedEnd = TimeZoneInfo.ConvertTimeToUtc(new DateTime(2026, 5, 31, 23, 59, 0), pacific).ToLocalTime();
    AssertEqual(LodestoneEntryKind.SpecialEvent, entry.Kind, "campaign promoted kind");
    AssertNear(expectedStart, entry.StartsAt, "campaign start");
    AssertNear(expectedEnd, entry.EndsAt ?? DateTime.MinValue, "campaign end");
    AssertContains(entry.SourceTimeText, "Monday, May 25, 2026", "campaign source time");
}

static void TestIndex()
{
    var entries = LodestoneClient.ParseIndex(ReadFixture("index.html"), baseUrl: "https://na.finalfantasyxiv.com/lodestone/").ToArray();
    AssertEqual(2, entries.Length, "entry count");
    AssertEqual(LodestoneEntryKind.Maintenance, entries[0].Kind, "first kind");
    AssertEqual(LodestoneEntryKind.SpecialEvent, entries[1].Kind, "second kind");
}

static void TestIcyVeinsArticle()
{
    const string html = """
                        <html><body>
                        <img class="news-image" src="https://static.icy-veins.com/wp/wp-content/uploads/2026/06/hero.webp" />
                        <article class="news article-content">
                        <p>First full paragraph.</p>
                        <h2>Where to Unlock</h2>
                        <p>Second paragraph with <strong>important</strong> text and <em>soft emphasis</em>.</p>
                        <ul><li>Bring friends.</li><li>Try the new duty.</li></ul>
                        <img src="https://static.icy-veins.com/wp/wp-content/uploads/2026/06/detail.webp" />
                        </article>
                        <aside>sidebar noise</aside>
                        </body></html>
                        """;
    var entry = LodestoneClient.ParseIcyVeinsFixture(
        new LodestoneEntry
        {
            Id = "icy",
            Title = "Icy Article",
            Url = "https://www.icy-veins.com/ffxiv/news/test/",
            Kind = LodestoneEntryKind.IcyVeins,
            StartsAt = new DateTime(2026, 6, 1),
            Author = "Piyo"
        },
        html);

    AssertEqual(true, entry.FullArticleParsed, "full article parsed");
    AssertContains(entry.Summary, "First full paragraph.", "article first paragraph");
    AssertContains(entry.Summary, "## Where to Unlock", "article heading");
    AssertContains(entry.Summary, "**important**", "article bold emphasis");
    AssertContains(entry.Summary, "*soft emphasis*", "article italic emphasis");
    AssertContains(entry.Summary, "- Bring friends.", "article first list item");
    AssertContains(entry.Summary, "- Try the new duty.", "article second list item");
    AssertEqual(3, entry.ArticleFormatVersion, "article format version");
    AssertEqual("https://static.icy-veins.com/wp/wp-content/uploads/2026/06/hero.webp", entry.HeroImageUrl, "icy hero");
}

static void TestIcyVeinsGuide()
{
    const string html = """
                        <html><head>
                        <meta property="og:title" content="Occult Crescent: South Horn Guide" />
                        <meta property="og:image" content="//static.icy-veins.com/images/ffxiv/og-images/dawntrail.jpg" />
                        <script type="application/ld+json">
                        {
                            "dateModified": "2026-03-13T12:00:00+00:00",
                            "datePublished": "2025-05-27T12:00:00+00:00",
                            "author": { "name": "Stella" }
                        }
                        </script>
                        </head><body>
                        <div class="page_content_container text_color">
                        <div class="page_content_header" style="background-image:url('//static.icy-veins.com/images/ffxiv/background-images/dawntrail.jpg');">
                        <div class="page_content_header_intro"><p>Learn everything we know about South Horn.</p></div>
                        </div>
                        <h2>Occult Crescent: The South Horn</h2>
                        <p>South Horn is the first area of Occult Crescent released in patch 7.25.</p>
                        <ul><li>Complete FATEs and critical encounters.</li></ul>
                        </div>
                        <div id="footer">footer noise</div>
                        </body></html>
                        """;
    var entry = LodestoneClient.ParseIcyVeinsGuideFixture(
        new LodestoneEntry
        {
            Id = "icy-guide",
            Title = "Guide",
            Url = "https://www.icy-veins.com/ffxiv/occult-crescent-south-horn-guide",
            Kind = LodestoneEntryKind.IcyVeinsGuide,
            StartsAt = new DateTime(2026, 6, 1)
        },
        html);

    var expectedDate = DateTimeOffset.Parse("2026-03-13T12:00:00+00:00").LocalDateTime;
    AssertEqual(true, entry.FullArticleParsed, "full guide parsed");
    AssertEqual("Occult Crescent: South Horn Guide", entry.Title, "guide title");
    AssertEqual("Stella", entry.Author, "guide author");
    AssertEqual(expectedDate, entry.StartsAt, "guide updated date");
    AssertContains(entry.Summary, "## Occult Crescent: The South Horn", "guide heading");
    AssertContains(entry.Summary, "- Complete FATEs", "guide list");
    AssertEqual(3, entry.ArticleFormatVersion, "guide format version");
    AssertEqual("https://static.icy-veins.com/images/ffxiv/background-images/dawntrail.jpg", entry.HeroImageUrl, "guide hero");
}

static void TestDeveloperBlogArticle()
{
    const string html = """
                        <html><head>
                        <title>Developer Blog | FINAL FANTASY XIV: Official Blog</title>
                        <meta property="og:image" content="https://lds-img.finalfantasyxiv.com/blog_image/na_blog/hero.png" />
                        </head><body>
                        <div class="blog-entry-detail__body">
                        <p>Hello, everyone!</p>
                        <p><span><strong>Event Period</strong></span><br />From Friday to Wednesday.</p>
                        <ul><li>Reward one</li></ul>
                        <p><img src="https://lds-img.finalfantasyxiv.com/blog_image/na_blog/test.png" /></p>
                        </div>
                        <div class="blog-entry-detail__footer">footer noise</div>
                        </body></html>
                        """;
    var entry = LodestoneClient.ParseDeveloperBlogFixture(
        new LodestoneEntry
        {
            Id = "dev",
            Title = "Developer Blog",
            Url = "https://na.finalfantasyxiv.com/blog/test.html",
            Kind = LodestoneEntryKind.DeveloperPost,
            StartsAt = new DateTime(2026, 6, 1)
        },
        html);

    AssertEqual(true, entry.FullArticleParsed, "full article parsed");
    AssertContains(entry.Summary, "Hello, everyone!", "blog first paragraph");
    AssertContains(entry.Summary, "Event Period", "blog heading");
    AssertContains(entry.Summary, "Reward one", "blog list");
    AssertEqual(3, entry.ArticleFormatVersion, "blog format version");
    AssertEqual("Developer Blog", entry.Title, "blog title suffix stripped");
    AssertEqual("https://lds-img.finalfantasyxiv.com/blog_image/na_blog/hero.png", entry.HeroImageUrl, "blog hero prefers og image");
}

static void TestOfficialBlogIndex()
{
    const string html = """
                        <html><body>
                        <li class="blog-entry__card">
                            <a href="https://na.finalfantasyxiv.com/blog/003855.html"
                                class="js--entry"
                                data-new_dt=""
                                data-publish_dt="2026-05-26T02:00:00-08:00">
                                <div class="blog-entry__img" style="background:url('fallback.jpg') no-repeat center center;background-size:cover;"
                                    data-thumbnail_big="https://lds-img.finalfantasyxiv.com/blog_image/na_blog/assets_c/2026/05/260520_SA_01-thumb-600xauto-20503.png"></div>
                                <div class="blog-entry__title"><p>An Otterly Amazing Time to Make It Rain!</p></div>
                                <div class="blog-entry__status"><p>Event</p><time>05/26/2026</time></div>
                            </a>
                        </li>
                        <li class="blog-entry__card"><a href="<%= tmpl.entry.url %>"><p>template</p></a></li>
                        </body></html>
                        """;
    var entries = LodestoneClient.ParseOfficialBlogIndexFixture(html);
    AssertEqual(1, entries.Count, "official blog entry count");
    AssertEqual("An Otterly Amazing Time to Make It Rain!", entries[0].Title, "official blog title");
    AssertEqual("FFXIV Official Blog", entries[0].SourceName, "official blog source");
    AssertEqual("https://lds-img.finalfantasyxiv.com/blog_image/na_blog/assets_c/2026/05/260520_SA_01-thumb-600xauto-20503.png", entries[0].HeroImageUrl, "official blog thumbnail");
}

static void TestQuestLookupHeadingCleanup()
{
    AssertEqual("You Otter Be There", QuestLookupWindow.CleanQuestNameCandidate("## You Otter Be There"), "quest heading cleanup");
    AssertEqual("You Otter Be There", QuestLookupWindow.CleanQuestNameCandidate("**You Otter Be There**"), "quest emphasis cleanup");
}

static string ReadFixture(string fileName)
    => File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName));

static TimeZoneInfo FindTimeZone(params string[] ids)
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

    throw new InvalidOperationException($"None of these time zones exist: {string.Join(", ", ids)}");
}

static void AssertEqual<T>(T expected, T actual, string label)
{
    if (!EqualityComparer<T>.Default.Equals(expected, actual))
        throw new InvalidOperationException($"{label}: expected {expected}, got {actual}");
}

static void AssertNear(DateTime expected, DateTime actual, string label)
{
    if ((actual - expected).Duration() > TimeSpan.FromSeconds(1))
        throw new InvalidOperationException($"{label}: expected {expected:o}, got {actual:o}");
}

static void AssertContains(string value, string expected, string label)
{
    if (!value.Contains(expected, StringComparison.OrdinalIgnoreCase))
        throw new InvalidOperationException($"{label}: expected text containing '{expected}', got '{value}'");
}
