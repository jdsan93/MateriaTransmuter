using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using Dalamud.Game.Addon.Events;
using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Game.ClientState.Conditions;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using FFXIVClientStructs.FFXIV.Client.UI;
using FFXIVClientStructs.FFXIV.Client.UI.Agent;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace MateriaTransmuter.Transmutation;

public sealed unsafe class GameTransmuteClient : IDisposable
{
    private const string TradeAddonName = "TradeMultiple";

    public GameTransmuteClient()
    {
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PostSetup, OnAddonSetup);
        Plugin.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, OnPickerReceiveEvent);
    }

    public void Dispose()
    {
        Plugin.AddonLifecycle.UnregisterListener(OnAddonSetup);
        Plugin.AddonLifecycle.UnregisterListener(OnPickerReceiveEvent);
    }

    public bool IsPlayerBusy()
    {
        // OccupiedInEvent is set for Mutamix's menu and transmutation windows; do not treat that as blocked.
        return Plugin.Condition[ConditionFlag.OccupiedInCutSceneEvent]
               || Plugin.Condition[ConditionFlag.WatchingCutscene]
               || Plugin.Condition[ConditionFlag.WatchingCutscene78]
               || Plugin.Condition[ConditionFlag.BetweenAreas]
               || Plugin.Condition[ConditionFlag.Casting];
    }

    public bool IsTransmuteWindowOpen()
        => TryGetTradeMultiple(out _);

    public bool HasQuantityPrompt()
        => TryGetAddon("InputNumeric", out _);

    public bool HasInventoryPicker()
        => TryGetAddon("Inventory", out _)
           || TryGetAddon("InventoryLarge", out _)
           || TryGetAddon("InventoryExpansion", out _)
           || TryGetAddon("InventoryGrid0E", out _)
           || TryGetAddon("InventoryGrid0", out _);

    public int GetFilledSlotCount()
    {
        var agent = AgentTradeMultiple.Instance();
        if (agent is not null && agent->IsAgentActive())
        {
            return CountAgentFilled(agent);
        }

        if (!TryReadTradeValues(out var values))
        {
            return 0;
        }

        var filled = 0;
        var slots = values->Slots;
        for (var i = 0; i < AddonTradeMultiple.MaxSlots; i++)
        {
            if (IsFilledOffer(slots[i]))
            {
                filled++;
            }
        }

        return filled;
    }

    public bool IsConfirmReady()
    {
        var agent = AgentTradeMultiple.Instance();
        if (agent is not null && agent->IsAgentActive())
        {
            return agent->IsFull;
        }

        return GetFilledSlotCount() >= 5;
    }

    public HashSet<(GameInventoryType Container, uint Slot)> GetOfferedSlots()
    {
        var offered = new HashSet<(GameInventoryType Container, uint Slot)>();
        var agent = AgentTradeMultiple.Instance();
        if (agent is not null && agent->IsAgentActive())
        {
            var slots = agent->Slots;
            for (var i = 0; i < slots.Length; i++)
            {
                ref var slot = ref slots[i];
                if (!IsLiveAgentSlot(slot))
                {
                    continue;
                }

                offered.Add(((GameInventoryType)slot.Container, (uint)slot.Slot));
            }

            return offered;
        }

        if (!TryReadTradeValues(out var values))
        {
            return offered;
        }

        var atkSlots = values->Slots;
        for (var i = 0; i < AddonTradeMultiple.MaxSlots; i++)
        {
            if (!IsFilledOffer(atkSlots[i]))
            {
                continue;
            }

            var packed = atkSlots[i].InventorySlot.UInt;
            var container = (GameInventoryType)(packed >> 16);
            var slot = packed & 0xFFFF;
            if (slot > 34)
            {
                continue;
            }

            offered.Add((container, slot));
        }

        return offered;
    }

    public string DescribePicker()
    {
        TryGetBagLayout(out var layout, out var window);
        var tab = window is null ? -1 : layout switch
        {
            BagLayout.Single => ((AddonInventory*)window)->TabIndex,
            BagLayout.Double => ((AddonInventoryLarge*)window)->TabIndex,
            BagLayout.Quad => ((AddonInventoryExpansion*)window)->TabIndex,
            _ => -1,
        };

        return $"layout={layout} tab={tab} filled={GetFilledSlotCount()}/5 qtyPrompt={HasQuantityPrompt()} grids={ListInventoryAddonNames()}";
    }

    public bool TryAddMateria(GameInventoryType container, uint slot)
    {
        if (HasQuantityPrompt())
        {
            return TryConfirmQuantity(1);
        }

        if (!IsTransmuteWindowOpen())
        {
            LogDebug("Add skipped: TradeMultiple is not open.");
            return false;
        }

        var agent = AgentTradeMultiple.Instance();
        if (agent is null || !agent->IsAgentActive())
        {
            LogDebug($"Add skipped: AgentTradeMultiple inactive. {DescribePicker()}");
            return false;
        }

        if (agent->IsFull)
        {
            LogDebug("Add skipped: agent already has 5 slots.");
            return true;
        }

        if (IsAgentSlotFilled(agent, container, slot))
        {
            LogDebug($"Add skipped: {container} slot {slot} is already offered.");
            return true;
        }

        var manager = InventoryManager.Instance();
        var item = manager is null ? null : manager->GetInventorySlot((InventoryType)container, (int)slot);
        if (item is null || item->IsEmpty())
        {
            LogDebug($"Add skipped: {container} slot {slot} is empty.");
            return false;
        }

        var stack = item->GetQuantity();
        LogDebug($"Agent add {container} slot {slot} item={item->GetBaseItemId()} stack={stack} remaining={agent->GetSlotsRemaining()} pending={agent->PendingAdd} | {DescribePicker()}");

        // AddItemQuantity commits one piece without synthesizing DragDrop clicks (those crash).
        // Stacks of 1 go through AddItem, matching the native no-prompt path.
        if (stack <= 1)
        {
            agent->AddItem((InventoryType)container, (short)slot);
            LogDebug($"Called AddItem. After: remaining={agent->GetSlotsRemaining()} filled={GetFilledSlotCount()}/5 qtyPrompt={HasQuantityPrompt()} {DescribeAgentSlots(agent)}");
        }
        else
        {
            agent->AddItemQuantity(item, 1);
            LogDebug($"Called AddItemQuantity(1). After: remaining={agent->GetSlotsRemaining()} filled={GetFilledSlotCount()}/5 qtyPrompt={HasQuantityPrompt()} {DescribeAgentSlots(agent)}");
        }

        return true;
    }

    public bool TryConfirmQuantity(int quantity)
    {
        if (!TryGetAddon("InputNumeric", out var addon))
        {
            return false;
        }

        Fire(addon, true, quantity);
        LogDebug($"Set InputNumeric quantity to {quantity}.");
        return true;
    }

    public bool TryAdvanceTalk()
    {
        if (!TryGetAddon("Talk", out var talk))
        {
            return false;
        }

        var text = GetAddonText(talk);
        Fire(talk, true, 0);
        LogDebug($"Clicked Talk: {Truncate(text, 120)}");
        return true;
    }

    public bool TryConfirmTransmute()
    {
        if (TryConfirmYesNo() || TryAdvanceTalk())
        {
            return true;
        }

        if (!IsConfirmReady())
        {
            return false;
        }

        var agent = AgentTradeMultiple.Instance();
        if (agent is not null && agent->IsAgentActive() && agent->Confirm())
        {
            LogDebug("AgentTradeMultiple.Confirm() succeeded.");
            return true;
        }

        if (!TryGetTradeMultiple(out var trade))
        {
            return false;
        }

        if (TryClickButton(trade->ConfirmButton))
        {
            LogDebug("Clicked TradeMultiple Confirm.");
            return true;
        }

        ((AtkUnitBase*)trade)->FireCallbackInt(0);
        LogDebug("Fired TradeMultiple confirm callback.");
        return true;
    }

    public bool TryConfirmYesNo()
    {
        if (!TryGetAddon("SelectYesno", out var addon))
        {
            return false;
        }

        var prompt = GetAddonText(addon);
        if (!string.IsNullOrWhiteSpace(prompt) && !LooksLikeTransmutePrompt(prompt))
        {
            LogDebug($"Ignoring unrelated yes/no prompt: {prompt}");
            return false;
        }

        Fire(addon, true, 0);
        LogDebug($"Confirmed yes/no: {prompt}");
        return true;
    }

    public bool HasTalk()
        => TryGetAddon("Talk", out _);

    private static bool LooksLikeTransmutePrompt(string prompt)
    {
        return prompt.Contains("transmute", StringComparison.OrdinalIgnoreCase)
               || prompt.Contains("materia", StringComparison.OrdinalIgnoreCase)
               || prompt.Contains("mutamix", StringComparison.OrdinalIgnoreCase)
               || prompt.Contains("shifty", StringComparison.OrdinalIgnoreCase)
               || prompt.Contains("mixy", StringComparison.OrdinalIgnoreCase)
               || prompt.Contains("uplander", StringComparison.OrdinalIgnoreCase)
               || prompt.Contains("need-not", StringComparison.OrdinalIgnoreCase)
               || prompt.Contains("reborn", StringComparison.OrdinalIgnoreCase);
    }

    public bool TryAdvanceNpcDialog()
    {
        if (TryAdvanceTalk())
        {
            return true;
        }

        if (IsTransmuteWindowOpen())
        {
            return false;
        }

        return TrySelectStringOption("Transmute Materia");
    }

    public IReadOnlyList<string> ListVisibleAddons()
    {
        var names = new List<string>();
        var manager = RaptureAtkUnitManager.Instance();
        if (manager is null)
        {
            return names;
        }

        try
        {
            var list = manager->AllLoadedUnitsList;
            for (var i = 0; i < list.Count; i++)
            {
                var addon = list.Entries[i].Value;
                if (addon is null || !addon->IsReady || !addon->IsVisible)
                {
                    continue;
                }

                var text = GetAddonText(addon);
                var popup = GetPopupEntries(addon);
                if (popup.Count > 0)
                {
                    text = string.IsNullOrWhiteSpace(text)
                        ? string.Join(" / ", popup)
                        : $"{text} | {string.Join(" / ", popup)}";
                }

                names.Add(string.IsNullOrWhiteSpace(text)
                    ? addon->NameString
                    : $"{addon->NameString} | {Truncate(text, 160)}");
            }
        }
        catch (Exception ex)
        {
            Plugin.Log.Warning(ex, "Failed to enumerate visible addons.");
        }

        names.Sort(StringComparer.OrdinalIgnoreCase);
        return names;
    }

    private void OnAddonSetup(AddonEvent eventType, AddonArgs args)
    {
        if (args.AddonName is not ("TradeMultiple" or "InputNumeric")
            && (string.IsNullOrEmpty(args.AddonName) || !args.AddonName.StartsWith("Inventory", StringComparison.Ordinal)))
        {
            return;
        }

        LogDebug($"Opened {args.AddonName}");
    }

    private bool TryGetTradeMultiple(out AddonTradeMultiple* trade)
    {
        if (!TryGetAddon(TradeAddonName, out var addon))
        {
            trade = null;
            return false;
        }

        trade = (AddonTradeMultiple*)addon;
        return true;
    }

    private bool TryReadTradeValues(out AddonTradeMultiple.TradeMultipleAtkValues* values)
    {
        values = null;
        if (!TryGetTradeMultiple(out var trade))
        {
            return false;
        }

        var addon = (AtkUnitBase*)trade;
        if (addon->AtkValues is null || addon->AtkValuesCount < 18)
        {
            return false;
        }

        values = trade->TypedAtkValues;
        return values is not null;
    }

    private static bool IsFilledOffer(AddonTradeMultiple.TradeMultipleSlotAtkValues slot)
    {
        var quantity = slot.Quantity.UInt;
        return quantity is > 0 and < 10_000;
    }

    private static int CountAgentFilled(AgentTradeMultiple* agent)
    {
        var filled = 0;
        var slots = agent->Slots;
        for (var i = 0; i < slots.Length; i++)
        {
            if (IsLiveAgentSlot(slots[i]))
            {
                filled++;
            }
        }

        return filled;
    }

    private static bool IsLiveAgentSlot(in InventoryItemRef slot)
        => slot.ItemId != 0
           && slot.Container is not InventoryType.Invalid
           && slot.Quantity > 0
           && slot.Slot is >= 0 and <= 34;

    private static bool IsAgentSlotFilled(AgentTradeMultiple* agent, GameInventoryType container, uint slot)
    {
        var slots = agent->Slots;
        for (var i = 0; i < slots.Length; i++)
        {
            ref var offered = ref slots[i];
            if (IsLiveAgentSlot(offered)
                && offered.Container == (InventoryType)container
                && offered.Slot == (short)slot)
            {
                return true;
            }
        }

        return false;
    }

    private static string DescribeAgentSlots(AgentTradeMultiple* agent)
    {
        var slots = agent->Slots;
        var parts = new List<string>();
        for (var i = 0; i < slots.Length; i++)
        {
            ref var offered = ref slots[i];
            parts.Add(offered.ItemId == 0
                ? "-"
                : $"{offered.Container}:{offered.Slot}#{offered.ItemId}x{offered.Quantity}");
        }

        return $"agent=[{string.Join(", ", parts)}]";
    }

    private bool TryMapGridToBag(string gridName, out GameInventoryType bag)
    {
        bag = GameInventoryType.Inventory1;
        if (!TryParseGridIndex(gridName, out var gridIndex)
            || !TryGetBagLayout(out var layout, out var window)
            || window is null)
        {
            return false;
        }

        var tab = layout switch
        {
            BagLayout.Single => ((AddonInventory*)window)->TabIndex,
            BagLayout.Double => ((AddonInventoryLarge*)window)->TabIndex,
            BagLayout.Quad => 0,
            _ => 0,
        };

        var bagIndex = layout switch
        {
            BagLayout.Single => tab,
            BagLayout.Double => (tab * 2) + gridIndex,
            BagLayout.Quad => gridIndex,
            _ => gridIndex,
        };

        if (bagIndex is < 0 or > 3)
        {
            return false;
        }

        bag = bagIndex switch
        {
            0 => GameInventoryType.Inventory1,
            1 => GameInventoryType.Inventory2,
            2 => GameInventoryType.Inventory3,
            _ => GameInventoryType.Inventory4,
        };
        return true;
    }

    private static bool TryParseGridIndex(string name, out int index)
    {
        index = 0;
        if (name is "InventoryGrid" or "InventoryGrid0" or "InventoryGrid0E")
        {
            return true;
        }

        if (name.StartsWith("InventoryGrid", StringComparison.Ordinal) && name.Length >= 14 && char.IsDigit(name[13]))
        {
            index = name[13] - '0';
            return index is >= 0 and <= 3;
        }

        return false;
    }

    private List<string> VisibleGridNames()
    {
        var names = new List<string>();
        foreach (var name in AllGridNames())
        {
            if (TryGetAddon(name, out _))
            {
                names.Add(name);
            }
        }

        return names;
    }

    private static uint GetSlotItemId(GameInventoryType container, uint slot)
    {
        var manager = InventoryManager.Instance();
        var item = manager is null ? null : manager->GetInventorySlot((InventoryType)container, (int)slot);
        if (item is null || item->IsEmpty())
        {
            return 0;
        }

        return item->GetBaseItemId();
    }

    private static int GetItemIconId(uint itemId)
    {
        if (itemId == 0)
        {
            return 0;
        }

        var sheet = Plugin.DataManager.GetExcelSheet<Item>();
        return sheet.TryGetRow(itemId, out var row) ? row.Icon : 0;
    }

    private void OnPickerReceiveEvent(AddonEvent eventType, AddonArgs args)
    {
        if (!Plugin.Instance.Configuration.DebugLogging
            || args is not AddonReceiveEventArgs received
            || string.IsNullOrEmpty(args.AddonName))
        {
            return;
        }

        if (args.AddonName is not ("TradeMultiple" or "InputNumeric")
            && !args.AddonName.StartsWith("Inventory", StringComparison.Ordinal))
        {
            return;
        }

        if (received.AtkEventType is not (
            AddonEventType.DragDropClick
            or AddonEventType.MouseClick
            or AddonEventType.ButtonClick))
        {
            return;
        }

        LogDebug($"UI {args.AddonName} {received.AtkEventType} param={received.EventParam}");
    }

    private bool EnsureBagVisible(GameInventoryType container)
    {
        if (!TryGetBagLayout(out var layout, out var window))
        {
            return true;
        }

        var bag = BagIndex(container);
        var neededTab = layout switch
        {
            BagLayout.Single => bag,
            BagLayout.Double => bag / 2,
            BagLayout.Quad => 0,
            _ => 0,
        };

        var currentTab = layout switch
        {
            BagLayout.Single => ((AddonInventory*)window)->TabIndex,
            BagLayout.Double => ((AddonInventoryLarge*)window)->TabIndex,
            BagLayout.Quad => ((AddonInventoryExpansion*)window)->TabIndex,
            _ => neededTab,
        };

        if (currentTab == neededTab)
        {
            return true;
        }

        switch (layout)
        {
            case BagLayout.Single:
                ((AddonInventory*)window)->SetTab(neededTab);
                break;
            case BagLayout.Double:
                ((AddonInventoryLarge*)window)->SetTab(neededTab);
                break;
            case BagLayout.Quad:
                ((AddonInventoryExpansion*)window)->SetTab(neededTab, true);
                break;
        }

        LogDebug($"Switched {layout} inventory to tab {neededTab} for {container} (was {currentTab}).");
        return false;
    }

    private bool TryGetBagLayout(out BagLayout layout, out AtkUnitBase* window)
    {
        if (TryGetAddon("InventoryExpansion", out window))
        {
            layout = BagLayout.Quad;
            return true;
        }

        if (TryGetAddon("InventoryLarge", out window))
        {
            layout = BagLayout.Double;
            return true;
        }

        if (TryGetAddon("Inventory", out window))
        {
            layout = BagLayout.Single;
            return true;
        }

        layout = BagLayout.Unknown;
        window = null;
        return false;
    }

    private static int BagIndex(GameInventoryType container)
        => container switch
        {
            GameInventoryType.Inventory1 => 0,
            GameInventoryType.Inventory2 => 1,
            GameInventoryType.Inventory3 => 2,
            GameInventoryType.Inventory4 => 3,
            _ => 0,
        };

    private static IEnumerable<string> AllGridNames()
    {
        yield return "InventoryGrid0E";
        yield return "InventoryGrid1E";
        yield return "InventoryGrid2E";
        yield return "InventoryGrid3E";
        yield return "InventoryGrid0";
        yield return "InventoryGrid1";
        yield return "InventoryGrid2";
        yield return "InventoryGrid3";
        yield return "InventoryGrid";
    }

    private enum BagLayout
    {
        Unknown,
        Single,
        Double,
        Quad,
    }

    private bool TrySelectStringOption(string option)
    {
        if (!TryGetAddon("SelectIconString", out var addon) && !TryGetAddon("SelectString", out addon))
        {
            return false;
        }

        var texts = GetPopupEntries(addon);
        if (texts.Count == 0)
        {
            texts = GetTextNodes(addon);
        }

        if (texts.Count == 0)
        {
            return false;
        }

        for (var i = 0; i < texts.Count; i++)
        {
            if (!texts[i].Contains(option, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (texts[i].Contains("Removal", StringComparison.OrdinalIgnoreCase)
                || texts[i].Equals("Materia Transmutation", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            Fire(addon, true, i);
            return true;
        }

        return false;
    }

    private string ListInventoryAddonNames()
    {
        var names = new List<string>();
        foreach (var name in new[]
                 {
                     "Inventory", "InventoryLarge", "InventoryExpansion",
                     "InventoryGrid0E", "InventoryGrid1E", "InventoryGrid2E", "InventoryGrid3E",
                     "InventoryGrid0", "InventoryGrid1", "InventoryGrid2", "InventoryGrid3",
                     "InventoryGrid", "InputNumeric",
                 })
        {
            if (TryGetAddon(name, out _))
            {
                names.Add(name);
            }
        }

        return names.Count == 0 ? "(none)" : string.Join(", ", names);
    }

    private static bool TryClickButton(AtkComponentButton* button)
    {
        if (button is null)
        {
            return false;
        }

        var evt = new AtkEvent();
        button->ReceiveEvent(AtkEventType.ButtonClick, 0, &evt);
        return true;
    }

    private static bool TryGetAddon(string name, out AtkUnitBase* addon)
    {
        addon = null;
        var manager = RaptureAtkUnitManager.Instance();
        if (manager is null)
        {
            return false;
        }

        var list = manager->AllLoadedUnitsList;
        for (var i = 0; i < list.Count; i++)
        {
            var unit = list.Entries[i].Value;
            if (unit is null || !unit->IsReady || !unit->IsVisible)
            {
                continue;
            }

            if (!string.Equals(unit->NameString, name, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            addon = unit;
            return true;
        }

        return false;
    }

    private static void Fire(AtkUnitBase* addon, bool close, params int[] values)
    {
        if (values.Length == 0)
        {
            return;
        }

        var atkValues = stackalloc AtkValue[values.Length];
        for (var i = 0; i < values.Length; i++)
        {
            atkValues[i].SetInt(values[i]);
        }

        addon->FireCallback((uint)values.Length, atkValues, close);
    }

    private static List<string> GetPopupEntries(AtkUnitBase* addon)
    {
        var entries = new List<string>();
        if (addon is null)
        {
            return entries;
        }

        PopupMenu* menu = addon->NameString switch
        {
            "SelectIconString" => (PopupMenu*)Unsafe.AsPointer(ref ((AddonSelectIconString*)addon)->PopupMenu),
            "SelectString" => (PopupMenu*)Unsafe.AsPointer(ref ((AddonSelectString*)addon)->PopupMenu),
            _ => null,
        };

        if (menu is null || menu->EntryNames is null || menu->EntryCount <= 0)
        {
            return entries;
        }

        for (var i = 0; i < menu->EntryCount; i++)
        {
            var text = menu->EntryNames[i].ToString();
            entries.Add(string.IsNullOrWhiteSpace(text) ? string.Empty : text.Trim());
        }

        return entries;
    }

    private static string GetAddonText(AtkUnitBase* addon)
        => string.Join(" ", GetTextNodes(addon));

    private static List<string> GetTextNodes(AtkUnitBase* addon)
    {
        var texts = new List<string>();
        if (addon is null)
        {
            return texts;
        }

        CollectTextNodes(&addon->UldManager, texts, 0);
        return texts;
    }

    private static void CollectTextNodes(AtkUldManager* uld, List<string> texts, int depth)
    {
        if (uld is null || depth > 6)
        {
            return;
        }

        var count = uld->NodeListCount;
        for (var i = 0; i < count; i++)
        {
            var node = uld->NodeList[i];
            if (node is null)
            {
                continue;
            }

            if (node->Type == NodeType.Text)
            {
                var text = ((AtkTextNode*)node)->NodeText.ToString();
                if (!string.IsNullOrWhiteSpace(text))
                {
                    texts.Add(text.Trim());
                }

                continue;
            }

            if ((ushort)node->Type < 1000)
            {
                continue;
            }

            var component = ((AtkComponentNode*)node)->Component;
            if (component is not null)
            {
                CollectTextNodes(&component->UldManager, texts, depth + 1);
            }
        }
    }

    private static string Truncate(string value, int max)
        => value.Length <= max ? value : value[..max] + "...";

    private static void LogDebug(string message)
    {
        if (Plugin.Instance.Configuration.DebugLogging)
        {
            Plugin.Log.Information($"[Transmute] {message}");
        }
        else
        {
            Plugin.Log.Debug(message);
        }
    }
}
