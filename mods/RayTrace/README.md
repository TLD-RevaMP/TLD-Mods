# RayTrace (experimental, just for fun)

Hardware ray traced sun shadows for The Long Drive's beta branch, on Direct3D 12 (DXR). A BepInEx 5 plugin plus a
native D3D12 plugin of its own; no Unity editor is involved: the shaders are HLSL compiled with Microsoft's DXC.

## What it does

Every frame, after the opaque geometry and before the post-processing:

1. the meshes that can cast a shadow within 300 m (and the terrain tiles and roads within 600 m) go into ray tracing
   acceleration structures built from the game's own GPU vertex and index buffers;
2. one shadow ray per pixel goes from the point the depth buffer shows toward the sun;
3. the shadow is applied to the frame. Mode 1 (the default) only beyond the game's own shadows (from 75 to 100 m on,
   where the game draws none), so distant hills, buildings and cars cast shadows too. Mode 2 everywhere, mode 3 shows
   the shadow mask itself.

Ray traced shadows of cut-out leaves and glass would be solid (the rays see triangles, not textures): those are left out.

## What it needs

- A GPU with ray tracing (DXR 1.0 or newer). Tested on an AMD Radeon RX 7800 XT under Proton (vkd3d-proton on RADV,
  Mesa 26.3). Not tested on Windows or on NVIDIA.
- The game started with `-force-d3d12` (Steam: Properties, Launch Options).
- **Unity's graphics jobs off**: in `TheLongDrive_Data/boot.config`, `gfx-enable-gfx-jobs=0` and
  `gfx-enable-native-gfx-jobs=0`. With them on, the game itself crashed about one world load in three on Direct3D 12
  (without this mod), and this mod made it nearly every time; with them off, 6 of 6 loads were clean with and without
  it. That costs frame rate on its own (49 to 36 fps in the test copy).

DLSS is not part of it: the test machine's GPU is AMD.

## What it changes besides

On Direct3D 12 the game's ambient occlusion (post-processing's Multi-Scale Volumetric Obscurance) has no compute shaders
and throws every frame; this mod switches it to Scalable Ambient Obscurance there.

## Not done yet

- Performance: in the test scene it went from 36 to 25 fps at 1280x720 (one ray per pixel, the scene structure rebuilt
  every frame). Half-resolution rays and updating the scene structure instead of rebuilding it are the next steps.
- The effect in screenshots is subtle; a scene that shows the far shadows clearly is still to find.
- The native plugin is written into `TheLongDrive_Data/Plugins/x86_64` by the mod; Unity loads it when the game starts,
  so a new version takes effect at the next start.

## Building

    mods/RayTrace/Native/build.sh [DXC folder]     # needs mingw-w64 and DXC's Linux release (default ~/.local/opt/dxc-1.9.2609)
    dotnet build -c Release -p:GameDir="<The Long Drive folder>"

Licence: GPL-3.0, like the rest of this repository.
