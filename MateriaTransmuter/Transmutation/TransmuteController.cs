using System;
using System.Collections.Generic;
using Dalamud.Game.Inventory;
using Dalamud.Game.Inventory.InventoryEventArgTypes;
using Dalamud.Plugin.Services;
using MateriaTransmuter.Inventory;
using MateriaTransmuter.Materia;

namespace MateriaTransmuter.Transmutation;

public enum TransmuteRunState
{
    Idle,
    WaitingForWindow,
    Adding,
    Confirming,
    WaitingForResult,
    Finished,
}

public sealed class TransmuteController : IDisposable
{
    private readonly MateriaCatalog catalog;
    private readonly GameTransmuteClient game;
    private readonly List<PlannedPiece> currentBatch = [];
    private DateTime nextActionAt;
    private DateTime waitingSince;
    private int addIndex;
    private int addAttempts;
    private int lastFilledSlots;
    private bool confirmSent;
    private bool resultNoted;
    private Dictionary<(uint TypeId, int Grade), int>? bagCountsAtSubmit;

    public TransmuteController(MateriaCatalog catalog, GameTransmuteClient game)
    {
        this.catalog = catalog;
        this.game = game;
        Plugin.Framework.Update += OnUpdate;
        Plugin.GameInventory.ItemAdded += OnInventoryGain;
        Plugin.GameInventory.ItemChanged += OnInventoryGain;
    }

    public TransmuteRunState State { get; private set; } = TransmuteRunState.Idle;

    public string Status { get; private set; } = "Idle.";

    public int ProducedThisRun { get; private set; }

    public int TargetCount { get; private set; }

    public int TransmutesThisRun { get; private set; }

    public bool IsRunning => State is not TransmuteRunState.Idle and not TransmuteRunState.Finished;

    public void Dispose()
    {
        Plugin.Framework.Update -= OnUpdate;
        Plugin.GameInventory.ItemAdded -= OnInventoryGain;
        Plugin.GameInventory.ItemChanged -= OnInventoryGain;
    }

    public void Start()
    {
        var configuration = Plugin.Instance.Configuration;
        if (configuration.DesiredMateriaTypeId == 0)
        {
            Status = "Choose a materia type first.";
            return;
        }

        if (configuration.DesiredCount <= 0)
        {
            Status = "Set how many materia you want to make.";
            return;
        }

        if (!Plugin.ClientState.IsLoggedIn)
        {
            Status = "Log in before starting.";
            return;
        }

        ProducedThisRun = 0;
        TransmutesThisRun = 0;
        TargetCount = configuration.DesiredCount;
        currentBatch.Clear();
        addIndex = 0;
        addAttempts = 0;
        lastFilledSlots = 0;
        confirmSent = false;
        resultNoted = false;
        bagCountsAtSubmit = null;
        waitingSince = DateTime.UtcNow;
        nextActionAt = DateTime.UtcNow;
        State = TransmuteRunState.WaitingForWindow;
        Status = "Open transmutation with Mutamix: talk, then choose Transmute Materia.";
        Plugin.ChatGui.Print("[Materia Transmuter] Starting.");
    }

    public void Stop(string? reason = null)
    {
        State = string.IsNullOrEmpty(reason) ? TransmuteRunState.Idle : TransmuteRunState.Finished;
        Status = reason ?? "Stopped.";
        currentBatch.Clear();
        Plugin.Log.Information($"Stopped: {Status}");
        Plugin.ChatGui.Print($"[Materia Transmuter] {Status}");
    }

    private void OnUpdate(IFramework framework)
    {
        if (!IsRunning)
        {
            return;
        }

        try
        {
            if (!Plugin.ClientState.IsLoggedIn)
            {
                Stop("Stopped because you logged out.");
                return;
            }

            if (DateTime.UtcNow < nextActionAt)
            {
                return;
            }

            if (game.IsPlayerBusy() && State is not TransmuteRunState.WaitingForResult)
            {
                Status = "Waiting for the client to finish a cutscene or event...";
                return;
            }

            switch (State)
            {
                case TransmuteRunState.WaitingForWindow:
                    TickWaitingForWindow();
                    break;
                case TransmuteRunState.Adding:
                    TickAdding();
                    break;
                case TransmuteRunState.Confirming:
                    TickConfirming();
                    break;
                case TransmuteRunState.WaitingForResult:
                    TickWaitingForResult();
                    break;
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Error(ex, "Transmute update failed.");
            Stop("Stopped because of an error. Check /xllog.");
        }
    }

    private void TickWaitingForWindow()
    {
        if (game.TryAdvanceTalk() || game.TryConfirmYesNo())
        {
            Status = "Advancing Mutamix dialogue...";
            Delay();
            return;
        }

        if (!game.IsTransmuteWindowOpen() && game.TryAdvanceNpcDialog())
        {
            Status = "Choosing Transmute Materia...";
            Delay();
            return;
        }

        if (!game.IsTransmuteWindowOpen())
        {
            if (DateTime.UtcNow - waitingSince > TimeSpan.FromSeconds(45))
            {
                Stop("Timed out waiting for Mutamix's transmutation window.");
            }

            return;
        }

        if (resultNoted && game.GetFilledSlotCount() > 0)
        {
            if (DateTime.UtcNow - waitingSince < TimeSpan.FromSeconds(8))
            {
                Status = "Waiting for Mutamix to clear the last batch...";
                return;
            }

            Plugin.Log.Warning(
                $"Trade window still reports {game.GetFilledSlotCount()}/5 after the result; starting the next batch anyway.");
        }

        if (!game.HasInventoryPicker() && DateTime.UtcNow - waitingSince < TimeSpan.FromSeconds(10))
        {
            Status = "Waiting for the transmutation inventory...";
            return;
        }

        BeginNextBatch();
    }

    private void BeginNextBatch()
    {
        if (ProducedThisRun >= TargetCount)
        {
            FinishSuccess();
            return;
        }

        var filled = game.GetFilledSlotCount();
        if (filled > 0 && (confirmSent || resultNoted)
            && DateTime.UtcNow - waitingSince < TimeSpan.FromSeconds(8))
        {
            State = TransmuteRunState.WaitingForWindow;
            Status = "Waiting for Mutamix to clear the last batch...";
            return;
        }

        if (resultNoted)
        {
            filled = 0;
        }

        var all = PlayerInventory.ScanMateria(catalog);
        var reserved = resultNoted
            ? []
            : game.GetOfferedSlots();
        var plan = TransmutePlanner.Plan(
            all,
            Plugin.Instance.Configuration.DesiredMateriaTypeId,
            Plugin.Instance.Configuration.DesiredGrade,
            Plugin.Instance.Configuration,
            reserved);

        Plugin.Log.Information(
            $"Plan: scanned {all.Count} stacks, eligible {plan.EligibleCount} pcs / {plan.UniqueTypeCount} types, filled {filled}/5, reserved {reserved.Count}."
            + (plan.FailureReason is null ? string.Empty : $" Reason: {plan.FailureReason}"));
        if (Plugin.Instance.Configuration.DebugLogging)
        {
            Plugin.Log.Information($"Picker: {game.DescribePicker()}");
            foreach (var piece in plan.Pieces)
            {
                Plugin.Log.Information($"  {MateriaCatalog.FormatMateria(piece.TypeName, piece.Grade)} {piece.Container} slot {piece.Slot} item {piece.ItemId}");
            }
        }

        if (plan.CanTransmute)
        {
            currentBatch.Clear();
            currentBatch.AddRange(plan.Pieces);
            addIndex = 0;
            addAttempts = 0;
            lastFilledSlots = filled;
            confirmSent = false;
            resultNoted = false;
            bagCountsAtSubmit = null;
            State = filled >= 5 ? TransmuteRunState.Confirming : TransmuteRunState.Adding;
            Status = filled >= 5
                ? "Confirming transmutation..."
                : $"Offering {currentBatch.Count} materia ({plan.UniqueTypeCount} types available)...";
            Delay();
            return;
        }

        if (filled >= 5)
        {
            State = TransmuteRunState.Confirming;
            Status = "Window already has 5 materia; confirming...";
            Delay();
            return;
        }

        Stop(plan.FailureReason ?? "Not enough eligible materia left.");
    }

    private void TickAdding()
    {
        if (!game.IsTransmuteWindowOpen())
        {
            State = TransmuteRunState.WaitingForWindow;
            waitingSince = DateTime.UtcNow;
            Status = "Transmutation window closed. Open it again with Mutamix.";
            return;
        }

        if (game.TryConfirmQuantity(1) || game.TryConfirmYesNo())
        {
            Delay();
            return;
        }

        var filled = game.GetFilledSlotCount();
        if (filled >= 5)
        {
            State = TransmuteRunState.Confirming;
            Status = "Confirming transmutation...";
            Delay();
            return;
        }

        if (filled > lastFilledSlots)
        {
            lastFilledSlots = filled;
            addIndex++;
            addAttempts = 0;
        }

        if (addIndex >= currentBatch.Count)
        {
            if (filled < 5)
            {
                BeginNextBatch();
            }

            return;
        }

        var piece = currentBatch[addIndex];
        Status = $"Offering {addIndex + 1}/5: {MateriaCatalog.FormatMateria(piece.TypeName, piece.Grade)}";
        Plugin.Log.Information(
            $"Offer {addIndex + 1}/5 {MateriaCatalog.FormatMateria(piece.TypeName, piece.Grade)} {piece.Container} slot {piece.Slot} (filled {filled}/5)");
        game.TryAddMateria(piece.Container, piece.Slot);
        addAttempts++;
        if (addAttempts >= 3 && game.GetFilledSlotCount() == lastFilledSlots)
        {
            Plugin.Log.Warning($"Skipping {piece.Container} slot {piece.Slot} after {addAttempts} clicks with no change.");
            addIndex++;
            addAttempts = 0;
        }

        Delay();
    }

    private void TickConfirming()
    {
        if (game.TryConfirmYesNo() || game.TryAdvanceTalk())
        {
            EnterWaitingForResult("Advancing Mutamix dialogue...");
            return;
        }

        if (confirmSent)
        {
            EnterWaitingForResult("Submitted. Waiting for Mutamix...");
            return;
        }

        if (game.IsConfirmReady() && game.TryConfirmTransmute())
        {
            confirmSent = true;
            SnapshotBagCounts();
            EnterWaitingForResult("Submitted. Advancing Mutamix dialogue...");
        }
    }

    private void TickWaitingForResult()
    {
        if (game.TryConfirmYesNo() || game.TryAdvanceTalk())
        {
            waitingSince = DateTime.UtcNow;
            Status = "Advancing Mutamix dialogue...";
            Delay();
            return;
        }

        if (game.IsPlayerBusy() && !game.IsTransmuteWindowOpen())
        {
            Status = "Waiting for Mutamix to finish...";
            waitingSince = DateTime.UtcNow;
            return;
        }

        if (TryPollResult())
        {
            return;
        }

        if (confirmSent && game.GetFilledSlotCount() == 0)
        {
            NoteResult(null);
            return;
        }

        if (DateTime.UtcNow - waitingSince > TimeSpan.FromSeconds(30))
        {
            Plugin.Log.Warning("Timed out waiting for a transmutation result; waiting for the window to clear before the next batch.");
            resultNoted = true;
            State = TransmuteRunState.WaitingForWindow;
            waitingSince = DateTime.UtcNow;
            Status = "Waiting for Mutamix to clear the last batch...";
        }
    }

    private void OnInventoryGain(GameInventoryEvent type, InventoryEventArgs args)
    {
        if (State is not TransmuteRunState.WaitingForResult and not TransmuteRunState.Confirming)
        {
            return;
        }

        if (!confirmSent)
        {
            return;
        }

        if (Array.IndexOf(PlayerInventory.PlayerBagTypes, args.Item.ContainerType) < 0)
        {
            return;
        }

        if (!catalog.TryGetIdentity(args.Item.BaseItemId, out var identity))
        {
            return;
        }

        if (args is InventoryItemChangedArgs changed && changed.Item.Quantity <= changed.OldItemState.Quantity)
        {
            return;
        }

        if (type is not GameInventoryEvent.Added and not GameInventoryEvent.Changed)
        {
            return;
        }

        NoteResult(identity);
    }

    private void SnapshotBagCounts()
    {
        bagCountsAtSubmit = [];
        foreach (var entry in PlayerInventory.ScanMateria(catalog))
        {
            var key = (entry.TypeId, entry.Grade);
            bagCountsAtSubmit[key] = bagCountsAtSubmit.GetValueOrDefault(key) + entry.Quantity;
        }
    }

    private bool TryPollResult()
    {
        if (bagCountsAtSubmit is null)
        {
            return false;
        }

        var now = new Dictionary<(uint TypeId, int Grade), int>();
        foreach (var entry in PlayerInventory.ScanMateria(catalog))
        {
            var key = (entry.TypeId, entry.Grade);
            now[key] = now.GetValueOrDefault(key) + entry.Quantity;
        }

        foreach (var (key, after) in now)
        {
            var before = bagCountsAtSubmit.GetValueOrDefault(key);
            if (after <= before)
            {
                continue;
            }

            var type = catalog.FindType(key.TypeId);
            NoteResult(new MateriaIdentity(key.TypeId, key.Grade, 0, type?.Name ?? catalog.GetTypeName(key.TypeId)));
            return true;
        }

        return false;
    }

    private void NoteResult(MateriaIdentity? identity)
    {
        if (resultNoted)
        {
            return;
        }

        resultNoted = true;
        TransmutesThisRun++;
        var configuration = Plugin.Instance.Configuration;
        if (identity is not null
            && identity.TypeId == configuration.DesiredMateriaTypeId
            && identity.Grade == configuration.DesiredGrade)
        {
            ProducedThisRun++;
            var label = catalog.FormatMateriaLabel(identity.TypeId, identity.Grade);
            Plugin.Log.Information($"Result: {label} ({ProducedThisRun}/{TargetCount}).");
            Plugin.ChatGui.Print($"[Materia Transmuter] Got {label} ({ProducedThisRun}/{TargetCount}).");
        }
        else if (identity is not null)
        {
            var label = catalog.FormatMateriaLabel(identity.TypeId, identity.Grade);
            Plugin.Log.Information($"Result: {label}.");
            Plugin.ChatGui.Print($"[Materia Transmuter] Rolled {label}.");
        }
        else
        {
            Plugin.Log.Information("Result: transmutation finished (type not identified).");
            Plugin.ChatGui.Print("[Materia Transmuter] Transmutation finished.");
        }

        if (ProducedThisRun >= TargetCount)
        {
            FinishSuccess();
            return;
        }

        waitingSince = DateTime.UtcNow;
        nextActionAt = DateTime.UtcNow.AddMilliseconds(Math.Max(250, configuration.ActionDelayMs));
        State = TransmuteRunState.WaitingForWindow;
        Status = "Result received. Continuing...";
    }

    private void FinishSuccess()
    {
        var name = catalog.GetTypeName(Plugin.Instance.Configuration.DesiredMateriaTypeId);
        Stop($"Done. Made {ProducedThisRun} {MateriaCatalog.FormatMateria(name, Plugin.Instance.Configuration.DesiredGrade)} in {TransmutesThisRun} transmutation(s).");
    }

    private void EnterWaitingForResult(string status)
    {
        State = TransmuteRunState.WaitingForResult;
        waitingSince = DateTime.UtcNow;
        Status = status;
        Delay();
    }

    private void Delay()
    {
        var ms = Math.Clamp(Plugin.Instance.Configuration.ActionDelayMs, 250, 5000);
        nextActionAt = DateTime.UtcNow.AddMilliseconds(ms);
    }
}
