using System.Collections.Generic;
using System.Linq;
using Dalamud.Game.Inventory;
using MateriaTransmuter.Inventory;
using MateriaTransmuter.Materia;

namespace MateriaTransmuter.Transmutation;

public sealed record PlannedPiece(
    uint ItemId,
    uint TypeId,
    int Grade,
    string TypeName,
    GameInventoryType Container,
    uint Slot);

public sealed class PlanResult
{
    public IReadOnlyList<PlannedPiece> Pieces { get; init; } = [];

    public int EligibleCount { get; init; }

    public int UniqueTypeCount { get; init; }

    public int DesiredCount { get; init; }

    public string? FailureReason { get; init; }

    public bool CanTransmute => Pieces.Count == 5 && FailureReason is null;
}

public static class TransmutePlanner
{
    public static bool IsBlacklisted(Configuration configuration, uint typeId, int grade)
        => configuration.Blacklist.Any(entry => entry.MateriaTypeId == typeId && entry.Grade == grade);

    public static IEnumerable<InventoryMateria> GetFodder(
        IReadOnlyList<InventoryMateria> all,
        uint desiredTypeId,
        int desiredGrade,
        Configuration configuration)
    {
        foreach (var entry in all)
        {
            if (entry.Grade != desiredGrade)
            {
                continue;
            }

            if (entry.TypeId == desiredTypeId)
            {
                continue;
            }

            if (IsBlacklisted(configuration, entry.TypeId, entry.Grade))
            {
                continue;
            }

            yield return entry;
        }
    }

    public static PlanResult Plan(
        IReadOnlyList<InventoryMateria> all,
        uint desiredTypeId,
        int desiredGrade,
        Configuration configuration,
        IReadOnlySet<(GameInventoryType Container, uint Slot)>? reservedSlots = null)
    {
        var desiredCount = PlayerInventory.Count(all, desiredTypeId, desiredGrade);
        var fodder = GetFodder(all, desiredTypeId, desiredGrade, configuration).ToList();
        var uniqueTypes = fodder.Select(entry => entry.TypeId).Distinct().Count();
        var eligibleCount = fodder.Sum(entry => entry.Quantity);

        if (desiredTypeId == 0)
        {
            return new PlanResult
            {
                DesiredCount = desiredCount,
                EligibleCount = eligibleCount,
                UniqueTypeCount = uniqueTypes,
                FailureReason = "Choose the materia type you want.",
            };
        }

        var remaining = new Dictionary<(GameInventoryType Container, uint Slot), StackState>();
        foreach (var entry in fodder)
        {
            var key = (entry.Container, entry.Slot);
            if (reservedSlots is not null && reservedSlots.Contains(key))
            {
                continue;
            }

            remaining[key] = new StackState(entry);
        }

        var selected = new List<PlannedPiece>(5);
        var usedTypes = new HashSet<uint>();

        while (selected.Count < 5)
        {
            var next = PickNext(remaining.Values, usedTypes, preferNewType: true);
            if (next is null)
            {
                next = PickNext(remaining.Values, usedTypes, preferNewType: false);
            }

            if (next is null)
            {
                break;
            }

            selected.Add(new PlannedPiece(
                next.ItemId,
                next.TypeId,
                next.Grade,
                next.TypeName,
                next.Container,
                next.Slot));
            usedTypes.Add(next.TypeId);

            var key = (next.Container, next.Slot);
            var stack = remaining[key];
            stack.Remaining--;
            if (stack.Remaining <= 0)
            {
                remaining.Remove(key);
            }
        }

        if (selected.Count < 5)
        {
            return new PlanResult
            {
                Pieces = selected,
                DesiredCount = desiredCount,
                EligibleCount = eligibleCount,
                UniqueTypeCount = uniqueTypes,
                FailureReason = eligibleCount < 5
                    ? $"Need 5 unused {MateriaCatalog.FormatGrade(desiredGrade)} materia of other types in your bags."
                    : "Could not assemble 5 eligible materia from your bags.",
            };
        }

        return new PlanResult
        {
            Pieces = selected,
            DesiredCount = desiredCount,
            EligibleCount = eligibleCount,
            UniqueTypeCount = uniqueTypes,
        };
    }

    private static StackState? PickNext(IEnumerable<StackState> stacks, HashSet<uint> usedTypes, bool preferNewType)
    {
        IEnumerable<StackState> candidates = stacks.Where(stack => stack.Remaining > 0);
        if (preferNewType)
        {
            candidates = candidates.Where(stack => !usedTypes.Contains(stack.TypeId));
        }

        return candidates
            .GroupBy(stack => stack.TypeId)
            .Select(group => new
            {
                TypeId = group.Key,
                Count = group.Sum(stack => stack.Remaining),
                Stack = group.OrderBy(stack => stack.Remaining).First(),
            })
            .OrderByDescending(group => group.Count)
            .ThenBy(group => group.TypeId)
            .Select(group => group.Stack)
            .FirstOrDefault();
    }

    private sealed class StackState(InventoryMateria source)
    {
        public uint ItemId { get; } = source.ItemId;
        public uint TypeId { get; } = source.TypeId;
        public int Grade { get; } = source.Grade;
        public string TypeName { get; } = source.TypeName;
        public GameInventoryType Container { get; } = source.Container;
        public uint Slot { get; } = source.Slot;
        public int Remaining { get; set; } = source.Quantity;
    }
}
