using Dalamud.Interface;
using Dalamud.Interface.Colors;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;

namespace Lodestone.Windows;

public sealed class ConfigWindow : Window
{
    private static readonly Vector4 Primary = new(0.42f, 0.22f, 0.78f, 0.88f);
    private static readonly Vector4 PrimaryAccent = new(0.64f, 0.38f, 1.00f, 0.98f);
    private static readonly Vector4 Muted = new(0.68f, 0.66f, 0.78f, 1f);
    private static readonly Vector4 Warn = new(0.84f, 0.72f, 1.00f, 1f);
    private static readonly uint Panel = ImGui.ColorConvertFloat4ToU32(new Vector4(0.1294f, 0.1333f, 0.1764f, 1f));
    private static readonly uint PanelElevated = ImGui.ColorConvertFloat4ToU32(Primary);
    private static readonly uint PanelGreen = ImGui.ColorConvertFloat4ToU32(new Vector4(0.18f, 0.42f, 0.32f, 0.9f));
    private static readonly uint PanelTeal = ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.34f, 0.52f, 0.96f));
    private static readonly uint PanelBlue = ImGui.ColorConvertFloat4ToU32(new Vector4(0.24f, 0.18f, 0.58f, 0.96f));
    private static readonly uint PanelNews = ImGui.ColorConvertFloat4ToU32(new Vector4(0.32f, 0.20f, 0.54f, 0.96f));
    private static readonly uint PanelDanger = ImGui.ColorConvertFloat4ToU32(new Vector4(0.62f, 0.08f, 0.08f, 0.96f));
    private static readonly Dictionary<string, Vector2> ContentBoxSizeCache = [];

    private readonly Plugin plugin;
    private int selectedView;

    public ConfigWindow(Plugin plugin) : base("Lodestone###LodestoneConfigWindow")
    {
        this.plugin = plugin;
        Size = new Vector2(760, 620);
        SizeCondition = ImGuiCond.FirstUseEver;
        Flags |= ImGuiWindowFlags.NoCollapse;
        AllowPinning = false;
        AllowClickthrough = false;
    }

    internal void OpenPartySyncSettings()
    {
        selectedView = 5;
        IsOpen = true;
    }

    public override void Draw()
    {
        DrawMainContent();

        ImGui.Dummy(new Vector2(0, 4));
        ImGui.Separator();
        ImGui.Dummy(new Vector2(0, 4));

        using var footerButton = ImRaii.PushColor(ImGuiCol.Button, Primary);
        using var footerButtonHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, PrimaryAccent);
        using var footerButtonActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.32f, 0.16f, 0.62f, 0.96f));
        using var table = ImRaii.Table("##lodestoneConfigFooter", 4, ImGuiTableFlags.None);
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("##footerStretch", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("##footerRefresh", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("##footerCalendar", ImGuiTableColumnFlags.WidthFixed);
        ImGui.TableSetupColumn("##footerClose", ImGuiTableColumnFlags.WidthFixed);

        ImGui.TableNextRow();
        ImGui.TableNextColumn();

        ImGui.TableNextColumn();
        using (ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.12f, 0.34f, 0.62f, 0.96f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.18f, 0.48f, 0.88f, 1f)))
        using (ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.08f, 0.24f, 0.48f, 1f)))
        {
            if (ImGui.Button("Refresh Lodestone##footerRefresh"))
                _ = plugin.CalendarWindow.RefreshAsync(true);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Fetch current Lodestone entries now.");

        ImGui.TableNextColumn();
        if (ImGui.Button("Open Calendar##footerCalendar"))
            plugin.CalendarWindow.IsOpen = true;

        ImGui.TableNextColumn();
        if (ImGui.Button("Close##footerClose"))
            IsOpen = false;
    }

    private void DrawMainContent()
    {
        using var child = ImRaii.Child("##lodestoneConfigMainContent", new Vector2(0, -55), border: false, flags: ImGuiWindowFlags.NoResize);
        if (!child.Success)
            return;

        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f);
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 8f);
        using var spacing = ImRaii.PushStyle(ImGuiStyleVar.ItemSpacing, new Vector2(8, 8));
        using var navBg = ImRaii.PushColor(ImGuiCol.ChildBg, Panel);
        using var border = ImRaii.PushColor(ImGuiCol.Border, PanelElevated);
        using var button = ImRaii.PushColor(ImGuiCol.Button, Panel);
        using var buttonHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, PrimaryAccent);
        using var buttonActive = ImRaii.PushColor(ImGuiCol.ButtonActive, Primary);

        using (var nav = ImRaii.Child("##lodestoneConfigNav", new Vector2(230, 0), true))
        {
            if (nav.Success)
            {
                ImGui.Dummy(new Vector2(0, 4));
                CenteredTitle(FontAwesomeIcon.CalendarAlt, "LODESTONE");
                ImGui.Dummy(new Vector2(0, 4));
                DrawNavButton(0, FontAwesomeIcon.Rss, "Overview");
                DrawNavButton(1, FontAwesomeIcon.Filter, "Sources");
                DrawNavButton(2, FontAwesomeIcon.CalendarDay, "Calendar");
                DrawNavButton(3, FontAwesomeIcon.CloudDownloadAlt, "Refresh");
                DrawNavButton(4, FontAwesomeIcon.PaintBrush, "Customize");
                DrawNavButton(5, FontAwesomeIcon.Users, "Party Sync");
                DrawNavButton(6, FontAwesomeIcon.InfoCircle, "About");
            }
        }

        ImGui.SameLine();
        using var content = ImRaii.Child("##lodestoneConfigContent", new Vector2(0, 0), true);
        if (!content.Success)
            return;

        switch (selectedView)
        {
            case 0:
                DrawOverviewTab();
                break;
            case 1:
                DrawSourcesTab();
                break;
            case 2:
                DrawCalendarTab();
                break;
            case 3:
                DrawRefreshTab();
                break;
            case 4:
                DrawCustomizationTab();
                break;
            case 5:
                DrawPartySyncTab();
                break;
            case 6:
                DrawAboutTab();
                break;
        }
    }

    private void DrawNavButton(int index, FontAwesomeIcon icon, string label)
    {
        var selected = selectedView == index;
        using var selectedColor = ImRaii.PushColor(ImGuiCol.Button, Primary, selected);
        ImGui.SetCursorPosX(10);
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(selected ? Warn : Muted, icon.ToIconString());
        }

        ImGui.SameLine();
        var buttonWidth = ImGui.GetWindowWidth() - 58;
        if (ImGui.Button($"{label}##lodestoneConfigNav{index}", new Vector2(buttonWidth, 34)))
            selectedView = index;
    }

    private void DrawOverviewTab()
    {
        ImGui.Dummy(new Vector2(0, 4));
        ContentBox("overviewHeader", PanelElevated, true, () =>
        {
            CenteredTitle(FontAwesomeIcon.Rss, "LODESTONE CALENDAR", 1.2f);
            TextCentered("Live Lodestone entries, cached for in-game viewing.");
        });

        ContentBox("overviewStatus", PanelGreen, true, () =>
        {
            SectionHeader(FontAwesomeIcon.CheckCircle, "Status", White());
            TextWrappedColored(White(), $"Region: {plugin.Configuration.Region.ToUpperInvariant()}.");
            TextWrappedColored(White(), $"Refresh interval: {FormatRefreshInterval(plugin.Configuration.RefreshMinutes)}.");
            TextWrappedColored(White(), $"Archive pages per source: {plugin.Configuration.MaxPagesPerSource}.");
            TextWrappedColored(White(), $"Entries enriched per refresh: {plugin.Configuration.MaxEntriesToScan}.");
        });

        ContentBox("overviewButtons", PanelBlue, false, () =>
        {
            SectionHeader(FontAwesomeIcon.Bolt, "Actions", White());
            if (PanelButton("Refresh now##overviewRefresh", PanelBlue, new Vector2(120, 0)))
                _ = plugin.CalendarWindow.RefreshAsync(true);
            ImGui.SameLine();
            if (PanelButton("Open calendar##overviewCalendar", PanelBlue, new Vector2(130, 0)))
                plugin.CalendarWindow.IsOpen = true;
        });
    }

    private void DrawSourcesTab()
    {
        ContentBox("sourcesHeader", PanelElevated, true, () =>
        {
            CenteredTitle(FontAwesomeIcon.Filter, "SOURCES", 1.2f);
            TextCentered("Choose which Lodestone sections appear on the calendar.");
        });

        ContentBox("sourcesToggles", Panel, false, () =>
        {
            SectionHeader(FontAwesomeIcon.Rss, "Lodestone sections");
            DrawRegionCombo();
            DrawCheckbox("Events", "Seasonal/special event pages such as Make It Rain. These can span multiple calendar days.", plugin.Configuration.ShowEvents, v => plugin.Configuration.ShowEvents = v, refresh: true);
            DrawCheckbox("Topics", "Topical Lodestone announcements that are not parsed as seasonal event pages.", plugin.Configuration.ShowTopics, v => plugin.Configuration.ShowTopics = v, refresh: true);
            DrawCheckbox("Notices", "Important notices and general news posts.", plugin.Configuration.ShowNotices, v => plugin.Configuration.ShowNotices = v, refresh: true);
            DrawCheckbox("Maintenance", "Maintenance windows are placed on the outage date range when the page includes times.", plugin.Configuration.ShowMaintenance, v => plugin.Configuration.ShowMaintenance = v, refresh: true);
            DrawCheckbox("Recovery", "Recovery posts from Lodestone status/news. These have their own priority and visibility toggle.", plugin.Configuration.ShowRecovery, v => plugin.Configuration.ShowRecovery = v, refresh: true);
            DrawCheckbox("Updates", "Patch and update posts from the Lodestone update section.", plugin.Configuration.ShowUpdates, v => plugin.Configuration.ShowUpdates = v, refresh: true);
            DrawCheckbox("Status", "Recovery and obstacle/status posts from the Lodestone status section.", plugin.Configuration.ShowStatus, v => plugin.Configuration.ShowStatus = v, refresh: true);
            DrawCheckbox("Developer posts", "Adds official FFXIV Developers' Blog posts from na.finalfantasyxiv.com/blog.", plugin.Configuration.ShowDeveloperPosts, v => plugin.Configuration.ShowDeveloperPosts = v, refresh: true);
        });

        ContentBox("sourcesExternal", PanelTeal, false, () =>
        {
            SectionHeader(FontAwesomeIcon.Globe, "External sources", White());
            TextWrappedColored(White(), "Optional third-party sources can add more FFXIV article dates to the calendar. They are off by default so Lodestone stays the main feed.");
            DrawCheckbox("Icy Veins", "Adds dated FFXIV news articles from Icy Veins with full article text and source links.", plugin.Configuration.ShowIcyVeins, v => plugin.Configuration.ShowIcyVeins = v, refresh: true);
            ImGui.Indent(18f);
            using (ImRaii.Disabled(!plugin.Configuration.ShowIcyVeins))
                DrawCheckbox("Include Icy Veins guides", "Adds FFXIV guide pages from Icy Veins under the Icy Veins source. Guide pages use their own updated date and full guide text.", plugin.Configuration.ShowIcyVeinsGuides, v => plugin.Configuration.ShowIcyVeinsGuides = v, refresh: true);
            ImGui.Unindent(18f);
        });

        ContentBox("sourcesIntegrations", PanelBlue, false, () =>
        {
            SectionHeader(FontAwesomeIcon.Plug, "Plugin integrations", White());
            DrawCheckbox("AutoRetainer submarine returns", "Reads submarine return times from AutoRetainer IPC when AutoRetainer is loaded. Lodestone keeps this data in memory and does not save it to your config.", plugin.Configuration.ShowSubmarineReturns, v =>
            {
                plugin.Configuration.ShowSubmarineReturns = v;
                plugin.SubmarineService.Refresh(force: true);
            });
            TextWrappedColored(White(), plugin.SubmarineService.Status);
        });
    }

    private void DrawCalendarTab()
    {
        ContentBox("calendarHeader", PanelElevated, true, () =>
        {
            CenteredTitle(FontAwesomeIcon.CalendarDay, "CALENDAR", 1.2f);
            TextCentered("Tune day cell behavior and startup visibility.");
        });

        ContentBox("calendarDisplay", Panel, false, () =>
        {
            SectionHeader(FontAwesomeIcon.Image, "Display");
            DrawCheckbox("Show Lodestone images in day cells", "When an event has a hero image, the calendar paints that image behind the day entries.", plugin.Configuration.ShowDayImages, v => plugin.Configuration.ShowDayImages = v);
            DrawCheckbox("Only show day text on hover", "Keeps day cells clean until your mouse is over a date. Corner icons stay visible.", plugin.Configuration.ShowCalendarTextOnHoverOnly, v => plugin.Configuration.ShowCalendarTextOnHoverOnly = v);
            DrawCheckbox("Auto-cycle day hero images", "Cycles through that day's available Lodestone images every 3 seconds. Uses the image cache and is off by default.", plugin.Configuration.AutoCycleDayHeroImages, v => plugin.Configuration.AutoCycleDayHeroImages = v);
            DrawCheckbox("Pause new image loading in combat", "Performance guard: cached images keep drawing, but Lodestone will not start new image loads while you are in combat.", plugin.Configuration.PauseImageLoadingInCombat, v => plugin.Configuration.PauseImageLoadingInCombat = v);
            DrawCheckbox("Hover text opens exact entry", "When enabled, clicking a Lodestone line in the hover popup opens that specific details window instead of the day list.", plugin.Configuration.HoverTextClickOpensEntry, v => plugin.Configuration.HoverTextClickOpensEntry = v);
            DrawCheckbox("Only show current and future entries", "Hide entries that ended before today.", plugin.Configuration.OnlyCurrentAndFuture, v => plugin.Configuration.OnlyCurrentAndFuture = v);
            DrawCheckbox("Open calendar when plugin loads", "Show the month grid automatically after Dalamud loads the plugin.", plugin.Configuration.OpenCalendarOnStartup, v => plugin.Configuration.OpenCalendarOnStartup = v);
            TextWrappedColored(Muted, $"Seen/hidden entries: {plugin.Configuration.HiddenEntryIds.Count}");
            if (PanelButton("Show hidden entries again##calendarClearSeen", PanelDanger, new Vector2(190, 0)))
            {
                plugin.Configuration.HiddenEntryIds.Clear();
                plugin.Configuration.Save();
            }
        });

        ContentBox("calendarDtr", PanelGreen, false, () =>
        {
            SectionHeader(FontAwesomeIcon.Clock, "Server bar (DTR)", White());
            DrawCheckbox("Show server bar Lodestone status", "Adds a DTR entry. Click it to open the calendar.", plugin.Configuration.ShowDtrEntry, v => { plugin.Configuration.ShowDtrEntry = v; plugin.ServerBar.Refresh(); });
            DrawCheckbox("Use compact server bar text", "Shows an icon and count instead of longer status text.", plugin.Configuration.UseShortDtrText, v => { plugin.Configuration.UseShortDtrText = v; plugin.ServerBar.Refresh(); });
            DrawCheckbox("Hide server bar when empty", "Hides the DTR entry when enabled categories have nothing active.", plugin.Configuration.HideDtrWhenNoEntries, v => { plugin.Configuration.HideDtrWhenNoEntries = v; plugin.ServerBar.Refresh(); });
            DrawCheckbox("DTR: active events", "Counts active Lodestone special events such as Make It Rain.", plugin.Configuration.DtrShowEvents, v => { plugin.Configuration.DtrShowEvents = v; plugin.ServerBar.Refresh(); });
            DrawCheckbox("DTR: notes", "Counts local notes for today.", plugin.Configuration.DtrShowNotes, v => { plugin.Configuration.DtrShowNotes = v; plugin.ServerBar.Refresh(); });
            DrawCheckbox("DTR: party events", "Counts shared party events for today.", plugin.Configuration.DtrShowPartyEvents, v => { plugin.Configuration.DtrShowPartyEvents = v; plugin.ServerBar.Refresh(); });
            DrawCheckbox("DTR: active maintenance", "Shows active maintenance even when no calendar events are active.", plugin.Configuration.DtrShowActiveMaintenance, v => { plugin.Configuration.DtrShowActiveMaintenance = v; plugin.ServerBar.Refresh(); });
            DrawCheckbox("DTR: upcoming maintenance", "Shows upcoming maintenance inside the warning window.", plugin.Configuration.DtrShowUpcomingMaintenance, v => { plugin.Configuration.DtrShowUpcomingMaintenance = v; plugin.ServerBar.Refresh(); });
            DrawCheckbox("Notify for upcoming maintenance", "Shows a Dalamud notification once for maintenance inside the warning window.", plugin.Configuration.NotifyMaintenance, v => plugin.Configuration.NotifyMaintenance = v);
        });

        ContentBox("calendarNotes", PanelBlue, false, () =>
        {
            SectionHeader(FontAwesomeIcon.StickyNote, "Notes & alarms", White());
            DrawCheckbox("Enable note alarms", "Shows a Dalamud notification before timed player notes.", plugin.Configuration.EnableNoteAlarms, v => plugin.Configuration.EnableNoteAlarms = v);
            DrawCheckbox("Use 12-hour note times", "Shows note alarm times as 9:00 PM instead of 21:00.", plugin.Configuration.UseTwelveHourNoteTimes, v => plugin.Configuration.UseTwelveHourNoteTimes = v);
            DrawAlarmWarningCombo();
            TextWrappedColored(White(), $"Saved notes: {plugin.Configuration.Notes.Count}");
            DrawAlarmCenter();
        });

        ContentBox("calendarPriority", PanelNews, false, () =>
        {
            SectionHeader(FontAwesomeIcon.SortAmountUp, "Display priority", White());
            DrawPrioritySlider("Updates", plugin.Configuration.PriorityUpdates, v => plugin.Configuration.PriorityUpdates = v);
            DrawPrioritySlider("Topics", plugin.Configuration.PriorityTopics, v => plugin.Configuration.PriorityTopics = v);
            DrawPrioritySlider("Maintenance", plugin.Configuration.PriorityMaintenance, v => plugin.Configuration.PriorityMaintenance = v);
            DrawPrioritySlider("Recovery", plugin.Configuration.PriorityRecovery, v => plugin.Configuration.PriorityRecovery = v);
            DrawPrioritySlider("Status", plugin.Configuration.PriorityStatus, v => plugin.Configuration.PriorityStatus = v);
            DrawPrioritySlider("Notices / News", plugin.Configuration.PriorityNotices, v => plugin.Configuration.PriorityNotices = v);
            DrawPrioritySlider("Events", plugin.Configuration.PriorityEvents, v => plugin.Configuration.PriorityEvents = v);
            DrawPrioritySlider("Developer Posts", plugin.Configuration.PriorityDeveloperPosts, v => plugin.Configuration.PriorityDeveloperPosts = v);
            DrawPrioritySlider("Icy Veins", plugin.Configuration.PriorityIcyVeins, v => plugin.Configuration.PriorityIcyVeins = v);

            if (PanelButton("Reset priority##priorityReset", PanelNews, new Vector2(130, 0)))
            {
                plugin.Configuration.SetDefaultPriorities();
                plugin.Configuration.Save();
                plugin.ServerBar.Refresh();
            }
        });

        ContentBox("calendarPriorityRules", Panel, false, () =>
        {
            SectionHeader(FontAwesomeIcon.ListOl, "Priority rules");
            TextWrappedColored(Muted, "Named rules sit on top of the section sliders. They can boost common topics, push noisy posts down, or assign a custom hero image.");
            DrawPriorityRules();
        });
    }

    private void DrawRefreshTab()
    {
        ContentBox("refreshHeader", PanelElevated, true, () =>
        {
            CenteredTitle(FontAwesomeIcon.CloudDownloadAlt, "REFRESH", 1.2f);
            TextCentered("Control how often Lodestone is checked.");
        });

        ContentBox("refreshSettings", Panel, false, () =>
        {
            SectionHeader(FontAwesomeIcon.Sync, "Refresh behavior");
            DrawCheckbox("Refresh on startup", "Load Lodestone data when the plugin starts.", plugin.Configuration.AutoRefreshOnStartup, v => plugin.Configuration.AutoRefreshOnStartup = v);

            var refreshMinutes = plugin.Configuration.RefreshMinutes;
            ImGui.TextUnformatted("Refresh interval");
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("minutes##lodestoneRefreshMinutes", ref refreshMinutes, 15, 1440))
            {
                plugin.Configuration.RefreshMinutes = refreshMinutes;
                plugin.Configuration.Save();
            }
            TextWrappedColored(Muted, $"Current: {FormatRefreshInterval(plugin.Configuration.RefreshMinutes)}. Default is 24 hours; use Refresh now when you want an immediate scan.");

            var maxEntries = plugin.Configuration.MaxEntriesToScan;
            ImGui.TextUnformatted("Entries enriched");
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("entries##lodestoneMaxEntries", ref maxEntries, 5, 500))
            {
                plugin.Configuration.MaxEntriesToScan = maxEntries;
                plugin.Configuration.Save();
            }
            TextWrappedColored(Muted, "Higher values fetch more detail pages and discover more older calendar entries. Maximum is 500.");

            var maxPages = plugin.Configuration.MaxPagesPerSource;
            ImGui.TextUnformatted("Archive pages per source");
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("pages##lodestoneMaxPages", ref maxPages, 1, 20))
            {
                plugin.Configuration.MaxPagesPerSource = maxPages;
                plugin.Configuration.Save();
            }
            TextWrappedColored(Muted, "Scans Topics, Notices, Maintenance, Updates, and Status with Lodestone's page=N archive links.");

            var warningHours = plugin.Configuration.MaintenanceWarningHours;
            ImGui.TextUnformatted("Maintenance warning window");
            ImGui.SetNextItemWidth(220);
            if (ImGui.SliderInt("hours##lodestoneMaintenanceWarning", ref warningHours, 1, 72))
            {
                plugin.Configuration.MaintenanceWarningHours = warningHours;
                plugin.Configuration.Save();
            }
            TextWrappedColored(Muted, "The server bar entry appears when maintenance is active or starts within this many hours.");

            DrawScanProgress();

            if (PanelButton("Refresh now##refreshTabRefresh", PanelTeal, new Vector2(120, 0)))
                _ = plugin.CalendarWindow.RefreshAsync(true);
            ImGui.SameLine();
            if (PanelButton("Clear data cache##refreshClearData", PanelDanger, new Vector2(140, 0)))
            {
                plugin.LodestoneClient.ClearCache();
                _ = plugin.CalendarWindow.RefreshAsync(true);
            }
            ImGui.SameLine();
            if (PanelButton("Clear image cache##refreshClearImages", PanelDanger, new Vector2(145, 0)))
                plugin.ImageCache.Clear();
        });

        ContentBox("refreshCacheManager", PanelBlue, false, () =>
        {
            SectionHeader(FontAwesomeIcon.Database, "Cache manager", White());
            var cache = plugin.LodestoneClient.GetCacheInfo();
            TextWrappedColored(White(), cache.Exists
                ? $"Lodestone data cache: {FormatBytes(cache.Length)} saved {cache.LastWriteTimeUtc?.ToLocalTime():g}."
                : "Lodestone data cache: empty.");
            TextWrappedColored(White(), $"Visible entries after filters: {plugin.CalendarWindow.GetVisibleEntries().Length}.");
            TextWrappedColored(White(), $"Image cache: {plugin.ImageCache.CachedTextureCount} loaded, {plugin.ImageCache.ActiveDownloadCount} downloading, {plugin.ImageCache.FailedImageCount} failed.");
            TextWrappedColored(White(), plugin.ImageCache.LoadingPaused
                ? $"Image loading paused in combat. {plugin.ImageCache.PausedImageRequestCount} new request{(plugin.ImageCache.PausedImageRequestCount == 1 ? string.Empty : "s")} skipped."
                : $"Image loading active. {plugin.ImageCache.PausedImageRequestCount} request{(plugin.ImageCache.PausedImageRequestCount == 1 ? string.Empty : "s")} skipped while paused.");

            DrawCacheRetentionCombo();
            ImGui.Spacing();

            if (PanelButton("Clear data cache##cacheBoxClearData", PanelDanger, new Vector2(145, 0)))
            {
                plugin.LodestoneClient.ClearCache();
                _ = plugin.CalendarWindow.RefreshAsync(true);
            }

            ImGui.SameLine();
            if (PanelButton("Clear image cache##cacheBoxClearImages", PanelDanger, new Vector2(150, 0)))
                plugin.ImageCache.Clear();
        });

        ContentBox("refreshScanDebug", Panel, false, () =>
        {
            SectionHeader(FontAwesomeIcon.Database, "Lodestone scan debug");
            DrawScanDiagnostics();
        });
    }

    private void DrawCustomizationTab()
    {
        ContentBox("customizationHeader", PanelElevated, true, () =>
        {
            CenteredTitle(FontAwesomeIcon.PaintBrush, "CUSTOMIZATION", 1.2f);
            TextCentered("Adjust the calendar colors without changing the Lodestone data.");
        });

        ContentBox("customizationCalendar", Panel, false, () =>
        {
            SectionHeader(FontAwesomeIcon.Palette, "Calendar colors");
            DrawColorEditor(
                "Current day highlight",
                plugin.Configuration.CurrentDayHighlightColor(),
                color => plugin.Configuration.CalendarCurrentDayHighlightColor = Configuration.ColorArray(color));
            DrawColorEditor(
                "Day color",
                plugin.Configuration.DayColor(),
                color => plugin.Configuration.CalendarDayColor = Configuration.ColorArray(color));
            DrawColorEditor(
                "Day highlight color",
                plugin.Configuration.DayHighlightColor(),
                color => plugin.Configuration.CalendarDayHighlightColor = Configuration.ColorArray(color));
            DrawColorEditor(
                "Days of week text",
                plugin.Configuration.DayOfWeekColor(),
                color => plugin.Configuration.CalendarDayOfWeekColor = Configuration.ColorArray(color));

            DrawCheckbox("Custom day image dim", "Lets you control how dark non-current-day images are. Off keeps the current default look.", plugin.Configuration.UseCustomDayImageDim, v => plugin.Configuration.UseCustomDayImageDim = v);
            if (plugin.Configuration.UseCustomDayImageDim)
            {
                var dimPercent = (int)MathF.Round(Math.Clamp(plugin.Configuration.DayImageDimAmount, 0f, 0.9f) * 100f);
                ImGui.TextUnformatted("Other day darkness");
                ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
                if (ImGui.SliderInt("##dayImageDimAmount", ref dimPercent, 0, 90, $"{dimPercent}%"))
                {
                    plugin.Configuration.DayImageDimAmount = dimPercent / 100f;
                    plugin.Configuration.Save();
                }
                TextWrappedColored(Muted, "0% leaves other day images bright. Higher values add more dark overlay.");
            }

            DrawCheckbox("Use full weekday names", "Shows Sunday, Monday, Tuesday instead of Sun, Mon, Tue.", plugin.Configuration.UseFullDayNames, v => plugin.Configuration.UseFullDayNames = v);

            if (PanelButton("Restore defaults##customizationDefaults", PanelDanger, new Vector2(150, 0)))
            {
                plugin.Configuration.SetDefaultCalendarCustomization();
                plugin.Configuration.Save();
            }
        });
    }

    private void DrawPartySyncTab()
    {
        ContentBox("partySyncHeader", PanelElevated, true, () =>
        {
            CenteredTitle(FontAwesomeIcon.Users, "PARTY SYNC", 1.2f);
            TextCentered("Share planned party events with your group.");
        });

        ContentBox("partySyncExplain", PanelNews, true, () =>
        {
            SectionHeader(FontAwesomeIcon.Users, "What it does", White());
            TextWrappedColored(White(), "Party Sync is for player-made calendar plans: raid nights, trial farms, map nights, hunts, gathering routes, or anything your group wants to coordinate.");
            TextWrappedColored(White(), "Right-click a calendar day and choose Plan Party Event. Everyone using the same Supabase party key, or the same external bridge group, can see the event, open it, and mark Interested, Maybe, or Remove.");
            TextWrappedColored(Muted, "Local notes stay local. Lodestone scrape data stays local. Supabase mode stores party events and sign-up responses, with player display names encrypted by your Party key before upload.");
        });

        ContentBox("partySyncStatus", PanelBlue, true, () =>
        {
            SectionHeader(FontAwesomeIcon.Cloud, "Sync connection", White());
            TextWrappedColored(White(), $"Transport: {plugin.PartySyncService.TransportLabel}");
            TextWrappedColored(White(), plugin.PartySyncService.Status);
            TextWrappedColored(Muted, "Built-in sharing uses a Supabase Edge Function named lodestone-party-sync. The Party key stays client-side for new clients; Supabase receives a hash for routing and encrypted display names. External IPC Bridge mode lets another sync plugin use its own group/key instead.");
        });

        ContentBox("partySyncSettings", Panel, false, () =>
        {
            SectionHeader(FontAwesomeIcon.Key, "Sharing transport");
            DrawCheckbox("Show party events on calendar", "Shows shared party events on day cells and in the day details window.", plugin.Configuration.ShowPartyEvents, v => plugin.Configuration.ShowPartyEvents = v);
            DrawCheckbox("Enable Supabase party sync", "Turns on Lodestone's built-in shared party event polling and posting. This mode needs the Supabase fields and Party key below.", plugin.Configuration.PartySyncEnabled, v => plugin.Configuration.PartySyncEnabled = v);
            DrawCheckbox("Enable External IPC Bridge", "Lets another plugin handle the actual sharing through its own group/key. This mode does not require a Lodestone Party key, but it only syncs if another plugin consumes Lodestone.PartySync IPC.", plugin.Configuration.PartySyncExternalBridgeEnabled, v => plugin.Configuration.PartySyncExternalBridgeEnabled = v);
            DrawTextSetting("Supabase project URL", "https://your-project-ref.supabase.co", plugin.Configuration.PartySyncSupabaseUrl, value => plugin.Configuration.PartySyncSupabaseUrl = value, secret: false);
            DrawTextSetting("Supabase anon key", "eyJ...", plugin.Configuration.PartySyncAnonKey, value => plugin.Configuration.PartySyncAnonKey = value, secret: true);
            DrawTextSetting("Party key", "Only needed for Supabase mode", plugin.Configuration.PartySyncKey, value => plugin.Configuration.PartySyncKey = value, secret: true);
            DrawTextSetting("Display name override", "Leave blank to use your character name", plugin.Configuration.PartySyncDisplayName, value => plugin.Configuration.PartySyncDisplayName = value, secret: false);

            var pollSeconds = plugin.Configuration.PartySyncPollSeconds;
            ImGui.TextUnformatted("Polling interval");
            ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
            if (ImGui.SliderInt("seconds##partySyncPoll", ref pollSeconds, 15, 600))
            {
                plugin.Configuration.PartySyncPollSeconds = Math.Clamp(pollSeconds, 15, 600);
                plugin.Configuration.Save();
            }
            TextWrappedColored(Muted, "Default is 60 seconds. Polling keeps the implementation simpler than always-open realtime sockets.");

            if (PanelButton("Refresh party events##partySyncRefresh", PanelBlue, new Vector2(160, 0)))
                _ = plugin.PartySyncService.RefreshVisibleRangeAsync(force: true);
        });

        ContentBox("partySyncIpc", PanelTeal, true, () =>
        {
            SectionHeader(FontAwesomeIcon.Plug, "IPC sharing bridge", White());
            TextWrappedColored(White(), "Lodestone exposes local IPC for other plugins that want to read party events or queue creates/responses.");
            TextWrappedColored(Muted, "In External IPC Bridge mode, Lodestone does not ask for a Party key. The other plugin's existing group/key decides who receives the data.");
            TextWrappedColored(White(), "Providers: Lodestone.PartySync.ApiVersion, IsConfigured, IsSupabaseConfigured, IsExternalBridgeEnabled, Transport, Status, GetIconCatalogJson, GetEventsJson, QueueEventJson, QueueResponseJson, ImportEventsJson, QueueRefresh.");
        });
    }

    private void DrawTextSetting(string label, string hint, string value, Action<string> setter, bool secret)
    {
        var local = value ?? string.Empty;
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(-1f);
        var flags = secret ? ImGuiInputTextFlags.Password : ImGuiInputTextFlags.None;
        if (ImGui.InputTextWithHint($"##partySync{label}", hint, ref local, 512, flags))
        {
            setter(local.Trim());
            plugin.Configuration.Save();
        }
    }

    private void DrawColorEditor(string label, Vector4 value, Action<Vector4> setter)
    {
        ImGui.TextUnformatted(label);
        ImGui.SetNextItemWidth(210f * ImGuiHelpers.GlobalScale);
        if (ImGui.ColorEdit4($"##color{label}", ref value, ImGuiColorEditFlags.NoInputs | ImGuiColorEditFlags.AlphaPreviewHalf))
        {
            setter(value);
            plugin.Configuration.Save();
        }
    }

    private void DrawAboutTab()
    {
        ContentBox("aboutHeader", PanelElevated, true, () =>
        {
            CenteredTitle(FontAwesomeIcon.InfoCircle, "ABOUT", 1.2f);
            TextCentered("Lodestone-powered FFXIV event calendar.");
        });

        ContentBox("aboutParser", PanelNews, true, () =>
        {
            SectionHeader(FontAwesomeIcon.ExclamationTriangle, "Parser notes", White());
            TextWrappedColored(White(), "This plugin reads live Lodestone HTML and caches parsed entries locally. Square Enix can change markup.");
        });

        ContentBox("aboutIpc", PanelBlue, true, () =>
        {
            SectionHeader(FontAwesomeIcon.Plug, "Party Sync IPC", White());
            TextWrappedColored(White(), "Other plugins can integrate with Lodestone party events through the Lodestone.PartySync.* IPC providers.");
            TextWrappedColored(Muted, "The IPC can expose cached events, icon metadata, queued event creation, queued RSVP changes, and refresh requests. It is a local bridge, not a network relay by itself.");
        });

        ContentBox("aboutIntegrations", PanelNews, true, () =>
        {
            SectionHeader(FontAwesomeIcon.Ship, "AutoRetainer integration", White());
            TextWrappedColored(White(), "When AutoRetainer is installed and has cached workshop data, Lodestone can show submarine voyage return times on the calendar.");
            TextWrappedColored(Muted, "This uses AutoRetainer IPC locally. Lodestone does not write submarine data to disk.");
        });

        ContentBox("aboutLinks", PanelTeal, false, () =>
        {
            SectionHeader(FontAwesomeIcon.ExternalLinkAlt, "Links", White());
            if (PanelButton("Open NA Lodestone##aboutOpenLodestone", PanelTeal, new Vector2(160, 0)))
                Dalamud.Utility.Util.OpenLink("https://na.finalfantasyxiv.com/lodestone/");
            ImGui.SameLine();
            if (PanelButton("Open config folder##aboutOpenConfig", PanelTeal, new Vector2(160, 0)))
                Dalamud.Utility.Util.OpenLink(Plugin.PluginInterface.ConfigDirectory.FullName);
        });

        ContentBox("aboutThanks", Panel, true, () =>
        {
            SectionHeader(FontAwesomeIcon.Heart, "Thanks", White());
            TextWrappedColored(White(), "This was inspired by Eventy, and I still 100% recommend it. Eventy is clean, fast, and straight to the point for events. I just wanted FFXIV to have a WoW-like calendar.");
        });
    }

    private void DrawCheckbox(string label, string helpText, bool value, Action<bool> setter, bool refresh = false)
    {
        var localValue = value;
        if (ImGui.Checkbox($"{label}##lodestone{label}", ref localValue))
        {
            setter(localValue);
            plugin.Configuration.Save();
            if (refresh)
                _ = plugin.CalendarWindow.RefreshAsync(true);
        }

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip(helpText);
        TextWrappedColored(Muted, helpText);
    }

    private void DrawRegionCombo()
    {
        var regions = new[] { "na", "eu", "jp", "fr", "de" };
        ImGui.TextUnformatted("Region");
        ImGui.SetNextItemWidth(160);
        using var combo = ImRaii.Combo("##lodestoneRegion", plugin.Configuration.Region.ToUpperInvariant());
        if (!combo.Success)
            return;

        foreach (var region in regions)
        {
            var selected = plugin.Configuration.Region.Equals(region, StringComparison.OrdinalIgnoreCase);
            if (ImGui.Selectable(region.ToUpperInvariant(), selected))
            {
                plugin.Configuration.Region = region;
                plugin.Configuration.Save();
                _ = plugin.CalendarWindow.RefreshAsync(true);
            }
        }
    }

    private void DrawAlarmWarningCombo()
    {
        var presetMinutes = new[] { 120, 60, 45, 30, 15 };
        var current = plugin.Configuration.UseCustomNoteAlarmWarning
            ? "Custom"
            : FormatRefreshInterval(plugin.Configuration.NoteAlarmWarningMinutes);

        ImGui.TextUnformatted("Note warning time");
        ImGui.SetNextItemWidth(180f * ImGuiHelpers.GlobalScale);
        using (var combo = ImRaii.Combo("##noteAlarmWarning", current))
        {
            if (combo.Success)
            {
                foreach (var minutes in presetMinutes)
                {
                    var selected = !plugin.Configuration.UseCustomNoteAlarmWarning && plugin.Configuration.NoteAlarmWarningMinutes == minutes;
                    if (ImGui.Selectable(FormatRefreshInterval(minutes), selected))
                    {
                        plugin.Configuration.UseCustomNoteAlarmWarning = false;
                        plugin.Configuration.NoteAlarmWarningMinutes = minutes;
                        plugin.Configuration.Save();
                    }
                }

                if (ImGui.Selectable("Custom", plugin.Configuration.UseCustomNoteAlarmWarning))
                {
                    plugin.Configuration.UseCustomNoteAlarmWarning = true;
                    plugin.Configuration.Save();
                }
            }
        }

        if (plugin.Configuration.UseCustomNoteAlarmWarning)
        {
            var custom = plugin.Configuration.CustomNoteAlarmWarningMinutes;
            ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
            if (ImGui.InputInt("minutes##customNoteAlarmWarning", ref custom))
            {
                plugin.Configuration.CustomNoteAlarmWarningMinutes = Math.Clamp(custom, 1, 1440);
                plugin.Configuration.Save();
            }
        }
    }

    private void DrawAlarmCenter()
    {
        ImGui.Spacing();
        ImGui.Separator();
        SectionHeader(FontAwesomeIcon.Clock, "Upcoming reminders", White());

        var noteAlarms = plugin.NoteAlarmService.GetUpcomingNoteAlarms(6);
        if (noteAlarms.Count > 0)
        {
            ImGui.TextColored(Warn, "Notes");
            foreach (var alarm in noteAlarms)
                TextWrappedColored(White(), $"{FormatDateTime(alarm.ScheduledAt)} - {alarm.Note.Text} (warning {FormatRefreshInterval(alarm.WarningMinutes)} before)");
        }

        var maintenance = plugin.CalendarWindow.GetMaintenanceWarnings()
            .Take(6)
            .ToArray();
        if (maintenance.Length > 0)
        {
            ImGui.TextColored(Warn, "Maintenance");
            foreach (var entry in maintenance)
            {
                var source = string.IsNullOrWhiteSpace(entry.SourceTimeText) ? string.Empty : $" Source: {entry.SourceTimeText}";
                TextWrappedColored(White(), $"{FormatDateTime(entry.StartsAt)} - {entry.Title}.{source}");
            }
        }

        var now = DateTime.Now;
        var subs = plugin.SubmarineService.Returns
            .Where(sub => sub.ReturnAt >= now)
            .OrderBy(sub => sub.ReturnAt)
            .Take(6)
            .ToArray();
        if (subs.Length > 0)
        {
            ImGui.TextColored(Warn, "Submarines");
            foreach (var sub in subs)
                TextWrappedColored(White(), $"{FormatDateTime(sub.ReturnAt)} - {sub.VesselName} ({sub.CharacterName})");
        }

        if (noteAlarms.Count == 0 && maintenance.Length == 0 && subs.Length == 0)
            TextWrappedColored(Muted, "No upcoming note alarms, maintenance warnings, or submarine returns are inside the current reminder window.");
    }

    private void DrawCacheRetentionCombo()
    {
        var options = CacheRetentionOptions();
        var current = options.FirstOrDefault(option => option.Days == plugin.Configuration.AutoClearCacheEntriesDays);
        if (current.Label == null)
            current = options[0];

        ImGui.TextUnformatted("Auto-clear old data entries");
        ImGui.SetNextItemWidth(220);
        using var combo = ImRaii.Combo("##lodestoneCacheRetention", current.Label);
        if (combo.Success)
        {
            foreach (var option in options)
            {
                var selected = option.Days == plugin.Configuration.AutoClearCacheEntriesDays;
                if (ImGui.Selectable(option.Label, selected))
                {
                    plugin.Configuration.AutoClearCacheEntriesDays = option.Days;
                    plugin.Configuration.Save();
                    _ = plugin.CalendarWindow.RefreshAsync(false);
                }

                if (selected)
                    ImGui.SetItemDefaultFocus();
            }
        }

        TextWrappedColored(Muted, plugin.Configuration.AutoClearCacheEntriesDays <= 0
            ? "Off. Cached Lodestone entries stay until you clear data cache or refresh overwrites them."
            : $"Entries whose end date is older than {current.Label.ToLowerInvariant()} are removed from the local data cache.");
    }

    private static (string Label, int Days)[] CacheRetentionOptions()
        =>
        [
            ("Off", 0),
            ("30 days", 30),
            ("2 months", 60),
            ("3 months", 90),
            ("4 months", 120),
            ("5 months", 150),
            ("6 months", 180)
        ];

    private void DrawScanDiagnostics()
    {
        DrawScanProgress();

        var diagnostics = plugin.LodestoneClient.LastDiagnostics;
        if (diagnostics.StartedAtUtc == default)
        {
            TextWrappedColored(Muted, diagnostics.Status);
            return;
        }

        TextWrappedColored(White(), diagnostics.Status);
        TextWrappedColored(Muted, $"Last scan: {diagnostics.StartedAtUtc.ToLocalTime():g}{(diagnostics.Duration.HasValue ? $" ({diagnostics.Duration.Value.TotalSeconds:0.0}s)" : string.Empty)}.");
        TextWrappedColored(Muted, $"Mode: {(diagnostics.Force ? "forced" : "automatic")} | fresh cache: {(diagnostics.UsedFreshCache ? "yes" : "no")}.");
        TextWrappedColored(White(), $"Sources: {diagnostics.SourceCount}, pages: {diagnostics.PagesFetched}, feed images: {diagnostics.FeedImages}.");
        TextWrappedColored(White(), $"Index entries: {diagnostics.IndexEntries}, filtered: {diagnostics.FilteredEntries}, enriched: {diagnostics.EnrichedEntries}, cache hits: {diagnostics.CacheHits}, pruned: {diagnostics.PrunedCacheEntries}.");
        TextWrappedColored(White(), $"External: {diagnostics.ExternalEntries} total, {diagnostics.DeveloperPostEntries} developer posts, {diagnostics.IcyVeinsEntries} Icy Veins articles, {diagnostics.IcyVeinsGuideEntries} Icy Veins guides.");
        TextWrappedColored(White(), $"Images kept: {diagnostics.ImageUrlsKept}, rejected: {diagnostics.ImageUrlsRejected}, errors: {diagnostics.Errors}.");

        if (!string.IsNullOrWhiteSpace(diagnostics.LastError))
            TextWrappedColored(new Vector4(1f, 0.72f, 0.72f, 1f), $"Last error: {diagnostics.LastError}");

        foreach (var source in diagnostics.SourceSummaries.Take(6))
            TextWrappedColored(Muted, source);
    }

    private void DrawScanProgress()
    {
        var progress = plugin.LodestoneClient.CurrentProgress;
        if (progress.StartedAtUtc == default && !progress.IsActive)
            return;

        var label = progress.IsActive
            ? progress.Total > 0
                ? $"{progress.Status} ({progress.Completed}/{progress.Total})"
                : progress.Status
            : $"Last scan: {progress.Status}";
        UiWidgets.ProgressBar(progress.Percent, label, indeterminate: progress.IsActive && progress.Total <= 0);
        if (!string.IsNullOrWhiteSpace(progress.Source))
        {
            UiWidgets.NeonBadge(progress.Source, new NeonPalette(
                UiWidgets.Color(0.04f, 0.18f, 0.34f, 0.94f),
                UiWidgets.Color(0.36f, 0.70f, 1f, 0.90f),
                UiWidgets.Color(0.72f, 0.88f, 1f, 1f)));
        }
    }

    private void DrawPrioritySlider(string label, int value, Action<int> setter)
    {
        var localValue = value;
        ImGui.TextUnformatted(label);
        ImGui.SameLine(130f * ImGuiHelpers.GlobalScale);
        ImGui.SetNextItemWidth(210f * ImGuiHelpers.GlobalScale);
        if (ImGui.SliderInt($"##priority{label}", ref localValue, 0, 120))
        {
            setter(localValue);
            plugin.Configuration.Save();
            plugin.ServerBar.Refresh();
        }
    }

    private void DrawPriorityRules()
    {
        var rules = plugin.Configuration.GetPriorityRules();
        foreach (var rule in rules)
        {
            using var id = ImRaii.PushId(rule.Id);
            var enabled = rule.Enabled;
            if (ImGui.Checkbox(rule.Label, ref enabled))
            {
                rule.Enabled = enabled;
                plugin.Configuration.Save();
            }

            if (!string.IsNullOrWhiteSpace(rule.Notes))
                TextWrappedColored(Muted, rule.Notes);

            if (rule.UseAbsolutePriority)
            {
                var absolute = rule.AbsolutePriority;
                ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt("absolute priority", ref absolute))
                {
                    rule.AbsolutePriority = absolute;
                    plugin.Configuration.Save();
                }
            }
            else if (rule.AnchorKind.HasValue)
            {
                var offset = rule.AnchorOffset;
                ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt($"offset from {rule.AnchorKind.Value}", ref offset))
                {
                    rule.AnchorOffset = offset;
                    plugin.Configuration.Save();
                }
            }
            else
            {
                var offset = rule.PriorityOffset;
                ImGui.SetNextItemWidth(120f * ImGuiHelpers.GlobalScale);
                if (ImGui.InputInt("priority bonus", ref offset))
                {
                    rule.PriorityOffset = offset;
                    plugin.Configuration.Save();
                }
            }

            if (!string.IsNullOrWhiteSpace(rule.HeroAsset))
                TextWrappedColored(Muted, $"Hero override: {rule.HeroAsset}");

            ImGui.Separator();
        }

        if (PanelButton("Restore priority rules##priorityRulesReset", PanelDanger, new Vector2(180, 0)))
        {
            plugin.Configuration.PriorityRules = [];
            plugin.Configuration.SetDefaultPriorities();
            plugin.Configuration.Save();
        }
    }

    private static void ContentBox(string id, uint backgroundColor, bool includeEndPadding, Action drawContent)
    {
        var draw = ImGui.GetWindowDrawList();
        var padding = new Vector2(8, 8);
        const float blockGap = 10f;
        var startCursor = ImGui.GetCursorPos();
        var startScreen = ImGui.GetCursorScreenPos();

        if (ContentBoxSizeCache.TryGetValue(id, out var cachedSize))
            draw.AddRectFilled(startScreen, startScreen + cachedSize, backgroundColor, 8f);

        ImGui.SetCursorPos(startCursor + padding);
        ImGui.BeginGroup();
        drawContent();
        ImGui.EndGroup();

        var contentSize = ImGui.GetItemRectSize();
        var boxSize = new Vector2(ImGui.GetWindowWidth(), contentSize.Y + padding.Y * 2);
        ContentBoxSizeCache[id] = boxSize;

        ImGui.SetCursorPosY(startCursor.Y + boxSize.Y + blockGap + (includeEndPadding ? padding.Y : 0));
    }

    private static bool PanelButton(string label, uint panelColor, Vector2 size)
    {
        var color = ImGui.ColorConvertU32ToFloat4(panelColor);
        using var button = ImRaii.PushColor(ImGuiCol.Button, ShadeColor(color, 0.58f));
        using var hovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, ShadeColor(color, 0.78f));
        using var active = ImRaii.PushColor(ImGuiCol.ButtonActive, ShadeColor(color, 0.46f));
        return ImGui.Button(label, size);
    }

    private static Vector4 ShadeColor(Vector4 color, float amount)
        => new(Math.Clamp(color.X * amount, 0f, 1f), Math.Clamp(color.Y * amount, 0f, 1f), Math.Clamp(color.Z * amount, 0f, 1f), color.W);

    private static void CenteredTitle(FontAwesomeIcon icon, string text, float scale = 1.12f)
    {
        ImGui.SetWindowFontScale(scale);
        var iconText = icon.ToIconString();
        var iconWidth = ImGui.CalcTextSize(iconText).X;
        var textWidth = ImGui.CalcTextSize(text).X;
        var spacing = ImGui.GetStyle().ItemInnerSpacing.X;
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), (ImGui.GetWindowWidth() - iconWidth - spacing - textWidth) * 0.5f));
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(White(), iconText);
        }

        ImGui.SameLine(0, spacing);
        ImGui.TextColored(White(), text);
        ImGui.SetWindowFontScale(1f);
    }

    private static void SectionHeader(FontAwesomeIcon icon, string text)
        => SectionHeader(icon, text, Warn);

    private static void SectionHeader(FontAwesomeIcon icon, string text, Vector4 color)
    {
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            ImGui.TextColored(color, icon.ToIconString());
        }

        ImGui.SameLine();
        ImGui.TextColored(color, text);
    }

    private static void TextCentered(string text)
    {
        var width = ImGui.CalcTextSize(text).X;
        ImGui.SetCursorPosX(MathF.Max(ImGui.GetCursorPosX(), (ImGui.GetWindowWidth() - width) * 0.5f));
        ImGui.TextColored(White(), text);
    }

    private static void TextWrappedColored(Vector4 color, string text)
    {
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static Vector4 White() => new(1f, 1f, 1f, 1f);

    private static string FormatRefreshInterval(int minutes)
    {
        if (minutes >= 1440 && minutes % 1440 == 0)
            return $"{minutes / 1440} day{(minutes == 1440 ? string.Empty : "s")}";

        if (minutes >= 60 && minutes % 60 == 0)
            return $"{minutes / 60} hour{(minutes == 60 ? string.Empty : "s")}";

        return $"{minutes} minutes";
    }

    private string FormatDateTime(DateTime value)
        => plugin.Configuration.UseTwelveHourNoteTimes
            ? value.ToString("M/d/yyyy h:mm tt")
            : value.ToString("M/d/yyyy HH:mm");

    private static string FormatBytes(long bytes)
    {
        if (bytes >= 1024 * 1024)
            return $"{bytes / 1024f / 1024f:0.0} MB";

        if (bytes >= 1024)
            return $"{bytes / 1024f:0.0} KB";

        return $"{bytes} B";
    }
}
