using System;
using System.Collections.Generic;
using Dalamud.Configuration;

namespace MateriaTransmuter;

[Serializable]
public sealed class BlacklistEntry
{
    public uint MateriaTypeId { get; set; }
    public int Grade { get; set; }
}

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 1;

    public bool IsConfigWindowMovable { get; set; } = true;

    public uint DesiredMateriaTypeId { get; set; }

    public int DesiredGrade { get; set; } = 10;

    public int DesiredCount { get; set; } = 1;

    public int ActionDelayMs { get; set; } = 650;

    public bool DebugLogging { get; set; }

    public List<BlacklistEntry> Blacklist { get; set; } = [];

    public void Save()
    {
        Plugin.PluginInterface.SavePluginConfig(this);
    }
}
