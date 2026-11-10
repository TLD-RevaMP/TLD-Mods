TerrainChoice 0.4.1 - pick the terrain when you start or host a new game (The Long Drive BETA branch only)

Beta = Steam branch "beta", V2024.11.26b_test (build 16539689). It does nothing useful on the public game.

WHAT IT DOES
  The game-mode settings (where you pick map and biome for a new game) get a new dropdown right under the biome:
    Roads: with crossroads  - (default) the beta's normal world: main road plus side roads and crossroads
    Roads: main road only   - the beta's own built-in "main road only" mode: no side roads, no crossroads
    Old terrain (2023), main road
                            - the world of the public game v2023.05.02d: its landscape (height and biomes), its
                              road (the same path, bends and slopes) and its roadside buildings (the same buildings
                              in the same places, every few km), as in the old game for the same seed; drawn with
                              the beta's road pieces, power poles, building models, scenery and textures.
                              Nothing from the public game's files is needed or shipped.
  - The choice is applied before any road generates.
  - It's kept in the save: the map name gets " - main road only" / " - old terrain", which also shows on the load screen
    and in the lobby list.
  - Loaded worlds keep their terrain. The setting only affects NEW games.
  - Joiners always get the host's terrain:
      - the game's own multiplayer: through the map name the host already sends;
      - RevaMP: at the end of RevaMP's Welcome message.

EVERY PLAYER NEEDS IT
  - With RevaMP, the mod registers with RevaMP's mod check:
      - a joiner without it is refused with "install TerrainChoice 0.4.1";
      - a joiner WITH it can't join a host WITHOUT it ("remove terrainchoice").
  - With the game's own multiplayer there is no check: a joiner without the mod would get crossroads in a main-road-only world.

INSTALL
  Copy BepInEx\plugins\TerrainChoice\TerrainChoice.dll into the beta game folder's BepInEx\plugins\TerrainChoice\.
  Remove that folder to uninstall.

CONFIG  BepInEx\config\anonymoustld.terrainchoice.cfg
  [NewGame] Terrain = Crossroads | MainRoadOnly | OldTerrain   (same as the dropdown)
  [Debug]   DumpUi  = false                        (true writes the settings UI hierarchy to the log once)

TEST MODE (test copies only)
  TheLongDrive.exe -tcdiag crossroads|mainroadonly|oldterrain|load [-tcseed 12345]
  - crossroads / mainroadonly / oldterrain: starts a new game with that terrain, writes road statistics and screenshots
    (BepInEx\TerrainChoice-diag.txt, TerrainChoice-*.png), autosaves, quits.
  - load: continues that save and writes the same statistics.

OLD TERRAIN: WHAT IS AND ISN'T OLD
  Old (exactly the 2023 formulas, checked against the real public game for seed 12345: terrain heights equal, road
  within 1 mm over 40 km, all 612 roadside buildings with the same type, position and rotation): ground height, the
  desert/grass/snow pattern, the road's path and slopes, which buildings stand where along the road.
  Beta: the models (road pieces, power poles, buildings and what is inside them), scenery (cacti, trees, rocks, the
  big desert rock formations), textures. The beta's own random buildings are left out; its start house stays.
  Differences from the old game:
    - the old world is shifted 60 m east (the beta puts the start house beside the road next to the world's origin);
    - behind the start the old game had nothing; here a road with buildings runs the other way too, by the same rules;
    - the old mansion is the beta's mansion; the old village (falu01) never appeared on the old road either.
  A far save loads a little slower the first time: the old road is computed from the start up to where you are.

BUILD
  dotnet build -c Release   (against the game in TLD_DIR, else the default Steam library; or -p:GameDir=...)

LICENCE  GPL-3.0 (LICENSE). It links TLD RevaMP's multiplayer API, which is GPL-3.0 too.
