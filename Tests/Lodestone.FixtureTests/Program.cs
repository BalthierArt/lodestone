using Lodestone.Models;
using Lodestone.Services;
using Lodestone.Windows;

var tests = new (string Name, Action Test)[]
{
    ("maintenance PDT converts to local and preserves source text", TestMaintenancePdt),
    ("invalid maintenance dates do not parse as now", TestInvalidMaintenanceDate),
    ("special event fixture parses schedule, quest, map, and hero", TestSpecialEvent),
    ("newer special event meta schedule parses optional end year", TestSpecialEventMetaSchedule),
    ("duplicate special event topic keeps special page", TestSpecialEventDedupe),
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
    AssertEqual(6, entry.ArticleFormatVersion, "maintenance format version");
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
    AssertEqual(6, entry.ArticleFormatVersion, "special format version");
    AssertContains(entry.StartingLocation, "Ul'dah", "starting location");
    AssertEqual("https://lds-img.finalfantasyxiv.com/h/s/NWFaAgSpPh0h2emJx69yeD85nY.jpg", entry.HeroImageUrl, "hero");
}

static void TestSpecialEventMetaSchedule()
{
    const string html = """
                        <html><head>
                        <title>Breaking Brick Mountains 2026 | FINAL FANTASY XIV, The Lodestone</title>
                        <meta property="og:description" content="Event Schedule / From Thursday, June 25, 2026 at 1:00 a.m. (PDT) to Monday, July 13 at 7:59 a.m. (PDT)">
                        <meta property="og:title" content="Breaking Brick Mountains 2026">
                        </head><body>
                        <h3>Event Schedule</h3>
                        <h2 class="content__event-info__quest--title">A Rocky Relationship</h2>
                        <p class="content__event-info__quest--text">A strangely familiar golem needs a hand.</p>
                        <div class="body"><div class="inr inr_1sp">
                        <h3>Event Items</h3>
                        <p class="mb20">During this event, players can purchase a King Slime Crown and Dragon Quest X Framer's Kit by speaking with the toughie in Ul'dah - Steps of Nald (X:8 Y:12).</p>
                        <h3>Event Schedule</h3>
                        <p class="mb20">From Thursday, June 25, 2026 at 1:00 (PDT)<br />to Monday, July 13 at 7:59 (PDT)</p>
                        <h3>Breaking Brick Mountains</h3>
                        <p class="mb20">Speak with Havak Alvak of the <i>Mythril Eye</i> to learn where these strange new golems may be found.</p>
                        <h3>Havak Alvak's Location</h3>
                        <p class="green mb5">Ul'dah - Steps of Nald</p>
                        <img src="https://lds-img.finalfantasyxiv.com/h/4/PxJgLA3pMHGK3kZIeTi2gS4pvQ.jpg">
                        </div></div>
                        </body></html>
                        """;
    var entry = LodestoneClient.ParseSpecialFixture(
        new LodestoneEntry
        {
            Id = "breaking-brick",
            Title = "Breaking Brick Mountains",
            Url = "https://na.finalfantasyxiv.com/lodestone/special/2026/Theres_Golems_in_Those_Hills/uG8fdJUc3q",
            Kind = LodestoneEntryKind.SpecialEvent,
            StartsAt = new DateTime(2026, 6, 29)
        },
        html,
        string.Empty);

    AssertEqual(new DateTime(2026, 6, 25, 1, 0, 0), entry.StartsAt, "breaking brick start");
    AssertEqual(new DateTime(2026, 7, 13, 7, 59, 0), entry.EndsAt ?? DateTime.MinValue, "breaking brick end");
    AssertEqual("PDT", entry.SourceTimeZone, "breaking brick timezone");
    AssertContains(entry.SourceTimeText, "July 13, 2026", "breaking brick source time");
    AssertContains(entry.Summary, "## Event Items", "breaking brick event items heading");
    AssertContains(entry.Summary, "King Slime Crown", "breaking brick item purchase text");
    AssertContains(entry.Summary, "Speak with Havak Alvak", "breaking brick quest text");
    AssertEqual("Havak Alvak", entry.StartingNpc, "breaking brick npc");
    AssertEqual("Ul'dah - Steps of Nald", entry.StartingLocation, "breaking brick location");
    AssertEqual("https://img.finalfantasyxiv.com/t/53409c75eddec473f0554fe84d6169c4ba5edc97.jpg?1781769746?1781248814", entry.HeroImageUrl, "breaking brick hero");
}

static void TestSpecialEventDedupe()
{
    var entries = LodestoneClient.DeduplicateSpecialEventsFixture(new[]
    {
        new LodestoneEntry
        {
            Id = "topic",
            Title = "Breaking Brick Mountains Returns on June 25!",
            Url = "https://na.finalfantasyxiv.com/lodestone/topics/detail/53409c75eddec473f0554fe84d6169c4ba5edc97",
            Kind = LodestoneEntryKind.SpecialEvent,
            StartsAt = new DateTime(2026, 6, 25, 1, 0, 0),
            EndsAt = new DateTime(2026, 7, 13, 7, 59, 0),
            HeroImageUrl = "topic.jpg"
        },
        new LodestoneEntry
        {
            Id = "special",
            Title = "Breaking Brick Mountains 2026",
            Url = "https://na.finalfantasyxiv.com/lodestone/special/2026/Theres_Golems_in_Those_Hills/uG8fdJUc3q",
            Kind = LodestoneEntryKind.SpecialEvent,
            StartsAt = new DateTime(2026, 6, 25, 1, 0, 0),
            EndsAt = new DateTime(2026, 7, 13, 7, 59, 0),
            HeroImageUrl = "special.jpg",
            Summary = "quest text",
            Rewards = [new LodestoneReward { Kind = "Item", Name = "Brickman", ImageUrl = "reward.png" }]
        }
    });

    AssertEqual(1, entries.Count, "deduped count");
    AssertContains(entries[0].Url, "/lodestone/special/", "dedupe winner");
    AssertEqual(1, entries[0].Rewards.Count, "dedupe rewards");
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
    AssertEqual(6, entry.ArticleFormatVersion, "article format version");
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
                        <img src="//static.icy-veins.com/images/ffxiv/dawntrail/raids/m9/waymark.jpg" alt="Waymarks" />
                        <div class="export-string-wrapper">
                          <details class="export-string">
                            <summary><span class="export-string__title">Waymark Code</span></summary>
                            <span class="export-string__code">{"Name":"M9S","MapID":1069}</span>
                          </details>
                          <button class="export-string__copy" type="button"><span>Copy</span></button>
                        </div>
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
    AssertContains(entry.Summary, "[[lodestone-image:", "guide inline image marker");
    AssertContains(entry.Summary, "[[lodestone-copy:", "guide copy marker");
    AssertEqual(6, entry.ArticleFormatVersion, "guide format version");
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
    AssertEqual(6, entry.ArticleFormatVersion, "blog format version");
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
