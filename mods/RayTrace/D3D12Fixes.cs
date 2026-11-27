using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.PostProcessing;

namespace RayTrace
{
    /// The game ships for Direct3D 11. On Direct3D 12 its ambient occlusion (post-processing's Multi-Scale Volumetric
    /// Obscurance) has no compute shaders: it throws every frame while the post-processing builds its command buffers
    /// ("Kernel 'MultiScaleVODownsample1' not found"), and loading a world crashed one time in three without this mod
    /// (2026-11-27). The other ambient occlusion mode (Scalable Ambient Obscurance) needs no compute shaders: every
    /// profile that uses the first is switched to the second, on Direct3D 12 only.
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
                if (ao.mode.value != AmbientOcclusionMode.MultiScaleVolumetricObscurance) continue;
                ao.mode.Override(AmbientOcclusionMode.ScalableAmbientObscurance);
                Switched++;
                Plugin.Log("ambient occlusion of " + volume.name + " switched to Scalable Ambient Obscurance (Direct3D 12)");
            }
        }
    }
}
