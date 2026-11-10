BusGut 0.2.0 - empty buses for cargo (The Long Drive, BETA branch, BepInEx 5)

Every Bus01 (both sections) and Bus03 loses its passenger seats, seat frames and handrails/poles: one open
cargo space. The driver seat, dashboard, cockpit, floor, wood trim, steps, doors, windows, lights, roof hatches
and all buttons stay. Bus02 is a van with no passenger seats and is left as it is.

How it works: the parts are switched off when a bus loads, never deleted, so it's undone by switching the
addon off and reloading the save. Items lying in the bus are never touched.

Setting (BepInEx\config\anonymoustld.busgut.cfg):
  GutBuses = true   - false leaves buses as they are (takes effect after reloading the save)

Multiplayer: every player in the session needs BusGut with the same setting, otherwise one player has seats
where the other has empty space. With TLD RevaMP it registers with RevaMP's mod check, so RevaMP refuses a join
when the host and the joiner don't both have the same BusGut. Not tested in a multiplayer session yet.

Install: copy BusGut.dll to BepInEx\plugins\BusGut\. Remove that folder to uninstall.
Build: dotnet build -c Release (against the game in TLD_DIR, else the default Steam library; or -p:GameDir=...).
Licence: GPL-3.0 (LICENSE). It links TLD RevaMP's multiplayer API, which is GPL-3.0 too.

Tested (unattended, single player): Bus03 18 seats / 17 seat and pole colliders removed, Bus01 26 seats /
29 removed; rays along the aisle at seat height went from 28 and 24 hits to 4 (the end walls), while the floor
and both walls stayed solid at every measured point.
