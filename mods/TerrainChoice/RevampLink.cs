using System;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace TerrainChoice
{
    /// RevaMP (TLD Revamp, the beta multiplayer mod the user plays with) has its own join: the host's Welcome carries
    /// seed, start car, map type and game mode, and the joiner starts a new game with them. The terrain type rides at
    /// the end of that Welcome, after RevaMP's game-mode block (GameMode.Write / GameMode.Read), behind a 2-byte tag,
    /// so RevaMP itself never misreads it (0.68.1 reads nothing after the game mode).
    /// The mod also registers with RevaMP's multiplayer API, so RevaMP refuses a join when host and joiner don't both
    /// have the same TerrainChoice ("install TerrainChoice 0.1.0" / "remove terrainchoice").
    internal static class RevampLink
    {
        private const byte Tag0 = 0xC7, Tag1 = (byte)'T';

        public static bool Active;
        private static MethodInfo _writerU8;
        private static FieldInfo _readerBuf, _readerPos, _readerEnd, _inHostWorld;
        private static TerrainMode? _pending;
        private static float _pendingAt;

        public static void Init(Harmony harmony)
        {
            try
            {
                var gameMode = AccessTools.TypeByName("TLDRevamp.Net.GameMode");
                var writer = AccessTools.TypeByName("TLDRevamp.Net.NetWriter");
                var reader = AccessTools.TypeByName("TLDRevamp.Net.NetReader");
                var safety = AccessTools.TypeByName("TLDRevamp.TestSafety");
                if (gameMode == null || writer == null || reader == null) return;   // no RevaMP
                var write = AccessTools.Method(gameMode, "Write", new[] { writer });
                var read = AccessTools.Method(gameMode, "Read", new[] { reader });
                _writerU8 = AccessTools.Method(writer, "U8", new[] { typeof(byte) });
                _readerBuf = AccessTools.Field(reader, "Buf");
                _readerPos = AccessTools.Field(reader, "Pos");
                _readerEnd = AccessTools.Field(reader, "End");
                _inHostWorld = safety != null ? AccessTools.Field(safety, "InHostWorld") : null;
                if (write == null || read == null || _writerU8 == null || _readerBuf == null || _readerPos == null || _readerEnd == null)
                {
                    Plugin.Log.LogWarning("RevaMP found, but not the Welcome code this version knows: joiners won't get the host's terrain through RevaMP.");
                    return;
                }
                harmony.Patch(write, postfix: new HarmonyMethod(typeof(RevampLink), nameof(AfterWrite)));
                harmony.Patch(read, postfix: new HarmonyMethod(typeof(RevampLink), nameof(AfterRead)));
                Active = true;
                RegisterWithRevamp();
            }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP link: " + e.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterWithRevamp()
        {
            try { TLDRevamp.Multiplayer.MpApi.RegisterMod("terrainchoice", "TerrainChoice", Plugin.Version, typeof(RevampLink).Assembly); }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP mod registration: " + e.Message); }
        }

        /// Host: the Welcome to a joiner. The terrain of the world the host is in.
        private static void AfterWrite(object[] __args)
        {
            try
            {
                var w = __args[0];
                _writerU8.Invoke(w, new object[] { Tag0 });
                _writerU8.Invoke(w, new object[] { Tag1 });
                _writerU8.Invoke(w, new object[] { (byte)WorldPatches.Current });
            }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP Welcome (host): " + e.Message); }
        }

        /// Joiner: right after RevaMP read the host's game mode, before its world loads.
        private static void AfterRead(object[] __args)
        {
            try
            {
                var r = __args[0];
                var buf = (byte[])_readerBuf.GetValue(r);
                int pos = (int)_readerPos.GetValue(r), end = (int)_readerEnd.GetValue(r);
                TerrainMode mode = TerrainMode.Crossroads;   // a host without this mod plays the normal terrain
                if (end - pos >= 3 && buf[pos] == Tag0 && buf[pos + 1] == Tag1)
                {
                    if (MapTag.Valid(buf[pos + 2])) mode = (TerrainMode)buf[pos + 2];
                    else Plugin.Log.LogWarning("The host uses a terrain type this version doesn't know (" + buf[pos + 2] + "): update TerrainChoice.");
                    _readerPos.SetValue(r, pos + 3);
                }
                _pending = mode;
                _pendingAt = Time.realtimeSinceStartup;
                Plugin.Log.LogInfo("RevaMP host's terrain: " + MapTag.Label(mode));
            }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP Welcome (joiner): " + e.Message); }
        }

        /// The world now loading is the RevaMP host's: its terrain, once.
        public static bool TakeHostMode(out TerrainMode mode)
        {
            mode = TerrainMode.Crossroads;
            if (_pending == null) return false;
            var p = _pending.Value;
            _pending = null;
            bool inHostWorld = _inHostWorld == null || (bool)_inHostWorld.GetValue(null);
            if (!inHostWorld || Time.realtimeSinceStartup - _pendingAt > 300f) return false;   // a join that didn't load
            mode = p;
            return true;
        }
    }
}
