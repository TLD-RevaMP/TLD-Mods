using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace RayTrace
{
    /// The game ships for Direct3D 11. On Direct3D 12 its ambient occlusion (post-processing's Multi-Scale Volumetric
    /// Obscurance) has no compute shaders: it throws every frame while the post-processing builds its command buffers
    /// ("Kernel 'MultiScaleVODownsample1' not found") and draws nothing. It is turned off there. (Switching it to the
    /// other mode, Scalable Ambient Obscurance, drew dark outlines of the player's own head and dark rectangles around
    /// trees and rocks: that mode works from the depth-normals texture, which holds both. 2026-11-27, a player's report.)
    internal static class D3D12Fixes
    {
        public static bool On = true;
        public static int Switched;
        private static float _next;

        public static void Tick()
        {
            if (!On || SystemInfo.graphicsDeviceType != GraphicsDeviceType.Direct3D12 || Time.unscaledTime < _next) return;
            _next = Time.unscaledTime + 2f;
            foreach (var volume in Object.FindObjectsOfType<PostProcessVolume>())
            {
                var profile = volume.sharedProfile;
                if (profile == null || !profile.TryGetSettings<AmbientOcclusion>(out var ao)) continue;
                if (ao.mode.value != AmbientOcclusionMode.MultiScaleVolumetricObscurance || !ao.enabled.value) continue;
                ao.enabled.Override(false);
                Switched++;
                Plugin.Log("ambient occlusion of " + volume.name + " turned off (it has no shaders on Direct3D 12)");
            }
        }
    }
}
