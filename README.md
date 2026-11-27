# TLD Mods

Smaller mods for The Long Drive (beta branch, BepInEx 5), each in its own folder under `mods/`, built and released on
its own. They work with [TLD RevaMP](https://github.com/TLD-RevaMP/TLD-RevaMP) and can be installed from its in-game mod
browser.

| Mod | What it does |
| --- | --- |
| [BusGut](mods/BusGut) | Empties the game's buses into cargo space |
| [MountAnything](mods/MountAnything) | Mounts any held item anywhere, with a placement preview |
| [TerrainChoice](mods/TerrainChoice) | Pick crossroads, main road only or the old 2023 terrain for a new game |
| [HeadUnit](mods/HeadUnit) | A digital head unit for the radio slot with a YouTube queue everyone in the car shares |
| [TvBrowser](mods/TvBrowser) | A web browser on the game's TV, used with the keyboard and mouse; everyone sees the same page, and replays show it |
| [CarTrailer](mods/CarTrailer) | A car trailer that RevaMP shares like the game's own trailer, with no multiplayer code |
| [TrailerHitches](mods/TrailerHitches) | Adds receiver hitches so cars such as car06p can accept the game's trailer drawbar |
| [RoadEnd](mods/RoadEnd) | Picked on the new-game screen: the main road ends after 5000 km at the game's roadside grave with a note |
| [RayTrace](mods/RayTrace) | Ray traced shadows, reflections, ambient occlusion and bounced light on Direct3D 12; made as a joke, not worth the frame rate it costs |

HeadUnit, TvBrowser, CarTrailer, TrailerHitches, RoadEnd and RayTrace are by Normal99. They were RevaMP's example mods
(`src/Mods` in TLD-RevaMP) and moved here with their history; they show how a mod works with RevaMP's multiplayer API
([docs/MODDING-MULTIPLAYER.md](https://github.com/TLD-RevaMP/TLD-RevaMP/blob/dev/docs/MODDING-MULTIPLAYER.md)).

## Building

Each mod is a plain .NET project. Build it against your game folder (beta branch, BepInEx installed):

    dotnet build mods/BusGut/BusGut.csproj -c Release -p:GameDir="C:\Program Files (x86)\Steam\steamapps\common\The Long Drive"

`-p:GameDir` can be left out when `TLD_DIR` is set, or when the game is in Steam's default folder.

HeadUnit and TvBrowser reference RevaMP's multiplayer API and build against the `TLDRevamp.Multiplayer.dll` installed
in that game folder (`BepInEx/plugins/TLDRevamp/`). To build against another one,
such as a RevaMP checkout's own build, add `-p:MpApi=/path/to/TLDRevamp.Multiplayer.dll`. At run time they load without
RevaMP as well (single player only). TrailerHitches and RoadEnd reach the API by reflection, so they build without it.

## Tests

`tests/` holds the unit tests that need no game; CI runs every project there. The TV Browser and Head Unit tests compile
RevaMP's record format from a TLD-RevaMP checkout (`TLD_REVAMP_DIR`, by default `TLD-RevaMP` next to this repository):

    dotnet run --project tests/TvBrowserCoreTest.csproj
    dotnet run --project tests/HeadUnitStateTest.csproj
    dotnet run --project tests/HeadUnitScreenTest.csproj   # also draws every screen to screens/ beside its build (or HU_SCREENS)
    TVB_CHROME=/path/to/chrome-headless-shell dotnet run --project tests/TvBrowserPageTest.csproj   # skipped without it

The in-game tests for the mods stay in TLD-RevaMP's `tools/` (`headunit.py`, `tvbrowser.py`, `cartrailer.py`,
`trailerhitches.py`, `roadend.py` and others): `TLD_TEST_MODS=HeadUnit` builds and deploys a mod from a checkout of this
repository (`TLD_MODS_DIR`, by default `TLD-Mods` next to TLD-RevaMP).

## Releases

Each mod is released on its own, with a tag named after it: `busgut-v0.2.0`, `mountanything-v0.2.2`,
`terrainchoice-v0.4.1`. A release's zip holds `BepInEx/plugins/<Mod>/<Mod>.dll`; `python3 tools/package.py <Mod>` builds
it into `dist/` and prints the DLL's approved-mods.txt line. The releases of the five mods above made before the move
are on TLD-RevaMP (tags such as `headunit-v0.2.1`).

RevaMP's in-game mod browser downloads these releases (its catalog is `packaging/mods.json` in TLD-RevaMP). A new
version is a new release here plus a catalog entry and an approved-mods.txt line there.

## A new mod

A new mod is a new folder under `mods/`, not a new repository.

## Licence

GPL-3.0 (see `LICENSE`), like each mod before. A mod that uses RevaMP's code, such as the multiplayer API, is released
under GPL-3.0 or a GPL-3.0-compatible licence.
