using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace MountAnything
{
    /// How it works: the beta already has a free attachment, the "glue" (Glue key, attachablescript.Attach(RaycastHit)),
    /// and a save record for it (save_attachable: what it's on - an item and which of its colliders, a POI building, or
    /// the world - and where). Only items whose prefab has an attachablescript can use it. This mod gives a held item
    /// that lacks one an attachablescript (parentAtAttach: the item becomes a rigid child of what it's mounted on and
    /// loses its own Rigidbody while mounted, the same as the game's glued items) at the moment it is mounted, and again
    /// wherever such an item is rebuilt from a record that says it is mounted:
    ///  - savedatascript.LoadStuff (loading a save, an item placed back near the player, RevaMP building a joiner's copy);
    ///  - RevaMP's live attach messages (see RevampLink).
    /// Taken off (picked up, broken off), the item gets its plain self back: the added component is removed again.
    internal static class Mounting
    {
        /// Components this mod added (as opposed to ones the item's prefab has).
        internal static readonly HashSet<attachablescript> Added = new HashSet<attachablescript>();
        internal static long Mounts, AddedOnLoad, AddedForRevamp, Removed;
        internal static string LastMount = "";
        internal static float HintUntil;
        private static int _keyFrame = -1;

        /// Set by the unattended test: acts as if the mount key was pressed this frame.
        internal static bool TestPress;
        /// Set by the unattended test: acts as if the game's Glue key went down this frame (for an item the game glues).
        internal static bool TestGlueDown;
        /// Set by the unattended test: the game window has no focus (its pause menu is up), offer and place anyway.
        internal static bool TestUnfocused;

        internal static attachablescript Ensure(tosaveitemscript ts)
        {
            if (ts == null) return null;
            if (ts.attachable != null) return ts.attachable;
            var at = ts.gameObject.AddComponent<attachablescript>();
            at.parentAtAttach = true;   // rigid: parented into what it's on, no joint
            at.tipus = new string[0];
            at.maintipus = new string[0];
            at.tosave = ts;
            at.T = ts.transform;
            at.RB = ts.GetComponent<Rigidbody>();
            ts.attachable = at;
            at.FStart();
            Added.Add(at);
            return at;
        }

        /// Called twice a second: a component this mod added to an item that is no longer mounted is removed, so the
        /// item is the game's plain item again (the game shows "toggle collision" on R for held glueable items, and R
        /// is also reload).
        internal static void Cleanup()
        {
            if (Added.Count == 0) return;
            List<attachablescript> gone = null;
            foreach (var at in Added)
            {
                if (at == null) { (gone ??= new List<attachablescript>()).Add(at); continue; }
                if (at.attached) continue;
                (gone ??= new List<attachablescript>()).Add(at);
                var ts = at.tosave;
                if (ts != null && ts.attachable == at) ts.attachable = null;
                Object.Destroy(at);
                Removed++;
            }
            if (gone != null) foreach (var at in gone) Added.Remove(at);
        }

        internal static bool IsMountable(pickupable p, out string why)
        {
            why = "";
            if (p == null) { why = "nothing held"; return false; }
            var ts = p.tosave;
            if (ts == null) { why = "not a saved item"; return false; }
            if (ts.P != p) { why = "held by a part of it"; return false; }
            if (ts.car != null) { why = "a vehicle"; return false; }
            if (ts.ragdollPos != null) { why = "a body"; return false; }
            var at = ts.attachable;
            if (at != null)
            {
                if (at.attached) { why = "already mounted"; return false; }
                if (at.onlyToSlot && !Plugin.AllowSlotParts.Value) { why = "slot-only part"; return false; }
            }
            return true;
        }

        /// The first solid thing the crosshair is on (not the held item, not the player), the way the game's glue looks.
        internal static bool FindHit(fpscontroller pl, out RaycastHit hit) => FindHit(pl, new Ray(pl.Cam.transform.position, pl.Cam.transform.forward), pl.FrayRange, out hit);

        internal static bool FindHit(fpscontroller pl, Ray ray, float range, out RaycastHit hit)
        {
            hit = default;
            var hits = Physics.RaycastAll(ray, range, pl.attachLayer);
            System.Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var h in hits)
            {
                if (h.collider == null || h.collider.isTrigger) continue;
                var t = h.collider.transform;
                if (pl.pickedUp != null && t.IsChildOf(pl.pickedUp.transform)) continue;
                if (t.IsChildOf(pl.transform)) continue;
                hit = h;
                return true;
            }
            return false;
        }

        /// Mounts the held item at the hit, as the game's glue does (drop, then attach), and returns its attachment.
        internal static attachablescript Mount(fpscontroller pl, RaycastHit hit)
        {
            var p = pl.pickedUp;
            var ts = p.tosave;
            var at = Ensure(ts);
            // the pose shown by the preview (worked out this frame for this hit), or the 0.1 way without placement
            bool placed = Plugin.Place.Value && Placement.Ready(p);
            Vector3 pos = Placement.Pos; Quaternion rot = Placement.Rot;
            pl.Drop();
            pl.BAttach = true;
            var t = ts.transform;
            if (placed) t.SetPositionAndRotation(pos, rot);
            else if (Plugin.AlignToSurface.Value) t.rotation = Quaternion.FromToRotation(t.up, hit.normal) * t.rotation;
            bool keep = at.tempDontMoveAtAttach;
            if (placed) at.tempDontMoveAtAttach = true;   // the game's glue would put the item's nearest point on the spot
            try
            {
                var col = hit.collider;
                if (col.GetComponentInParent<tosaveitemscript>() != null || col.GetComponentInParent<poiScript>() != null)
                    at.Attach(hit);   // the point rides the collider: a car's body, an item, a building
                else
                {
                    // the ground, a road, a scatter object: the game saves these as a world position (attach type 4) and
                    // loads them on a point of their own. The same now, so a terrain tile that unloads never takes the
                    // item with it. The floating-origin shift moves the point (NewPointShift).
                    at.attachedC = col;
                    at.Attach(at.NewPoint(null, hit.point + hit.normal * at.offsetback, Quaternion.identity));
                }
            }
            finally { at.tempDontMoveAtAttach = keep; }
            if (placed)
            {
                t.SetPositionAndRotation(pos, rot);
                var pt = at.point != null ? at.point : t.parent;
                Placement.LastPos = pt != null ? Quaternion.Inverse(pt.rotation) * (pos - pt.position) : pos;
                Placement.LastRot = pt != null ? Quaternion.Inverse(pt.rotation) * rot : rot;
            }
            Mounts++;
            RevampLink.SendPose(at);
            var to = at.AttachedToTosave(); var poi = at.AttachedToPOI();
            LastMount = ts.name + " (" + ts.idInSave + ") on " + (to != null ? to.name + " (" + to.idInSave + ")" : poi != null ? "building " + poi.name : "the world (" + hit.collider.name + ")") +
                        ", attached " + at.attached + (placed ? ", placed " + Placement.State() : "");
            Plugin.Log.LogInfo("mounted " + LastMount);
            return at;
        }

        /// An item the game glues by itself, rigidly, anywhere (not a slot part, not one on a joint): with placement on,
        /// the mod shows and places it too.
        private static bool GameGlueWeTake(pickupable p)
        {
            var at = p != null && p.tosave != null ? p.tosave.attachable : null;
            return Plugin.Place.Value && Plugin.PlaceGameGlue.Value && at != null && !Added.Contains(at) && at.parentAtAttach && !at.onlyToSlot && !at.attached;
        }

        private static bool _takeOver;

        [HarmonyPatch(typeof(fpscontroller), nameof(fpscontroller.RayStuff))]
        private static class RayStuffHook
        {
            /// Before the game's look-at work: an item the game would glue itself on its Glue key, whose placement is shown
            /// → the key is ours (the postfix mounts it at the shown pose).
            [HarmonyPrefix]
            private static void Prefix(fpscontroller __instance)
            {
                var inp = inputscript.i;
                bool test = TestGlueDown;
                TestGlueDown = false;
                if (!Plugin.Enabled.Value || inp == null || !(inp.glueDown || test) || __instance == null || __instance.pickedUp == null) return;
                if (!Placement.ShownFor(__instance.pickedUp) || !Placement.ForGameGlue || !GameGlueWeTake(__instance.pickedUp)) return;
                inp.glueDown = false;
                _takeOver = true;
            }

            /// After the game's own look-at work: holding an item that can be mounted here, looking at something solid →
            /// offer it (the game's own "[G]: Attach" line, or our hint for a key of our own), show the placement, and
            /// mount on the key.
            [HarmonyPostfix]
            private static void Postfix(fpscontroller __instance)
            {
                bool takeOver = _takeOver;
                _takeOver = false;
                var pl = __instance;
                if (!Plugin.Enabled.Value || pl == null || pl.pickedUp == null || _keyFrame == Time.frameCount) return;
                if (mainscript.s == null || mainscript.s.player != pl || (!mainscript.s.crsrLocked && !TestPress && !TestUnfocused)) return;   // the test: a game window without focus has its pause menu up
                var inp = inputscript.i;
                if (inp != null && inp.usingComputer) return;
                if (pl.BAttach || pl.BnowPickedUp || pl.pushgripping) return;
                bool gameGlue = pl.BCanAttachGlue;
                if (gameGlue && (pl.BCanAttach || !GameGlueWeTake(pl.pickedUp))) return;   // the game glues it itself (or into a slot)
                if (!IsMountable(pl.pickedUp, out _) || !FindHit(pl, out var hit)) return;
                bool gameKey = Plugin.MountKey.Value == KeyCode.None;
                if (gameKey) pl.BCanAttachGlue = true;   // the HUD shows "[G]: Attach"
                else HintUntil = Time.unscaledTime + 0.15f;
                if (Plugin.Place.Value) Placement.Update(pl, hit, gameGlue);
                bool pressed = TestPress || takeOver || (gameKey ? !gameGlue && inp != null && inp.glueDown : Input.GetKeyDown(Plugin.MountKey.Value));
                if (!pressed) return;
                TestPress = false;
                _keyFrame = Time.frameCount;
                Mount(pl, hit);
            }
        }

        [HarmonyPatch(typeof(savedatascript), nameof(savedatascript.LoadStuff), new[] { typeof(savedata), typeof(uint), typeof(bool) })]
        private static class LoadHook
        {
            /// An item rebuilt from records that say it is mounted, but whose prefab has no attachablescript: give it one
            /// before the game reads those records (it loads the attachment only for items that have one).
            [HarmonyPrefix]
            private static void Prefix(savedatascript __instance, savedata _data, uint _key)
            {
                if (_data == null || _data.itemData == null || _data.itemData.attachable == null) return;
                if (!__instance.items.TryGetValue(_key, out var ts) || ts == null || ts.attachable != null || ts.loaded) return;
                foreach (var a in _data.itemData.attachable)
                    if (a != null && a.id == _key && a.attachType != 0)
                    {
                        Ensure(ts);
                        AddedOnLoad++;
                        return;
                    }
            }
        }

        [HarmonyPatch(typeof(attachablescript), nameof(attachablescript.NewPoint), new[] { typeof(Transform), typeof(Vector3), typeof(Quaternion) })]
        private static class NewPointShift
        {
            /// A mount point standing on its own (mounted on the ground, or loaded so) must move with the game's
            /// floating-origin shift: it gets the game's own visszarako, as RevaMP gives the game's own free points.
            [HarmonyPostfix]
            private static void Postfix(attachablescript __instance, Transform __result)
            {
                if (__result == null || !Added.Contains(__instance) || __result.parent != null) return;
                if (__result.GetComponent<visszarako>() != null) return;
                __result.gameObject.AddComponent<visszarako>().FStart(_dontlookUnderMap: true, _importantUnderMapLook: false, _moveWhenParented: false);
            }
        }
    }
}
