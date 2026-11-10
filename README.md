# TLD Mods

Smaller mods for The Long Drive (beta branch, BepInEx 5), each in its own folder under `mods/`, built and released on
its own. They work with [TLD RevaMP](https://github.com/TLD-RevaMP/TLD-RevaMP) and can be installed from its in-game mod
browser.

| Mod | What it does |
| --- | --- |
| [BusGut](mods/BusGut) | Empties the game's buses into cargo space |
| [MountAnything](mods/MountAnything) | Mounts any held item anywhere, with a placement preview |
| [TerrainChoice](mods/TerrainChoice) | Pick crossroads, main road only or the old 2023 terrain for a new game |

## Building

Each mod is a plain .NET project. Build it against your game folder (beta branch, BepInEx installed):

    dotnet build mods/BusGut/BusGut.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\The Long Drive"

`-p:GameDir` can be left out when `TLD_DIR` is set, or when the game is in Steam's default folder.

## Releases

Each mod is released on its own, with a tag named after it: `busgut-v0.2.0`, `mountanything-v0.2.1`,
`terrainchoice-v0.4.1`. A release's zip holds `BepInEx/plugins/<Mod>/<Mod>.dll`.

Releases before this repo existed stay in the mods' old repos (now archived), so their download links keep working.

## A new mod

A new mod is a new folder under `mods/`, not a new repository.

## Licence

GPL-3.0 (see `LICENSE`), like each mod before.
