using Dalamud.Game.Command;
using Dalamud.IoC;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;
using Dalamud.Plugin.Services;
using MateriaTransmuter.Materia;
using MateriaTransmuter.Transmutation;
using MateriaTransmuter.Windows;

namespace MateriaTransmuter;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/mt";
    private const string CommandAlias = "/transmute";

    [PluginService] internal static IDalamudPluginInterface PluginInterface { get; private set; } = null!;
    [PluginService] internal static ICommandManager CommandManager { get; private set; } = null!;
    [PluginService] internal static IClientState ClientState { get; private set; } = null!;
    [PluginService] internal static IDataManager DataManager { get; private set; } = null!;
    [PluginService] internal static IPluginLog Log { get; private set; } = null!;
    [PluginService] internal static IGameInventory GameInventory { get; private set; } = null!;
    [PluginService] internal static IGameGui GameGui { get; private set; } = null!;
    [PluginService] internal static IFramework Framework { get; private set; } = null!;
    [PluginService] internal static IChatGui ChatGui { get; private set; } = null!;
    [PluginService] internal static ICondition Condition { get; private set; } = null!;
    [PluginService] internal static IAddonLifecycle AddonLifecycle { get; private set; } = null!;

    internal static Plugin Instance { get; private set; } = null!;

    public Configuration Configuration { get; }
    public MateriaCatalog Catalog { get; }
    public GameTransmuteClient Game { get; }
    public TransmuteController Controller { get; }

    public readonly WindowSystem WindowSystem = new("MateriaTransmuter");
    private readonly ConfigWindow configWindow;
    private readonly MainWindow mainWindow;

    public Plugin()
    {
        Instance = this;
        Configuration = PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        Catalog = new MateriaCatalog();
        Catalog.Reload();
        Game = new GameTransmuteClient();
        Controller = new TransmuteController(Catalog, Game);

        configWindow = new ConfigWindow(this);
        mainWindow = new MainWindow(this);
        WindowSystem.AddWindow(configWindow);
        WindowSystem.AddWindow(mainWindow);

        CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Open Materia Transmuter. Talk to Mutamix, then start a run from the window.",
        });
        CommandManager.AddHandler(CommandAlias, new CommandInfo(OnCommand)
        {
            HelpMessage = "Alias for /mt.",
        });

        PluginInterface.UiBuilder.Draw += WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        Log.Information("Materia Transmuter loaded. Use /mt to open the window.");
    }

    public void Dispose()
    {
        PluginInterface.UiBuilder.Draw -= WindowSystem.Draw;
        PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;

        WindowSystem.RemoveAllWindows();
        configWindow.Dispose();
        mainWindow.Dispose();
        Controller.Dispose();
        Game.Dispose();

        CommandManager.RemoveHandler(CommandName);
        CommandManager.RemoveHandler(CommandAlias);
    }

    private void OnCommand(string command, string args)
        => mainWindow.Toggle();

    public void ToggleConfigUi() => configWindow.Toggle();
    public void ToggleMainUi() => mainWindow.Toggle();
}
