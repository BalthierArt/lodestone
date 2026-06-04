using Dalamud.Interface;
using Dalamud.Interface.Components;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Lodestone.Models;
using Lodestone.Services;

namespace Lodestone.Windows;

public sealed partial class CalendarWindow : Window
{
    private const long HeroCycleMilliseconds = 3_000;
    private const string DefaultMaintenanceHeroAsset = ImageCache.AssetScheme + "default-maintenance-hero.png";
    private const string DefaultNewsHeroAsset = ImageCache.AssetScheme + "default-news-hero.png";
    private const string NoteIconAsset = ImageCache.AssetScheme + "note-icon.png";
    private const string EventStartMarkerAsset = ImageCache.AssetScheme + "eventstart.png";
    private const string EventEndMarkerAsset = ImageCache.AssetScheme + "eventend.png";
    private const string SubmarineReturnMarkerAsset = ImageCache.AssetScheme + "subicon.png";
    private const string SubmarineReturnedHeroAsset = ImageCache.AssetScheme + "subreturned.png";
    private const string IcyVeinsArticleMarkerAsset = ImageCache.AssetScheme + "ivart.png";
    private const string LodestoneImageStopMarker = "https://lds-img.finalfantasyxiv.com/h/L/EbtcXqPUGzsVYdi23FpUR25oH4.png";
    private static readonly Vector4 DetailPrimary = new(0.42f, 0.22f, 0.78f, 0.92f);
    private static readonly Vector4 DetailAccent = new(0.64f, 0.38f, 1.00f, 1f);
    private static readonly Vector4 DetailMuted = new(0.68f, 0.66f, 0.78f, 1f);
    private static readonly Vector4 DetailGreen = new(0.62f, 0.90f, 0.42f, 1f);
    private static readonly Vector4 DetailOrange = new(1f, 0.58f, 0.25f, 1f);
    private static readonly Vector4 DetailBlue = new(0.42f, 0.72f, 1f, 1f);
    private static readonly uint DetailPanel = ImGui.ColorConvertFloat4ToU32(new Vector4(0.12f, 0.12f, 0.16f, 0.96f));
    private static readonly uint DetailPanelElevated = ImGui.ColorConvertFloat4ToU32(new Vector4(0.18f, 0.19f, 0.22f, 0.96f));
    private static readonly uint DetailPanelPurple = ImGui.ColorConvertFloat4ToU32(DetailPrimary);
    private static readonly string[] DayNames = ["Sun", "Mon", "Tue", "Wed", "Thu", "Fri", "Sat"];
    private static readonly string[] FullDayNames = ["Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday"];
    private static readonly string[] MonthNames = ["January", "February", "March", "April", "May", "June", "July", "August", "September", "October", "November", "December"];

    private readonly Plugin plugin;
    private readonly object entryLock = new();
    private IReadOnlyList<LodestoneEntry> entries = [];
    private DateTime visibleMonth = new(DateTime.Now.Year, DateTime.Now.Month, 1);
    private LodestoneEntry? selectedEntry;
    private bool detailTextCopyMode;
    private string detailTextCopyEntryId = string.Empty;
    private string detailTextCopyBuffer = string.Empty;
    private DateTime? selectedDay;
    private string searchText = string.Empty;
    private bool noteEditorOpen;
    private CalendarNote? editingNote;
    private DateTime noteEditorDate = DateTime.Today;
    private string noteEditorText = string.Empty;
    private bool noteEditorHasTime;
    private int noteEditorHour = 21;
    private int noteEditorMinute;
    private bool noteEditorIsPm = true;
    private bool noteEditorAlarmEnabled = true;
    private bool showAgenda;
    private bool refreshInProgress;
    private string status = "Loading Lodestone...";
    private DayHoverOverlay? dayHoverOverlay;
    private Vector2 calendarWindowPos;
    private Vector2 calendarWindowSize;
    private bool dayWindowNeedsPlacement;
    private bool detailWindowNeedsPlacement;
    private bool noteWindowNeedsPlacement;
    private DayHoverOverlay? activeDayHoverOverlay;
    private DayHeroPreview? activeDayHeroPreview;
    private readonly Dictionary<string, int> displayPriorityCache = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> heroImageUrlCache = new(StringComparer.Ordinal);
    private CalendarDayCache? calendarDayCache;
    private AgendaCache? agendaCache;
    private int cachedPrioritySignature;
    private int entriesVersion;

    public CalendarWindow(Plugin plugin) : base("Lodestone Calendar##LodestoneCalendar")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(900, 650),
            MaximumSize = new Vector2(1800, 1400)
        };
        TitleBarButtons.Add(new TitleBarButton
        {
            Icon = FontAwesomeIcon.Cog,
            Click = _ => plugin.OpenConfig()
        });
    }

    public async Task RefreshAsync(bool force)
    {
        if (refreshInProgress)
            return;

        refreshInProgress = true;
        status = force ? "Refreshing Lodestone..." : "Loading Lodestone...";
        try
        {
            var loaded = await plugin.LodestoneClient.RefreshAsync(plugin.Configuration, force);
            lock (entryLock)
            {
                entries = loaded;
                entriesVersion++;
            }

            status = $"{loaded.Count} Lodestone entries loaded.";
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Lodestone refresh failed.");
            var cached = await plugin.LodestoneClient.LoadCachedAsync(plugin.Configuration);
            lock (entryLock)
            {
                entries = cached;
                entriesVersion++;
            }

            status = cached.Count > 0
                ? $"Refresh failed. Showing {cached.Count} cached entries."
                : "Refresh failed and no cache is available.";
        }
        finally
        {
            refreshInProgress = false;
        }
    }

    public override void Draw()
    {
        calendarWindowPos = ImGui.GetWindowPos();
        calendarWindowSize = ImGui.GetWindowSize();
        dayHoverOverlay = null;
        RefreshPerDataCaches();
        DrawToolbar();
        ImGui.Separator();
        if (showAgenda)
            DrawAgenda();
        else
            DrawCalendar();

        DrawFilterBar();
        DrawPendingDayHoverOverlay();
        DrawDayWindow();
        DrawNoteEditorWindow();
        DrawPartyPlannerWindow();
        DrawPartyEventWindow();
        DrawSubmarineReturnWindow();
        DrawDetailWindow();
    }

    private void DrawToolbar()
    {
        if (ImGuiComponents.IconButton(FontAwesomeIcon.ChevronLeft))
            visibleMonth = visibleMonth.AddMonths(-1);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Previous month");

        ImGui.SameLine();
        UiWidgets.NeonBadge($"{MonthNames[visibleMonth.Month - 1]} {visibleMonth.Year}", UiWidgets.SeasonPalette(visibleMonth.Month));
        ImGui.SameLine();

        if (ImGuiComponents.IconButton(FontAwesomeIcon.ChevronRight))
            visibleMonth = visibleMonth.AddMonths(1);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Next month");

        ImGui.SameLine();
        if (ImGuiComponents.IconButton(FontAwesomeIcon.CalendarDay))
            visibleMonth = new DateTime(DateTime.Now.Year, DateTime.Now.Month, 1);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Current month");

        ImGui.SameLine();
        using (ImRaii.Disabled(refreshInProgress))
        {
            if (ImGuiComponents.IconButton(FontAwesomeIcon.Sync))
                _ = RefreshAsync(true);
        }
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Refresh Lodestone");

        ImGui.SameLine();
        ImGui.TextUnformatted(status);

        ImGui.SameLine();
        ImGui.SetNextItemWidth(220f * ImGuiHelpers.GlobalScale);
        ImGui.InputTextWithHint("##lodestoneSearch", "Search...", ref searchText, 80);
        ImGui.SameLine();
        ImGui.Checkbox("Agenda##lodestoneAgendaToggle", ref showAgenda);

        var progress = plugin.LodestoneClient.CurrentProgress;
        if (refreshInProgress || progress.IsActive)
        {
            ImGui.Spacing();
            var label = progress.Total > 0
                ? $"{progress.Status} ({progress.Completed}/{progress.Total})"
                : progress.Status;
            UiWidgets.ProgressBar(progress.Percent, label, indeterminate: progress.Total <= 0);
        }
    }

    private void DrawCalendar()
    {
        var available = ImGui.GetContentRegionAvail();
        var cellWidth = Math.Max(110f * ImGuiHelpers.GlobalScale, available.X / 7f - ImGui.GetStyle().ItemSpacing.X);
        var cellHeight = Math.Max(92f * ImGuiHelpers.GlobalScale, (available.Y - 42f * ImGuiHelpers.GlobalScale) / 6f - ImGui.GetStyle().ItemSpacing.Y);
        var cellSize = new Vector2(cellWidth, cellHeight);

        var dayNames = plugin.Configuration.UseFullDayNames ? FullDayNames : DayNames;
        using (ImRaii.PushColor(ImGuiCol.Text, plugin.Configuration.DayOfWeekColor()))
        {
            for (var i = 0; i < dayNames.Length; i++)
            {
                ImGui.TextUnformatted(dayNames[i]);
                if (i < dayNames.Length - 1)
                    ImGui.SameLine((cellWidth + ImGui.GetStyle().ItemSpacing.X) * (i + 1));
            }
        }

        var first = new DateTime(visibleMonth.Year, visibleMonth.Month, 1);
        var start = first.AddDays(-(int)first.DayOfWeek);
        var end = start.AddDays(41);
        plugin.PartySyncService.SetVisibleRange(start, end);
        var dayData = GetCalendarDayData(start, end);
        for (var row = 0; row < 6; row++)
        {
            for (var col = 0; col < 7; col++)
            {
                var day = start.AddDays(row * 7 + col);
                DrawDayCell(day, cellSize, dayData.TryGetValue(day.Date, out var data) ? data : CalendarDayData.Empty);
                if (col < 6)
                    ImGui.SameLine();
            }
        }
    }

    private Dictionary<DateTime, CalendarDayData> BuildCalendarDayData(DateTime start, DateTime end)
    {
        var buckets = new Dictionary<DateTime, CalendarDayBucket>();
        for (var day = start.Date; day <= end.Date; day = day.AddDays(1))
            buckets[day] = new CalendarDayBucket();

        foreach (var entry in GetVisibleEntriesUnsorted())
        {
            var from = entry.StartsAt.Date > start.Date ? entry.StartsAt.Date : start.Date;
            var to = entry.EffectiveEnd.Date < end.Date ? entry.EffectiveEnd.Date : end.Date;
            for (var day = from; day <= to; day = day.AddDays(1))
            {
                if (buckets.TryGetValue(day, out var bucket))
                    bucket.Entries.Add(entry);
            }
        }

        foreach (var note in plugin.Configuration.Notes)
        {
            if (buckets.TryGetValue(note.Date.Date, out var bucket))
                bucket.Notes.Add(note);
        }

        if (plugin.Configuration.ShowPartyEvents)
        {
            foreach (var partyEvent in plugin.PartySyncService.Events)
            {
                if (buckets.TryGetValue(partyEvent.Date.Date, out var bucket))
                    bucket.PartyEvents.Add(partyEvent);
            }
        }

        if (plugin.Configuration.ShowSubmarineReturns)
        {
            foreach (var submarineReturn in plugin.SubmarineService.Returns)
            {
                if (buckets.TryGetValue(submarineReturn.ReturnAt.Date, out var bucket))
                    bucket.SubmarineReturns.Add(submarineReturn);
            }
        }

        return buckets.ToDictionary(pair => pair.Key, pair =>
        {
            var day = pair.Key;
            var sortedEntries = pair.Value.Entries
                .OrderByDescending(entry => DayDisplayPriority(entry, day))
                .ThenBy(IsMultiDay)
                .ThenBy(entry => entry.StartsAt)
                .ToArray();
            var sortedNotes = pair.Value.Notes
                .OrderBy(note => note.ScheduledAt ?? note.Date)
                .ThenBy(note => note.Text)
                .ToArray();
            var sortedPartyEvents = pair.Value.PartyEvents
                .OrderBy(partyEvent => partyEvent.ScheduledAt ?? partyEvent.Date)
                .ThenBy(partyEvent => partyEvent.Title)
                .ToArray();
            var sortedSubmarineReturns = pair.Value.SubmarineReturns
                .OrderBy(submarineReturn => submarineReturn.ReturnAt)
                .ThenBy(submarineReturn => submarineReturn.VesselName)
                .ToArray();
            var heroCandidates = GetHeroImageCandidates(sortedEntries).ToArray();
            if (sortedSubmarineReturns.Length > 0)
                heroCandidates = [SubmarineReturnedHeroAsset, .. heroCandidates];
            var cornerKinds = sortedEntries
                .Select(entry => entry.Kind)
                .Distinct()
                .OrderByDescending(KindDisplayPriority)
                .Take(4)
                .ToArray();

            return new CalendarDayData(
                sortedEntries,
                sortedNotes,
                sortedPartyEvents,
                sortedSubmarineReturns,
                sortedSubmarineReturns.Length > 0 ? SubmarineReturnedHeroAsset : SelectHeroImageUrl(sortedEntries),
                heroCandidates,
                cornerKinds,
                sortedEntries.Any(entry => entry.Kind == LodestoneEntryKind.SpecialEvent && entry.IsMultiDay),
                sortedEntries.Any(entry => IsEventStartDay(entry, day)),
                sortedEntries.Any(entry => IsEventEndDay(entry, day)),
                sortedSubmarineReturns.Length > 0);
        });
    }

    private Dictionary<DateTime, CalendarDayData> GetCalendarDayData(DateTime start, DateTime end)
    {
        var key = BuildCalendarDayCacheKey(start, end);
        if (calendarDayCache is { } cache && cache.Key.Equals(key))
            return cache.Data;

        var data = BuildCalendarDayData(start, end);
        calendarDayCache = new CalendarDayCache(key, data);
        return data;
    }

    private CalendarDayCacheKey BuildCalendarDayCacheKey(DateTime start, DateTime end)
        => new(
            start.Date,
            end.Date,
            entriesVersion,
            plugin.PartySyncService.EventsVersion,
            searchText,
            plugin.Configuration.ShowEvents,
            plugin.Configuration.ShowTopics,
            plugin.Configuration.ShowNotices,
            plugin.Configuration.ShowMaintenance,
            plugin.Configuration.ShowUpdates,
            plugin.Configuration.ShowStatus,
            plugin.Configuration.ShowRecovery,
            plugin.Configuration.ShowDeveloperPosts,
            plugin.Configuration.ShowIcyVeins,
            plugin.Configuration.ShowIcyVeinsGuides,
            plugin.Configuration.ShowPartyEvents,
            plugin.Configuration.ShowSubmarineReturns,
            plugin.SubmarineService.Version,
            plugin.Configuration.OnlyCurrentAndFuture ? DateTime.Now.Date : DateTime.MinValue,
            HiddenEntrySignature(),
            NoteSignature(),
            PrioritySignature());

    private void RefreshPerDataCaches()
    {
        var signature = HashCode.Combine(entriesVersion, HiddenEntrySignature(), PrioritySignature());
        if (signature == cachedPrioritySignature)
            return;

        displayPriorityCache.Clear();
        heroImageUrlCache.Clear();
        cachedPrioritySignature = signature;
    }

    private int HiddenEntrySignature()
    {
        var hash = new HashCode();
        hash.Add(plugin.Configuration.HiddenEntryIds.Count);
        foreach (var id in plugin.Configuration.HiddenEntryIds)
            hash.Add(id, StringComparer.Ordinal);

        return hash.ToHashCode();
    }

    private int NoteSignature()
    {
        var hash = new HashCode();
        hash.Add(plugin.Configuration.Notes.Count);
        foreach (var note in plugin.Configuration.Notes)
        {
            hash.Add(note.Id, StringComparer.Ordinal);
            hash.Add(note.Date);
            hash.Add(note.Text, StringComparer.Ordinal);
            hash.Add(note.Hour);
            hash.Add(note.Minute);
            hash.Add(note.AlarmEnabled);
        }

        return hash.ToHashCode();
    }

    private int PrioritySignature()
    {
        var hash = new HashCode();
        hash.Add(plugin.Configuration.PriorityUpdates);
        hash.Add(plugin.Configuration.PriorityTopics);
        hash.Add(plugin.Configuration.PriorityMaintenance);
        hash.Add(plugin.Configuration.PriorityRecovery);
        hash.Add(plugin.Configuration.PriorityStatus);
        hash.Add(plugin.Configuration.PriorityNotices);
        hash.Add(plugin.Configuration.PriorityEvents);
        hash.Add(plugin.Configuration.PriorityDeveloperPosts);
        hash.Add(plugin.Configuration.PriorityIcyVeins);

        foreach (var rule in plugin.Configuration.GetPriorityRules())
        {
            hash.Add(rule.Id, StringComparer.Ordinal);
            hash.Add(rule.Enabled);
            hash.Add(rule.Kind);
            hash.Add(rule.PriorityOffset);
            hash.Add(rule.UseAbsolutePriority);
            hash.Add(rule.AbsolutePriority);
            hash.Add(rule.AnchorKind);
            hash.Add(rule.AnchorOffset);
            hash.Add(rule.HeroAsset, StringComparer.Ordinal);
            AddStrings(rule.AnyTextContains, ref hash);
            AddStrings(rule.AllTextContains, ref hash);
        }

        return hash.ToHashCode();
    }

    private static void AddStrings(IEnumerable<string> values, ref HashCode hash)
    {
        foreach (var value in values)
            hash.Add(value, StringComparer.OrdinalIgnoreCase);
    }

    private void DrawDayCell(DateTime day, Vector2 size, CalendarDayData data)
    {
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();
        var dayEntries = data.Entries;
        var dayNotes = data.Notes;
        var partyEvents = data.PartyEvents;
        var submarineReturns = data.SubmarineReturns;
        var currentMonth = day.Month == visibleMonth.Month;
        var today = day.Date == DateTime.Now.Date;
        var overlayHoveredForAnyDay = IsAnyActiveDayHoverOverlayHovered();
        var overlayHovered = IsActiveDayHoverOverlayHovered(day.Date);
        var dayHovered = ImGui.IsMouseHoveringRect(min, max, true) && (!overlayHoveredForAnyDay || overlayHovered);

        var bg = WithAlpha(plugin.Configuration.DayColor(), currentMonth ? plugin.Configuration.DayColor().W : plugin.Configuration.DayColor().W * 0.45f);
        drawList.AddRectFilled(min, max, bg, 3f);

        var heroImageUrl = SelectHeroImageUrl(day.Date, data, dayHovered || overlayHovered);
        if (!string.IsNullOrEmpty(heroImageUrl) && plugin.Configuration.ShowDayImages)
        {
            var texture = plugin.ImageCache.GetTexture(heroImageUrl);
            if (texture != null)
            {
                var imageAlpha = DayImageAlpha(today, currentMonth);
                ImGui.GetWindowDrawList().AddImage(texture.Handle, min, max, Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, imageAlpha));
                if (IsIcyVeinsArticleHeroActive(heroImageUrl, data))
                    DrawIcyVeinsArticleMarker(min, max, currentMonth);
            }

            var filmAlpha = DayFilmAlpha(today, currentMonth);
            if (filmAlpha > 0f)
                drawList.AddRectFilled(min, max, Color(0f, 0f, 0f, filmAlpha), 3f);
        }

        if (today)
            drawList.AddRect(min + Vector2.One, max - Vector2.One, ToColor(plugin.Configuration.CurrentDayHighlightColor()), 3f, 0, 2.5f);
        else
        {
            var highlight = plugin.Configuration.DayHighlightColor();
            drawList.AddRect(min, max, WithAlpha(highlight, currentMonth ? highlight.W : highlight.W * 0.36f), 3f);
        }

        if (data.HasMultiDayEvent)
        {
            var ribbonMin = new Vector2(min.X + 2f * ImGuiHelpers.GlobalScale, max.Y - 5f * ImGuiHelpers.GlobalScale);
            var ribbonMax = new Vector2(max.X - 2f * ImGuiHelpers.GlobalScale, max.Y - 2f * ImGuiHelpers.GlobalScale);
            drawList.AddRectFilled(ribbonMin, ribbonMax, KindColor(LodestoneEntryKind.SpecialEvent), 1f);
        }

        var submarineHeroShown = plugin.Configuration.ShowDayImages && string.Equals(heroImageUrl, SubmarineReturnedHeroAsset, StringComparison.OrdinalIgnoreCase);
        DrawSubmarineReturnMarker(data.HasSubmarineReturn && !submarineHeroShown, data.HasEventStart || data.HasEventEnd, min, max, currentMonth);

        ImGui.SetCursorScreenPos(min + new Vector2(6f, 4f) * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(day.Day.ToString());

        DrawDayCornerIcons(data.CornerKinds, dayNotes.Length, data.HasSubmarineReturn, min, max, currentMonth);
        DrawPartyEventCenterIcon(partyEvents, min, max, currentMonth);
        DrawEventBoundaryMarkers(data.HasEventStart, data.HasEventEnd, min, max, currentMonth);

        var deferHoverText = plugin.Configuration.ShowCalendarTextOnHoverOnly && (dayHovered || overlayHovered);
        if (deferHoverText)
            dayHoverOverlay = new DayHoverOverlay(day.Date, min, max, dayNotes, partyEvents, submarineReturns, dayEntries, currentMonth);

        var drawEntryText = !plugin.Configuration.ShowCalendarTextOnHoverOnly;
        if (drawEntryText)
        {
            var y = min.Y + 26f * ImGuiHelpers.GlobalScale;
            var rowsUsed = 0;
            foreach (var note in dayNotes.Take(2))
            {
                DrawNoteChip(note, min, max, y);
                y += 21f * ImGuiHelpers.GlobalScale;
                rowsUsed++;
            }

            foreach (var partyEvent in partyEvents.Take(Math.Max(0, 3 - rowsUsed)))
            {
                DrawPartyEventChip(partyEvent, min, max, y);
                y += 21f * ImGuiHelpers.GlobalScale;
                rowsUsed++;
            }

            foreach (var submarineReturn in submarineReturns.Take(Math.Max(0, 3 - rowsUsed)))
            {
                DrawSubmarineReturnChip(submarineReturn, min, max, y);
                y += 21f * ImGuiHelpers.GlobalScale;
                rowsUsed++;
            }

            foreach (var entry in dayEntries.Take(Math.Max(0, 3 - rowsUsed)))
            {
                var chipMin = new Vector2(min.X + 5f * ImGuiHelpers.GlobalScale, y);
                var chipMax = new Vector2(max.X - 5f * ImGuiHelpers.GlobalScale, y + 19f * ImGuiHelpers.GlobalScale);
                drawList.AddRectFilled(chipMin, chipMax, KindColor(entry.Kind), 2f);
                ImGui.SetCursorScreenPos(chipMin + new Vector2(4f, 1f) * ImGuiHelpers.GlobalScale);
                using (ImRaii.PushFont(UiBuilder.IconFont))
                    ImGui.TextUnformatted(KindIcon(entry.Kind).ToIconString());
                ImGui.SameLine(0, 3f * ImGuiHelpers.GlobalScale);
                ImGui.PushTextWrapPos(chipMax.X - 4f * ImGuiHelpers.GlobalScale);
                ImGui.TextUnformatted(entry.Title);
                ImGui.PopTextWrapPos();
                y += 21f * ImGuiHelpers.GlobalScale;
                rowsUsed++;
            }

            var hiddenCount = dayEntries.Length + dayNotes.Length + partyEvents.Length + submarineReturns.Length - rowsUsed;
            if (hiddenCount > 0)
            {
                var text = $"+{hiddenCount}";
                var textSize = ImGui.CalcTextSize(text);
                drawList.AddText(new Vector2(max.X - textSize.X - 6f * ImGuiHelpers.GlobalScale, max.Y - textSize.Y - 4f * ImGuiHelpers.GlobalScale), Color(1f, 1f, 1f, currentMonth ? 0.95f : 0.45f), text);
            }
        }

        ImGui.SetCursorScreenPos(min);
        using (ImRaii.PushId(day.ToString("yyyyMMdd")))
        {
            var blockDayClick = IsAnyActiveDayHoverOverlayHovered();
            if (!blockDayClick && ImGui.InvisibleButton("day", size) && dayEntries.Length + dayNotes.Length + partyEvents.Length + submarineReturns.Length > 0)
                ToggleDaySelection(day.Date, dayEntries, dayNotes, partyEvents, submarineReturns);
            else if (blockDayClick)
                ImGui.Dummy(size);

            if (ImGui.BeginPopupContextItem("dayContext"))
            {
                if (ImGui.MenuItem("Add note"))
                    OpenNoteEditor(day.Date, null);
                if (ImGui.MenuItem("Plan Party Event"))
                    OpenPartyPlanner(day.Date, null);

                ImGui.EndPopup();
            }
        }

        if (ImGui.IsItemHovered() && dayEntries.Length + dayNotes.Length + partyEvents.Length + submarineReturns.Length > 0)
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            var tooltipLines = dayNotes.Select(n => $"Note: {NoteLabel(n)}")
                .Concat(partyEvents.Select(e => $"Party: {PartyEventLabel(e)}"))
                .Concat(submarineReturns.Select(e => $"Submarine: {SubmarineReturnLabel(e)}"))
                .Concat(dayEntries.Select(e => $"{KindLabel(e.Kind)}: {e.Title}"));
            ImGui.SetTooltip(string.Join("\n", tooltipLines));
        }
    }

    private void ToggleDaySelection(DateTime day, LodestoneEntry[] dayEntries, CalendarNote[] dayNotes, PartyEvent[] partyEvents, SubmarineReturn[] submarineReturns)
    {
        if (selectedDay?.Date == day.Date)
        {
            selectedDay = null;
            return;
        }

        if (selectedEntry != null && EntryCoversDay(selectedEntry, day))
        {
            selectedEntry = null;
            return;
        }

        if (selectedPartyEvent != null && selectedPartyEvent.Date.Date == day.Date)
        {
            selectedPartyEvent = null;
            return;
        }

        if (selectedSubmarineReturn != null && selectedSubmarineReturn.ReturnAt.Date == day.Date)
        {
            selectedSubmarineReturn = null;
            return;
        }

        if (dayEntries.Length == 1 && dayNotes.Length == 0 && partyEvents.Length == 0 && submarineReturns.Length == 0)
        {
            selectedDay = null;
            SelectEntry(dayEntries[0]);
            return;
        }

        if (partyEvents.Length == 1 && dayEntries.Length == 0 && dayNotes.Length == 0 && submarineReturns.Length == 0)
        {
            selectedDay = null;
            SelectPartyEvent(partyEvents[0]);
            return;
        }

        if (submarineReturns.Length == 1 && dayEntries.Length == 0 && dayNotes.Length == 0 && partyEvents.Length == 0)
        {
            selectedDay = null;
            SelectSubmarineReturn(submarineReturns[0]);
            return;
        }

        selectedEntry = null;
        selectedPartyEvent = null;
        selectedSubmarineReturn = null;
        selectedDay = day.Date;
        dayWindowNeedsPlacement = true;
    }

    private static bool EntryCoversDay(LodestoneEntry entry, DateTime day)
        => entry.StartsAt.Date <= day.Date && entry.EffectiveEnd.Date >= day.Date;

    private void DrawPendingDayHoverOverlay()
    {
        if (dayHoverOverlay == null)
        {
            activeDayHoverOverlay = null;
            activeDayHeroPreview = null;
            return;
        }

        var overlay = dayHoverOverlay;
        if (overlay.Entries.Length + overlay.Notes.Length + overlay.PartyEvents.Length + overlay.SubmarineReturns.Length == 0)
        {
            activeDayHoverOverlay = null;
            activeDayHeroPreview = null;
            return;
        }

        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var rows = overlay.Notes
            .Take(2)
            .Select(note => new DayHoverRow(NoteLabel(note), Color(0.18f, 0.30f, 0.42f, 0.96f), null, null))
            .Concat(overlay.PartyEvents.Select(partyEvent => new DayHoverRow($"Party: {PartyEventLabel(partyEvent)}", PartyEventColor(), null, null)))
            .Concat(overlay.SubmarineReturns.Select(submarineReturn => new DayHoverRow($"Submarine: {SubmarineReturnLabel(submarineReturn)}", SubmarineReturnColor(), null, submarineReturn)))
            .Concat(overlay.Entries.Select(entry => new DayHoverRow($"{KindLabel(entry.Kind)}: {entry.Title}", KindColor(entry.Kind), entry, null)))
            .Take(4)
            .ToList();

        var hiddenCount = overlay.Entries.Length + overlay.Notes.Length + overlay.PartyEvents.Length + overlay.SubmarineReturns.Length - rows.Count;
        if (hiddenCount > 0)
            rows.Add(new DayHoverRow($"+{hiddenCount} more", Color(0f, 0f, 0f, 0.88f), null, null));

        var contentMinX = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMin().X + 4f * scale;
        var contentMaxX = ImGui.GetWindowPos().X + ImGui.GetWindowContentRegionMax().X - 4f * scale;
        var rowHeight = 21f * scale;
        var padding = 6f * scale;
        var desiredWidth = rows
            .Select(row => ImGui.CalcTextSize(row.Text).X + padding * 2f)
            .DefaultIfEmpty(overlay.Max.X - overlay.Min.X)
            .Max();
        desiredWidth = Math.Clamp(desiredWidth, overlay.Max.X - overlay.Min.X - 10f * scale, 520f * scale);

        var x = overlay.Min.X + 5f * scale;
        if (x + desiredWidth > contentMaxX)
            x = contentMaxX - desiredWidth;
        x = Math.Max(contentMinX, x);

        var y = overlay.Min.Y + 26f * scale;
        var popupMin = new Vector2(x, y);
        var popupMax = new Vector2(x + desiredWidth, y + rows.Count * rowHeight);
        DayHeroPreview? hoveredPreview = null;
        var totalItems = overlay.Entries.Length + overlay.Notes.Length + overlay.PartyEvents.Length + overlay.SubmarineReturns.Length;
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var chipMin = new Vector2(x, y + i * rowHeight);
            var chipMax = new Vector2(x + desiredWidth, chipMin.Y + 19f * scale);
            var hovered = ImGui.IsMouseHoveringRect(chipMin, chipMax, true);
            drawList.AddRectFilled(chipMin, chipMax, row.Background, 2f);
            drawList.AddRect(chipMin, chipMax, Color(1f, 1f, 1f, hovered ? 0.68f : overlay.CurrentMonth ? 0.20f : 0.10f), 2f, 0, hovered ? 1.5f * scale : 1f);

            var text = FitTextToWidth(row.Text, desiredWidth - padding * 2f);
            drawList.AddText(chipMin + new Vector2(padding, 2f * scale), Color(1f, 1f, 1f, overlay.CurrentMonth ? 1f : 0.72f), text);

            if (!hovered)
                continue;

            if (row.Entry != null)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                var heroImageUrl = ResolveHeroImageUrl(row.Entry);
                if (!string.IsNullOrWhiteSpace(heroImageUrl))
                    hoveredPreview = new DayHeroPreview(overlay.Day, heroImageUrl);

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    if (plugin.Configuration.HoverTextClickOpensEntry || totalItems == 1)
                        SelectEntry(row.Entry);
                    else
                        ToggleDaySelection(overlay.Day, overlay.Entries, overlay.Notes, overlay.PartyEvents, overlay.SubmarineReturns);
                }
            }
            else if (row.SubmarineReturn != null)
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                hoveredPreview = new DayHeroPreview(overlay.Day, SubmarineReturnedHeroAsset);

                if (ImGui.IsMouseClicked(ImGuiMouseButton.Left))
                {
                    if (plugin.Configuration.HoverTextClickOpensEntry || totalItems == 1)
                        SelectSubmarineReturn(row.SubmarineReturn);
                    else
                        ToggleDaySelection(overlay.Day, overlay.Entries, overlay.Notes, overlay.PartyEvents, overlay.SubmarineReturns);
                }
            }
        }

        activeDayHoverOverlay = overlay with
        {
            PopupMin = popupMin,
            PopupMax = popupMax,
            HasPopupBounds = true
        };
        activeDayHeroPreview = hoveredPreview;
    }

    private static string FitTextToWidth(string text, float width)
    {
        if (ImGui.CalcTextSize(text).X <= width)
            return text;

        const string suffix = "...";
        var low = 0;
        var high = text.Length;
        while (low < high)
        {
            var mid = (low + high + 1) / 2;
            var candidate = text[..mid] + suffix;
            if (ImGui.CalcTextSize(candidate).X <= width)
                low = mid;
            else
                high = mid - 1;
        }

        return low <= 0 ? suffix : text[..low] + suffix;
    }

    private void DrawNoteChip(CalendarNote note, Vector2 min, Vector2 max, float y)
    {
        var drawList = ImGui.GetWindowDrawList();
        var chipMin = new Vector2(min.X + 5f * ImGuiHelpers.GlobalScale, y);
        var chipMax = new Vector2(max.X - 5f * ImGuiHelpers.GlobalScale, y + 19f * ImGuiHelpers.GlobalScale);
        drawList.AddRectFilled(chipMin, chipMax, Color(0.18f, 0.30f, 0.42f, 0.90f), 2f);
        ImGui.SetCursorScreenPos(chipMin + new Vector2(4f, 1f) * ImGuiHelpers.GlobalScale);
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextUnformatted(FontAwesomeIcon.StickyNote.ToIconString());
        ImGui.SameLine(0, 3f * ImGuiHelpers.GlobalScale);
        ImGui.PushTextWrapPos(chipMax.X - 4f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(NoteLabel(note));
        ImGui.PopTextWrapPos();
    }

    private void DrawEventBoundaryMarkers(bool hasStart, bool hasEnd, Vector2 min, Vector2 max, bool currentMonth)
    {
        if (!hasStart && !hasEnd)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var drawList = ImGui.GetWindowDrawList();
        var count = hasStart && hasEnd ? 2 : 1;
        var gap = 6f * scale;
        var startSize = new Vector2(80f, 80f) * scale;
        var endSize = new Vector2(96f, 60f) * scale;
        var totalWidth = (hasStart ? startSize.X : 0f) + (hasEnd ? endSize.X : 0f) + gap * (count - 1);
        var center = (min + max) * 0.5f;
        var x = center.X - totalWidth * 0.5f;

        if (hasStart)
        {
            DrawEventBoundaryMarker(EventStartMarkerAsset, "START", FontAwesomeIcon.PlayCircle, new Vector2(x, center.Y - startSize.Y * 0.5f), startSize, currentMonth);
            x += startSize.X + gap;
        }

        if (hasEnd)
            DrawEventBoundaryMarker(EventEndMarkerAsset, "END", FontAwesomeIcon.FlagCheckered, new Vector2(x, center.Y - endSize.Y * 0.5f), endSize, currentMonth);
    }

    private void DrawEventBoundaryMarker(string asset, string fallbackText, FontAwesomeIcon fallbackIcon, Vector2 min, Vector2 size, bool currentMonth)
    {
        var max = min + size;
        var drawList = ImGui.GetWindowDrawList();

        var texture = plugin.ImageCache.GetTexture(asset);
        if (texture != null)
        {
            drawList.AddImage(texture.Handle, min, max, Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, currentMonth ? 1f : 0.72f));
            return;
        }

        Vector2 iconSize;
        var icon = fallbackIcon.ToIconString();
        using (ImRaii.PushFont(UiBuilder.IconFont))
        {
            iconSize = ImGui.CalcTextSize(icon);
            drawList.AddText(min + new Vector2(4f * ImGuiHelpers.GlobalScale, (size.Y - iconSize.Y) * 0.5f), Color(1f, 0.73f, 0.24f, currentMonth ? 1f : 0.72f), icon);
        }

        var text = FitTextToWidth(fallbackText, size.X - iconSize.X - 10f * ImGuiHelpers.GlobalScale);
        drawList.AddText(min + new Vector2(iconSize.X + 7f * ImGuiHelpers.GlobalScale, (size.Y - ImGui.GetTextLineHeight()) * 0.5f), Color(1f, 0.82f, 0.42f, currentMonth ? 1f : 0.72f), text);
    }

    private void DrawIcyVeinsArticleMarker(Vector2 min, Vector2 max, bool currentMonth)
    {
        var texture = plugin.ImageCache.GetTexture(IcyVeinsArticleMarkerAsset);
        if (texture == null)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var cellWidth = max.X - min.X;
        var maxWidth = Math.Max(24f * scale, Math.Min(cellWidth - 12f * scale, 115f * scale));
        var minWidth = Math.Min(76f * scale, maxWidth);
        var width = Math.Clamp(cellWidth * 0.62f, minWidth, maxWidth);
        var height = width * 0.34f;
        var markerMin = new Vector2(min.X + 6f * scale, max.Y - height - 7f * scale);
        var markerMax = markerMin + new Vector2(width, height);
        ImGui.GetWindowDrawList().AddImage(texture.Handle, markerMin, markerMax, Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, currentMonth ? 1f : 0.72f));
    }

    private void DrawDayCornerIcons(IReadOnlyList<LodestoneEntryKind> kinds, int noteCount, bool hasSubmarineReturn, Vector2 min, Vector2 max, bool currentMonth)
    {
        if (kinds.Count == 0 && noteCount == 0 && !hasSubmarineReturn)
            return;

        var drawList = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var padding = 5f * scale;
        var slot = 18f * scale;
        var gap = 3f * scale;
        var x = max.X - padding - slot;
        var y = min.Y + padding;

        if (hasSubmarineReturn)
        {
            DrawSubmarineCornerIcon(new Vector2(x, y), slot, currentMonth);
            x -= slot + gap;
        }

        foreach (var kind in kinds)
        {
            var badgeMin = new Vector2(x, y);
            var badgeMax = badgeMin + new Vector2(slot, slot);
            drawList.AddRectFilled(badgeMin, badgeMax, Color(0f, 0f, 0f, currentMonth ? 0.68f : 0.46f), slot * 0.45f);

            var asset = CornerIconAsset(kind);
            var texture = string.IsNullOrEmpty(asset) ? null : plugin.ImageCache.GetTexture(asset);
            if (texture != null)
            {
                var inset = 2f * scale;
                drawList.AddImage(texture.Handle, badgeMin + new Vector2(inset), badgeMax - new Vector2(inset), Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, currentMonth ? 1f : 0.72f));
            }
            else
            {
                using var font = ImRaii.PushFont(UiBuilder.IconFont);
                var icon = KindIcon(kind).ToIconString();
                var iconSize = ImGui.CalcTextSize(icon);
                var iconColor = KindTextColor(kind) with { W = currentMonth ? 1f : 0.72f };
                var textPos = badgeMin + new Vector2((slot - iconSize.X) * 0.5f, (slot - iconSize.Y) * 0.5f);
                drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(iconColor), icon);
            }

            x -= slot + gap;
        }

        if (noteCount > 0)
        {
            var badgeMin = new Vector2(max.X - padding - slot, max.Y - padding - slot);
            var badgeMax = badgeMin + new Vector2(slot, slot);
            drawList.AddRectFilled(badgeMin, badgeMax, Color(0f, 0f, 0f, currentMonth ? 0.68f : 0.46f), slot * 0.45f);

            var texture = plugin.ImageCache.GetTexture(NoteIconAsset);
            if (texture != null)
            {
                var inset = 1f * scale;
                drawList.AddImage(texture.Handle, badgeMin + new Vector2(inset), badgeMax - new Vector2(inset), Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, currentMonth ? 1f : 0.72f));
            }
            else
            {
                using var font = ImRaii.PushFont(UiBuilder.IconFont);
                var icon = FontAwesomeIcon.StickyNote.ToIconString();
                var iconSize = ImGui.CalcTextSize(icon);
                var textPos = badgeMin + new Vector2((slot - iconSize.X) * 0.5f, (slot - iconSize.Y) * 0.5f);
                drawList.AddText(textPos, ImGui.ColorConvertFloat4ToU32(new Vector4(1f, 0.82f, 0.34f, currentMonth ? 1f : 0.72f)), icon);
            }
        }
    }

    internal IEnumerable<LodestoneEntry> GetEntriesForDay(DateTime day)
    {
        return GetVisibleEntriesUnsorted().Where(e => e.StartsAt.Date <= day.Date && e.EffectiveEnd.Date >= day.Date)
            .OrderByDescending(e => DayDisplayPriority(e, day.Date))
            .ThenBy(e => IsMultiDay(e))
            .ThenBy(e => e.StartsAt);
    }

    private IEnumerable<CalendarNote> GetNotesForDay(DateTime day)
    {
        return plugin.Configuration.Notes
            .Where(n => n.Date.Date == day.Date)
            .OrderBy(n => n.ScheduledAt ?? n.Date)
            .ThenBy(n => n.Text);
    }

    internal LodestoneEntry[] GetVisibleEntries()
        => GetAgendaEntries();

    private LodestoneEntry[] GetVisibleEntriesUnsorted()
    {
        LodestoneEntry[] snapshot;
        lock (entryLock)
            snapshot = entries.ToArray();

        return snapshot.Where(EntryVisible).ToArray();
    }

    internal IEnumerable<LodestoneEntry> GetMaintenanceWarnings()
    {
        LodestoneEntry[] snapshot;
        lock (entryLock)
            snapshot = entries.ToArray();

        var now = DateTime.Now;
        var warningLimit = now.AddHours(Math.Clamp(plugin.Configuration.MaintenanceWarningHours, 1, 72));
        return snapshot
            .Where(e => e.Kind == LodestoneEntryKind.Maintenance)
            .Where(e => !plugin.Configuration.HiddenEntryIds.Contains(e.Id))
            .Where(e => e.EffectiveEnd >= now && e.StartsAt <= warningLimit)
            .OrderBy(e => e.StartsAt);
    }

    internal IEnumerable<LodestoneEntry> GetServerBarEntries(DateTime day)
    {
        LodestoneEntry[] snapshot;
        lock (entryLock)
            snapshot = entries.ToArray();

        return snapshot
            .Where(EntryVisibleForServerBar)
            .Where(e => e.Kind == LodestoneEntryKind.SpecialEvent)
            .Where(e => e.StartsAt.Date <= day.Date && e.EffectiveEnd.Date >= day.Date)
            .OrderByDescending(e => DisplayPriority(e))
            .ThenBy(e => IsMultiDay(e))
            .ThenBy(e => e.StartsAt);
    }

    private bool EntryVisible(LodestoneEntry entry)
    {
        if (plugin.Configuration.HiddenEntryIds.Contains(entry.Id))
            return false;

        if (plugin.Configuration.OnlyCurrentAndFuture && entry.EffectiveEnd.Date < DateTime.Now.Date)
            return false;

        if (!KindEnabled(entry.Kind))
            return false;

        if (!string.IsNullOrWhiteSpace(searchText) &&
            !entry.Title.Contains(searchText, StringComparison.OrdinalIgnoreCase) &&
            !entry.Summary.Contains(searchText, StringComparison.OrdinalIgnoreCase))
            return false;

        return true;
    }

    private bool EntryVisibleForServerBar(LodestoneEntry entry)
    {
        if (plugin.Configuration.HiddenEntryIds.Contains(entry.Id))
            return false;

        if (plugin.Configuration.OnlyCurrentAndFuture && entry.EffectiveEnd.Date < DateTime.Now.Date)
            return false;

        return KindEnabled(entry.Kind);
    }

    private bool KindEnabled(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => plugin.Configuration.ShowEvents,
        LodestoneEntryKind.Topic => plugin.Configuration.ShowTopics,
        LodestoneEntryKind.Notice => plugin.Configuration.ShowNotices,
        LodestoneEntryKind.Maintenance => plugin.Configuration.ShowMaintenance,
        LodestoneEntryKind.Update => plugin.Configuration.ShowUpdates,
        LodestoneEntryKind.Status => plugin.Configuration.ShowStatus,
        LodestoneEntryKind.Recovery => plugin.Configuration.ShowRecovery,
        LodestoneEntryKind.DeveloperPost => plugin.Configuration.ShowDeveloperPosts,
        LodestoneEntryKind.IcyVeins => plugin.Configuration.ShowIcyVeins,
        LodestoneEntryKind.IcyVeinsGuide => plugin.Configuration.ShowIcyVeins && plugin.Configuration.ShowIcyVeinsGuides,
        _ => true
    };

    private LodestoneEntry[] GetAgendaEntries()
    {
        var key = BuildAgendaCacheKey();
        if (agendaCache is { } cache && cache.Key.Equals(key))
            return cache.Entries;

        var agendaEntries = GetVisibleEntriesUnsorted()
            .OrderByDescending(e => DisplayPriority(e))
            .ThenBy(e => e.StartsAt)
            .ToArray();
        agendaCache = new AgendaCache(key, agendaEntries);
        return agendaEntries;
    }

    private AgendaCacheKey BuildAgendaCacheKey()
        => new(
            entriesVersion,
            searchText,
            plugin.Configuration.ShowEvents,
            plugin.Configuration.ShowTopics,
            plugin.Configuration.ShowNotices,
            plugin.Configuration.ShowMaintenance,
            plugin.Configuration.ShowUpdates,
            plugin.Configuration.ShowStatus,
            plugin.Configuration.ShowRecovery,
            plugin.Configuration.ShowDeveloperPosts,
            plugin.Configuration.ShowIcyVeins,
            plugin.Configuration.ShowIcyVeinsGuides,
            plugin.Configuration.OnlyCurrentAndFuture ? DateTime.Now.Date : DateTime.MinValue,
            HiddenEntrySignature(),
            PrioritySignature());

    private string SelectHeroImageUrl(DateTime day, CalendarDayData data, bool previewAllowed)
    {
        if (previewAllowed && activeDayHeroPreview is { } preview && preview.Day.Date == day.Date)
            return preview.HeroImageUrl;

        if (plugin.Configuration.AutoCycleDayHeroImages)
        {
            var cyclingHero = SelectCyclingHeroImageUrl(day, data.HeroImageCandidates);
            if (!string.IsNullOrWhiteSpace(cyclingHero))
                return cyclingHero;
        }

        return data.HeroImageUrl;
    }

    private string SelectHeroImageUrl(IReadOnlyList<LodestoneEntry> dayEntries)
    {
        var heroEntry = SelectHeroEntry(dayEntries);
        if (heroEntry == null)
            return string.Empty;

        return ResolveHeroImageUrl(heroEntry);
    }

    private bool IsIcyVeinsArticleHeroActive(string heroImageUrl, CalendarDayData data)
    {
        if (string.IsNullOrWhiteSpace(heroImageUrl))
            return false;

        return data.Entries.Any(entry =>
            entry.Kind == LodestoneEntryKind.IcyVeins &&
            string.Equals(ResolveHeroImageUrl(entry), heroImageUrl, StringComparison.OrdinalIgnoreCase));
    }

    private string SelectCyclingHeroImageUrl(DateTime day, IReadOnlyList<string> heroes)
    {
        if (heroes.Count == 0)
            return string.Empty;

        if (heroes.Count == 1)
            return heroes[0];

        var cycle = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / HeroCycleMilliseconds;
        var index = (int)((cycle + day.DayOfYear) % heroes.Count);
        return heroes[index];
    }

    private IEnumerable<string> GetHeroImageCandidates(IReadOnlyList<LodestoneEntry> dayEntries)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in dayEntries)
        {
            var hero = ResolveHeroImageUrl(entry);
            if (!string.IsNullOrWhiteSpace(hero) && seen.Add(hero))
                yield return hero;
        }
    }

    private bool IsActiveDayHoverOverlayHovered(DateTime day)
        => activeDayHoverOverlay is { HasPopupBounds: true } overlay
           && overlay.Day.Date == day.Date
           && ImGui.IsMouseHoveringRect(overlay.PopupMin, overlay.PopupMax, true);

    private bool IsAnyActiveDayHoverOverlayHovered()
        => activeDayHoverOverlay is { HasPopupBounds: true } overlay
           && ImGui.IsMouseHoveringRect(overlay.PopupMin, overlay.PopupMax, true);

    private LodestoneEntry? SelectHeroEntry(IReadOnlyList<LodestoneEntry> dayEntries)
    {
        if (dayEntries.Count == 0)
            return null;

        var primary = dayEntries[0];
        if (!string.IsNullOrEmpty(ResolveHeroImageUrl(primary)))
            return primary;

        if (primary.Kind != LodestoneEntryKind.SpecialEvent && DisplayPriority(primary) >= plugin.Configuration.PriorityUpdates)
            return null;

        return dayEntries.FirstOrDefault(e => !string.IsNullOrEmpty(ResolveHeroImageUrl(e)));
    }

    private float DayImageAlpha(bool today, bool currentMonth)
    {
        if (today)
            return 1f;

        if (plugin.Configuration.UseCustomDayImageDim)
            return 1f;

        return currentMonth ? 0.86f : 0.36f;
    }

    private float DayFilmAlpha(bool today, bool currentMonth)
    {
        if (today)
            return 0f;

        if (plugin.Configuration.UseCustomDayImageDim)
            return Math.Clamp(plugin.Configuration.DayImageDimAmount, 0f, 0.9f);

        return currentMonth ? 0.28f : 0.58f;
    }

    private int DayDisplayPriority(LodestoneEntry entry, DateTime day)
    {
        var priority = DisplayPriority(entry);
        if (IsEventBoundaryDay(entry, day))
            return 50_000 + priority;

        return priority;
    }

    private int DisplayPriority(LodestoneEntry entry)
    {
        if (displayPriorityCache.TryGetValue(entry.Id, out var cached))
            return cached;

        var priority = ComputeDisplayPriority(entry);
        displayPriorityCache[entry.Id] = priority;
        return priority;
    }

    private int ComputeDisplayPriority(LodestoneEntry entry)
    {
        var priority = KindDisplayPriority(entry.Kind);
        foreach (var rule in MatchingPriorityRules(entry))
        {
            if (rule.UseAbsolutePriority)
                priority = rule.AbsolutePriority;

            if (rule.AnchorKind.HasValue)
                priority = plugin.Configuration.GetPriority(rule.AnchorKind.Value) + rule.AnchorOffset;

            priority += rule.PriorityOffset;
        }

        if (UsesDefaultNewsHero(entry))
            priority -= 10;

        if (IsMultiDay(entry) && entry.Kind is not LodestoneEntryKind.Maintenance and not LodestoneEntryKind.SpecialEvent)
            priority -= 15;

        return priority;
    }

    private int KindDisplayPriority(LodestoneEntryKind kind) => plugin.Configuration.GetPriority(kind);

    private static bool IsMultiDay(LodestoneEntry entry) => entry.EffectiveEnd.Date > entry.StartsAt.Date;

    private static bool IsEventBoundaryDay(LodestoneEntry entry, DateTime day)
        => IsEventStartDay(entry, day) || IsEventEndDay(entry, day);

    private static bool IsEventStartDay(LodestoneEntry entry, DateTime day)
        => entry.Kind == LodestoneEntryKind.SpecialEvent && entry.StartsAt.Date == day.Date;

    private static bool IsEventEndDay(LodestoneEntry entry, DateTime day)
        => entry.Kind == LodestoneEntryKind.SpecialEvent && entry.EffectiveEnd.Date == day.Date;

    private static bool UsesMaintenanceFallback(LodestoneEntryKind kind)
        => kind is LodestoneEntryKind.Maintenance or LodestoneEntryKind.Status or LodestoneEntryKind.Recovery;

    private string ResolveHeroImageUrl(LodestoneEntry entry)
    {
        if (heroImageUrlCache.TryGetValue(entry.Id, out var cached))
            return cached;

        var resolved = ResolveHeroImageUrlUncached(entry);
        heroImageUrlCache[entry.Id] = resolved;
        return resolved;
    }

    private string ResolveHeroImageUrlUncached(LodestoneEntry entry)
    {
        var heroAsset = MatchingPriorityRules(entry)
            .Select(rule => rule.HeroAsset)
            .FirstOrDefault(asset => !string.IsNullOrWhiteSpace(asset));
        if (!string.IsNullOrWhiteSpace(heroAsset))
            return HeroAssetUrl(heroAsset);

        if (UsesMaintenanceFallback(entry.Kind))
            return DefaultMaintenanceHeroAsset;

        if (IsDecorativeHeroImage(entry.HeroImageUrl))
            return DefaultNewsHeroAsset;

        return entry.HeroImageUrl;
    }

    private IEnumerable<PriorityRule> MatchingPriorityRules(LodestoneEntry entry)
        => plugin.Configuration.GetPriorityRules().Where(rule => rule.Matches(entry));

    private static string HeroAssetUrl(string heroAsset)
        => heroAsset.StartsWith(ImageCache.AssetScheme, StringComparison.OrdinalIgnoreCase) || Uri.TryCreate(heroAsset, UriKind.Absolute, out _)
            ? heroAsset
            : ImageCache.AssetScheme + heroAsset;

    private bool UsesDefaultNewsHero(LodestoneEntry entry)
        => string.Equals(ResolveHeroImageUrl(entry), DefaultNewsHeroAsset, StringComparison.OrdinalIgnoreCase);

    private static bool IsDecorativeHeroImage(string url)
        => !string.IsNullOrWhiteSpace(url)
           && (url.Contains("1LbK-2Cqoku3zorQFR0VQ6jP0Y", StringComparison.OrdinalIgnoreCase)
           || url.Contains("6PLTZ82M99GJ7tKOee1RSwvNrQ", StringComparison.OrdinalIgnoreCase)
           || url.Contains("U2uGfVX4GdZgU1jASO0m9h_xLg", StringComparison.OrdinalIgnoreCase)
           || url.Contains("/pc/global/", StringComparison.OrdinalIgnoreCase)
           || url.Contains("favicon", StringComparison.OrdinalIgnoreCase));

    private void DrawAgenda()
    {
        using var table = ImRaii.Table("##lodestoneAgenda", 4, ImGuiTableFlags.RowBg | ImGuiTableFlags.ScrollY | ImGuiTableFlags.BordersInnerH, ImGui.GetContentRegionAvail());
        if (!table.Success)
            return;

        ImGui.TableSetupColumn("Date", ImGuiTableColumnFlags.WidthFixed, 155f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Type", ImGuiTableColumnFlags.WidthFixed, 115f * ImGuiHelpers.GlobalScale);
        ImGui.TableSetupColumn("Title", ImGuiTableColumnFlags.WidthStretch);
        ImGui.TableSetupColumn("Open", ImGuiTableColumnFlags.WidthFixed, 70f * ImGuiHelpers.GlobalScale);
        ImGui.TableHeadersRow();

        var agendaEntries = GetAgendaEntries();
        if (agendaEntries.Length == 0)
        {
            ImGui.TableNextRow();
            ImGui.TableNextColumn();
            ImGui.TextColored(DetailMuted, "No matching entries.");
            return;
        }

        var rowHeight = 25f * ImGuiHelpers.GlobalScale;
        var clipper = new ImGuiListClipper();
        clipper.Begin(agendaEntries.Length, rowHeight);
        while (clipper.Step())
        {
            for (var i = clipper.DisplayStart; i < clipper.DisplayEnd; i++)
            {
                var entry = agendaEntries[i];
                DrawAgendaRow(entry);
            }
        }
    }

    private void DrawAgendaRow(LodestoneEntry entry)
    {
        ImGui.TableNextRow();
        ImGui.TableNextColumn();
        ImGui.TextUnformatted($"{entry.StartsAt:g}{(entry.EndsAt.HasValue ? $" - {entry.EndsAt.Value:g}" : string.Empty)}");
        ImGui.TableNextColumn();
        UiWidgets.NeonBadge(KindLabel(entry.Kind).ToUpperInvariant(), UiWidgets.KindPalette(entry.Kind), new Vector2(6f, 2f) * ImGuiHelpers.GlobalScale);
        ImGui.TableNextColumn();
        if (ImGui.Selectable($"{entry.Title}##agenda{entry.Id}", false, ImGuiSelectableFlags.SpanAllColumns))
            SelectEntry(entry);
        ImGui.TableNextColumn();
        if (ImGui.SmallButton($"URL##agendaUrl{entry.Id}"))
            Dalamud.Utility.Util.OpenLink(entry.Url);
    }

    private void DrawFilterBar()
    {
        ImGui.Separator();
        var changed = false;

        var showMaintenance = plugin.Configuration.ShowMaintenance;
        changed |= DrawFilterToggle(LodestoneEntryKind.Maintenance, "Maintenance", ref showMaintenance);
        plugin.Configuration.ShowMaintenance = showMaintenance;
        ImGui.SameLine();
        var showRecovery = plugin.Configuration.ShowRecovery;
        changed |= DrawFilterToggle(LodestoneEntryKind.Recovery, "Recovery", ref showRecovery);
        plugin.Configuration.ShowRecovery = showRecovery;
        ImGui.SameLine();
        var showStatus = plugin.Configuration.ShowStatus;
        changed |= DrawFilterToggle(LodestoneEntryKind.Status, "Status", ref showStatus);
        plugin.Configuration.ShowStatus = showStatus;
        ImGui.SameLine();
        var showNotices = plugin.Configuration.ShowNotices;
        changed |= DrawFilterToggle(LodestoneEntryKind.Notice, "Notices", ref showNotices);
        plugin.Configuration.ShowNotices = showNotices;
        ImGui.SameLine();
        var showUpdates = plugin.Configuration.ShowUpdates;
        changed |= DrawFilterToggle(LodestoneEntryKind.Update, "Updates", ref showUpdates);
        plugin.Configuration.ShowUpdates = showUpdates;
        ImGui.SameLine();
        var showTopics = plugin.Configuration.ShowTopics;
        changed |= DrawFilterToggle(LodestoneEntryKind.Topic, "Topics", ref showTopics);
        plugin.Configuration.ShowTopics = showTopics;
        ImGui.SameLine();
        var showEvents = plugin.Configuration.ShowEvents;
        changed |= DrawFilterToggle(LodestoneEntryKind.SpecialEvent, "Events", ref showEvents);
        plugin.Configuration.ShowEvents = showEvents;
        ImGui.SameLine();
        var showPartyEvents = plugin.Configuration.ShowPartyEvents;
        changed |= DrawPartyFilterToggle(ref showPartyEvents);
        plugin.Configuration.ShowPartyEvents = showPartyEvents;
        ImGui.SameLine();
        var showSubmarineReturns = plugin.Configuration.ShowSubmarineReturns;
        changed |= DrawSubmarineFilterToggle(ref showSubmarineReturns);
        plugin.Configuration.ShowSubmarineReturns = showSubmarineReturns;

        if (changed)
        {
            plugin.Configuration.Save();
            plugin.SubmarineService.Refresh(force: true);
            _ = RefreshAsync(true);
        }
    }

    private bool DrawFilterToggle(LodestoneEntryKind kind, string label, ref bool value)
    {
        DrawFilterIcon(kind);
        ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
        return ImGui.Checkbox($"{label}##filter{kind}", ref value);
    }

    private void DrawFilterIcon(LodestoneEntryKind kind)
    {
        var size = new Vector2(18f, 18f) * ImGuiHelpers.GlobalScale;
        var asset = CornerIconAsset(kind);
        var texture = string.IsNullOrEmpty(asset) ? null : plugin.ImageCache.GetTexture(asset);
        if (texture != null)
        {
            ImGui.Image(texture.Handle, size);
            return;
        }

        using (ImRaii.PushColor(ImGuiCol.Text, KindTextColor(kind)))
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextUnformatted(KindIcon(kind).ToIconString());
    }

    private void DrawDayWindow()
    {
        if (selectedDay == null)
            return;

        var open = true;
        PrimeSideWindowPlacement(new Vector2(560, 480) * ImGuiHelpers.GlobalScale, ref dayWindowNeedsPlacement);
        if (!ImGui.Begin($"{selectedDay.Value:D}##LodestoneDayDetails", ref open))
        {
            ImGui.End();
            if (!open)
                selectedDay = null;
            return;
        }

        if (!open)
            selectedDay = null;

        if (selectedDay != null)
        {
            var day = selectedDay.Value;
            using var rounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 7f);
            using var button = ImRaii.PushColor(ImGuiCol.Button, DetailPrimary);
            using var buttonHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, DetailAccent);
            using var buttonActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.32f, 0.16f, 0.62f, 1f));

            var notes = GetNotesForDay(day).ToArray();
            if (ImGui.Button("Add note##dayWindowAddNote"))
                OpenNoteEditor(day, null);
            ImGui.SameLine();
            if (ImGui.Button("Plan Party Event##dayWindowPlanParty"))
                OpenPartyPlanner(day, null);

            if (notes.Length > 0)
            {
                ImGui.Spacing();
                DrawDaySectionHeader(FontAwesomeIcon.StickyNote, "Notes", new Vector4(1f, 0.82f, 0.34f, 1f));
                foreach (var note in notes)
                    DrawDayNoteTile(note);
            }

            var partyEvents = GetPartyEventsForDay(day).ToArray();
            if (partyEvents.Length > 0)
            {
                ImGui.Spacing();
                DrawDaySectionHeader(FontAwesomeIcon.Users, "Party Events", DetailGreen);
                foreach (var partyEvent in partyEvents)
                    DrawDayPartyEventTile(partyEvent);
            }

            var submarineReturns = GetSubmarineReturnsForDay(day).ToArray();
            if (submarineReturns.Length > 0)
            {
                ImGui.Spacing();
                DrawDaySectionHeader(FontAwesomeIcon.Ship, "Submarine Returns", DetailBlue);
                foreach (var submarineReturn in submarineReturns)
                    DrawDaySubmarineReturnTile(submarineReturn);
            }

            var dayEntries = GetEntriesForDay(day).ToArray();
            var lodestoneEntries = dayEntries.Where(entry => !IsIcyVeinsEntry(entry)).ToArray();
            var icyVeinsEntries = dayEntries.Where(IsIcyVeinsEntry).ToArray();
            if (dayEntries.Length == 0)
            {
                ImGui.Spacing();
                DrawDaySectionHeader(FontAwesomeIcon.Newspaper, "Lodestone", DetailMuted);
                ImGui.TextColored(DetailMuted, "No Lodestone entries for this day.");
            }
            else
            {
                if (lodestoneEntries.Length > 0)
                {
                    ImGui.Spacing();
                    DrawDaySectionHeader(FontAwesomeIcon.Newspaper, "Lodestone", DetailMuted);
                    foreach (var entry in lodestoneEntries)
                        DrawDayEntryTile(entry);
                }

                if (icyVeinsEntries.Length > 0)
                {
                    ImGui.Spacing();
                    DrawDaySectionHeader(FontAwesomeIcon.Globe, "Icy Veins", DetailBlue);
                    foreach (var entry in icyVeinsEntries)
                        DrawDayEntryTile(entry);
                }
            }
        }

        ImGui.End();
    }

    private static bool IsIcyVeinsEntry(LodestoneEntry entry)
        => entry.Kind is LodestoneEntryKind.IcyVeins or LodestoneEntryKind.IcyVeinsGuide;

    private static void DrawDaySectionHeader(FontAwesomeIcon icon, string label, Vector4 color)
    {
        ImGui.Separator();
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(color, icon.ToIconString());
        ImGui.SameLine(0, 5f * ImGuiHelpers.GlobalScale);
        ImGui.TextColored(color, label);
    }

    private void DrawDayEntryTile(LodestoneEntry entry)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(240f * scale, ImGui.GetContentRegionAvail().X);
        var height = 50f * scale;
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();

        draw.AddRectFilled(start, start + size, DayTileColor(entry.Kind), 7f * scale);
        draw.AddRect(start, start + size, DayTileBorderColor(entry.Kind), 7f * scale, 0, 1.2f * scale);
        DrawDayTileIcon(entry.Kind, start + new Vector2(10f, 11f) * scale, 28f * scale, currentMonth: true);

        var textX = start.X + 48f * scale;
        var titleWidth = width - 60f * scale;
        draw.AddText(new Vector2(textX, start.Y + 7f * scale), Color(1f, 1f, 1f, 1f), FitTextToWidth(entry.Title, titleWidth));
        var meta = $"{KindLabel(entry.Kind)}  |  {EntrySourceLabel(entry)}  |  {entry.StartsAt:g}{(entry.EndsAt.HasValue ? $" - {entry.EndsAt.Value:g}" : string.Empty)}";
        draw.AddText(new Vector2(textX, start.Y + 29f * scale), Color(1f, 1f, 1f, 0.72f), FitTextToWidth(meta, titleWidth));

        if (ImGui.InvisibleButton($"##dayEntryTile{entry.Id}", size))
            SelectEntry(entry);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            draw.AddRect(start, start + size, Color(1f, 1f, 1f, 0.62f), 7f * scale, 0, 2f * scale);
            ImGui.SetTooltip($"{KindLabel(entry.Kind)}\n{entry.StartsAt:g}{(entry.EndsAt.HasValue ? $" - {entry.EndsAt.Value:g}" : string.Empty)}");
        }

        ImGui.Spacing();
    }
    private void DrawDayNoteTile(CalendarNote note)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(240f * scale, ImGui.GetContentRegionAvail().X);
        var height = 44f * scale;
        var startCursor = ImGui.GetCursorPos();
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();

        draw.AddRectFilled(start, start + size, Color(0.18f, 0.30f, 0.42f, 0.92f), 7f * scale);
        draw.AddRect(start, start + size, Color(0.70f, 0.86f, 1f, 0.52f), 7f * scale, 0, 1.1f * scale);
        DrawNoteTileIcon(start + new Vector2(10f, 9f) * scale, 26f * scale);

        var buttonWidth = 104f * scale;
        var titleWidth = width - 58f * scale - buttonWidth;
        draw.AddText(new Vector2(start.X + 46f * scale, start.Y + 6f * scale), Color(1f, 1f, 1f, 1f), FitTextToWidth(NoteLabel(note), titleWidth));
        draw.AddText(new Vector2(start.X + 46f * scale, start.Y + 26f * scale), Color(1f, 1f, 1f, 0.66f), note.HasTime ? "Player note with alarm time" : "Player note");

        ImGui.SetCursorScreenPos(new Vector2(start.X + width - buttonWidth, start.Y + 9f * scale));
        if (ImGui.SmallButton($"Edit##noteEdit{note.Id}"))
            OpenNoteEditor(note.Date, note);
        ImGui.SameLine();
        if (ImGui.SmallButton($"Delete##noteDelete{note.Id}"))
        {
            plugin.Configuration.Notes.Remove(note);
            plugin.Configuration.Save();
        }

        ImGui.SetCursorPos(startCursor + new Vector2(0, height + 6f * scale));
    }

    private void DrawDayTileIcon(LodestoneEntryKind kind, Vector2 min, float size, bool currentMonth)
    {
        var draw = ImGui.GetWindowDrawList();
        var max = min + new Vector2(size, size);
        draw.AddRectFilled(min, max, Color(0f, 0f, 0f, 0.54f), size * 0.45f);

        var asset = DayWindowIconAsset(kind);
        var texture = string.IsNullOrEmpty(asset) ? null : plugin.ImageCache.GetTexture(asset);
        if (texture != null)
        {
            var inset = 3f * ImGuiHelpers.GlobalScale;
            draw.AddImage(texture.Handle, min + new Vector2(inset), max - new Vector2(inset), Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, currentMonth ? 1f : 0.72f));
            return;
        }

        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var icon = KindIcon(kind).ToIconString();
        var iconSize = ImGui.CalcTextSize(icon);
        var pos = min + new Vector2((size - iconSize.X) * 0.5f, (size - iconSize.Y) * 0.5f);
        draw.AddText(pos, ImGui.ColorConvertFloat4ToU32(KindTextColor(kind)), icon);
    }

    private void DrawNoteTileIcon(Vector2 min, float size)
    {
        var draw = ImGui.GetWindowDrawList();
        var max = min + new Vector2(size, size);
        draw.AddRectFilled(min, max, Color(0f, 0f, 0f, 0.54f), size * 0.45f);
        var texture = plugin.ImageCache.GetTexture(NoteIconAsset);
        if (texture != null)
        {
            var inset = 2f * ImGuiHelpers.GlobalScale;
            draw.AddImage(texture.Handle, min + new Vector2(inset), max - new Vector2(inset), Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, 1f));
        }
    }

    private void OpenNoteEditor(DateTime day, CalendarNote? note)
    {
        editingNote = note;
        noteEditorDate = day.Date;
        noteEditorText = note?.Text ?? string.Empty;
        noteEditorHasTime = note?.HasTime ?? false;
        noteEditorHour = note?.Hour ?? 21;
        noteEditorMinute = note?.Minute ?? 0;
        noteEditorIsPm = noteEditorHour >= 12;
        noteEditorAlarmEnabled = note?.AlarmEnabled ?? true;
        noteWindowNeedsPlacement = true;
        noteEditorOpen = true;
    }

    private void DrawNoteEditorWindow()
    {
        if (!noteEditorOpen)
            return;

        var open = true;
        PrimeSideWindowPlacement(new Vector2(430, 360) * ImGuiHelpers.GlobalScale, ref noteWindowNeedsPlacement);
        if (!ImGui.Begin($"{(editingNote == null ? "Add" : "Edit")} Note##LodestoneNoteEditor", ref open))
        {
            ImGui.End();
            if (!open)
                noteEditorOpen = false;
            return;
        }

        if (!open)
            noteEditorOpen = false;

        if (noteEditorOpen)
        {
            using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f);
            using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 7f);
            using var button = ImRaii.PushColor(ImGuiCol.Button, DetailPrimary);
            using var buttonHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, DetailAccent);
            using var buttonActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.32f, 0.16f, 0.62f, 1f));

            DrawNoteEditorHeader();

            DrawNoteEditorBodyPanel();

            using (ImRaii.Disabled(string.IsNullOrWhiteSpace(noteEditorText)))
            {
                if (ImGui.Button("Save##noteSave", new Vector2(90, 0) * ImGuiHelpers.GlobalScale))
                    SaveNoteEditor();
            }

            if (editingNote != null)
            {
                ImGui.SameLine();
                using var deleteButton = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.62f, 0.08f, 0.08f, 0.96f));
                using var deleteHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.90f, 0.18f, 0.20f, 1f));
                if (ImGui.Button("Delete##noteDelete", new Vector2(90, 0) * ImGuiHelpers.GlobalScale))
                {
                    plugin.Configuration.Notes.Remove(editingNote);
                    plugin.Configuration.Save();
                    noteEditorOpen = false;
                }
            }

            ImGui.SameLine();
            if (ImGui.Button("Cancel##noteCancel", new Vector2(90, 0) * ImGuiHelpers.GlobalScale))
                noteEditorOpen = false;
        }

        ImGui.End();
    }

    private void DrawNoteEditorBodyPanel()
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.ColorConvertU32ToFloat4(DetailPanel));
        using var border = ImRaii.PushColor(ImGuiCol.Border, DetailPrimary);
        using var child = ImRaii.Child("##noteEditorBodyPanel", new Vector2(0, 154f * scale), true);
        if (!child.Success)
            return;

        ImGui.TextColored(DetailGreen, "Note");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##noteText", "Raid Night", ref noteEditorText, 160);

        ImGui.Spacing();
        ImGui.Checkbox("Set time", ref noteEditorHasTime);
        if (noteEditorHasTime)
        {
            DrawNoteTimeEditor(scale);
            ImGui.Checkbox("Alarm enabled", ref noteEditorAlarmEnabled);
        }
    }

    private void DrawNoteTimeEditor(float scale)
    {
        if (plugin.Configuration.UseTwelveHourNoteTimes)
        {
            var displayHour = ToTwelveHour(noteEditorHour);
            ImGui.SetNextItemWidth(90f * scale);
            if (ImGui.InputInt("Hour##noteHour12", ref displayHour))
            {
                displayHour = Math.Clamp(displayHour, 1, 12);
                noteEditorHour = FromTwelveHour(displayHour, noteEditorIsPm);
            }

            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f * scale);
            ImGui.InputInt("Minute##noteMinute12", ref noteEditorMinute);
            noteEditorMinute = Math.Clamp(noteEditorMinute, 0, 59);

            ImGui.SameLine();
            ImGui.SetNextItemWidth(78f * scale);
            var meridiem = noteEditorIsPm ? 1 : 0;
            if (ImGui.Combo("##noteMeridiem", ref meridiem, "AM\0PM\0"))
            {
                noteEditorIsPm = meridiem == 1;
                noteEditorHour = FromTwelveHour(displayHour, noteEditorIsPm);
            }

            return;
        }

        ImGui.SetNextItemWidth(90f * scale);
        ImGui.InputInt("Hour##noteHour24", ref noteEditorHour);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(90f * scale);
        ImGui.InputInt("Minute##noteMinute24", ref noteEditorMinute);
        noteEditorHour = Math.Clamp(noteEditorHour, 0, 23);
        noteEditorMinute = Math.Clamp(noteEditorMinute, 0, 59);
        noteEditorIsPm = noteEditorHour >= 12;
    }

    private void DrawNoteEditorHeader()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = 74f * scale;
        draw.AddRectFilled(start, start + new Vector2(width, height), DetailPanelElevated, 8f * scale);
        draw.AddRect(start, start + new Vector2(width, height), DetailPanelPurple, 8f * scale, 0, 1.4f * scale);

        var texture = plugin.ImageCache.GetTexture(NoteIconAsset);
        var iconMin = start + new Vector2(12f, 12f) * scale;
        var iconSize = new Vector2(48f, 48f) * scale;
        if (texture != null)
            draw.AddImage(texture.Handle, iconMin, iconMin + iconSize, Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, 1f));

        ImGui.SetCursorScreenPos(start + new Vector2(72f, 12f) * scale);
        ImGui.TextColored(DetailMuted, editingNote == null ? "Add player note" : "Edit player note");
        ImGui.SetCursorScreenPos(start + new Vector2(72f, 38f) * scale);
        ImGui.SetWindowFontScale(1.08f);
        ImGui.TextColored(Vector4.One, noteEditorDate.ToLongDateString());
        ImGui.SetWindowFontScale(1f);
        ImGui.SetCursorScreenPos(start + new Vector2(0, height + 10f * scale));
    }

    private void SaveNoteEditor()
    {
        var note = editingNote ?? new CalendarNote { Date = noteEditorDate };
        note.Date = noteEditorDate;
        note.Text = noteEditorText.Trim();
        note.Hour = noteEditorHasTime ? noteEditorHour : null;
        note.Minute = noteEditorHasTime ? noteEditorMinute : null;
        note.AlarmEnabled = noteEditorHasTime && noteEditorAlarmEnabled;
        note.NotifiedWarningMinutes.Clear();

        if (editingNote == null)
            plugin.Configuration.Notes.Add(note);

        plugin.Configuration.Save();
        noteEditorOpen = false;
    }

    private void SelectEntry(LodestoneEntry entry)
    {
        if (selectedEntry == null || !selectedEntry.Id.Equals(entry.Id, StringComparison.Ordinal))
            ClearDetailTextCopyMode();

        selectedEntry = entry;
        selectedPartyEvent = null;
        selectedSubmarineReturn = null;
        detailWindowNeedsPlacement = true;
    }

    private void ClearDetailTextCopyMode()
    {
        detailTextCopyMode = false;
        detailTextCopyEntryId = string.Empty;
        detailTextCopyBuffer = string.Empty;
    }

    private void PrimeSideWindowPlacement(Vector2 desiredSize, ref bool needsPlacement)
    {
        ImGui.SetNextWindowSize(desiredSize, ImGuiCond.FirstUseEver);
        if (!needsPlacement)
            return;

        ImGui.SetNextWindowSize(desiredSize, ImGuiCond.Always);
        ImGui.SetNextWindowPos(CalculateSideWindowPosition(desiredSize), ImGuiCond.Always);
        needsPlacement = false;
    }

    internal void PrimeExternalSideWindowPlacement(Vector2 desiredSize, ref bool needsPlacement)
        => PrimeSideWindowPlacement(desiredSize, ref needsPlacement);

    private Vector2 CalculateSideWindowPosition(Vector2 desiredSize)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var gap = 8f * scale;
        var displaySize = ImGui.GetIO().DisplaySize;
        var workMin = new Vector2(gap, gap);
        var workMax = new Vector2(
            Math.Max(workMin.X + desiredSize.X, displaySize.X - gap),
            Math.Max(workMin.Y + desiredSize.Y, displaySize.Y - gap));

        var rightX = calendarWindowPos.X + calendarWindowSize.X + gap;
        var leftX = calendarWindowPos.X - desiredSize.X - gap;
        var x = rightX + desiredSize.X <= workMax.X
            ? rightX
            : leftX >= workMin.X
                ? leftX
                : ClampFloat(rightX, workMin.X, workMax.X - desiredSize.X);

        var y = ClampFloat(calendarWindowPos.Y, workMin.Y, workMax.Y - desiredSize.Y);
        return new Vector2(x, y);
    }

    private static float ClampFloat(float value, float min, float max)
        => max < min ? min : Math.Clamp(value, min, max);

    private void DrawDetailWindow()
    {
        if (selectedEntry == null)
            return;

        var open = true;
        var scale = ImGuiHelpers.GlobalScale;
        var maxHeight = Math.Max(420f * scale, ImGui.GetIO().DisplaySize.Y - 16f * scale);
        var desiredHeight = Math.Clamp(calendarWindowSize.Y > 0 ? calendarWindowSize.Y : 720f * scale, 420f * scale, maxHeight);
        PrimeSideWindowPlacement(new Vector2(760f * scale, desiredHeight), ref detailWindowNeedsPlacement);
        if (!ImGui.Begin($"Lodestone Details##{selectedEntry.Id}", ref open))
        {
            ImGui.End();
            if (!open)
            {
                ClearDetailTextCopyMode();
                selectedEntry = null;
            }
            return;
        }

        if (!open)
        {
            ClearDetailTextCopyMode();
            selectedEntry = null;
        }

        if (selectedEntry != null)
        {
            DrawDetail(selectedEntry);
        }

        ImGui.End();
    }

    private void DrawDetail(LodestoneEntry entry)
    {
        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f);
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 7f);
        using var tabColor = ImRaii.PushColor(ImGuiCol.Tab, DetailPrimary);
        using var tabHovered = ImRaii.PushColor(ImGuiCol.TabHovered, DetailAccent);
        using var tabActive = ImRaii.PushColor(ImGuiCol.TabActive, DetailAccent);
        using var button = ImRaii.PushColor(ImGuiCol.Button, DetailPrimary);
        using var buttonHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, DetailAccent);
        using var buttonActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.32f, 0.16f, 0.62f, 1f));

        DrawDetailHeader(entry);

        using (var body = ImRaii.Child("##lodestoneDetailBody", new Vector2(0, -42f * ImGuiHelpers.GlobalScale), border: false))
        {
            if (body.Success)
            {
                DrawDetailHero(entry);

                using var tabs = ImRaii.TabBar("##lodestoneDetailTabs");
                if (tabs.Success)
                {
                    using (var overview = ImRaii.TabItem(entry.FullArticleParsed ? "Article" : "Overview"))
                    {
                        if (overview.Success)
                            DrawOverview(entry);
                    }

                    if (entry.Rewards.Count > 0)
                    {
                        using var rewards = ImRaii.TabItem("Rewards");
                        if (rewards.Success)
                            DrawRewards(entry);
                    }

                    using (var images = ImRaii.TabItem("Images"))
                    {
                        if (images.Success)
                            DrawImages(entry);
                    }

                    using (var source = ImRaii.TabItem("Source"))
                    {
                        if (source.Success)
                        {
                            ImGui.TextColored(DetailMuted, "URL");
                            WrappedText(entry.Url);
                            ImGui.TextColored(DetailMuted, $"Fetched: {entry.FetchedAt:g}");
                        }
                    }
                }
            }
        }

        DrawDetailActions(entry);
    }

    private void DrawDetailHeader(LodestoneEntry entry)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = 68f * scale;
        draw.AddRectFilled(start, start + new Vector2(width, height), DetailPanelElevated, 8f * scale);
        draw.AddRect(start, start + new Vector2(width, height), DetailPanelPurple, 8f * scale, 0, 1.5f * scale);

        DrawDetailKindIcon(entry.Kind, start + new Vector2(14f, 10f) * scale, 22f * scale);
        ImGui.SetCursorScreenPos(start + new Vector2(44f, 8f) * scale);
        var badgeLabel = DetailBadgeLabel(entry);
        var sourceLabel = EntrySourceLabel(entry);
        UiWidgets.NeonBadge(badgeLabel.ToUpperInvariant(), UiWidgets.KindPalette(entry.Kind), new Vector2(6f, 2f) * scale);
        if (!sourceLabel.Equals("Lodestone", StringComparison.OrdinalIgnoreCase)
            && !sourceLabel.Equals(badgeLabel, StringComparison.OrdinalIgnoreCase))
        {
            ImGui.SameLine(0, 5f * scale);
            UiWidgets.NeonBadge(sourceLabel, UiWidgets.KindPalette(entry.Kind), new Vector2(6f, 2f) * scale);
        }

        ImGui.SameLine(0, 7f * scale);
        ImGui.TextColored(DetailMuted, $"{entry.StartsAt:g}{(entry.EndsAt.HasValue ? $" - {entry.EndsAt.Value:g}" : string.Empty)}");

        ImGui.SetCursorScreenPos(start + new Vector2(14f, 35f) * scale);
        ImGui.SetWindowFontScale(1.08f);
        ImGui.TextColored(new Vector4(1f, 1f, 1f, 1f), entry.Title);
        ImGui.SetWindowFontScale(1f);

        ImGui.SetCursorScreenPos(start + new Vector2(0f, height + 10f * scale));
    }

    private void DrawDetailKindIcon(LodestoneEntryKind kind, Vector2 min, float size)
    {
        var asset = DayWindowIconAsset(kind);
        var texture = string.IsNullOrEmpty(asset) ? null : plugin.ImageCache.GetTexture(asset);
        if (texture != null)
        {
            ImGui.GetWindowDrawList().AddImage(texture.Handle, min, min + new Vector2(size), Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, 1f));
            return;
        }

        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var icon = KindIcon(kind).ToIconString();
        var iconSize = ImGui.CalcTextSize(icon);
        var pos = min + new Vector2((size - iconSize.X) * 0.5f, (size - iconSize.Y) * 0.5f);
        ImGui.GetWindowDrawList().AddText(pos, ImGui.ColorConvertFloat4ToU32(KindTextColor(kind)), icon);
    }

    private void DrawDetailHero(LodestoneEntry entry)
    {
        var heroImageUrl = ResolveHeroImageUrl(entry);
        if (string.IsNullOrEmpty(heroImageUrl))
            return;

        var texture = plugin.ImageCache.GetTexture(heroImageUrl);
        if (texture == null)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var padding = 8f * scale;
        var imageWidth = Math.Min(availableWidth - padding * 2f, 680f * scale);
        var imageHeight = Math.Min(240f * scale, imageWidth * 0.36f);
        var panelSize = new Vector2(imageWidth + padding * 2f, imageHeight + padding * 2f);

        draw.AddRectFilled(start, start + panelSize, DetailPanel, 8f * scale);
        draw.AddImage(texture.Handle, start + new Vector2(padding), start + new Vector2(padding + imageWidth, padding + imageHeight), Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, 1f));
        draw.AddRect(start, start + panelSize, DetailPanelPurple, 8f * scale, 0, 1f * scale);
        ImGui.Dummy(panelSize + new Vector2(0, 8f * scale));
    }

    private void DrawOverview(LodestoneEntry entry)
    {
        if (!string.IsNullOrWhiteSpace(entry.Summary))
        {
            if (IsDetailTextCopyMode(entry))
                DrawArticleCopyText(entry);
            else
                DrawDetailPanel("##detailOverviewPanel", DetailPanel, () => DrawArticleText(entry.Summary));
        }
        else
        {
            DrawDetailPanel("##detailOverviewPanel", DetailPanel, () => ImGui.TextColored(DetailMuted, "No overview text was parsed for this entry."));
        }

        var hasMetadata = !string.IsNullOrWhiteSpace(entry.StartingNpc)
                          || !string.IsNullOrWhiteSpace(entry.StartingLocation)
                          || !string.IsNullOrWhiteSpace(entry.SourceTimeText)
                          || entry.Requirements.Count > 0;
        if (!hasMetadata)
            return;

        DrawDetailPanel("##detailMetadataPanel", DetailPanelElevated, () =>
        {
            if (!string.IsNullOrWhiteSpace(entry.StartingNpc) || !string.IsNullOrWhiteSpace(entry.StartingLocation))
            {
                ImGui.TextColored(DetailGreen, "Starts");
                WrappedText($"{entry.StartingNpc} {entry.StartingLocation}".Trim());
            }

            if (!string.IsNullOrWhiteSpace(entry.SourceTimeText))
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextColored(DetailGreen, EntrySourceLabel(entry).Equals("Lodestone", StringComparison.OrdinalIgnoreCase) ? "Lodestone time" : "Source time");
                WrappedText(entry.SourceTimeText);
                ImGui.TextColored(DetailMuted, "Shown on the calendar in your local time.");
            }

            if (entry.Requirements.Count > 0)
            {
                ImGui.Spacing();
                ImGui.Separator();
                ImGui.TextColored(DetailGreen, "Requirements");
                foreach (var requirement in entry.Requirements)
                    ImGui.BulletText(requirement);
            }
        });
    }

    private bool IsDetailTextCopyMode(LodestoneEntry entry)
        => detailTextCopyMode && detailTextCopyEntryId.Equals(entry.Id, StringComparison.Ordinal);

    private void ToggleDetailTextCopyMode(LodestoneEntry entry)
    {
        if (IsDetailTextCopyMode(entry))
        {
            ClearDetailTextCopyMode();
            return;
        }

        detailTextCopyMode = true;
        detailTextCopyEntryId = entry.Id;
        detailTextCopyBuffer = PlainArticleText(entry.Summary);
    }

    private void DrawArticleCopyText(LodestoneEntry entry)
    {
        if (!IsDetailTextCopyMode(entry))
            ToggleDetailTextCopyMode(entry);

        var scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var startCursor = ImGui.GetCursorPos();
        var startScreen = ImGui.GetCursorScreenPos();
        var panelWidth = ImGui.GetContentRegionAvail().X;
        var padding = new Vector2(10f, 10f) * scale;
        var panelHeight = Math.Clamp(ImGui.GetContentRegionAvail().Y * 0.45f, 230f * scale, 380f * scale);
        var panelSize = new Vector2(panelWidth, panelHeight);

        draw.AddRectFilled(startScreen, startScreen + panelSize, DetailPanel, 8f * scale);
        draw.AddRect(startScreen, startScreen + panelSize, DetailPanelPurple, 8f * scale, 0, 1f * scale);

        ImGui.SetCursorPos(startCursor + padding);
        var copyText = detailTextCopyBuffer;
        using var frameBg = ImRaii.PushColor(ImGuiCol.FrameBg, new Vector4(0.06f, 0.06f, 0.09f, 0.98f));
        using var frameHovered = ImRaii.PushColor(ImGuiCol.FrameBgHovered, new Vector4(0.08f, 0.08f, 0.13f, 0.98f));
        using var frameActive = ImRaii.PushColor(ImGuiCol.FrameBgActive, new Vector4(0.08f, 0.08f, 0.13f, 0.98f));
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, new Vector4(1f, 1f, 1f, 1f));
        ImGui.InputTextMultiline(
            "##detailCopyText",
            ref copyText,
            Math.Max(4096, detailTextCopyBuffer.Length + 1),
            panelSize - padding * 2f,
            ImGuiInputTextFlags.ReadOnly);

        ImGui.SetCursorPos(new Vector2(startCursor.X, startCursor.Y + panelSize.Y + 8f * scale));
    }

    private static void DrawDetailPanel(string id, uint backgroundColor, Action drawContent)
    {
        var draw = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(10f, 10f) * scale;
        var startCursor = ImGui.GetCursorPos();
        var startScreen = ImGui.GetCursorScreenPos();
        var panelWidth = ImGui.GetContentRegionAvail().X;

        ImGui.SetCursorPos(startCursor + padding);
        ImGui.BeginGroup();
        drawContent();
        ImGui.EndGroup();

        var contentSize = ImGui.GetItemRectSize();
        var panelSize = new Vector2(panelWidth, contentSize.Y + padding.Y * 2f);
        draw.AddRectFilled(startScreen, startScreen + panelSize, backgroundColor, 8f * scale);
        draw.AddRect(startScreen, startScreen + panelSize, DetailPanelPurple, 8f * scale, 0, 1f * scale);

        ImGui.SetCursorPos(startCursor + padding);
        ImGui.BeginGroup();
        drawContent();
        ImGui.EndGroup();
        ImGui.SetCursorPos(new Vector2(startCursor.X, startCursor.Y + panelSize.Y + 8f * scale));
    }

    private static void DrawArticleText(string summary)
    {
        var previousHeading = string.Empty;
        foreach (var rawLine in summary.Replace('\r', '\n').Split('\n', StringSplitOptions.TrimEntries))
        {
            var line = rawLine.Trim();
            if (line.Length == 0)
            {
                ImGui.Dummy(new Vector2(1f, 5f) * ImGuiHelpers.GlobalScale);
                continue;
            }

            if (line.StartsWith("- ", StringComparison.Ordinal))
            {
                using (ImRaii.PushColor(ImGuiCol.Text, DetailGreen))
                    ImGui.Bullet();
                ImGui.SameLine();
                DrawRichArticleText(line[2..], new Vector4(1f, 1f, 1f, 1f));
                continue;
            }

            if (line.StartsWith("> ", StringComparison.Ordinal))
            {
                using (ImRaii.PushColor(ImGuiCol.Text, DetailMuted))
                    DrawRichArticleText(line[2..], DetailMuted);
                continue;
            }

            if (line.StartsWith("## ", StringComparison.Ordinal) || IsArticleHeading(line))
            {
                var heading = line.StartsWith("## ", StringComparison.Ordinal) ? line[3..].Trim() : line;
                ImGui.Spacing();
                ImGui.TextColored(DetailGreen, heading);
                previousHeading = heading.Trim().TrimEnd(':');
                continue;
            }

            var color = previousHeading.Equals("When", StringComparison.OrdinalIgnoreCase)
                ? DetailOrange
                : previousHeading.Equals("Where", StringComparison.OrdinalIgnoreCase)
                    ? DetailBlue
                    : new Vector4(1f, 1f, 1f, 1f);

            DrawRichArticleText(line, color);
        }
    }

    private static void DrawRichArticleText(string text, Vector4 baseColor)
    {
        var runs = ParseArticleInlineRuns(text).ToArray();
        if (runs.Length == 0)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var lineStartX = ImGui.GetCursorScreenPos().X;
        var wrapRight = lineStartX + Math.Max(96f * scale, ImGui.GetContentRegionAvail().X);
        var firstWordOnLine = true;
        var wroteAny = false;

        foreach (var run in runs)
        {
            foreach (var word in run.Text.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var token = firstWordOnLine ? word : $" {word}";
                var tokenSize = ImGui.CalcTextSize(token);
                var cursorX = ImGui.GetCursorScreenPos().X;
                if (!firstWordOnLine && cursorX + tokenSize.X > wrapRight)
                {
                    ImGui.NewLine();
                    ImGui.SetCursorScreenPos(new Vector2(lineStartX, ImGui.GetCursorScreenPos().Y));
                    token = word;
                    firstWordOnLine = true;
                }

                DrawArticleToken(token, run, baseColor);
                ImGui.SameLine(0, 0);
                firstWordOnLine = false;
                wroteAny = true;
            }
        }

        if (wroteAny)
            ImGui.NewLine();
    }

    private static IEnumerable<ArticleInlineRun> ParseArticleInlineRuns(string text)
    {
        for (var i = 0; i < text.Length;)
        {
            if (i + 1 < text.Length && text[i] == '*' && text[i + 1] == '*')
            {
                var end = text.IndexOf("**", i + 2, StringComparison.Ordinal);
                if (end > i + 2)
                {
                    yield return new ArticleInlineRun(text[(i + 2)..end], true, false);
                    i = end + 2;
                    continue;
                }
            }

            if (text[i] == '*')
            {
                var end = text.IndexOf('*', i + 1);
                if (end > i + 1)
                {
                    yield return new ArticleInlineRun(text[(i + 1)..end], false, true);
                    i = end + 1;
                    continue;
                }
            }

            var next = text.IndexOf('*', i);
            if (next < 0)
                next = text.Length;

            if (next > i)
                yield return new ArticleInlineRun(text[i..next], false, false);

            i = next == i ? i + 1 : next;
        }
    }

    private static void DrawArticleToken(string token, ArticleInlineRun run, Vector4 baseColor)
    {
        var color = run.Bold
            ? new Vector4(1f, 1f, 1f, baseColor.W)
            : run.Italic
                ? new Vector4(Math.Min(baseColor.X + 0.16f, 1f), Math.Min(baseColor.Y + 0.16f, 1f), Math.Min(baseColor.Z + 0.16f, 1f), baseColor.W)
                : baseColor;
        var pos = ImGui.GetCursorScreenPos();
        using (ImRaii.PushColor(ImGuiCol.Text, color))
            ImGui.TextUnformatted(token);

        if (run.Bold)
            ImGui.GetWindowDrawList().AddText(pos + new Vector2(0.65f * ImGuiHelpers.GlobalScale, 0f), ToColor(color), token);
    }

    private static bool IsArticleHeading(string line)
    {
        var normalized = line.Trim().TrimEnd(':');
        if (normalized is "Who" or "When" or "Where" or "Giveaways" or "Community Commendations" or "Requirements" or "Rewards")
            return true;

        return normalized.Length is > 0 and <= 36
               && !normalized.StartsWith("- ", StringComparison.Ordinal)
               && !normalized.StartsWith("By ", StringComparison.OrdinalIgnoreCase)
               && normalized.IndexOfAny(new[] { '.', ',', '!', '?', ';' }) < 0;
    }

    private static string PlainArticleText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            return string.Empty;

        var lines = text
            .Replace("\r", string.Empty, StringComparison.Ordinal)
            .Split('\n')
            .Select(line => line.StartsWith("## ", StringComparison.Ordinal) ? line[3..] : line)
            .Select(line => line.StartsWith("> ", StringComparison.Ordinal) ? line[2..] : line)
            .Select(line => line.Replace("**", string.Empty, StringComparison.Ordinal).Replace("*", string.Empty, StringComparison.Ordinal));
        return string.Join("\n", lines).Trim();
    }

    private void DrawDetailActions(LodestoneEntry entry)
    {
        if (ImGui.Button($"Open {EntrySourceLabel(entry)}"))
            Dalamud.Utility.Util.OpenLink(entry.Url);
        if (entry.Kind == LodestoneEntryKind.SpecialEvent)
        {
            ImGui.SameLine();
            if (ImGui.Button("Quest Lookup"))
                plugin.QuestLookupWindow.Open(entry);
        }
        ImGui.SameLine();
        if (ImGui.Button("Copy URL"))
            ImGui.SetClipboardText(entry.Url);
        if (!string.IsNullOrWhiteSpace(entry.Summary))
        {
            ImGui.SameLine();
            if (ImGui.Button(IsDetailTextCopyMode(entry) ? "Revert" : "Copy Text"))
                ToggleDetailTextCopyMode(entry);
        }

        if (entry.Kind == LodestoneEntryKind.SpecialEvent)
        {
            ImGui.SameLine();
            using var hideButton = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.54f, 0.08f, 0.08f, 1f));
            using var hideHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.76f, 0.12f, 0.12f, 1f));
            using var hideActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.40f, 0.05f, 0.05f, 1f));
            if (ImGui.Button("Hide Event"))
                HideEntry(entry);
        }

        ImGui.SameLine();
        if (ImGui.Button("Mark Seen"))
            HideEntry(entry);
    }

    private void HideEntry(LodestoneEntry entry)
    {
        if (!plugin.Configuration.HiddenEntryIds.Contains(entry.Id))
            plugin.Configuration.HiddenEntryIds.Add(entry.Id);

        plugin.Configuration.Save();
        ClearDetailTextCopyMode();
        selectedEntry = null;
    }

    private void DrawRewards(LodestoneEntry entry)
    {
        if (entry.Rewards.Count == 0)
        {
            ImGui.TextUnformatted("No reward data parsed for this entry.");
            return;
        }

        var columns = Math.Max(1, (int)(ImGui.GetContentRegionAvail().X / (112f * ImGuiHelpers.GlobalScale)));
        using var table = ImRaii.Table("##rewardGrid", columns, ImGuiTableFlags.SizingStretchSame);
        if (!table.Success)
            return;

        foreach (var reward in entry.Rewards)
        {
            ImGui.TableNextColumn();
            if (!string.IsNullOrEmpty(reward.ImageUrl))
            {
                var texture = plugin.ImageCache.GetTexture(reward.ImageUrl);
                if (texture != null)
                    ImGui.Image(texture.Handle, new Vector2(56, 56) * ImGuiHelpers.GlobalScale);
                else
                    ImGui.Dummy(new Vector2(56, 56) * ImGuiHelpers.GlobalScale);
            }
            ImGui.TextWrapped(string.IsNullOrEmpty(reward.Name) ? reward.Kind : $"{reward.Kind}\n{reward.Name}");
        }
    }

    private void DrawImages(LodestoneEntry entry)
    {
        var urls = TrimDisplayImages(entry.ImageUrls).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (urls.Length == 0)
        {
            ImGui.TextUnformatted("No images parsed for this entry.");
            return;
        }

        foreach (var url in urls)
        {
            var texture = plugin.ImageCache.GetTexture(url);
            if (texture == null)
                continue;

            var width = Math.Min(ImGui.GetContentRegionAvail().X, 360f * ImGuiHelpers.GlobalScale);
            ImGui.Image(texture.Handle, new Vector2(width, width * 0.55f));
            if (ImGui.IsItemHovered())
            {
                ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
                ImGui.SetTooltip(url);
            }

            if (ImGui.IsItemClicked())
                Dalamud.Utility.Util.OpenLink(url);
        }
    }

    private static void WrappedText(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static void TextWrappedColored(Vector4 color, string text)
    {
        using var textColor = ImRaii.PushColor(ImGuiCol.Text, color);
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static string KindLabel(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => "Event",
        LodestoneEntryKind.Maintenance => "Maintenance",
        LodestoneEntryKind.Notice => "Notice",
        LodestoneEntryKind.Update => "Update",
        LodestoneEntryKind.Status => "Status",
        LodestoneEntryKind.Recovery => "Recovery",
        LodestoneEntryKind.DeveloperPost => "Dev Post",
        LodestoneEntryKind.IcyVeins => "Icy Veins",
        LodestoneEntryKind.IcyVeinsGuide => "Icy Guide",
        _ => "Topic"
    };

    private static string DetailBadgeLabel(LodestoneEntry entry) => entry.Kind switch
    {
        LodestoneEntryKind.IcyVeins => "Article",
        LodestoneEntryKind.IcyVeinsGuide => "Guide",
        _ => KindLabel(entry.Kind)
    };

    private static string EntrySourceLabel(LodestoneEntry entry)
        => string.IsNullOrWhiteSpace(entry.SourceName) ? "Lodestone" : entry.SourceName;

    private string NoteLabel(CalendarNote note)
    {
        return note.ScheduledAt is { } scheduledAt
            ? $"{FormatNoteTime(scheduledAt)} {note.Text}"
            : note.Text;
    }

    private string FormatNoteTime(DateTime scheduledAt)
        => plugin.Configuration.UseTwelveHourNoteTimes
            ? scheduledAt.ToString("h:mm tt")
            : scheduledAt.ToString("HH:mm");

    private static int ToTwelveHour(int hour)
    {
        var value = hour % 12;
        return value == 0 ? 12 : value;
    }

    private static int FromTwelveHour(int hour, bool isPm)
    {
        hour = Math.Clamp(hour, 1, 12);
        if (hour == 12)
            return isPm ? 12 : 0;

        return isPm ? hour + 12 : hour;
    }

    private static FontAwesomeIcon KindIcon(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => FontAwesomeIcon.Star,
        LodestoneEntryKind.Maintenance => FontAwesomeIcon.Wrench,
        LodestoneEntryKind.Recovery => FontAwesomeIcon.Heartbeat,
        LodestoneEntryKind.Notice => FontAwesomeIcon.ExclamationCircle,
        LodestoneEntryKind.Update => FontAwesomeIcon.Download,
        LodestoneEntryKind.Status => FontAwesomeIcon.TimesCircle,
        LodestoneEntryKind.DeveloperPost => FontAwesomeIcon.Rss,
        LodestoneEntryKind.IcyVeins => FontAwesomeIcon.Globe,
        LodestoneEntryKind.IcyVeinsGuide => FontAwesomeIcon.Book,
        _ => FontAwesomeIcon.Newspaper
    };

    private static string CornerIconAsset(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => ImageCache.AssetScheme + "corner-event.png",
        LodestoneEntryKind.Maintenance => ImageCache.AssetScheme + "corner-maintenance.png",
        LodestoneEntryKind.Topic => ImageCache.AssetScheme + "corner-news.png",
        LodestoneEntryKind.Notice => ImageCache.AssetScheme + "corner-news.png",
        LodestoneEntryKind.Status => ImageCache.AssetScheme + "corner-news.png",
        LodestoneEntryKind.Recovery => ImageCache.AssetScheme + "corner-news.png",
        LodestoneEntryKind.DeveloperPost => ImageCache.AssetScheme + "corner-news.png",
        LodestoneEntryKind.IcyVeins => ImageCache.AssetScheme + "corner-news.png",
        LodestoneEntryKind.IcyVeinsGuide => ImageCache.AssetScheme + "corner-news.png",
        _ => string.Empty
    };

    private static string DayWindowIconAsset(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => ImageCache.AssetScheme + "corner-event.png",
        LodestoneEntryKind.Maintenance => ImageCache.AssetScheme + "corner-maintenance.png",
        LodestoneEntryKind.Topic => ImageCache.AssetScheme + "corner-news.png",
        LodestoneEntryKind.Notice => ImageCache.AssetScheme + "corner-news.png",
        LodestoneEntryKind.DeveloperPost => ImageCache.AssetScheme + "corner-news.png",
        LodestoneEntryKind.IcyVeins => ImageCache.AssetScheme + "corner-news.png",
        LodestoneEntryKind.IcyVeinsGuide => ImageCache.AssetScheme + "corner-news.png",
        _ => string.Empty
    };

    private static Vector4 KindTextColor(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => new Vector4(0.42f, 0.95f, 0.72f, 1f),
        LodestoneEntryKind.Maintenance => new Vector4(1f, 0.48f, 0.34f, 1f),
        LodestoneEntryKind.Recovery => new Vector4(0.78f, 0.68f, 1f, 1f),
        LodestoneEntryKind.Notice => new Vector4(0.86f, 0.76f, 1f, 1f),
        LodestoneEntryKind.Update => new Vector4(0.62f, 0.78f, 1f, 1f),
        LodestoneEntryKind.Status => new Vector4(1f, 0.82f, 0.42f, 1f),
        LodestoneEntryKind.DeveloperPost => new Vector4(0.70f, 1f, 1f, 1f),
        LodestoneEntryKind.IcyVeins => new Vector4(0.70f, 0.88f, 1f, 1f),
        LodestoneEntryKind.IcyVeinsGuide => new Vector4(0.70f, 0.88f, 1f, 1f),
        _ => new Vector4(0.92f, 0.86f, 0.66f, 1f)
    };

    private static uint KindColor(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => Color(0.08f, 0.35f, 0.28f, 0.86f),
        LodestoneEntryKind.Maintenance => Color(0.55f, 0.20f, 0.12f, 0.86f),
        LodestoneEntryKind.Notice => Color(0.28f, 0.22f, 0.46f, 0.86f),
        LodestoneEntryKind.Update => Color(0.18f, 0.31f, 0.55f, 0.86f),
        LodestoneEntryKind.Status => Color(0.48f, 0.34f, 0.08f, 0.86f),
        LodestoneEntryKind.Recovery => Color(0.30f, 0.24f, 0.56f, 0.86f),
        LodestoneEntryKind.DeveloperPost => Color(0.08f, 0.32f, 0.36f, 0.86f),
        LodestoneEntryKind.IcyVeins => Color(0.08f, 0.20f, 0.42f, 0.86f),
        LodestoneEntryKind.IcyVeinsGuide => Color(0.10f, 0.24f, 0.48f, 0.86f),
        _ => Color(0.30f, 0.25f, 0.12f, 0.86f)
    };

    private static uint DayTileColor(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => Color(0.08f, 0.40f, 0.27f, 0.94f),
        LodestoneEntryKind.Maintenance => Color(0.56f, 0.16f, 0.11f, 0.95f),
        LodestoneEntryKind.Recovery => Color(0.30f, 0.20f, 0.58f, 0.94f),
        LodestoneEntryKind.Status => Color(0.58f, 0.39f, 0.08f, 0.95f),
        LodestoneEntryKind.Update => Color(0.13f, 0.31f, 0.58f, 0.94f),
        LodestoneEntryKind.Notice => Color(0.58f, 0.42f, 0.10f, 0.94f),
        LodestoneEntryKind.DeveloperPost => Color(0.07f, 0.33f, 0.38f, 0.94f),
        LodestoneEntryKind.IcyVeins => Color(0.08f, 0.22f, 0.48f, 0.94f),
        LodestoneEntryKind.IcyVeinsGuide => Color(0.10f, 0.25f, 0.52f, 0.94f),
        _ => Color(0.48f, 0.36f, 0.13f, 0.94f)
    };

    private static uint DayTileBorderColor(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => Color(0.50f, 1.00f, 0.73f, 0.55f),
        LodestoneEntryKind.Maintenance => Color(1.00f, 0.55f, 0.42f, 0.58f),
        LodestoneEntryKind.Recovery => Color(0.82f, 0.70f, 1.00f, 0.55f),
        LodestoneEntryKind.Status => Color(1.00f, 0.82f, 0.36f, 0.58f),
        LodestoneEntryKind.Update => Color(0.58f, 0.76f, 1.00f, 0.55f),
        LodestoneEntryKind.Notice => Color(1.00f, 0.84f, 0.38f, 0.58f),
        LodestoneEntryKind.DeveloperPost => Color(0.52f, 1.00f, 1.00f, 0.55f),
        LodestoneEntryKind.IcyVeins => Color(0.56f, 0.78f, 1.00f, 0.55f),
        LodestoneEntryKind.IcyVeinsGuide => Color(0.58f, 0.80f, 1.00f, 0.55f),
        _ => Color(1.00f, 0.82f, 0.42f, 0.50f)
    };

    private static IEnumerable<string> TrimDisplayImages(IEnumerable<string> urls)
    {
        foreach (var url in urls)
        {
            yield return url;
            if (url.Equals(LodestoneImageStopMarker, StringComparison.OrdinalIgnoreCase))
                yield break;
        }
    }

    private static uint WithAlpha(Vector4 color, float alpha)
        => ToColor(new Vector4(color.X, color.Y, color.Z, Math.Clamp(alpha, 0f, 1f)));

    private static uint ToColor(Vector4 color)
        => ImGui.ColorConvertFloat4ToU32(color);

    private static uint Color(float r, float g, float b, float a)
    {
        uint R(float value) => (uint)Math.Clamp(value * 255f, 0, 255);
        return R(r) | R(g) << 8 | R(b) << 16 | R(a) << 24;
    }

    private sealed record ArticleInlineRun(string Text, bool Bold, bool Italic);

    private sealed record DayHoverRow(string Text, uint Background, LodestoneEntry? Entry, SubmarineReturn? SubmarineReturn);

    private sealed record DayHeroPreview(DateTime Day, string HeroImageUrl);

    private sealed record DayHoverOverlay(DateTime Day, Vector2 Min, Vector2 Max, CalendarNote[] Notes, PartyEvent[] PartyEvents, SubmarineReturn[] SubmarineReturns, LodestoneEntry[] Entries, bool CurrentMonth)
    {
        public Vector2 PopupMin { get; init; }
        public Vector2 PopupMax { get; init; }
        public bool HasPopupBounds { get; init; }
    }

    private sealed record CalendarDayData(
        LodestoneEntry[] Entries,
        CalendarNote[] Notes,
        PartyEvent[] PartyEvents,
        SubmarineReturn[] SubmarineReturns,
        string HeroImageUrl,
        string[] HeroImageCandidates,
        LodestoneEntryKind[] CornerKinds,
        bool HasMultiDayEvent,
        bool HasEventStart,
        bool HasEventEnd,
        bool HasSubmarineReturn)
    {
        public static readonly CalendarDayData Empty = new([], [], [], [], string.Empty, [], [], false, false, false, false);
    }

    private sealed record CalendarDayCache(CalendarDayCacheKey Key, Dictionary<DateTime, CalendarDayData> Data);

    private sealed record AgendaCache(AgendaCacheKey Key, LodestoneEntry[] Entries);

    private readonly record struct CalendarDayCacheKey(
        DateTime Start,
        DateTime End,
        int EntriesVersion,
        int PartyEventsVersion,
        string SearchText,
        bool ShowEvents,
        bool ShowTopics,
        bool ShowNotices,
        bool ShowMaintenance,
        bool ShowUpdates,
        bool ShowStatus,
        bool ShowRecovery,
        bool ShowDeveloperPosts,
        bool ShowIcyVeins,
        bool ShowIcyVeinsGuides,
        bool ShowPartyEvents,
        bool ShowSubmarineReturns,
        int SubmarineReturnsVersion,
        DateTime CurrentFutureCutoff,
        int HiddenEntrySignature,
        int NoteSignature,
        int PrioritySignature);

    private readonly record struct AgendaCacheKey(
        int EntriesVersion,
        string SearchText,
        bool ShowEvents,
        bool ShowTopics,
        bool ShowNotices,
        bool ShowMaintenance,
        bool ShowUpdates,
        bool ShowStatus,
        bool ShowRecovery,
        bool ShowDeveloperPosts,
        bool ShowIcyVeins,
        bool ShowIcyVeinsGuides,
        DateTime CurrentFutureCutoff,
        int HiddenEntrySignature,
        int PrioritySignature);

    private sealed class CalendarDayBucket
    {
        public List<LodestoneEntry> Entries { get; } = [];
        public List<CalendarNote> Notes { get; } = [];
        public List<PartyEvent> PartyEvents { get; } = [];
        public List<SubmarineReturn> SubmarineReturns { get; } = [];
    }
}
