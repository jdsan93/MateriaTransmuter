using System;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Windowing;

namespace MateriaTransmuter.Windows;

public sealed class ConfigWindow : Window, IDisposable
{
    private readonly Plugin plugin;

    public ConfigWindow(Plugin plugin)
        : base("Materia Transmuter Settings###MateriaTransmuterConfig")
    {
        Size = new Vector2(420, 220);
        SizeCondition = ImGuiCond.FirstUseEver;
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void PreDraw()
    {
        if (plugin.Configuration.IsConfigWindowMovable)
        {
            Flags &= ~ImGuiWindowFlags.NoMove;
        }
        else
        {
            Flags |= ImGuiWindowFlags.NoMove;
        }
    }

    public override void Draw()
    {
        var delay = plugin.Configuration.ActionDelayMs;
        if (ImGui.SliderInt("Action delay (ms)", ref delay, 250, 2000))
        {
            plugin.Configuration.ActionDelayMs = delay;
            plugin.Configuration.Save();
        }

        var debug = plugin.Configuration.DebugLogging;
        if (ImGui.Checkbox("Log addon names and actions to /xllog", ref debug))
        {
            plugin.Configuration.DebugLogging = debug;
            plugin.Configuration.Save();
        }

        var movable = plugin.Configuration.IsConfigWindowMovable;
        if (ImGui.Checkbox("Movable settings window", ref movable))
        {
            plugin.Configuration.IsConfigWindowMovable = movable;
            plugin.Configuration.Save();
        }

        ImGui.Separator();
        if (ImGui.Button("Log visible addons"))
        {
            var addons = plugin.Game.ListVisibleAddons();
            Plugin.Log.Information($"Visible addons ({addons.Count}): {string.Join(", ", addons)}");
        }

        ImGui.SameLine();
        ImGui.TextUnformatted("Open Mutamix's window first, then click this if transmutation is not detected.");
    }
}
