using Dalamud.Interface;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using Lodestone.Models;

namespace Lodestone.Windows;

public sealed class QuestLookupWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private const string LookupFailedMessage = "Look up failed, Try again later.";
    private LodestoneEntry? sourceEntry;
    private GameEscapeQuest? quest;
    private string query = string.Empty;
    private string status = "No quest loaded.";
    private string lookupProgressLabel = string.Empty;
    private float lookupProgressPercent;
    private bool lookupProgressIndeterminate = true;
    private bool loading;
    private bool needsPlacement;
    private DateTime keepFlagClipboardUntilUtc;
    private CancellationTokenSource? cancellationTokenSource;

    public QuestLookupWindow(Plugin plugin) : base("Quest Lookup##LodestoneQuestLookup")
    {
        this.plugin = plugin;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(620, 620),
            MaximumSize = new Vector2(1100, 1300)
        };
    }

    public void Dispose()
    {
        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
    }

    public void Open(LodestoneEntry entry)
    {
        sourceEntry = entry;
        quest = null;
        query = GuessQuestName(entry);
        status = string.IsNullOrWhiteSpace(query) ? "No quest name found for this Lodestone entry." : $"Looking up {query}...";
        lookupProgressLabel = status;
        lookupProgressPercent = 0f;
        lookupProgressIndeterminate = true;
        loading = !string.IsNullOrWhiteSpace(query);
        needsPlacement = true;
        IsOpen = true;

        if (!loading)
            return;

        cancellationTokenSource?.Cancel();
        cancellationTokenSource?.Dispose();
        cancellationTokenSource = new CancellationTokenSource();
        _ = LoadAsync(query, cancellationTokenSource.Token);
    }

    public override void PreDraw()
    {
        plugin.CalendarWindow.PrimeExternalSideWindowPlacement(new Vector2(720, 760) * ImGuiHelpers.GlobalScale, ref needsPlacement);
    }

    public override void Draw()
    {
        KeepFlagClipboardFresh();

        using var rounding = ImRaii.PushStyle(ImGuiStyleVar.ChildRounding, 8f);
        using var frameRounding = ImRaii.PushStyle(ImGuiStyleVar.FrameRounding, 7f);
        using var tabColor = ImRaii.PushColor(ImGuiCol.Tab, new Vector4(0.42f, 0.22f, 0.78f, 0.92f));
        using var tabHovered = ImRaii.PushColor(ImGuiCol.TabHovered, new Vector4(0.64f, 0.38f, 1.00f, 1f));
        using var tabActive = ImRaii.PushColor(ImGuiCol.TabActive, new Vector4(0.64f, 0.38f, 1.00f, 1f));
        using var button = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.42f, 0.22f, 0.78f, 0.92f));
        using var buttonHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(0.64f, 0.38f, 1.00f, 1f));
        using var buttonActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.32f, 0.16f, 0.62f, 1f));

        DrawHeader();
        ImGui.Spacing();

        if (loading)
        {
            DrawLoadingPanel();
            return;
        }

        if (quest == null)
        {
            DrawFailurePanel();
            return;
        }

        DrawQuest(quest);
    }

    private async Task LoadAsync(string questName, CancellationToken cancellationToken)
    {
        try
        {
            var loaded = await plugin.GameEscapeClient.LookupQuestAsync(questName, cancellationToken, progress =>
            {
                lookupProgressLabel = progress.Status;
                lookupProgressPercent = progress.Percent;
                lookupProgressIndeterminate = progress.Indeterminate;
                status = progress.Status;
            });
            if (cancellationToken.IsCancellationRequested)
                return;

            quest = loaded;
            status = $"Loaded {loaded.Title}.";
        }
        catch (Exception ex)
        {
            if (cancellationToken.IsCancellationRequested)
                return;

            Plugin.Log.Information("Quest lookup failed: {Message}", ex.Message);
            status = LookupFailedMessage;
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
                loading = false;
        }
    }

    private void DrawLoadingPanel()
    {
        DrawPanel("##questLookupLoading", () =>
        {
            var scale = ImGuiHelpers.GlobalScale;
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(new Vector4(0.62f, 0.90f, 0.42f, 1f), FontAwesomeIcon.Search.ToIconString());
            ImGui.SameLine(0, 7f * scale);
            ImGui.TextColored(new Vector4(0.84f, 0.72f, 1f, 1f), string.IsNullOrWhiteSpace(lookupProgressLabel) ? status : lookupProgressLabel);
            ImGui.Spacing();
            UiWidgets.ProgressBar(lookupProgressPercent, string.Empty, indeterminate: lookupProgressIndeterminate);
        });
    }

    private void DrawFailurePanel()
    {
        DrawPanel("##questLookupFailure", () =>
        {
            var scale = ImGuiHelpers.GlobalScale;
            using (ImRaii.PushFont(UiBuilder.IconFont))
                ImGui.TextColored(new Vector4(1f, 0.42f, 0.48f, 1f), FontAwesomeIcon.ExclamationTriangle.ToIconString());
            ImGui.SameLine(0, 7f * scale);
            ImGui.SetWindowFontScale(1.08f);
            ImGui.TextColored(new Vector4(1f, 0.82f, 0.88f, 1f), LookupFailedMessage);
            ImGui.SetWindowFontScale(1f);

            ImGui.Spacing();
            ImGui.TextColored(new Vector4(0.68f, 0.66f, 0.78f, 1f), "Quest data was not available from Gamer Escape or ConsoleGamesWiki.");
            if (!string.IsNullOrWhiteSpace(query))
                ImGui.TextColored(new Vector4(0.68f, 0.66f, 0.78f, 1f), $"Search term: {query}");
        });

        if (!string.IsNullOrWhiteSpace(query))
        {
            if (ImGui.Button("Open Gamer Escape Search"))
                Dalamud.Utility.Util.OpenLink($"https://ffxiv.gamerescape.com/wiki/Special:Search?search={Uri.EscapeDataString(query)}");
            ImGui.SameLine();
            if (ImGui.Button("Open ConsoleGamesWiki Search"))
                Dalamud.Utility.Util.OpenLink($"https://ffxiv.consolegameswiki.com/wiki/Special:Search?search={Uri.EscapeDataString(query)}");
        }
    }

    private void DrawHeader()
    {
        var draw = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var start = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;
        var height = 70f * scale;
        draw.AddRectFilled(start, start + new Vector2(width, height), Color(0.18f, 0.19f, 0.22f, 0.96f), 8f * scale);
        draw.AddRect(start, start + new Vector2(width, height), Color(0.64f, 0.38f, 1f, 0.75f), 8f * scale, 0, 1.4f * scale);

        ImGui.SetCursorScreenPos(start + new Vector2(14f, 10f) * scale);
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(new Vector4(0.62f, 0.90f, 0.42f, 1f), FontAwesomeIcon.Search.ToIconString());
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.68f, 0.66f, 0.78f, 1f), "EXPERIMENTAL - May be unable to look up data.");

        ImGui.SetCursorScreenPos(start + new Vector2(14f, 36f) * scale);
        ImGui.SetWindowFontScale(1.08f);
        ImGui.TextColored(Vector4.One, string.IsNullOrWhiteSpace(query) ? sourceEntry?.Title ?? "Quest" : query);
        ImGui.SetWindowFontScale(1f);
        ImGui.SetCursorScreenPos(start + new Vector2(0, height + 10f * scale));
    }

    private void DrawQuest(GameEscapeQuest item)
    {
        using var tabs = ImRaii.TabBar("##questLookupTabs");
        if (!tabs.Success)
            return;

        using (var details = ImRaii.TabItem("Details"))
        {
            if (details.Success)
                DrawDetails(item);
        }

        using (var source = ImRaii.TabItem("Source"))
        {
            if (source.Success)
            {
                ImGui.TextColored(new Vector4(0.68f, 0.66f, 0.78f, 1f), $"{SourceLabel(item)} URL");
                WrappedText(item.Url);
                ImGui.TextColored(new Vector4(0.68f, 0.66f, 0.78f, 1f), $"Fetched: {item.FetchedAt:g}");
            }
        }

        ImGui.Separator();
        DrawActions(item);
    }

    private void DrawDetails(GameEscapeQuest item)
    {
        DrawPanel("##questDetails", () =>
        {
            Section("Acquisition", FontAwesomeIcon.UserCircle);
            if (!string.IsNullOrWhiteSpace(item.Acquisition))
                WrappedText(item.Acquisition);

            if (!string.IsNullOrWhiteSpace(item.ClosestAetheryte))
                ImGui.TextColored(new Vector4(0.70f, 0.92f, 1f, 1f), $"Closest Aetheryte: {item.ClosestAetheryte}");

            if (item.Requirements.Count > 0)
            {
                Section("Requirements", FontAwesomeIcon.QuestionCircle);
                foreach (var requirement in item.Requirements)
                    ImGui.BulletText(requirement);
            }

            if (item.Rewards.Count > 0)
            {
                Section("Rewards", FontAwesomeIcon.BoxOpen);
                foreach (var reward in item.Rewards)
                    ImGui.BulletText(reward);
            }

            if (!string.IsNullOrWhiteSpace(item.Description))
            {
                Section("Description", FontAwesomeIcon.BookOpen);
                WrappedText(item.Description);
            }

            if (item.Objectives.Count > 0)
            {
                Section("Objectives", FontAwesomeIcon.ExclamationCircle);
                foreach (var objective in item.Objectives)
                    ImGui.BulletText(objective);
            }
        });
    }

    private void DrawActions(GameEscapeQuest item)
    {
        if (ImGui.Button($"Open {SourceLabel(item)}"))
            Dalamud.Utility.Util.OpenLink(item.Url);

        ImGui.SameLine();
        var canCopyFlag = item.MapX.HasValue && item.MapY.HasValue && !string.IsNullOrWhiteSpace(item.Zone);
        using (ImRaii.Disabled(!canCopyFlag))
        {
            if (ImGui.Button("Copy Flag"))
            {
                if (plugin.QuestNavigationService.TrySetMapFlag(item))
                {
                    CopyFlagToClipboard();
                    status = "Copied <flag>. Paste it in chat.";
                }
                else
                {
                    status = plugin.QuestNavigationService.Status;
                }
            }
        }

        if (ImGui.IsItemHovered() && !canCopyFlag)
            ImGui.SetTooltip("A parsed zone and X/Y coordinate are required to copy a map flag.");

        ImGui.SameLine();
        var canTeleport = item.HasLocation && plugin.QuestNavigationService.IsLifestreamAvailable();
        using (ImRaii.Disabled(!canTeleport))
        {
            if (ImGui.Button("Teleport"))
            {
                var ok = plugin.QuestNavigationService.TeleportToQuest(item, false);
                status = ok ? "Teleport requested." : "Could not call Lifestream teleport.";
            }
        }

        if (ImGui.IsItemHovered() && !canTeleport)
            ImGui.SetTooltip(item.HasLocation ? "Lifestream IPC is not available." : "No quest location was parsed.");

        if (plugin.QuestNavigationService.HasPendingNavigation || !plugin.QuestNavigationService.Status.Equals("Navigation idle.", StringComparison.OrdinalIgnoreCase))
            ImGui.TextColored(new Vector4(0.68f, 0.66f, 0.78f, 1f), plugin.QuestNavigationService.Status);

        if (plugin.QuestNavigationService.HasNavigationActivity)
        {
            ImGui.SameLine();
            using var panic = ImRaii.PushColor(ImGuiCol.Button, new Vector4(0.72f, 0.05f, 0.08f, 1f));
            using var panicHovered = ImRaii.PushColor(ImGuiCol.ButtonHovered, new Vector4(1f, 0.12f, 0.16f, 1f));
            using var panicActive = ImRaii.PushColor(ImGuiCol.ButtonActive, new Vector4(0.52f, 0.02f, 0.05f, 1f));
            if (ImGui.Button("Panic Stop"))
                plugin.QuestNavigationService.PanicStop();
        }
    }

    private void CopyFlagToClipboard()
    {
        keepFlagClipboardUntilUtc = DateTime.UtcNow.AddSeconds(1);
        ImGui.SetClipboardText("<flag>");
    }

    private void KeepFlagClipboardFresh()
    {
        if (keepFlagClipboardUntilUtc <= DateTime.UtcNow)
            return;

        ImGui.SetClipboardText("<flag>");
    }

    private static void Section(string label, FontAwesomeIcon icon)
    {
        ImGui.Spacing();
        using (ImRaii.PushFont(UiBuilder.IconFont))
            ImGui.TextColored(new Vector4(0.62f, 0.90f, 0.42f, 1f), icon.ToIconString());
        ImGui.SameLine();
        ImGui.TextColored(new Vector4(0.62f, 0.90f, 0.42f, 1f), label);
    }

    private static string SourceLabel(GameEscapeQuest item)
        => string.IsNullOrWhiteSpace(item.SourceName) ? "Quest Source" : item.SourceName;

    private static void DrawPanel(string id, Action drawContent)
    {
        var draw = ImGui.GetWindowDrawList();
        var scale = ImGuiHelpers.GlobalScale;
        var padding = new Vector2(10f, 10f) * scale;
        var startCursor = ImGui.GetCursorPos();
        var startScreen = ImGui.GetCursorScreenPos();
        var width = ImGui.GetContentRegionAvail().X;

        ImGui.SetCursorPos(startCursor + padding);
        ImGui.BeginGroup();
        drawContent();
        ImGui.EndGroup();

        var contentSize = ImGui.GetItemRectSize();
        var panelSize = new Vector2(width, contentSize.Y + padding.Y * 2f);
        draw.AddRectFilled(startScreen, startScreen + panelSize, Color(0.12f, 0.12f, 0.16f, 0.96f), 8f * scale);
        draw.AddRect(startScreen, startScreen + panelSize, Color(0.64f, 0.38f, 1f, 0.70f), 8f * scale, 0, 1f * scale);

        ImGui.SetCursorPos(startCursor + padding);
        ImGui.BeginGroup();
        drawContent();
        ImGui.EndGroup();
        ImGui.SetCursorPos(new Vector2(startCursor.X, startCursor.Y + panelSize.Y + 8f * scale));
    }

    private static void WrappedText(string text)
    {
        ImGui.PushTextWrapPos(0f);
        ImGui.TextUnformatted(text);
        ImGui.PopTextWrapPos();
    }

    private static string GuessQuestName(LodestoneEntry entry)
    {
        if (entry.Kind == LodestoneEntryKind.SpecialEvent)
        {
            var firstLine = entry.Summary
                .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(CleanQuestNameCandidate)
                .FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(firstLine) && firstLine.Length <= 80 && firstLine.IndexOfAny(new[] { '.', '!', '?' }) < 0)
                return firstLine.Trim();
        }

        return CleanQuestNameCandidate(entry.Title)
            .Replace("The ", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Replace(" Campaign", string.Empty, StringComparison.OrdinalIgnoreCase)
            .Trim();
    }

    internal static string CleanQuestNameCandidate(string value)
    {
        return value
            .Trim()
            .TrimStart('#', '>', '-', '*', ' ')
            .Replace("**", string.Empty, StringComparison.Ordinal)
            .Replace("*", string.Empty, StringComparison.Ordinal)
            .Trim();
    }

    private static uint Color(float r, float g, float b, float a)
    {
        uint R(float value) => (uint)Math.Clamp(value * 255f, 0, 255);
        return R(r) | R(g) << 8 | R(b) << 16 | R(a) << 24;
    }
}
