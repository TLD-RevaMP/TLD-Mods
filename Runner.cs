using UnityEngine;

namespace MountAnything
{
    /// Per-frame work on the mod's own object: removes the mod's component from items that came off, draws the
    /// placement preview, shows the hint for a key of the mod's own and the placement keys, and runs the unattended
    /// test (-madiag).
    internal class Runner : MonoBehaviour
    {
        private float _nextCleanup;
        private GUIStyle _style, _help;

        private void Update()
        {
            if (Time.unscaledTime >= _nextCleanup)
            {
                _nextCleanup = Time.unscaledTime + 0.5f;
                try { Mounting.Cleanup(); }
                catch (System.Exception e) { Plugin.Log.LogWarning("cleanup: " + e.Message); }
            }
            RevampLink.ApplyPending();
            Diag.Tick();
        }

        private void LateUpdate()
        {
            try { Placement.Draw(); }
            catch (System.Exception e) { Plugin.Log.LogWarning("preview: " + e.Message); }
        }

        private void OnGUI()
        {
            float now = Time.unscaledTime;
            if (now <= Mounting.HintUntil && Plugin.MountKey.Value != KeyCode.None)
            {
                _style ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontSize = 16 };
                GUI.Label(new Rect(Screen.width / 2f - 150f, Screen.height * 0.62f, 300f, 26f), "[" + Plugin.MountKey.Value + "]: Mount here", _style);
            }
            if (now <= Placement.ActiveUntil && Plugin.Place.Value && Plugin.ShowHelp.Value)
            {
                _help ??= new GUIStyle(GUI.skin.label) { alignment = TextAnchor.UpperCenter, fontSize = 14, wordWrap = true };
                var r = new Rect(Screen.width / 2f - 360f, Screen.height * 0.82f, 720f, 48f);
                var text = Placement.Help();
                // a dark copy underneath, so it reads on bright ground too
                var c = GUI.color;
                GUI.color = new Color(0f, 0f, 0f, 0.8f);
                GUI.Label(new Rect(r.x + 1f, r.y + 1f, r.width, r.height), text, _help);
                GUI.color = c;
                GUI.Label(r, text, _help);
            }
        }
    }
}
