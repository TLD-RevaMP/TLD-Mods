# RayTrace

Mod that enables ray tracing to achieve true AAA slop in The Long Drive, made as a joke. We do not recommend using
this mod, as it's not worth it for the performance loss.

Hardware ray traced sun shadows, ambient occlusion, reflections and bounced light for The Long Drive's beta branch, on
Direct3D 12 (DXR). A BepInEx 5 plugin plus a native D3D12 plugin of its own; no Unity editor is involved: the shaders
are HLSL compiled with Microsoft's DXC.

## What it does

Every frame the meshes near the camera (within 300 m, and the terrain tiles and roads within 600 m) go into ray tracing
acceleration structures built from the game's own GPU vertex and index buffers, and rays are traced from the points the
depth buffer shows:

- **Sun shadows** (mode 1, the default): one shadow ray per pixel toward a point on the sun's disk (`SunSize`, 0.6
  degrees: sharp where a shadow meets its caster, softer further from it), averaged over the pixel's neighbours. They go
  into the game's own sun shadow mask (Unity's screen-space shadow mask, right after the game draws it), so the game's
  lighting takes the sun away there and keeps its ambient light. The meshes the rays see stop casting into the game's
  shadow map; it keeps only what the rays can't see: cut-out leaves, glass, animated characters. Beyond the game's shadow
  distance (100 m) the shadows are the rays' alone. Mode 2 joins the ray traced shadows with all of the game's own, mode
  0 leaves the game's alone, mode 3 shows the mask.
- **Ambient occlusion** (Ctrl+F6): four rays per pixel, 2.5 m long, darken corners, the ground under cars and where
  things touch, within 80 m. It stands in for the game's own, which has no shaders on Direct3D 12.
- **Reflections** (Ctrl+F5): a primary ray finds each pixel's surface (glass and water in front of it too), its material
  and its exact normal (each mesh's normals are read once from the game's own buffers). On metal, car paint, chrome and
  glass from smoothness 0.55 (fully from 0.7), and on other materials from 0.8, a mirror ray finds what the surface
  reflects: its colour from the frame where that point is on screen, else from the material's tint lit by sun and sky,
  else the sky. The game's car paint and chrome are 0.6, its default 0.5, the bus's paint 1. `ReflectionStrength` (0.8)
  and `Shine` (smoothness times this) tune it. The game gives some stone 0.7: as a mirror, a stone well looked like
  glazed chrome, hence the higher start for non-metals.
- **Bounced light** (global illumination, Ctrl+F4): two rays per pixel within 60 m look for what is within 8 m of a
  surface; what they find (its colour on screen, or its material lit by sun and sky) lights the surface, nearer things
  more. A crate's face in shade beside sunlit grass and walls picks up their warm light; open ground gets none (the
  sky's light is the game's own ambient). The frame has no albedo, so a pixel's colour over the light that reached it
  (the game's ambient for its facing, plus the sun where the shadow rays reach it) stands in. `BouncedLightStrength`
  (1.5) tunes it; mode 7 shows the bounced light alone.

Only what the player's camera draws is in the ray traced scene: meshes on layers it leaves out (the house's ramp has an
invisible collider mesh over its planks) would cast shadows the player cannot see the cause of.

**Ctrl+F7** switches between the modes in game (a label at the bottom of the screen names it): 0 the game's own shadows,
1 ray traced, 2 ray traced and all the game's own, 3 the shadow mask, 4 to 7 the occlusion, the reflections, what the
primary ray found and the bounced light alone.

## Settings

In RevaMP's mods list, RayTrace's **Settings** button opens its settings page (the mod's own BepInEx config,
`BepInEx/config/tldrevamp.raytrace.cfg`). Every change applies at once:

- **Effects**: the mode (the shadows, or one effect alone for testing), and ambient occlusion, reflections and bounced
  light on or off.
- **Keys**: Ctrl+F7 next mode, Ctrl+F6 ambient occlusion, Ctrl+F5 reflections, Ctrl+F4 bounced light.
- **Tuning**: reflection strength, `Shine` (each material's smoothness times this), bounced light strength, the sun's
  size for the soft shadows, and how dark the shadows are when the game's own shadows are switched off.

At start the mod says on screen (for 20 s) when it can't run: the game not on Direct3D 12, a graphics card without ray
tracing, or Unity's graphics jobs still on.

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
and throws every frame without drawing anything; this mod turns it off there. (Switching it to the other mode drew dark
outlines of the player's own head and rectangles around trees and rocks.) The game looks somewhat different on Direct3D
12 even without this mod: less haze, darker grass.

In mode 1 the meshes the rays see have their shadow casting turned off (`ShadowCastingMode.Off`) while they are in the
ray traced scene; they get it back when they leave it, in the other modes and at night. Unity draws the sun's
screen-space shadow mask only when something casts into its shadow map, so a millimetre cube that casts shadows only
stays 1.5 m in front of the camera. The sun's command buffer is on the sun only while the player's camera renders (from
its culling, else it does not run that frame), and off while another camera (a mirror) renders.

## Cost

With everything on (shadows into the game's mask, occlusion, reflections, bounced light) the GPU's share at the house,
1280x720, was about 1.1 ms (structures 0.36, rays 0.67 and composite 0.05, each the average of the frame's two passes).
The rays' cost grows with the pixels: at 3840x2160 the rays of the version before took 7.9 ms. The game no longer draws
the traced meshes into its own shadow map in mode 1, which saves some of that. The numbers below are from the first
version, shadows alone.

Measured standing still in the test copy (1280x720, graphics jobs off, uncapped; `RayTrace.Bench` through the debug
bridge): about 4 ms a frame (31.6 to 28.0 fps in one pair of 10 s windows, 34.5 to 38.3 ms in the next). Of that, the GPU
work is 0.6 ms (structures 0.38, rays 0.18, composite 0.03, from timestamp queries); the rest is the mod's CPU side
(1.8 ms: the transforms of about 1700 meshes and the spread-out refresh of the list) and the three full-screen copies.
The first version cost 11.7 ms of CPU a frame: it asked Unity for every mesh's buffers every frame.

## Not done yet

- From eye level, a shadow on the ground 150 m away is a thin line: the far shadows show best from hills or behind tall
  objects. Hills beyond 600 m and the distant terrain are not in the ray traced scene.
- When the sun has no shadows of its own (the game's shadows off in its settings), modes 1 and 2 darken the frame by
  `Strength` beyond the game's shadow distance instead, as the first version did.
- The native plugin is written into `TheLongDrive_Data/Plugins/x86_64` by the mod; Unity loads it when the game starts,
  so a new version takes effect at the next start.

## Building

    mods/RayTrace/Native/build.sh [DXC folder]     # needs mingw-w64 and DXC's Linux release (default ~/.local/opt/dxc-1.9.2609)
    dotnet build -c Release -p:GameDir="<The Long Drive folder>"

Licence: GPL-3.0, like the rest of this repository.
