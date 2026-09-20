# Materia Transmuter

A Dalamud plugin that bulk-transmutes unwanted materia at Mutamix Bubblypots until you get the type and grade you want.

## What it does

Mutamix in Central Thanalan (X:23.7, Y:13.6) takes **5 materia** and returns **1 random materia**. The result is never one of the types you submitted, and its grade matches one of the grades you put in (upgrades are ignored for targeting).

This plugin:

- Lets you pick the **type** and **grade** you want, plus **how many** to make
- Lets you **blacklist** type/grade pairs you do not want to spend
- Uses **only your four inventory bags** (never retainers or the chocobo saddlebag)
- Only spends materia of the **same grade** as the target
- Never spends the target type itself
- Prefers **as many different types as possible** in each set of 5, which raises the odds of rolling the type you want
- Keeps rolling until it hits your count or runs out of eligible fodder

## Requirements

- XIVLauncher + Dalamud, with the game launched at least once
- .NET 10 SDK (or Visual Studio / Rider, which will fetch it)
- The `Marvelously Mutable Materia` quest completed so Mutamix will transmute for you

## Building

1. Open `MateriaTransmuter.slnx` in Visual Studio or Rider, or run `dotnet build` from this folder.
2. The plugin DLL is written to `MateriaTransmuter/bin/x64/Debug/MateriaTransmuter.dll` (or `Release`).

If Dalamud is not in the default XIVLauncher path, set `DALAMUD_HOME` to your Dalamud `dev` hooks directory.

## Loading in-game

1. `/xlsettings` → **Experimental** → add the full path to `MateriaTransmuter.dll` under Dev Plugin Locations.
2. `/xlplugins` → **Dev Tools > Installed Dev Plugins** → enable **Materia Transmuter**.
3. `/mt` (or `/transmute`) opens the window.

## Using it

1. Put the materia you are willing to spend in your **player bags**.
2. Choose the desired type and grade, how many you want to make, and any keep-list entries.
3. Talk to Mutamix and open the transmutation window.
4. Click **Start transmuting**.

The plugin fills five slots, confirms, waits for the result, and repeats. Stop at any time with **Stop**.

If the transmutation window is not detected, open it, then use **Settings → Log visible addons** and check `/xllog`. Enable debug logging there if you need more detail.

## Notes

- Inventory preview and planning work even before you start a run.
- Upgrade cutscenes are not used for targeting; if one plays, the plugin waits it out and continues.
- This is a personal/dev plugin. Automation like this is generally not accepted into the official Dalamud plugin repository.
