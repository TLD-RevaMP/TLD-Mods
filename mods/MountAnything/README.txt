MountAnything 0.2.1 - The Long Drive BETA branch (V2024.11.26b_test), works alongside TLD RevaMP (tested with 0.69.1)

Mount (glue) any item you hold onto whatever you look at: a car's body, another item, a building, the ground.

Use
  1. Pick an item up.
  2. Look at the spot where it should go (as close as you'd normally reach).
     A see-through green glowing copy of the item shows where and how it will be mounted, and the placement keys show at
     the bottom of the screen.
  3. Turn, tilt and move it with the placement keys (below) if you like.
  4. Press G (the game's own Glue key). The item is mounted exactly where the green copy was.
  To take it off, pick it up as usual (F).

Placement keys (while the green copy shows; standing, not in a car seat)
  Left / Right arrow    turn it around the surface's normal (like turning it on a table)
  Up / Down arrow       tip its top away from you / toward you
  , / .                 tip its top to the left / right
  = / -                 move it out of / into the surface
  /                     reset: square on the surface again (no turn, tilt or offset), and switch what it starts from:
                        as held -> flat (lying on its biggest side) -> upright (its own up side out) -> as held
  hold Left Shift       fine steps (1 degree, 1 mm instead of 15 degrees, 1 cm)
  Holding a key repeats it. The settings start fresh for every item you pick up.
  The item starts the way you hold it (turn it in your hands first, or press / for flat or upright). Its middle goes on the
  crosshair and its lowest point touches the surface there (then the = / - offset).
  None of these keys is one of the game's default keys on foot or one of RevaMP's (PageUp, PageDown, K, F11).
  They can all be changed in the settings.

Items the game could already glue rigidly (mirrors, the clock, the compass, plates...) get the green copy and the keys too, and the
game's G mounts them at the shown pose (turn off with GameGlueItems). The game's own Shift+R "attach mode" doesn't
apply to them then. Ones that hang on a joint (air fresheners, the sausage) keep the game's own glue, without a preview.
Car parts that normally only fit their slot (wheels, doors, engine parts...) can be mounted
anywhere too (turn off with AllowSlotParts); the game's own key (F) still puts them into their slot.

Settings: BepInEx\config\anonymoustld.mountanything.cfg
  [Mount] Enabled         = true
  [Mount] Key             = None    (None = the game's Glue key; or any Unity KeyCode name, e.g. H)
  [Mount] AllowSlotParts  = true
  [Mount] AlignToSurface  = false   (true: every placement starts upright: the item's own up side out of the surface)
  [Place] Enabled         = true    (false: no preview or keys, the 0.1 behaviour: the item keeps the way you hold
                                     it and its point nearest the crosshair goes on the spot)
  [Place] ShowPreview     = true
  [Place] ShowHelp        = true
  [Place] GameGlueItems   = true
  [Place] TurnLeft / TurnRight / TiltAway / TiltToward / TiltLeft / TiltRight / MoveOut / MoveIn / Reset /
          FineModifier    = LeftArrow / RightArrow / UpArrow / DownArrow / Comma / Period / Equals / Minus / Slash /
                            LeftShift   (Unity KeyCode names; None turns one off)
  [Place] AngleStep = 15, FineAngleStep = 1 (degrees), MoveStep = 0.01, FineMoveStep = 0.001 (metres)
  [Place] PreviewColor    = RGBA of the green copy (A = how strongly it glows)

Saving: mounted items are saved by the game itself (its glue record) and come back mounted after loading, at the
pose you gave them.
Multiplayer (RevaMP): everyone in the session needs this mod, the same version (RevaMP checks it at join). Mounts and
take-offs are shared by RevaMP like the game's own glued items; the mod sends the item's exact pose along with them.
The preview and the keys are only on your own screen.

Install: copy MountAnything.dll to BepInEx\plugins\MountAnything\.
Build: dotnet build -c Release (against the game in TLD_DIR, else the default Steam library; or -p:GameDir=...).
Licence: GPL-3.0 (LICENSE). It links TLD RevaMP's multiplayer API, which is GPL-3.0 too.
Without the mod, the game skips the mount record of an item that has no glue of its own, so such items should come
back loose where they were (not tested).
