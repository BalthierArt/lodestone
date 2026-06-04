using Dalamud.Interface.Utility;
using Lodestone.Models;

namespace Lodestone.Windows;

internal readonly record struct NeonPalette(uint Background, uint Border, uint Text);

internal static class UiWidgets
{
    public static void NeonBadge(string text, NeonPalette palette, Vector2? padding = null)
    {
        if (string.IsNullOrWhiteSpace(text))
            return;

        var scale = ImGuiHelpers.GlobalScale;
        var pad = padding ?? new Vector2(8f, 3f) * scale;
        var textSize = ImGui.CalcTextSize(text);
        var size = textSize + pad * 2f;
        var min = ImGui.GetCursorScreenPos();
        var max = min + size;
        var draw = ImGui.GetWindowDrawList();

        draw.AddRectFilled(min, max, palette.Background, 4f * scale);
        draw.AddRect(min, max, palette.Border, 4f * scale, 0, 1.2f * scale);
        draw.AddText(min + pad, palette.Text, text);
        ImGui.Dummy(size);
    }

    public static void ProgressBar(float percent, string label, Vector2? size = null, bool indeterminate = false)
    {
        var scale = ImGuiHelpers.GlobalScale;
        var width = size?.X ?? ImGui.GetContentRegionAvail().X;
        var height = size?.Y ?? 18f * scale;
        width = Math.Max(80f * scale, width);
        height = Math.Max(14f * scale, height);

        var min = ImGui.GetCursorScreenPos();
        var max = min + new Vector2(width, height);
        var draw = ImGui.GetWindowDrawList();
        var bg = Color(0.08f, 0.09f, 0.13f, 0.96f);
        var border = Color(0.64f, 0.38f, 1f, 0.75f);
        var fill = Color(0.30f, 0.58f, 0.95f, 0.92f);

        draw.AddRectFilled(min, max, bg, 5f * scale);
        if (indeterminate)
        {
            var t = (float)((ImGui.GetTime() * 0.55) % 1.0);
            var segment = width * 0.32f;
            var x = min.X + (width + segment) * t - segment;
            draw.AddRectFilled(new Vector2(Math.Max(min.X, x), min.Y), new Vector2(Math.Min(max.X, x + segment), max.Y), fill, 5f * scale);
        }
        else
        {
            var filled = Math.Clamp(percent, 0f, 1f) * width;
            if (filled > 2f * scale)
                draw.AddRectFilled(min, new Vector2(min.X + filled, max.Y), fill, 5f * scale);
        }

        draw.AddRect(min, max, border, 5f * scale, 0, 1.1f * scale);
        if (!string.IsNullOrWhiteSpace(label))
        {
            var textSize = ImGui.CalcTextSize(label);
            var textPos = min + new Vector2(Math.Max(5f * scale, (width - textSize.X) * 0.5f), (height - textSize.Y) * 0.5f);
            draw.AddText(textPos, Color(1f, 1f, 1f, 0.95f), label);
        }

        ImGui.Dummy(new Vector2(width, height));
    }

    public static NeonPalette KindPalette(LodestoneEntryKind kind) => kind switch
    {
        LodestoneEntryKind.SpecialEvent => new(Color(0.02f, 0.24f, 0.17f, 0.94f), Color(0.35f, 1f, 0.68f, 0.92f), Color(0.62f, 1f, 0.78f, 1f)),
        LodestoneEntryKind.Maintenance => new(Color(0.34f, 0.07f, 0.04f, 0.94f), Color(1f, 0.38f, 0.24f, 0.9f), Color(1f, 0.68f, 0.56f, 1f)),
        LodestoneEntryKind.Recovery => new(Color(0.18f, 0.10f, 0.36f, 0.94f), Color(0.82f, 0.62f, 1f, 0.88f), Color(0.86f, 0.76f, 1f, 1f)),
        LodestoneEntryKind.Status => new(Color(0.36f, 0.23f, 0.02f, 0.94f), Color(1f, 0.78f, 0.25f, 0.9f), Color(1f, 0.86f, 0.44f, 1f)),
        LodestoneEntryKind.Update => new(Color(0.05f, 0.18f, 0.36f, 0.94f), Color(0.36f, 0.70f, 1f, 0.9f), Color(0.66f, 0.86f, 1f, 1f)),
        LodestoneEntryKind.Notice => new(Color(0.27f, 0.17f, 0.04f, 0.94f), Color(1f, 0.78f, 0.26f, 0.9f), Color(1f, 0.86f, 0.50f, 1f)),
        LodestoneEntryKind.DeveloperPost => new(Color(0.07f, 0.24f, 0.29f, 0.94f), Color(0.40f, 0.98f, 1f, 0.9f), Color(0.70f, 1f, 1f, 1f)),
        LodestoneEntryKind.IcyVeins => new(Color(0.08f, 0.18f, 0.38f, 0.94f), Color(0.42f, 0.72f, 1f, 0.9f), Color(0.70f, 0.88f, 1f, 1f)),
        LodestoneEntryKind.IcyVeinsGuide => new(Color(0.09f, 0.20f, 0.42f, 0.94f), Color(0.45f, 0.76f, 1f, 0.9f), Color(0.72f, 0.90f, 1f, 1f)),
        _ => new(Color(0.25f, 0.20f, 0.08f, 0.94f), Color(1f, 0.82f, 0.42f, 0.82f), Color(1f, 0.90f, 0.62f, 1f))
    };

    public static NeonPalette SeasonPalette(int month) => month switch
    {
        >= 3 and <= 5 => new(Color(0.03f, 0.24f, 0.15f, 0.94f), Color(0.40f, 1f, 0.58f, 0.85f), Color(0.70f, 1f, 0.78f, 1f)),
        >= 6 and <= 8 => new(Color(0.35f, 0.10f, 0.05f, 0.94f), Color(1f, 0.40f, 0.25f, 0.88f), Color(1f, 0.70f, 0.50f, 1f)),
        >= 9 and <= 11 => new(Color(0.30f, 0.19f, 0.03f, 0.94f), Color(1f, 0.72f, 0.25f, 0.88f), Color(1f, 0.84f, 0.52f, 1f)),
        _ => new(Color(0.04f, 0.16f, 0.34f, 0.94f), Color(0.42f, 0.72f, 1f, 0.88f), Color(0.72f, 0.88f, 1f, 1f))
    };

    public static uint Color(float r, float g, float b, float a)
    {
        uint R(float value) => (uint)Math.Clamp(value * 255f, 0, 255);
        return R(r) | R(g) << 8 | R(b) << 16 | R(a) << 24;
    }
}
