using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using HarmonyLib;
using UnityEngine;

namespace MountAnything
{
    /// Two-game RevaMP check on one PC (test copies only): one game runs -madiag mphost (continues its last save,
    /// hosts on UDP 27070), a second copy runs -madiag mpjoin (joins 127.0.0.1:27070 from its menu). They talk through
    /// files in a folder both reach: TLD_MP_SHARED, else "mp-shared" next to the game folder (beside both copies):
    ///  1. the joiner's view of every mounted item (item, what it's on, pose there) against the host's;
    ///  2. the host mounts a held item on the car's other side, then takes the item on the ground off: the joiner sees both;
    ///  3. the joiner mounts an item on the car's roof: the host sees it.
    internal static partial class Diag
    {
        private static readonly string Shared = Environment.GetEnvironmentVariable("TLD_MP_SHARED") is string s && s.Length > 0
            ? s : Path.Combine(Path.GetDirectoryName(BepInEx.Paths.GameRootPath), "mp-shared");
        private static string SharedFile(string name) => Path.Combine(Shared, name);

        private static string Mp(string method, params object[] args)
        {
            var t = AccessTools.TypeByName("TLDRevamp.Net.Mp");
            var m = t != null ? AccessTools.Method(t, method) : null;
            if (m == null) return "(no RevaMP " + method + ")";
            var ps = m.GetParameters();
            var call = new object[ps.Length];
            for (int i = 0; i < ps.Length; i++) call[i] = i < args.Length ? Convert.ChangeType(args[i], ps[i].ParameterType, CultureInfo.InvariantCulture) : ps[i].DefaultValue;
            return m.Invoke(null, call) as string ?? "";
        }

        private static string MpStatus() { try { return Mp("Status"); } catch { return ""; } }

        private static void Event(string who, string line)
        {
            Directory.CreateDirectory(Shared);
            File.AppendAllText(SharedFile(who + "-events.txt"), line + Environment.NewLine);
            Write("event " + line);
        }

        private static string FindEvent(string who, string prefix)
        {
            var f = SharedFile(who + "-events.txt");
            if (!File.Exists(f)) return null;
            return File.ReadAllLines(f).FirstOrDefault(l => l.StartsWith(prefix));
        }

        /// One line per mounted item, comparable between machines (save ids differ): item, kind, parent, pose.
        private static string ViewLine(tosaveitemscript t, Pose p) =>
            Norm(t.name) + " " + p.Kind + " " + Norm(p.ParentName) + " " + V(p.Pos) + " " +
            p.Rot.x.ToString("R", Ic) + " " + p.Rot.y.ToString("R", Ic) + " " + p.Rot.z.ToString("R", Ic) + " " + p.Rot.w.ToString("R", Ic);

        private static string Norm(string n) => n.Replace("(Clone)", "").Trim().Replace(' ', '_');

        private static List<string> View() => Poses().Select(kv => ViewLine(savedatascript.s.items[kv.Key], kv.Value)).OrderBy(x => x).ToList();

        private class VItem { public string Name, Kind, Parent; public Vector3d Pos; public Quaternion Rot; }
        private static VItem ParseView(string l)
        {
            var f = l.Split(' ');
            return new VItem { Name = f[0], Kind = f[1], Parent = f[2], Pos = new Vector3d(double.Parse(f[3], Ic), double.Parse(f[4], Ic), double.Parse(f[5], Ic)),
                               Rot = new Quaternion(float.Parse(f[6], Ic), float.Parse(f[7], Ic), float.Parse(f[8], Ic), float.Parse(f[9], Ic)) };
        }

        /// Does `mine` (this machine's view) have the item of `line` mounted on the same kind of thing at the same pose?
        private static bool Has(List<string> mine, string line, out string how)
        {
            var w = ParseView(line);
            var best = mine.Select(ParseView).Where(m => m.Name == w.Name && m.Kind == w.Kind && m.Parent == w.Parent)
                           .OrderBy(m => (m.Pos - w.Pos).magnitude).FirstOrDefault();
            if (best == null) { how = "missing"; return false; }
            double dp = (best.Pos - w.Pos).magnitude; float da = Quaternion.Angle(best.Rot, w.Rot);
            how = "off by " + (dp * 1000).ToString("F1") + " mm, " + da.ToString("F2") + " deg";
            return dp < 0.05 && da < 2f;
        }

        private static int _wait;
        private static string _liveName, _offName;

        private static tosaveitemscript CarWithMounts()
        {
            foreach (var kv in Poses())
            {
                if (kv.Value.Kind != "item") continue;
                var t = savedatascript.s.items[kv.Key];
                // on the car's body, or on one of its parts (a door is an item of its own)
                for (var to = t.attachable.AttachedToTosave(); to != null; to = to.transform.parent != null ? to.transform.parent.GetComponentInParent<tosaveitemscript>() : null)
                    if (to.car != null) return to;
            }
            return null;
        }


        // ------------------------------------------------------------------ host

        private static float HostStep()
        {
            var pl = Pl;
            switch (_step)
            {
                case 0:
                    if (Directory.Exists(Shared)) foreach (var f in Directory.GetFiles(Shared)) File.Delete(f);
                    Directory.CreateDirectory(Shared);
                    Write("hosting: " + Mp("Host", 27070, ""));
                    Event("host", "hosting");
                    _step = 1; _wait = 0;
                    return 1f;
                case 1:   // a player joins
                {
                    var s = MpStatus();
                    if (!s.Contains("\"remotePlayers\":0") && s.Contains("\"remotePlayers\":")) { Write("a player joined: " + s.Substring(0, Math.Min(200, s.Length))); _step = 2; return 45f; }
                    if (++_wait > 300) { Write("nobody joined in 5 minutes"); return -1f; }
                    return 1f;
                }
                case 2:
                {
                    var v = View();
                    File.WriteAllLines(SharedFile("host-view.txt"), v.ToArray());
                    Write("host view: " + v.Count + " mounted items written");
                    Event("host", "view " + Time.realtimeSinceStartup.ToString("F0", Ic));
                    _car = CarWithMounts();
                    if (_car == null) { Write("no car with mounted items here"); return -1f; }
                    var item = LooseItems(pl, 120f).FirstOrDefault(t => t.attachable == null);
                    if (item == null) { Write("no loose item to mount"); return -1f; }
                    _liveName = item.name;
                    if (pl.pickedUp != null) pl.Drop();
                    Hold(item);
                    _plans.Clear(); _plans.Add(new Plan { Name = "car's side, further back", Item = item, Target = () => SideOfCar(1f, -1.3f) ?? SideOfCar(1f, 1.3f) ?? SideOfCar(-1f) ?? RoofOfCar(), From = () => _car.transform.position + _car.transform.right * 10f });
                    _step = 3;
                    return 1f;
                }
                case 3:
                    if (_plans[0].Target() == null) { Check(false, "host: no spot on the car to aim at"); _step = 6; return 0.5f; }
                    StandAndLook(_plans[0].Target().Value, 1.4f, _plans[0].From());
                    // flat, turned 45 degrees, tipped 15 away, 2 cm out: the joiner must see this pose
                    PressPlacement(new[] { Placement.Act.Reset, Placement.Act.TurnRight, Placement.Act.TurnRight, Placement.Act.TurnRight, Placement.Act.TiltAway, Placement.Act.Out, Placement.Act.Out }, false);
                    _step = 4; return 0.6f;
                case 4:
                    StandAndLook(_plans[0].Target().Value, 1.4f, _plans[0].From());
                    Mounting.IsMountable(pl.pickedUp, out var whyNot);
                    Write("host: holding " + (pl.pickedUp != null ? pl.pickedUp.name : "nothing") + ", looking at " + DescribeLook(pl) + "; mountable: " + (whyNot.Length == 0 ? "yes" : whyNot) + ", game glue offered " + pl.BCanAttachGlue + ", BAttach " + pl.BAttach + ", BnowPickedUp " + pl.BnowPickedUp + ", crsrLocked " + mainscript.s.crsrLocked);
                    _mountsBefore = (int)Mounting.Mounts; Mounting.TestPress = true; _step = 5; return 0.6f;
                case 5:
                {
                    Mounting.TestPress = false;
                    var it = _plans[0].Item;
                    bool ok = Mounting.Mounts > _mountsBefore && it.attachable != null && it.attachable.attached;
                    Check(ok, "host mounts " + it.name + " on the car's other side: " + Mounting.LastMount);
                    if (pl.pickedUp != null) pl.Drop();
                    if (ok && Poses().TryGetValue(it.idInSave, out var p)) Event("host", "live " + ViewLine(it, p));
                    _step = 6;
                    return 10f;
                }
                case 6:   // take the item on the ground off
                {
                    var g = Poses().Where(kv => kv.Value.Kind == "world").Select(kv => savedatascript.s.items[kv.Key]).FirstOrDefault(t => Mounting.Added.Contains(t.attachable));
                    if (g == null) { Write("nothing mounted on the ground to take off"); _step = 8; return 0.5f; }
                    _offName = g.name;
                    var line = ViewLine(g, Poses()[g.idInSave]);
                    if (pl.pickedUp != null) pl.Drop();
                    StandAndLook(g.transform.position, 1.2f);
                    pl.Pickup(g.P);
                    _step = 7;
                    _offLine = line;
                    return 1.5f;
                }
                case 7:
                {
                    var g = savedatascript.s.items.Values.FirstOrDefault(t => t != null && t.P != null && t.P == pl.pickedUp);
                    Check(g != null && (g.attachable == null || !g.attachable.attached), "host took " + _offName + " off the ground (held: " + (pl.pickedUp != null ? pl.pickedUp.name : "nothing") + ")");
                    pl.Drop();
                    Event("host", "off " + _offLine);
                    _step = 8; _wait = 0;
                    return 1f;
                }
                case 8:   // the joiner mounts one: do we see it?
                {
                    var e = FindEvent("joiner", "live ");
                    if (e == null) { if (++_wait > 240) { Write("the joiner never mounted"); _step = 10; } return 1f; }
                    _step = 9;
                    return 8f;
                }
                case 9:
                {
                    var e = FindEvent("joiner", "live ").Substring(5);
                    bool ok = Has(View(), e, out var how);
                    Check(ok, "host sees the joiner's mount " + e.Split(' ')[0] + " on " + e.Split(' ')[2] + " (" + how + ")");
                    Event("host", "sawjoiner " + (ok ? "PASS" : "FAIL"));
                    _step = 10; _wait = 0;
                    return 1f;
                }
                case 10:
                    if (FindEvent("joiner", "done") == null && ++_wait < 120) return 1f;
                    Write("RevaMP: " + MpStatus().Substring(0, Math.Min(300, MpStatus().Length)));
                    Write("attach stats: " + AttachStats() + "; poses sent " + RevampLink.PosesSent + ", applied " + RevampLink.PosesApplied + ", expired " + RevampLink.PosesExpired);
                    Mp("Stop");
                    return -1f;
                default:
                    return -1f;
            }
        }

        private static string _offLine;

        private static string AttachStats()
        {
            try { var t = AccessTools.TypeByName("TLDRevamp.Net.Entities"); return AccessTools.Method(t, "AttachStats").Invoke(null, null) as string; }
            catch (Exception e) { return e.Message; }
        }

        // ------------------------------------------------------------------ joiner

        private static float JoinStep()
        {
            var pl = Pl;
            switch (_step)
            {
                case 0:   // the host's view
                    if (FindEvent("host", "view") == null) { if (++_wait > 300) { Write("the host never wrote its view"); return -1f; } return 1f; }
                    _step = 1;
                    return 5f;
                case 1:
                {
                    var mine = View();
                    var host = File.ReadAllLines(SharedFile("host-view.txt")).Where(l => l.Trim().Length > 0).ToList();
                    Write("joiner view: " + mine.Count + " mounted items, host's: " + host.Count);
                    foreach (var l in host)
                    {
                        bool ok = Has(mine, l, out var how);
                        Check(ok, "joiner sees " + l.Split(' ')[0] + " on " + l.Split(' ')[1] + " " + l.Split(' ')[2] + " (" + how + ")");
                    }
                    _step = 2; _wait = 0;
                    return 1f;
                }
                case 2:   // the host's live mount
                {
                    var e = FindEvent("host", "live ");
                    if (e == null) { if (++_wait > 120) { Check(false, "the host never mounted live"); _step = 4; } return 1f; }
                    _step = 3;
                    return 6f;
                }
                case 3:
                {
                    var e = FindEvent("host", "live ").Substring(5);
                    Check(Has(View(), e, out var how), "joiner sees the host's live mount " + e.Split(' ')[0] + " on " + e.Split(' ')[2] + " (" + how + ")");
                    _step = 4; _wait = 0;
                    return 1f;
                }
                case 4:   // the host's take-off
                {
                    var e = FindEvent("host", "off ");
                    if (e == null) { if (++_wait > 60) { Write("the host took nothing off"); _step = 6; } return 1f; }
                    _step = 5;
                    return 6f;
                }
                case 5:
                {
                    var e = FindEvent("host", "off ").Substring(4);
                    Check(!Has(View(), e, out var how), "joiner sees " + e.Split(' ')[0] + " taken off the ground (" + how + " = no longer mounted there)");
                    _step = 6;
                    return 1f;
                }
                case 6:   // the joiner mounts one on the car's roof
                {
                    _car = CarWithMounts();
                    if (_car == null) { Write("joiner: no car with mounted items"); _step = 20; return 1f; }
                    StandAndLook(_car.transform.position, 4f);
                    _step = 7;
                    return 1f;
                }
                case 7:
                {
                    var item = LooseItems(pl, 150f).FirstOrDefault(t => t.attachable == null);
                    if (item == null) { Write("joiner: no loose item"); _step = 20; return 1f; }
                    _plans.Clear(); _plans.Add(new Plan { Name = "car roof", Item = item, Target = () => RoofOfCar(), From = () => _car.transform.position + _car.transform.right * 10f });
                    if (pl.pickedUp != null) pl.Drop();
                    Hold(item);
                    Write("joiner holds " + item.name);
                    _step = 8;
                    return 1.5f;
                }
                case 8:
                    StandAndLook(_plans[0].Target().Value, 1.4f, _plans[0].From());
                    // as held, tipped 30 to the left, turned 15: the host must see this pose
                    PressPlacement(new[] { Placement.Act.TiltLeft, Placement.Act.TiltLeft, Placement.Act.TurnLeft }, false);
                    _step = 9; return 0.6f;
                case 9:
                    StandAndLook(_plans[0].Target().Value, 1.4f, _plans[0].From());
                    Write("joiner: holding " + (pl.pickedUp != null ? pl.pickedUp.name : "nothing") + ", looking at " + DescribeLook(pl));
                    _mountsBefore = (int)Mounting.Mounts; Mounting.TestPress = true; _step = 10; return 0.6f;
                case 10:
                {
                    Mounting.TestPress = false;
                    var it = _plans[0].Item;
                    bool ok = Mounting.Mounts > _mountsBefore && it.attachable != null && it.attachable.attached;
                    Check(ok, "joiner mounts " + it.name + " on the car's roof: " + Mounting.LastMount);
                    if (pl.pickedUp != null) pl.Drop();
                    if (ok && Poses().TryGetValue(it.idInSave, out var p)) Event("joiner", "live " + ViewLine(it, p));
                    _step = 11; _wait = 0;
                    return 3f;
                }
                case 11:
                {
                    var e = FindEvent("host", "sawjoiner");
                    if (e == null && ++_wait < 40) return 1f;
                    Write("host's answer: " + (e ?? "none"));
                    var it = _plans[0].Item;
                    Check(it.attachable != null && it.attachable.attached, "joiner's mount still on after the host's reply (not undone by the owner)");
                    _step = 20;
                    return 1f;
                }
                case 20:
                    Write("RevaMP: " + MpStatus().Substring(0, Math.Min(300, MpStatus().Length)));
                    Write("attach stats: " + AttachStats() + "; RevaMP attaches prepared by this mod " + RevampLink.Prepared + "; poses sent " + RevampLink.PosesSent + ", applied " + RevampLink.PosesApplied + ", expired " + RevampLink.PosesExpired);
                    Event("joiner", "done");
                    return -1f;
                default:
                    return -1f;
            }
        }
    }
}
