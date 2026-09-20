using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using Lumina.Excel.Sheets;

namespace MateriaTransmuter.Materia;

public sealed record MateriaTypeInfo(uint TypeId, string Name, string StatName, IReadOnlyList<int> Grades);

public sealed record MateriaIdentity(uint TypeId, int Grade, uint ItemId, string TypeName);

public sealed class MateriaCatalog
{
    private static readonly Regex MateriaNamePattern = new(@"^(.*) Materia ([IVXLCDM]+)$", RegexOptions.Compiled);
    private static readonly string[] RomanNumerals =
    [
        "I", "II", "III", "IV", "V", "VI", "VII", "VIII", "IX", "X",
        "XI", "XII", "XIII", "XIV", "XV", "XVI",
    ];

    // Current transmutable families, in the order they should appear in the UI.
    private static readonly string[] CurrentTypeNames =
    [
        "Savage Aim",
        "Savage Might",
        "Heavens' Eye",
        "Quickarm",
        "Quicktongue",
        "Battledance",
        "Piety",
        "Craftsman's Competence",
        "Craftsman's Command",
        "Craftsman's Cunning",
        "Gatherer's Grasp",
        "Gatherer's Guerdon",
        "Gatherer's Guile",
    ];

    private static readonly HashSet<string> CurrentTypeNameSet = new(CurrentTypeNames, StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> CurrentStatNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "Critical Hit",
        "Determination",
        "Direct Hit Rate",
        "Skill Speed",
        "Spell Speed",
        "Tenacity",
        "Piety",
        "Craftsmanship",
        "Control",
        "CP",
        "Gathering",
        "Perception",
        "GP",
    };

    private readonly Dictionary<uint, MateriaIdentity> identitiesByItemId = new();
    private readonly List<MateriaTypeInfo> types = [];

    public IReadOnlyList<MateriaTypeInfo> Types => types;

    public void Reload()
    {
        identitiesByItemId.Clear();
        types.Clear();

        var sheet = Plugin.DataManager.GetExcelSheet<Lumina.Excel.Sheets.Materia>();
        var typesByName = new Dictionary<string, MateriaTypeInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var row in sheet)
        {
            var grades = new List<int>();
            string? typeName = null;
            var statName = row.BaseParam.IsValid ? row.BaseParam.Value.Name.ToString() : string.Empty;

            for (var i = 0; i < row.Item.Count; i++)
            {
                var itemRef = row.Item[i];
                if (itemRef.RowId == 0 || !itemRef.IsValid)
                {
                    continue;
                }

                var item = itemRef.Value;
                var itemName = item.Name.ToString();
                if (string.IsNullOrWhiteSpace(itemName))
                {
                    continue;
                }

                typeName ??= NormalizeApostrophes(ParseTypeName(itemName, statName, row.RowId));
                if (!IsCurrentType(typeName, statName))
                {
                    break;
                }

                var grade = i + 1;
                var canonicalTypeId = typesByName.TryGetValue(typeName, out var existing)
                    ? existing.TypeId
                    : row.RowId;
                identitiesByItemId[item.RowId] = new MateriaIdentity(canonicalTypeId, grade, item.RowId, typeName);
                grades.Add(grade);
            }

            if (typeName is null || grades.Count == 0 || !IsCurrentType(typeName, statName))
            {
                continue;
            }

            if (typesByName.TryGetValue(typeName, out var current))
            {
                typesByName[typeName] = current with
                {
                    Grades = current.Grades.Union(grades).Distinct().OrderBy(grade => grade).ToList(),
                };
            }
            else
            {
                typesByName[typeName] = new MateriaTypeInfo(row.RowId, typeName, statName, grades);
            }
        }

        foreach (var name in CurrentTypeNames)
        {
            if (typesByName.TryGetValue(name, out var type))
            {
                types.Add(type);
            }
        }
    }

    public static string ComboLabel(MateriaTypeInfo type)
    {
        var stat = AbbreviateStat(type.StatName);
        if (string.IsNullOrWhiteSpace(stat)
            || string.Equals(type.Name, stat, StringComparison.OrdinalIgnoreCase)
            || string.Equals(type.Name, type.StatName, StringComparison.OrdinalIgnoreCase))
        {
            return type.Name;
        }

        return $"{type.Name} ({stat})";
    }

    public string FormatMateriaLabel(uint typeId, int grade)
    {
        var type = FindType(typeId);
        return FormatMateria(type?.Name ?? GetTypeName(typeId), grade, type?.StatName);
    }

    public static string FormatMateria(string typeName, int grade, string? statName = null)
    {
        var label = $"{typeName} Materia {FormatGrade(grade)}";
        var stat = AbbreviateStat(statName);
        if (string.IsNullOrWhiteSpace(stat)
            || typeName.Contains(stat, StringComparison.OrdinalIgnoreCase)
            || string.Equals(typeName, statName, StringComparison.OrdinalIgnoreCase))
        {
            return label;
        }

        return $"{label} ({stat})";
    }

    public static string AbbreviateStat(string? statName)
        => statName switch
        {
            "Critical Hit" => "Crit",
            "Determination" => "Det",
            "Direct Hit Rate" => "DH",
            "Skill Speed" => "SkS",
            "Spell Speed" => "SpS",
            "Tenacity" => "Ten",
            "Piety" => "Pie",
            _ => statName ?? string.Empty,
        };

    private static bool IsCurrentType(string typeName, string statName)
        => CurrentTypeNameSet.Contains(NormalizeApostrophes(typeName))
           || CurrentStatNames.Contains(statName);

    private static string NormalizeApostrophes(string value)
        => value.Replace('’', '\'').Replace('`', '\'');

    public bool TryGetIdentity(uint itemId, out MateriaIdentity identity)
        => identitiesByItemId.TryGetValue(itemId, out identity!);

    public MateriaTypeInfo? FindType(uint typeId)
        => types.FirstOrDefault(type => type.TypeId == typeId);

    public string GetTypeName(uint typeId)
        => FindType(typeId)?.Name ?? $"Type {typeId}";

    public static string FormatGrade(int grade)
    {
        if (grade <= 0)
        {
            return grade.ToString(CultureInfo.InvariantCulture);
        }

        if (grade <= RomanNumerals.Length)
        {
            return RomanNumerals[grade - 1];
        }

        return grade.ToString(CultureInfo.InvariantCulture);
    }

    private static string ParseTypeName(string itemName, string statName, uint typeId)
    {
        var match = MateriaNamePattern.Match(itemName);
        if (match.Success)
        {
            return match.Groups[1].Value;
        }

        if (!string.IsNullOrWhiteSpace(statName))
        {
            return statName;
        }

        return $"Materia {typeId}";
    }
}
