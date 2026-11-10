using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.CompilerServices;
using HarmonyLib;
using UnityEngine;

namespace MountAnything
{
    /// RevaMP (TLD Revamp) already shares the game's glue: the machine that mounts an item sends its save_attachable
    /// record (Entities.AttachHook → AttachSync), and every other machine replays it through the game's loader
    /// (Entities.ApplyAttachSync; a non-owner's mount goes to the owner first, Entities.ApplyAttachReq). Joiners and
    /// copies placed later get the item's save records, loaded by the game's LoadStuff (Mounting.LoadHook covers that).
    /// The one gap: the replay skips an item whose copy has no attachablescript, and items mounted by this mod only get
    /// one when they are mounted. So right before RevaMP applies an attach message, the item it names gets one here.
    ///
    /// The mod also registers with RevaMP's multiplayer API, so RevaMP refuses a join when host and joiner don't both
    /// have the same MountAnything (a player without it couldn't show mounted items).
    internal static class RevampLink
    {
        public static bool Active, Registered;
        public static long Prepared;
        private static FieldInfo _byNet, _root, _rootId, _buf, _pos, _end;

        public static void Init(Harmony harmony)
        {
            try
            {
                var ents = AccessTools.TypeByName("TLDRevamp.Net.Entities");
                var reader = AccessTools.TypeByName("TLDRevamp.Net.NetReader");
                if (ents == null || reader == null) return;   // no RevaMP: single player only
                var ent = AccessTools.Inner(ents, "Ent");
                _byNet = AccessTools.Field(ents, "ByNet");
                _root = ent != null ? AccessTools.Field(ent, "Root") : null;
                _rootId = ent != null ? AccessTools.Field(ent, "RootId") : null;
                _buf = AccessTools.Field(reader, "Buf");
                _pos = AccessTools.Field(reader, "Pos");
                _end = AccessTools.Field(reader, "End");
                var sync = AccessTools.Method(ents, "ApplyAttachSync", new[] { reader });
                var req = AccessTools.Method(ents, "ApplyAttachReq", new[] { reader });
                if (_byNet == null || _root == null || _buf == null || _pos == null || _end == null || sync == null || req == null)
                {
                    Plugin.Log.LogWarning("RevaMP found, but not the attach code this version knows: items mounted during a session may show loose for other players until they rejoin.");
                }
                else
                {
                    var pre = new HarmonyMethod(typeof(RevampLink), nameof(BeforeApply));
                    harmony.Patch(sync, prefix: pre);
                    harmony.Patch(req, prefix: pre);
                    Active = true;
                }
                RegisterWithRevamp();
            }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP link: " + e.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void RegisterWithRevamp()
        {
            try
            {
                _mod = TLDRevamp.Multiplayer.MpApi.RegisterMod("mountanything", "MountAnything", Plugin.Version, typeof(RevampLink).Assembly);
                Registered = true;
                ListenForPoses();
            }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP mod registration: " + e.Message); }
        }

        /// Both messages start with the item's network id (u32, little endian). Read without moving the reader.
        private static void BeforeApply(object[] __args)
        {
            try
            {
                var r = __args[0];
                var buf = (byte[])_buf.GetValue(r);
                int pos = (int)_pos.GetValue(r), end = (int)_end.GetValue(r);
                if (buf == null || pos + 4 > end) return;
                uint net = (uint)(buf[pos] | buf[pos + 1] << 8 | buf[pos + 2] << 16 | buf[pos + 3] << 24);
                var byNet = (IDictionary)_byNet.GetValue(null);
                if (byNet == null || !byNet.Contains(net)) return;
                var e = byNet[net];
                var ts = (tosaveitemscript)_root.GetValue(e);
                if (ts == null && _rootId != null && savedatascript.s != null) savedatascript.s.items.TryGetValue((uint)_rootId.GetValue(e), out ts);
                if (ts == null || ts.attachable != null) return;
                Mounting.Ensure(ts);
                Mounting.AddedForRevamp++;
                Prepared++;
            }
            catch (Exception ex) { Plugin.Log.LogWarning("RevaMP attach: " + ex.Message); }
        }

        // ------------------------------------------------------------------ the item's pose on its mount point
        // RevaMP's attach message is the game's save record: the point the item is on, not the item's own pose there.
        // The game keeps the item where it is when it attaches it from a record (attachablescript.tempDontMoveAtAttach),
        // which is right when loading a save (the item's record has its pose) but not on another player's machine,
        // whose copy of the item is wherever its last smoothed pose left it (0.9 m off in the first two-game test).
        // The game's own multiplayer sends that pose with the record (syncScript.SendAttachable: lpos/lrot); here it goes
        // as a mod event, and every other machine puts its copy there once the attach has arrived.

        private static object _mod;   // TLDRevamp.Multiplayer.ModInfo (kept as object: no RevaMP, no type load)
        private struct PendingPose { public Vector3 Pos; public Quaternion Rot; public float Until; }
        private static readonly Dictionary<uint, PendingPose> _pending = new Dictionary<uint, PendingPose>();
        public static long PosesSent, PosesApplied, PosesExpired;

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ListenForPoses()
        {
            TLDRevamp.Multiplayer.MpApi.OnEvent((TLDRevamp.Multiplayer.ModInfo)_mod, "pose", e =>
            {
                if (e.From == TLDRevamp.Multiplayer.MpApi.MyPlayerId) return;
                var r = e.Data;
                uint net = r.UInt();
                var p = new Vector3(r.Float(), r.Float(), r.Float());
                var q = new Quaternion(r.Float(), r.Float(), r.Float(), r.Float());
                if (!r.IsValid || net == 0 || p.sqrMagnitude > 400f) return;
                _pending[net] = new PendingPose { Pos = p, Rot = q.normalized, Until = Time.realtimeSinceStartup + 15f };
                ApplyPending();
            });
        }

        /// The machine that mounted the item: its pose on the point, to everyone else.
        internal static void SendPose(attachablescript at)
        {
            if (!Registered || _mod == null || at == null || !at.attached || at.point == null) return;
            try { SendPoseNow(at); }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP pose: " + e.Message); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void SendPoseNow(attachablescript at)
        {
            if (!TLDRevamp.Multiplayer.MpApi.InSession) return;
            uint net = TLDRevamp.Multiplayer.MpApi.WorldObjectId(at.gameObject);
            if (net == 0) return;
            var pt = at.point; var t = at.transform;
            var lp = Quaternion.Inverse(pt.rotation) * (t.position - pt.position);
            var lq = Quaternion.Inverse(pt.rotation) * t.rotation;
            if (TLDRevamp.Multiplayer.MpApi.SendEvent((TLDRevamp.Multiplayer.ModInfo)_mod, "pose", w =>
                {
                    w.UInt(net); w.Float(lp.x); w.Float(lp.y); w.Float(lp.z); w.Float(lq.x); w.Float(lq.y); w.Float(lq.z); w.Float(lq.w);
                }, TLDRevamp.Multiplayer.EventTarget.Others)) PosesSent++;
        }

        /// Called every frame: poses whose item is now mounted here are applied; old ones are dropped.
        internal static void ApplyPending()
        {
            if (_pending.Count == 0) return;
            try { ApplyPendingNow(); }
            catch (Exception e) { Plugin.Log.LogWarning("RevaMP pose: " + e.Message); _pending.Clear(); }
        }

        [MethodImpl(MethodImplOptions.NoInlining)]
        private static void ApplyPendingNow()
        {
            List<uint> done = null;
            foreach (var kv in _pending)
            {
                var go = TLDRevamp.Multiplayer.MpApi.FindWorldObject(kv.Key);
                var ts = go != null ? go.GetComponent<tosaveitemscript>() : null;
                var at = ts != null ? ts.attachable : null;
                if (at != null && at.attached && at.point != null)
                {
                    var pt = at.point;
                    ts.transform.SetPositionAndRotation(pt.position + pt.rotation * kv.Value.Pos, pt.rotation * kv.Value.Rot);
                    PosesApplied++;
                    (done ??= new List<uint>()).Add(kv.Key);
                }
                else if (Time.realtimeSinceStartup > kv.Value.Until) { PosesExpired++; (done ??= new List<uint>()).Add(kv.Key); }
            }
            if (done != null) foreach (var k in done) _pending.Remove(k);
        }
    }
}
