using System;
using System.Linq;
using System.Numerics;
using Dalamud.Bindings.ImGui;
using Dalamud.Interface.Utility;
using Dalamud.Interface.Windowing;
using MateriaTransmuter.Inventory;
using MateriaTransmuter.Materia;
using MateriaTransmuter.Transmutation;

namespace MateriaTransmuter.Windows;

public sealed class MainWindow : Window, IDisposable
{
    private readonly Plugin plugin;
    private int pendingBlacklistTypeIndex;
    private int pendingBlacklistGradeIndex;

    public MainWindow(Plugin plugin)
        : base("Materia Transmuter###MateriaTransmuterMain")
    {
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new Vector2(480, 420),
            MaximumSize = new Vector2(float.MaxValue, float.MaxValue),
        };
        this.plugin = plugin;
    }

    public void Dispose() { }

    public override void Draw()
    {
        var configuration = plugin.Configuration;
        var catalog = plugin.Catalog;
        if (catalog.Types.Count == 0)
        {
            ImGui.TextWrapped("No materia types were loaded from game data. Relog or click Reload.");
            if (ImGui.Button("Reload materia data"))
            {
                catalog.Reload();
            }

            return;
        }

        ImGui.TextWrapped("Talk to Mutamix Bubblypots in Central Thanalan (X:23.7, Y:13.6), choose Transmute Materia, then start a run. The plugin clicks one materia at a time (quantity 1) so each batch uses as many types as possible. Only your four inventory bags are used.");
        ImGuiHelpers.ScaledDummy(6);

        DrawTargetSelectors(configuration, catalog);
        ImGuiHelpers.ScaledDummy(4);
        DrawBlacklist(configuration, catalog);
        ImGuiHelpers.ScaledDummy(8);
        DrawControls(configuration);
        ImGuiHelpers.ScaledDummy(8);
        DrawPreview(configuration, catalog);
    }

    private void DrawTargetSelectors(Configuration configuration, MateriaCatalog catalog)
    {
        if (catalog.FindType(configuration.DesiredMateriaTypeId) is null)
        {
            configuration.DesiredMateriaTypeId = catalog.Types[0].TypeId;
            configuration.Save();
        }

        var typeIndex = 0;
        for (var i = 0; i < catalog.Types.Count; i++)
        {
            if (catalog.Types[i].TypeId == configuration.DesiredMateriaTypeId)
            {
                typeIndex = i;
                break;
            }
        }

        var typeNames = catalog.Types.Select(MateriaCatalog.ComboLabel).ToArray();
        var selectedType = catalog.FindType(configuration.DesiredMateriaTypeId) ?? catalog.Types[typeIndex];
        var gradeLabels = selectedType.Grades.Select(MateriaCatalog.FormatGrade).ToArray();
        var gradeIndex = Math.Max(0, selectedType.Grades.ToList().IndexOf(configuration.DesiredGrade));

        ImGui.TextUnformatted("Target");
        if (DrawTypeGradeCombos("Desired", typeNames, ref typeIndex, gradeLabels, ref gradeIndex))
        {
            configuration.DesiredMateriaTypeId = catalog.Types[typeIndex].TypeId;
            var grades = catalog.Types[typeIndex].Grades;
            if (gradeIndex >= 0 && gradeIndex < grades.Count)
            {
                configuration.DesiredGrade = grades[gradeIndex];
            }
            else if (!grades.Contains(configuration.DesiredGrade))
            {
                configuration.DesiredGrade = grades.Count > 0 ? grades[^1] : configuration.DesiredGrade;
            }

            configuration.Save();
        }

        var count = configuration.DesiredCount;
        if (ImGui.InputInt("How many to make", ref count))
        {
            configuration.DesiredCount = Math.Max(1, count);
            configuration.Save();
        }
    }

    private void DrawBlacklist(Configuration configuration, MateriaCatalog catalog)
    {
        ImGui.Separator();
        ImGui.TextUnformatted("Keep list (never transmute these)");
        ImGui.TextDisabled("The target type is already protected. Add other type/grade pairs you do not want to spend.");

        var typeNames = catalog.Types.Select(MateriaCatalog.ComboLabel).ToArray();
        pendingBlacklistTypeIndex = Math.Clamp(pendingBlacklistTypeIndex, 0, catalog.Types.Count - 1);
        var pendingType = catalog.Types[pendingBlacklistTypeIndex];
        var gradeLabels = pendingType.Grades.Select(MateriaCatalog.FormatGrade).ToArray();
        pendingBlacklistGradeIndex = Math.Clamp(pendingBlacklistGradeIndex, 0, Math.Max(0, pendingType.Grades.Count - 1));
        DrawTypeGradeCombos("Keep", typeNames, ref pendingBlacklistTypeIndex, gradeLabels, ref pendingBlacklistGradeIndex);
        pendingType = catalog.Types[Math.Clamp(pendingBlacklistTypeIndex, 0, catalog.Types.Count - 1)];
        pendingBlacklistGradeIndex = Math.Clamp(pendingBlacklistGradeIndex, 0, Math.Max(0, pendingType.Grades.Count - 1));

        if (ImGui.Button("Add to keep list"))
        {
            var typeId = pendingType.TypeId;
            var grade = pendingType.Grades[pendingBlacklistGradeIndex];
            if (!configuration.Blacklist.Any(entry => entry.MateriaTypeId == typeId && entry.Grade == grade))
            {
                configuration.Blacklist.Add(new BlacklistEntry { MateriaTypeId = typeId, Grade = grade });
                configuration.Save();
            }
        }

        ImGuiHelpers.ScaledDummy(4);
        for (var i = 0; i < configuration.Blacklist.Count; i++)
        {
            var entry = configuration.Blacklist[i];
            var label = catalog.FormatMateriaLabel(entry.MateriaTypeId, entry.Grade);
            ImGui.PushID(i);
            ImGui.TextUnformatted(label);
            ImGui.SameLine();
            if (ImGui.SmallButton("Remove"))
            {
                configuration.Blacklist.RemoveAt(i);
                configuration.Save();
                i--;
            }

            ImGui.PopID();
        }

        if (configuration.Blacklist.Count == 0)
        {
            ImGui.TextDisabled("Nothing extra is protected.");
        }
    }

    private void DrawPreview(Configuration configuration, MateriaCatalog catalog)
    {
        ImGui.Separator();
        if (!Plugin.ClientState.IsLoggedIn)
        {
            ImGui.TextUnformatted("Log in to preview your bags.");
            return;
        }

        var all = PlayerInventory.ScanMateria(catalog);
        var plan = TransmutePlanner.Plan(all, configuration.DesiredMateriaTypeId, configuration.DesiredGrade, configuration);
        var desiredName = configuration.DesiredMateriaTypeId == 0
            ? "(none)"
            : catalog.FormatMateriaLabel(configuration.DesiredMateriaTypeId, configuration.DesiredGrade);

        ImGui.TextUnformatted($"Target: {desiredName}");
        ImGui.TextUnformatted($"In your bags now: {plan.DesiredCount}");
        ImGui.TextUnformatted($"Eligible fodder of that grade: {plan.EligibleCount} ({plan.UniqueTypeCount} types)");
        ImGui.TextUnformatted($"This run: {plugin.Controller.ProducedThisRun}/{plugin.Controller.TargetCount} made, {plugin.Controller.TransmutesThisRun} rolls");

        if (plan.CanTransmute)
        {
            var offeredTypes = plan.Pieces.Select(piece => piece.TypeName).Distinct().Count();
            ImGui.TextUnformatted($"Next offer uses {offeredTypes} different types:");
            foreach (var piece in plan.Pieces)
            {
                ImGui.BulletText(catalog.FormatMateriaLabel(piece.TypeId, piece.Grade));
            }
        }
        else if (!string.IsNullOrEmpty(plan.FailureReason))
        {
            ImGui.TextWrapped(plan.FailureReason);
        }

        ImGui.TextUnformatted($"Window: {(plugin.Game.IsTransmuteWindowOpen() ? "open" : "not detected")}  Slots filled: {plugin.Game.GetFilledSlotCount()}/5");
    }

    private void DrawControls(Configuration configuration)
    {
        ImGui.Separator();
        ImGui.TextWrapped(plugin.Controller.Status);

        var running = plugin.Controller.IsRunning;
        if (!running)
        {
            if (ImGui.Button("Start transmuting"))
            {
                plugin.Controller.Start();
            }
        }
        else
        {
            if (ImGui.Button("Stop"))
            {
                plugin.Controller.Stop("Stopped.");
            }
        }

        ImGui.SameLine();
        if (ImGui.Button("Settings"))
        {
            plugin.ToggleConfigUi();
        }

        ImGui.SameLine();
        if (ImGui.Button("Reload materia data"))
        {
            plugin.Catalog.Reload();
        }

        ImGui.TextDisabled($"Delay {configuration.ActionDelayMs} ms between actions. Command: /mt");
    }

    private static bool DrawTypeGradeCombos(
        string id,
        string[] typeNames,
        ref int typeIndex,
        string[] gradeLabels,
        ref int gradeIndex)
    {
        var style = ImGui.GetStyle();
        var gradeWidth = ImGui.CalcTextSize("VIII").X
                         + ImGui.GetFrameHeight()
                         + (style.FramePadding.X * 2)
                         + style.ItemInnerSpacing.X
                         + 6;
        var typeWidth = Math.Max(120, ImGui.GetContentRegionAvail().X - gradeWidth - style.ItemSpacing.X);

        ImGui.SetNextItemWidth(typeWidth);
        var typeChanged = ImGui.Combo($"##{id}Type", ref typeIndex, typeNames, typeNames.Length);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(gradeWidth);
        var gradeChanged = ImGui.Combo($"##{id}Grade", ref gradeIndex, gradeLabels, gradeLabels.Length);
        return typeChanged || gradeChanged;
    }
}
