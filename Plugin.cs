using Dalamud.Interface.Windowing;
using Dalamud.IoC;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using Dalamud.Game.Gui.Dtr;
using Lodestone.Services;
using Lodestone.Windows;

namespace Lodestone;

public sealed class Plugin : IDalamudPlugin
{
    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static ITextureProvider TextureProvider { get; private set; } = null!;
    [PluginService] internal static INotificationManager Notifications { get; private set; } = null!;
    [PluginService] internal static IDtrBar DtrBar { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IPlayerState PlayerState { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;

    private const string CommandName = "/lodestone";

    internal Configuration Configuration { get; }
    internal WindowSystem WindowSystem { get; } = new("Lodestone");
    internal LodestoneClient LodestoneClient { get; }
    internal ImageCache ImageCache { get; }
    internal GameEscapeClient GameEscapeClient { get; }
    internal QuestNavigationService QuestNavigationService { get; }
    internal PartySyncService PartySyncService { get; }
    internal PartySyncIpcService PartySyncIpcService { get; }
    internal CalendarWindow CalendarWindow { get; }
    internal ConfigWindow ConfigWindow { get; }
    internal QuestLookupWindow QuestLookupWindow { get; }
    internal ServerBar ServerBar { get; }
    internal NoteAlarmService NoteAlarmService { get; }

    public Plugin()
    {
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Configuration.Initialize(PluginInterface);

        LodestoneClient = new LodestoneClient(PluginInterface.ConfigDirectory);
        ImageCache = new ImageCache();
        GameEscapeClient = new GameEscapeClient(PluginInterface.ConfigDirectory);
        QuestNavigationService = new QuestNavigationService(this);
        PartySyncService = new PartySyncService(this);
        PartySyncIpcService = new PartySyncIpcService(this);
        CalendarWindow = new CalendarWindow(this);
        ConfigWindow = new ConfigWindow(this);
        QuestLookupWindow = new QuestLookupWindow(this);
        WindowSystem.AddWindow(CalendarWindow);
        WindowSystem.AddWindow(ConfigWindow);
        WindowSystem.AddWindow(QuestLookupWindow);
        ServerBar = new ServerBar(this);
        NoteAlarmService = new NoteAlarmService(this);

        CommandManager.AddHandler(CommandName, new Dalamud.Game.Command.CommandInfo(OnCommand)
        {
            HelpMessage = "Open the Lodestone calendar."
        });

        PluginInterface.UiBuilder.Draw += Draw;
        PluginInterface.UiBuilder.OpenMainUi += OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi += OpenConfig;

        CalendarWindow.IsOpen = Configuration.OpenCalendarOnStartup;
        if (Configuration.AutoRefreshOnStartup)
            _ = CalendarWindow.RefreshAsync(false);
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= Draw;
        PluginInterface.UiBuilder.OpenMainUi -= OpenMain;
        PluginInterface.UiBuilder.OpenConfigUi -= OpenConfig;

        CommandManager.RemoveHandler(CommandName);
        WindowSystem.RemoveAllWindows();
        QuestLookupWindow.Dispose();
        NoteAlarmService.Dispose();
        ServerBar.Dispose();
        ImageCache.Dispose();
        LodestoneClient.Dispose();
        GameEscapeClient.Dispose();
        QuestNavigationService.Dispose();
        PartySyncIpcService.Dispose();
        PartySyncService.Dispose();
    }

    private void Draw() => WindowSystem.Draw();
    private void OpenMain() => CalendarWindow.IsOpen = true;
    internal void OpenConfig() => ConfigWindow.IsOpen = true;

    private void OnCommand(string command, string args)
    {
        if (args.Trim().Equals("config", StringComparison.OrdinalIgnoreCase) ||
            args.Trim().Equals("settings", StringComparison.OrdinalIgnoreCase))
        {
            OpenConfig();
            return;
        }

        OpenMain();
    }
}
