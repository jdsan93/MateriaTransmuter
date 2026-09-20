using System.Collections.Generic;
using Dalamud.Game.Inventory;
using FFXIVClientStructs.FFXIV.Client.Game;
using MateriaTransmuter.Materia;

namespace MateriaTransmuter.Inventory;

public sealed record InventoryMateria(
    uint ItemId,
    uint TypeId,
    int Grade,
    string TypeName,
    GameInventoryType Container,
    uint Slot,
    int Quantity);

public static unsafe class PlayerInventory
{
    public static readonly GameInventoryType[] PlayerBagTypes =
    [
        GameInventoryType.Inventory1,
        GameInventoryType.Inventory2,
        GameInventoryType.Inventory3,
        GameInventoryType.Inventory4,
    ];

    public static List<InventoryMateria> ScanMateria(MateriaCatalog catalog)
    {
        var results = ScanFromManager(catalog);
        return results.Count > 0 ? results : ScanFromDalamud(catalog);
    }

    public static int Count(IEnumerable<InventoryMateria> materia, uint typeId, int grade)
    {
        var total = 0;
        foreach (var entry in materia)
        {
            if (entry.TypeId == typeId && entry.Grade == grade)
            {
                total += entry.Quantity;
            }
        }

        return total;
    }

    private static List<InventoryMateria> ScanFromManager(MateriaCatalog catalog)
    {
        var results = new List<InventoryMateria>();
        var manager = InventoryManager.Instance();
        if (manager is null)
        {
            return results;
        }

        foreach (var bag in PlayerBagTypes)
        {
            for (var slot = 0; slot < 35; slot++)
            {
                var item = manager->GetInventorySlot((InventoryType)bag, slot);
                if (item is null || item->IsEmpty() || item->Quantity <= 0)
                {
                    continue;
                }

                if (!catalog.TryGetIdentity(item->GetBaseItemId(), out var identity))
                {
                    continue;
                }

                results.Add(new InventoryMateria(
                    identity.ItemId,
                    identity.TypeId,
                    identity.Grade,
                    identity.TypeName,
                    bag,
                    (uint)slot,
                    item->Quantity));
            }
        }

        return results;
    }

    private static List<InventoryMateria> ScanFromDalamud(MateriaCatalog catalog)
    {
        var results = new List<InventoryMateria>();
        foreach (var bag in PlayerBagTypes)
        {
            foreach (var item in Plugin.GameInventory.GetInventoryItems(bag))
            {
                if (item.IsEmpty || item.Quantity <= 0)
                {
                    continue;
                }

                if (!catalog.TryGetIdentity(item.BaseItemId, out var identity))
                {
                    continue;
                }

                results.Add(new InventoryMateria(
                    identity.ItemId,
                    identity.TypeId,
                    identity.Grade,
                    identity.TypeName,
                    item.ContainerType,
                    item.InventorySlot,
                    item.Quantity));
            }
        }

        return results;
    }
}
