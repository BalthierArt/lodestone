using Dalamud.Interface;
using Dalamud.Interface.Textures;
using Dalamud.Interface.Utility;
using Lodestone.Models;

namespace Lodestone.Windows;

public sealed partial class CalendarWindow
{
    private PartyEvent? selectedPartyEvent;
    private PartyEvent? editingPartyEvent;
    private bool partyPlannerOpen;
    private bool partyPlannerNeedsPlacement;
    private bool partyEventWindowNeedsPlacement;
    private bool partyOperationInProgress;
    private DateTime partyPlannerDate = DateTime.Today;
    private string partyPlannerTitle = string.Empty;
    private string partyPlannerDescription = string.Empty;
    private bool partyPlannerHasTime;
    private int partyPlannerHour = 21;
    private int partyPlannerMinute;
    private int partyPlannerIconIndex;

    private IEnumerable<PartyEvent> GetPartyEventsForDay(DateTime day)
    {
        if (!plugin.Configuration.ShowPartyEvents)
            return [];

        return plugin.PartySyncService.GetEventsForDay(day);
    }

    private bool DrawPartyFilterToggle(ref bool value)
    {
        var icon = PartyEventIcons.All[0];
        DrawInlineGameIcon(icon.IconId, new Vector2(18f, 18f) * ImGuiHelpers.GlobalScale);
        ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
        return ImGui.Checkbox("Party##filterPartyEvents", ref value);
    }

    private void DrawPartyEventCenterIcon(IReadOnlyList<PartyEvent> partyEvents, Vector2 min, Vector2 max, bool currentMonth)
    {
        if (partyEvents.Count == 0)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var primary = partyEvents[0];
        var icon = PartyEventIcons.Get(primary.IconKey);
        var draw = ImGui.GetWindowDrawList();
        var size = MathF.Min(max.X - min.X, max.Y - min.Y) * 0.36f;
        size = Math.Clamp(size, 30f * scale, 56f * scale);
        var center = (min + max) * 0.5f;
        var iconMin = center - new Vector2(size * 0.5f);
        var iconMax = iconMin + new Vector2(size);

        draw.AddRectFilled(iconMin - new Vector2(4f * scale), iconMax + new Vector2(4f * scale), Color(0f, 0f, 0f, currentMonth ? 0.54f : 0.36f), 6f * scale);
        DrawGameIcon(icon.IconId, iconMin, size, new Vector4(1f, 1f, 1f, currentMonth ? 0.95f : 0.58f));

        if (partyEvents.Count <= 1)
            return;

        var count = partyEvents.Count.ToString();
        var countSize = ImGui.CalcTextSize(count);
        var badgeSize = MathF.Max(countSize.X, countSize.Y) + 8f * scale;
        var badgeMin = iconMax - new Vector2(badgeSize * 0.65f);
        var badgeMax = badgeMin + new Vector2(badgeSize);
        draw.AddRectFilled(badgeMin, badgeMax, Color(0.42f, 0.22f, 0.78f, 0.98f), badgeSize * 0.5f);
        draw.AddText(badgeMin + new Vector2((badgeSize - countSize.X) * 0.5f, (badgeSize - countSize.Y) * 0.5f), Color(1f, 1f, 1f, 1f), count);
    }

    private void DrawPartyEventChip(PartyEvent partyEvent, Vector2 min, Vector2 max, float y)
    {
        var drawList = ImGui.GetWindowDrawList();
        var chipMin = new Vector2(min.X + 5f * ImGuiHelpers.GlobalScale, y);
        var chipMax = new Vector2(max.X - 5f * ImGuiHelpers.GlobalScale, y + 19f * ImGuiHelpers.GlobalScale);
        drawList.AddRectFilled(chipMin, chipMax, PartyEventColor(), 2f);
        var icon = PartyEventIcons.Get(partyEvent.IconKey);
        DrawGameIcon(icon.IconId, chipMin + new Vector2(2f, 1.5f) * ImGuiHelpers.GlobalScale, 16f * ImGuiHelpers.GlobalScale, Vector4.One);
        ImGui.SetCursorScreenPos(chipMin + new Vector2(22f, 1f) * ImGuiHelpers.GlobalScale);
        ImGui.PushTextWrapPos(chipMax.X - 4f * ImGuiHelpers.GlobalScale);
        ImGui.TextUnformatted(PartyEventLabel(partyEvent));
        ImGui.PopTextWrapPos();
    }

    private void DrawDayPartyEventTile(PartyEvent partyEvent)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(240f * scale, ImGui.GetContentRegionAvail().X);
        var height = 54f * scale;
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();
        var icon = PartyEventIcons.Get(partyEvent.IconKey);

        draw.AddRectFilled(start, start + size, PartyEventTileColor(), 7f * scale);
        draw.AddRect(start, start + size, PartyEventTileBorderColor(), 7f * scale, 0, 1.2f * scale);
        draw.AddRectFilled(start + new Vector2(9f, 10f) * scale, start + new Vector2(39f, 40f) * scale, Color(0f, 0f, 0f, 0.50f), 6f * scale);
        DrawGameIcon(icon.IconId, start + new Vector2(12f, 13f) * scale, 24f * scale, Vector4.One);

        var titleWidth = width - 58f * scale;
        draw.AddText(new Vector2(start.X + 48f * scale, start.Y + 7f * scale), Color(1f, 1f, 1f, 1f), FitTextToWidth(partyEvent.Title, titleWidth));
        var meta = $"{icon.Label}  |  {PartyEventWhen(partyEvent)}  |  {partyEvent.Responses.Count} signed";
        draw.AddText(new Vector2(start.X + 48f * scale, start.Y + 31f * scale), Color(1f, 1f, 1f, 0.72f), FitTextToWidth(meta, titleWidth));

        if (ImGui.InvisibleButton($"##partyEventTile{partyEvent.Id}", size))
            SelectPartyEvent(partyEvent);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            draw.AddRect(start, start + size, Color(1f, 1f, 1f, 0.62f), 7f * scale, 0, 2f * scale);
            ImGui.SetTooltip($"{partyEvent.Title}\n{PartyEventWhen(partyEvent)}");
        }

        ImGui.Spacing();
    }

    private void OpenPartyPlanner(DateTime day, PartyEvent? partyEvent)
    {
        editingPartyEvent = partyEvent;
        partyPlannerDate = partyEvent?.Date.Date ?? day.Date;
        partyPlannerTitle = partyEvent?.Title ?? string.Empty;
        partyPlannerDescription = partyEvent?.Description ?? string.Empty;
        partyPlannerHasTime = partyEvent?.HasTime ?? false;
        partyPlannerHour = partyEvent?.Hour ?? 21;
        partyPlannerMinute = partyEvent?.Minute ?? 0;
        var iconKey = partyEvent?.IconKey ?? PartyEventIcons.DefaultKey;
        partyPlannerIconIndex = Math.Max(0, Array.FindIndex(PartyEventIcons.All, icon => icon.Key.Equals(iconKey, StringComparison.OrdinalIgnoreCase)));
        partyPlannerNeedsPlacement = true;
        partyPlannerOpen = true;
    }

    private void DrawPartyPlannerWindow()
    {
        if (!partyPlannerOpen)
            return;

        var open = true;
        PrimeSideWindowPlacement(new Vector2(500, 500) * ImGuiHelpers.GlobalScale, ref partyPlannerNeedsPlacement);
        if (!ImGui.Begin($"{(editingPartyEvent == null ? "Plan" : "Edit")} Party Event##LodestonePartyPlanner", ref open))
        {
            ImGui.End();
            if (!open)
                partyPlannerOpen = false;
            return;
        }

        if (!open)
            partyPlannerOpen = false;

        if (partyPlannerOpen)
        {
            using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f);
            using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 7f);
            using var button = ImRaii.PushColor(ImGuiCol.Button, DetailPrimary);
            using var buttonHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, DetailAccent);
            using var buttonActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.32f, 0.16f, 0.62f, 1f));

            DrawPartyPlannerHeader();
            DrawPartyPlannerBody();
            DrawPartyPlannerActions();
        }

        ImGui.End();
    }

    private void DrawPartyPlannerHeader()
    {
        var scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = 78f * scale;
        var icon = PartyEventIcons.All[Math.Clamp(partyPlannerIconIndex, 0, PartyEventIcons.All.Length - 1)];

        draw.AddRectFilled(start, start + new Vector2(width, height), DetailPanelElevated, 8f * scale);
        draw.AddRect(start, start + new Vector2(width, height), DetailPanelPurple, 8f * scale, 0, 1.4f * scale);
        DrawGameIcon(icon.IconId, start + new Vector2(14f, 14f) * scale, 48f * scale, Vector4.One);

        ImGui.SetCursorScreenPos(start + new Vector2(74f, 12f) * scale);
        ImGui.TextColored(DetailMuted, editingPartyEvent == null ? "Plan shared party event" : "Edit shared party event");
        ImGui.SetCursorScreenPos(start + new Vector2(74f, 38f) * scale);
        ImGui.SetWindowFontScale(1.08f);
        ImGui.TextColored(Vector4.One, partyPlannerDate.ToLongDateString());
        ImGui.SetWindowFontScale(1f);
        ImGui.SetCursorScreenPos(start + new Vector2(0, height + 10f * scale));
    }

    private void DrawPartyPlannerBody()
    {
        var scale = ImGuiHelpers.GlobalScale;
        using var bg = ImRaii.PushColor(ImGuiCol.ChildBg, ImGui.ColorConvertU32ToFloat4(DetailPanel));
        using var border = ImRaii.PushColor(ImGuiCol.Border, DetailPrimary);
        using var child = ImRaii.Child("##partyPlannerBody", new Vector2(0, 326f * scale), true);
        if (!child.Success)
            return;

        if (!plugin.PartySyncService.CanCreateEvents)
        {
            TextWrappedColored(new Vector4(1f, 0.25f, 0.25f, 1f), "PARTY SYNC IS DISABLED. This event cannot be saved or shared until Supabase or the External IPC Bridge is enabled.");
            if (ImGui.Button("Open Party Sync Settings##partyPlannerSettings", new Vector2(210, 0) * scale))
                plugin.ConfigWindow.OpenPartySyncSettings();
            ImGui.Separator();
            ImGui.Spacing();
        }

        ImGui.TextColored(DetailGreen, "Title");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextWithHint("##partyTitle", "New Extreme Trial. Who's interested?", ref partyPlannerTitle, 160);

        ImGui.Spacing();
        ImGui.TextColored(DetailGreen, "Details");
        ImGui.SetNextItemWidth(-1f);
        ImGui.InputTextMultiline("##partyDescription", ref partyPlannerDescription, 600, new Vector2(-1f, 78f * scale));

        ImGui.Spacing();
        ImGui.Checkbox("Set time", ref partyPlannerHasTime);
        if (partyPlannerHasTime)
        {
            ImGui.SetNextItemWidth(90f * scale);
            ImGui.InputInt("Hour##partyHour", ref partyPlannerHour);
            ImGui.SameLine();
            ImGui.SetNextItemWidth(90f * scale);
            ImGui.InputInt("Minute##partyMinute", ref partyPlannerMinute);
            partyPlannerHour = Math.Clamp(partyPlannerHour, 0, 23);
            partyPlannerMinute = Math.Clamp(partyPlannerMinute, 0, 59);
        }

        ImGui.Spacing();
        DrawPartyIconPicker();
    }

    private void DrawPartyIconPicker()
    {
        var icon = PartyEventIcons.All[Math.Clamp(partyPlannerIconIndex, 0, PartyEventIcons.All.Length - 1)];
        ImGui.TextColored(DetailGreen, "Calendar icon");
        ImGui.SetNextItemWidth(-1f);
        using var combo = ImRaii.Combo("##partyIconPicker", icon.Label);
        if (!combo.Success)
            return;

        for (var i = 0; i < PartyEventIcons.All.Length; i++)
        {
            var option = PartyEventIcons.All[i];
            DrawInlineGameIcon(option.IconId, new Vector2(22f, 22f) * ImGuiHelpers.GlobalScale);
            ImGui.SameLine();
            if (ImGui.Selectable($"{option.Label}##partyIcon{i}", partyPlannerIconIndex == i))
                partyPlannerIconIndex = i;
        }
    }

    private void DrawPartyPlannerActions()
    {
        using (ImRaii.Disabled(partyOperationInProgress || string.IsNullOrWhiteSpace(partyPlannerTitle) || !plugin.PartySyncService.CanCreateEvents))
        {
            if (ImGui.Button("Save##partyPlannerSave", new Vector2(90, 0) * ImGuiHelpers.GlobalScale))
                _ = SavePartyPlannerAsync();
        }

        if (editingPartyEvent != null && plugin.PartySyncService.IsCreator(editingPartyEvent))
        {
            ImGui.SameLine();
            using var deleteButton = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.62f, 0.08f, 0.08f, 0.96f));
            using var deleteHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.90f, 0.18f, 0.20f, 1f));
            using (ImRaii.Disabled(partyOperationInProgress))
            {
                if (ImGui.Button("Delete##partyPlannerDelete", new Vector2(90, 0) * ImGuiHelpers.GlobalScale))
                    _ = DeletePartyEventAsync(editingPartyEvent);
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Cancel##partyPlannerCancel", new Vector2(90, 0) * ImGuiHelpers.GlobalScale))
            partyPlannerOpen = false;
    }

    private async Task SavePartyPlannerAsync()
    {
        partyOperationInProgress = true;
        try
        {
            var icon = PartyEventIcons.All[Math.Clamp(partyPlannerIconIndex, 0, PartyEventIcons.All.Length - 1)];
            var partyEvent = editingPartyEvent ?? new PartyEvent();
            partyEvent.Date = partyPlannerDate.Date;
            partyEvent.Hour = partyPlannerHasTime ? partyPlannerHour : null;
            partyEvent.Minute = partyPlannerHasTime ? partyPlannerMinute : null;
            partyEvent.Title = partyPlannerTitle.Trim();
            partyEvent.Description = partyPlannerDescription.Trim();
            partyEvent.IconKey = icon.Key;

            if (await plugin.PartySyncService.SaveEventAsync(partyEvent))
            {
                partyPlannerOpen = false;
                selectedPartyEvent = plugin.PartySyncService.Events.FirstOrDefault(e => e.Id == partyEvent.Id) ?? partyEvent;
                partyEventWindowNeedsPlacement = true;
            }
        }
        finally
        {
            partyOperationInProgress = false;
        }
    }

    private async Task DeletePartyEventAsync(PartyEvent partyEvent)
    {
        partyOperationInProgress = true;
        try
        {
            if (await plugin.PartySyncService.DeleteEventAsync(partyEvent))
            {
                partyPlannerOpen = false;
                selectedPartyEvent = null;
            }
        }
        finally
        {
            partyOperationInProgress = false;
        }
    }

    private void SelectPartyEvent(PartyEvent partyEvent)
    {
        selectedPartyEvent = partyEvent;
        selectedEntry = null;
        selectedDay = null;
        partyEventWindowNeedsPlacement = true;
    }

    private void DrawPartyEventWindow()
    {
        if (selectedPartyEvent == null)
            return;

        selectedPartyEvent = plugin.PartySyncService.Events.FirstOrDefault(e => e.Id.Equals(selectedPartyEvent.Id, StringComparison.OrdinalIgnoreCase)) ?? selectedPartyEvent;
        var open = true;
        PrimeSideWindowPlacement(new Vector2(620, 560) * ImGuiHelpers.GlobalScale, ref partyEventWindowNeedsPlacement);
        if (!ImGui.Begin($"Party Event##{selectedPartyEvent.Id}", ref open))
        {
            ImGui.End();
            if (!open)
                selectedPartyEvent = null;
            return;
        }

        if (!open)
            selectedPartyEvent = null;

        if (selectedPartyEvent != null)
        {
            using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f);
            using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 7f);
            using var button = ImRaii.PushColor(ImGuiCol.Button, DetailPrimary);
            using var buttonHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, DetailAccent);
            using var buttonActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.32f, 0.16f, 0.62f, 1f));

            DrawPartyEventHeader(selectedPartyEvent);
            DrawPartyEventDetails(selectedPartyEvent);
            DrawPartyEventActions(selectedPartyEvent);
        }

        ImGui.End();
    }

    private void DrawPartyEventHeader(PartyEvent partyEvent)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = 88f * scale;
        var icon = PartyEventIcons.Get(partyEvent.IconKey);

        draw.AddRectFilled(start, start + new Vector2(width, height), DetailPanelElevated, 8f * scale);
        draw.AddRect(start, start + new Vector2(width, height), DetailPanelPurple, 8f * scale, 0, 1.5f * scale);
        DrawGameIcon(icon.IconId, start + new Vector2(14f, 18f) * scale, 52f * scale, Vector4.One);

        ImGui.SetCursorScreenPos(start + new Vector2(78f, 12f) * scale);
        ImGui.TextColored(DetailMuted, $"{icon.Label}  |  {PartyEventWhen(partyEvent)}");
        ImGui.SetCursorScreenPos(start + new Vector2(78f, 36f) * scale);
        ImGui.SetWindowFontScale(1.08f);
        ImGui.TextColored(Vector4.One, partyEvent.Title);
        ImGui.SetWindowFontScale(1f);
        ImGui.SetCursorScreenPos(start + new Vector2(78f, 64f) * scale);
        ImGui.TextColored(DetailMuted, $"Created by {CreatorLabel(partyEvent)}");
        ImGui.SetCursorScreenPos(start + new Vector2(0, height + 10f * scale));
    }

    private void DrawPartyEventDetails(PartyEvent partyEvent)
    {
        using var body = ImRaii.Child("##partyEventBody", new Vector2(0, -42f * ImGuiHelpers.GlobalScale), false);
        if (!body.Success)
            return;

        DrawDetailPanel("##partyEventDescription", DetailPanel, () =>
        {
            ImGui.TextColored(DetailGreen, "Details");
            if (string.IsNullOrWhiteSpace(partyEvent.Description))
                ImGui.TextColored(DetailMuted, "No extra details were added.");
            else
                WrappedText(partyEvent.Description);
        });

        var interested = partyEvent.Responses.Where(r => r.Status == PartyEventResponseStatus.Interested).OrderBy(r => r.PlayerName).ToArray();
        var maybe = partyEvent.Responses.Where(r => r.Status == PartyEventResponseStatus.Maybe).OrderBy(r => r.PlayerName).ToArray();
        DrawResponsePanel("Interested", FontAwesomeIcon.CheckCircle, new Vector4(0.62f, 0.90f, 0.42f, 1f), interested);
        DrawResponsePanel("Maybe", FontAwesomeIcon.QuestionCircle, new Vector4(1f, 0.56f, 0.90f, 1f), maybe);
    }

    private void DrawResponsePanel(string title, FontAwesomeIcon icon, Vector4 color, IReadOnlyList<PartyEventResponse> responses)
    {
        DrawDetailPanel($"##partyResponse{title}", DetailPanel, () =>
        {
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(color, icon.ToIconString());
            ImGui.SameLine();
            ImGui.TextColored(color, $"{title} ({responses.Count})");
            if (responses.Count == 0)
            {
                ImGui.TextColored(DetailMuted, "Nobody yet.");
                return;
            }

            foreach (var response in responses)
                ImGui.BulletText(PlayerLabel(response.PlayerName, response.PlayerWorld));
        });
    }

    private void DrawPartyEventActions(PartyEvent partyEvent)
    {
        var currentResponse = plugin.PartySyncService.CurrentResponse(partyEvent);
        using (ImRaii.Disabled(partyOperationInProgress || !plugin.PartySyncService.CanCreateEvents))
        {
            if (ImGui.Button(currentResponse?.Status == PartyEventResponseStatus.Interested ? "Interested (set)" : "Interested"))
                _ = RespondPartyEventAsync(PartyEventResponseStatus.Interested);
            ImGui.SameLine();
            if (ImGui.Button(currentResponse?.Status == PartyEventResponseStatus.Maybe ? "Maybe ?" : "Maybe"))
                _ = RespondPartyEventAsync(PartyEventResponseStatus.Maybe);
            ImGui.SameLine();
            using (ImRaii.Disabled(currentResponse == null))
            {
                if (ImGui.Button("Remove"))
                    _ = RespondPartyEventAsync(null);
            }
        }

        if (plugin.PartySyncService.IsCreator(partyEvent))
        {
            ImGui.SameLine();
            if (ImGui.Button("Edit"))
                OpenPartyPlanner(partyEvent.Date, partyEvent);
        }
    }

    private async Task RespondPartyEventAsync(PartyEventResponseStatus? status)
    {
        if (selectedPartyEvent == null)
            return;

        partyOperationInProgress = true;
        try
        {
            await plugin.PartySyncService.RespondAsync(selectedPartyEvent, status);
        }
        finally
        {
            partyOperationInProgress = false;
        }
    }

    private static void DrawGameIcon(uint iconId, Vector2 min, float size, Vector4 tint)
    {
        try
        {
            var texture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId, itemHq: false, hiRes: true, language: null));
            var wrap = texture.GetWrapOrEmpty();
            ImGui.GetWindowDrawList().AddImage(wrap.Handle, min, min + new Vector2(size), Vector2.Zero, Vector2.One, ToColor(tint));
        }
        catch
        {
            using var font = ImRaii.PushFont(UiBuilder.IconFont);
            var fallback = FontAwesomeIcon.Users.ToIconString();
            ImGui.GetWindowDrawList().AddText(min, Color(1f, 1f, 1f, tint.W), fallback);
        }
    }

    private static void DrawInlineGameIcon(uint iconId, Vector2 size)
    {
        try
        {
            var texture = Plugin.TextureProvider.GetFromGameIcon(new GameIconLookup(iconId, itemHq: false, hiRes: true, language: null));
            ImGui.Image(texture.GetWrapOrEmpty().Handle, size);
        }
        catch
        {
            using var font = ImRaii.PushFont(UiBuilder.IconFont);
            ImGui.TextUnformatted(FontAwesomeIcon.Users.ToIconString());
        }
    }

    private static string PartyEventLabel(PartyEvent partyEvent)
        => partyEvent.ScheduledAt is { } scheduledAt
            ? $"{scheduledAt:t} {partyEvent.Title}"
            : partyEvent.Title;

    private static string PartyEventWhen(PartyEvent partyEvent)
        => partyEvent.ScheduledAt is { } scheduledAt
            ? scheduledAt.ToString("g")
            : partyEvent.Date.ToLongDateString();

    private static string CreatorLabel(PartyEvent partyEvent)
        => PlayerLabel(partyEvent.CreatorName, partyEvent.CreatorWorld);

    private static string PlayerLabel(string name, string world)
        => string.IsNullOrWhiteSpace(world) ? name : $"{name} @ {world}";

    private static uint PartyEventColor()
        => Color(0.16f, 0.38f, 0.34f, 0.90f);

    private static uint PartyEventTileColor()
        => Color(0.11f, 0.34f, 0.30f, 0.96f);

    private static uint PartyEventTileBorderColor()
        => Color(0.50f, 1.00f, 0.78f, 0.62f);
}
