using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Lodestone.Models;

namespace Lodestone.Windows;

public sealed partial class CalendarWindow
{
    private SubmarineReturn? selectedSubmarineReturn;
    private bool submarineReturnWindowNeedsPlacement;

    private IEnumerable<SubmarineReturn> GetSubmarineReturnsForDay(DateTime day)
    {
        if (!plugin.Configuration.ShowSubmarineReturns)
            return [];

        return plugin.SubmarineService.GetReturnsForDay(day);
    }

    private void SelectSubmarineReturn(SubmarineReturn submarineReturn)
    {
        selectedSubmarineReturn = submarineReturn;
        selectedEntry = null;
        selectedPartyEvent = null;
        selectedDay = null;
        submarineReturnWindowNeedsPlacement = true;
    }

    private void DrawSubmarineReturnWindow()
    {
        if (selectedSubmarineReturn == null)
            return;

        selectedSubmarineReturn = plugin.SubmarineService.Returns.FirstOrDefault(r => r.Id.Equals(selectedSubmarineReturn.Id, StringComparison.OrdinalIgnoreCase)) ?? selectedSubmarineReturn;
        var open = true;
        PrimeSideWindowPlacement(new Vector2(620, 560) * ImGuiHelpers.GlobalScale, ref submarineReturnWindowNeedsPlacement);
        if (!ImGui.Begin($"Submarine Return##{selectedSubmarineReturn.Id}", ref open))
        {
            ImGui.End();
            if (!open)
                selectedSubmarineReturn = null;
            return;
        }

        if (!open)
            selectedSubmarineReturn = null;

        if (selectedSubmarineReturn != null)
        {
            using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f);
            using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 7f);
            using var button = ImRaii.PushColor(ImGuiCol.Button, DetailPrimary);
            using var buttonHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, DetailAccent);
            using var buttonActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.32f, 0.16f, 0.62f, 1f));

            DrawSubmarineReturnHeader(selectedSubmarineReturn);
            DrawSubmarineReturnHero();
            DrawSubmarineReturnDetails(selectedSubmarineReturn);
            DrawSubmarineReturnActions();
        }

        ImGui.End();
    }

    private void DrawSubmarineReturnHeader(SubmarineReturn submarineReturn)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = 88f * scale;

        draw.AddRectFilled(start, start + new Vector2(width, height), DetailPanelElevated, 8f * scale);
        draw.AddRect(start, start + new Vector2(width, height), DetailPanelPurple, 8f * scale, 0, 1.5f * scale);
        DrawSubmarineAssetIcon(start + new Vector2(14f, 18f) * scale, 52f * scale, Vector4.One);

        ImGui.SetCursorScreenPos(start + new Vector2(78f, 12f) * scale);
        ImGui.TextColored(DetailMuted, $"{(submarineReturn.Returned ? "Returned" : "Returning")}  |  {SubmarineReturnWhen(submarineReturn)}");
        ImGui.SetCursorScreenPos(start + new Vector2(78f, 36f) * scale);
        ImGui.SetWindowFontScale(1.08f);
        ImGui.TextColored(Vector4.One, string.IsNullOrWhiteSpace(submarineReturn.VesselName) ? "Submarine voyage" : submarineReturn.VesselName);
        ImGui.SetWindowFontScale(1f);
        ImGui.SetCursorScreenPos(start + new Vector2(78f, 64f) * scale);
        ImGui.TextColored(DetailMuted, SubmarineOwnerLabel(submarineReturn));
        ImGui.SetCursorScreenPos(start + new Vector2(0, height + 10f * scale));
    }

    private void DrawSubmarineReturnHero()
    {
        var texture = plugin.ImageCache.GetTexture(SubmarineReturnedHeroAsset);
        if (texture == null)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var draw = ImGui.GetWindowDrawList();
        var start = ImGui.GetCursorScreenPos();
        var availableWidth = ImGui.GetContentRegionAvail().X;
        var padding = 8f * scale;
        var imageWidth = Math.Min(availableWidth - padding * 2f, 560f * scale);
        var imageHeight = Math.Min(170f * scale, imageWidth * 0.36f);
        var panelSize = new Vector2(imageWidth + padding * 2f, imageHeight + padding * 2f);

        draw.AddRectFilled(start, start + panelSize, DetailPanel, 8f * scale);
        draw.AddImage(texture.Handle, start + new Vector2(padding), start + new Vector2(padding + imageWidth, padding + imageHeight), Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, 1f));
        draw.AddRect(start, start + panelSize, DetailPanelPurple, 8f * scale, 0, 1f * scale);
        ImGui.Dummy(panelSize + new Vector2(0, 8f * scale));
    }

    private void DrawSubmarineReturnDetails(SubmarineReturn submarineReturn)
    {
        using var body = ImRaii.Child("##submarineReturnBody", new Vector2(0, -42f * ImGuiHelpers.GlobalScale), false);
        if (!body.Success)
            return;

        DrawDetailPanel("##submarineReturnSummary", DetailPanel, () =>
        {
            ImGui.TextColored(DetailBlue, "Voyage");
            DetailLine("Submarine", submarineReturn.VesselName);
            DetailLine("Character", SubmarineOwnerLabel(submarineReturn));
            DetailLine("Return time", submarineReturn.ReturnAt.ToString("F"));
            DetailLine(submarineReturn.Returned ? "Returned" : "Remaining", SubmarineReturnRelativeTime(submarineReturn));
        });

        DrawDetailPanel("##submarineReturnAutoRetainer", DetailPanel, () =>
        {
            ImGui.TextColored(DetailBlue, "AutoRetainer");
            DetailLine("Workshop enabled", YesNo(submarineReturn.WorkshopEnabled));
            DetailLine("Enabled sub", YesNo(submarineReturn.EnabledInAutoRetainer));
            DetailLine("Character excluded", YesNo(submarineReturn.CharacterExcluded));
            DetailLine("Wait for all deployables", YesNo(submarineReturn.WaitForAllDeployables));
        });

        if (submarineReturn.Level > 0 || submarineReturn.CurrentExp > 0 || submarineReturn.NextLevelExp > 0 || !string.IsNullOrWhiteSpace(submarineReturn.Behavior))
        {
            DrawDetailPanel("##submarineReturnVesselStats", DetailPanel, () =>
            {
                ImGui.TextColored(DetailBlue, "Vessel data");
                if (submarineReturn.Level > 0)
                    DetailLine("Level", submarineReturn.Level.ToString());
                if (submarineReturn.CurrentExp > 0 || submarineReturn.NextLevelExp > 0)
                    DetailLine("EXP", submarineReturn.NextLevelExp > 0 ? $"{submarineReturn.CurrentExp:N0} / {submarineReturn.NextLevelExp:N0}" : submarineReturn.CurrentExp.ToString("N0"));
                if (!string.IsNullOrWhiteSpace(submarineReturn.Behavior))
                    DetailLine("Behavior", submarineReturn.Behavior);
            });
        }

        if (!string.IsNullOrWhiteSpace(submarineReturn.SelectedPointPlan) || !string.IsNullOrWhiteSpace(submarineReturn.SelectedUnlockPlan) || submarineReturn.Points.Length > 0)
        {
            DrawDetailPanel("##submarineReturnRoute", DetailPanel, () =>
            {
                ImGui.TextColored(DetailBlue, "Route");
                if (!string.IsNullOrWhiteSpace(submarineReturn.SelectedPointPlan))
                    DetailLine("Point plan", submarineReturn.SelectedPointPlan);
                if (!string.IsNullOrWhiteSpace(submarineReturn.SelectedUnlockPlan))
                    DetailLine("Unlock plan", submarineReturn.SelectedUnlockPlan);
                if (submarineReturn.Points.Length > 0)
                    DetailLine("Points", string.Join(", ", submarineReturn.Points.Where(point => point > 0)));
            });
        }
    }

    private static void DetailLine(string label, string value)
    {
        ImGui.TextColored(DetailMuted, label);
        ImGui.SameLine(145f * ImGuiHelpers.GlobalScale);
        TextWrappedColored(Vector4.One, string.IsNullOrWhiteSpace(value) ? "Unknown" : value);
    }

    private void DrawSubmarineReturnActions()
    {
        if (ImGui.Button("Refresh AutoRetainer"))
            plugin.SubmarineService.Refresh(force: true);
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Re-read submarine return data from AutoRetainer IPC.");

        ImGui.SameLine();
        ImGui.TextColored(DetailMuted, plugin.SubmarineService.Status);
    }

    private void DrawDaySubmarineReturnTile(SubmarineReturn submarineReturn)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = Math.Max(240f * scale, ImGui.GetContentRegionAvail().X);
        var height = 54f * scale;
        var start = ImGui.GetCursorScreenPos();
        var size = new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();

        draw.AddRectFilled(start, start + size, SubmarineReturnTileColor(), 7f * scale);
        draw.AddRect(start, start + size, SubmarineReturnTileBorderColor(), 7f * scale, 0, 1.2f * scale);
        draw.AddRectFilled(start + new Vector2(9f, 10f) * scale, start + new Vector2(39f, 40f) * scale, Color(0f, 0f, 0f, 0.50f), 6f * scale);
        DrawSubmarineAssetIcon(start + new Vector2(12f, 13f) * scale, 24f * scale, Vector4.One);

        var titleWidth = width - 58f * scale;
        draw.AddText(new Vector2(start.X + 48f * scale, start.Y + 7f * scale), Color(1f, 1f, 1f, 1f), FitTextToWidth(SubmarineReturnLabel(submarineReturn), titleWidth));
        var meta = $"{SubmarineOwnerLabel(submarineReturn)}  |  {SubmarineReturnRelativeTime(submarineReturn)}";
        draw.AddText(new Vector2(start.X + 48f * scale, start.Y + 31f * scale), Color(1f, 1f, 1f, 0.72f), FitTextToWidth(meta, titleWidth));

        if (ImGui.InvisibleButton($"##submarineReturnTile{submarineReturn.Id}", size))
            SelectSubmarineReturn(submarineReturn);

        if (ImGui.IsItemHovered())
        {
            ImGui.SetMouseCursor(ImGuiMouseCursor.Hand);
            draw.AddRect(start, start + size, Color(1f, 1f, 1f, 0.62f), 7f * scale, 0, 2f * scale);
            ImGui.SetTooltip($"{submarineReturn.VesselName}\n{submarineReturn.ReturnAt:F}");
        }

        ImGui.Spacing();
    }

    private void DrawSubmarineReturnChip(SubmarineReturn submarineReturn, Vector2 min, Vector2 max, float y)
    {
        var drawList = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var chipMin = new Vector2(min.X + 5f * scale, y);
        var chipMax = new Vector2(max.X - 5f * scale, y + 19f * scale);
        drawList.AddRectFilled(chipMin, chipMax, SubmarineReturnColor(), 2f);
        DrawSubmarineAssetIcon(chipMin + new Vector2(2f, 1.5f) * scale, 16f * scale, Vector4.One);
        ImGui.SetCursorScreenPos(chipMin + new Vector2(22f, 1f) * scale);
        ImGui.PushTextWrapPos(chipMax.X - 4f * scale);
        ImGui.TextUnformatted(SubmarineReturnLabel(submarineReturn));
        ImGui.PopTextWrapPos();
    }

    private void DrawSubmarineReturnMarker(bool hasReturn, bool hasEventBoundary, Vector2 min, Vector2 max, bool currentMonth)
    {
        if (!hasReturn)
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var size = new Vector2(30f, 22f) * scale;
        var markerMin = new Vector2(min.X + (max.X - min.X - size.X) * 0.5f, max.Y - size.Y - (hasEventBoundary ? 27f : 5f) * scale);
        var markerMax = markerMin + size;
        var draw = ImGui.GetWindowDrawList();

        var texture = plugin.ImageCache.GetTexture(SubmarineReturnMarkerAsset);
        if (texture != null)
        {
            draw.AddImage(texture.Handle, markerMin, markerMax, Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, currentMonth ? 1f : 0.68f));
            return;
        }

        DrawSubmarineFallbackIcon(markerMin + new Vector2(5f, 2f) * scale, 17f * scale, currentMonth ? 1f : 0.68f);
    }

    private void DrawSubmarineCornerIcon(Vector2 min, float slot, bool currentMonth)
    {
        var draw = ImGui.GetWindowDrawList();
        var max = min + new Vector2(slot, slot);
        draw.AddRectFilled(min, max, Color(0f, 0f, 0f, currentMonth ? 0.68f : 0.46f), slot * 0.45f);

        var texture = plugin.ImageCache.GetTexture(SubmarineReturnMarkerAsset);
        if (texture != null)
        {
            var insetX = 1.5f * ImGuiHelpers.GlobalScale;
            var insetY = 3f * ImGuiHelpers.GlobalScale;
            draw.AddImage(texture.Handle, min + new Vector2(insetX, insetY), max - new Vector2(insetX, insetY), Vector2.Zero, Vector2.One, Color(1f, 1f, 1f, currentMonth ? 1f : 0.72f));
            return;
        }

        DrawSubmarineFallbackIcon(min, slot, currentMonth ? 1f : 0.72f);
    }

    private bool DrawSubmarineFilterToggle(ref bool value)
    {
        var texture = plugin.ImageCache.GetTexture(SubmarineReturnMarkerAsset);
        if (texture != null)
            ImGui.Image(texture.Handle, new Vector2(22f, 16f) * ImGuiHelpers.GlobalScale);
        else
            DrawInlineSubmarineFallbackIcon();

        ImGui.SameLine(0, 4f * ImGuiHelpers.GlobalScale);
        return ImGui.Checkbox("Subs##filterSubmarineReturns", ref value);
    }

    private void DrawSubmarineAssetIcon(Vector2 min, float size, Vector4 tint)
    {
        var texture = plugin.ImageCache.GetTexture(SubmarineReturnMarkerAsset);
        if (texture != null)
        {
            var iconHeight = size * 0.72f;
            var iconWidth = size;
            var pos = min + new Vector2(0f, (size - iconHeight) * 0.5f);
            ImGui.GetWindowDrawList().AddImage(texture.Handle, pos, pos + new Vector2(iconWidth, iconHeight), Vector2.Zero, Vector2.One, ToColor(tint));
            return;
        }

        DrawSubmarineFallbackIcon(min, size, tint.W);
    }

    private static void DrawSubmarineFallbackIcon(Vector2 min, float size, float alpha)
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        var icon = FontAwesomeIcon.Ship.ToIconString();
        var iconSize = ImGui.CalcTextSize(icon);
        var pos = min + new Vector2((size - iconSize.X) * 0.5f, (size - iconSize.Y) * 0.5f);
        ImGui.GetWindowDrawList().AddText(pos, Color(0.72f, 0.92f, 1f, alpha), icon);
    }

    private static void DrawInlineSubmarineFallbackIcon()
    {
        using var font = ImRaii.PushFont(UiBuilder.IconFont);
        ImGui.TextColored(DetailBlue, FontAwesomeIcon.Ship.ToIconString());
    }

    private static string SubmarineReturnLabel(SubmarineReturn submarineReturn)
    {
        var title = string.IsNullOrWhiteSpace(submarineReturn.VesselName) ? "Submarine" : submarineReturn.VesselName;
        return $"{submarineReturn.ReturnAt:t} {title}";
    }

    private static string SubmarineReturnWhen(SubmarineReturn submarineReturn)
        => submarineReturn.ReturnAt.ToString("g");

    private static string SubmarineOwnerLabel(SubmarineReturn submarineReturn)
    {
        var name = string.IsNullOrWhiteSpace(submarineReturn.CharacterName) ? "Unknown character" : submarineReturn.CharacterName;
        return string.IsNullOrWhiteSpace(submarineReturn.World) ? name : $"{name} @ {submarineReturn.World}";
    }

    private static string SubmarineReturnRelativeTime(SubmarineReturn submarineReturn)
    {
        var delta = submarineReturn.ReturnAt - DateTime.Now;
        return delta <= TimeSpan.Zero
            ? $"{FormatDuration(delta.Duration())} ago"
            : $"in {FormatDuration(delta)}";
    }

    private static string FormatDuration(TimeSpan value)
    {
        if (value.TotalDays >= 1)
            return $"{(int)value.TotalDays}d {value.Hours}h";
        if (value.TotalHours >= 1)
            return $"{(int)value.TotalHours}h {value.Minutes}m";
        if (value.TotalMinutes >= 1)
            return $"{(int)value.TotalMinutes}m";

        return "less than a minute";
    }

    private static string YesNo(bool value)
        => value ? "Yes" : "No";

    private static uint SubmarineReturnColor()
        => Color(0.08f, 0.35f, 0.48f, 0.90f);

    private static uint SubmarineReturnTileColor()
        => Color(0.08f, 0.31f, 0.43f, 0.96f);

    private static uint SubmarineReturnTileBorderColor()
        => Color(0.50f, 0.86f, 1.00f, 0.62f);
}
