using TLDRevamp.Multiplayer;
using UnityEngine;

namespace HeadUnit
{
    /// What the head unit shows off its screen: the big picture for passengers (BIG on the screen), and the short
    /// messages its screens show along their top. Questions and typing are on the unit's own screen.
    public static class Panel
    {
        public static bool Big;
        private static string _flash; private static float _flashUntil;

        /// A short message, shown on the screens of the units nearby for a few seconds.
        public static void Flash(string s) { _flash = s; _flashUntil = Time.realtimeSinceStartup + 4f; }
        public static string Flashing => Time.realtimeSinceStartup < _flashUntil ? _flash : null;

        public static void Draw()
        {
            var u = Controls.Mine;
            if (Big && u != null && u.VideoTexture != null)
            {
                float bw = Mathf.Min(Screen.width * 0.45f, 800f), bh = bw * 9f / 16f;
                GUI.DrawTexture(new Rect(Screen.width - bw - 16f, 16f, bw, bh), u.VideoTexture, ScaleMode.ScaleToFit);
            }
        }

    }
}
