using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using HarmonyLib;
using UnityEngine;
using Object = UnityEngine.Object;

namespace Rossa88
{
    // The Rossa 88 as the game's Car07 with the model's looks. Car07's shell and Full prefabs are cloned whole; only
    // meshes on Car07's visual nodes and on its own part items are replaced with the model's (cars/rossa88-parts.glb),
    // what the model lacks is hidden, every node moves along the car from Car07's landmarks (bumpers, axles, driver's
    // seat) to the model's, and each slot is moved so its item's hinge lands on the model's hinge. Components, layers,
    // box sizes, controls, scripts and materials stay Car07's. The Full car keeps its parts inside it and saves under the
    // shell's id, as the game's cars do. What goes where: cars/rossa88.json (ids 108800-108899). Every car listed in
    // Configs is built (each its own fit table and parts model); a car that fails to build is skipped alone.
    internal static class CarBuild
    {
        private static readonly string[] Configs = { "rossa88.json" };
        private sealed class Car { internal string File; internal Dictionary<string, object> Cfg; internal int IdMin, IdMax; }
        private static readonly List<Car> cars = new List<Car>();
        private static GameObject library;
        private static readonly Dictionary<int, GameObject> prefabs = new Dictionary<int, GameObject>();
        private static bool building, built;
        private static readonly CultureInfo IC = CultureInfo.InvariantCulture;

        internal static void Install(Harmony h)
        {
            foreach (var file in Configs)
            {
                try
                {
                    var c = (Dictionary<string, object>)Json.Parse(Encoding.UTF8.GetString(CarFiles.ReadAllBytes(Path.Combine(Plugin.CarsFolder, file))));
                    var range = c.ContainsKey("idRange") ? (List<object>)c["idRange"] : new List<object> { 107600, 107700 };
                    cars.Add(new Car { File = file, Cfg = c, IdMin = Int(range[0]), IdMax = Int(range[1]) });
                }
                catch (Exception e) { Log.Info("car07: " + file + " not loaded: " + e.Message); }
            }
            h.Patch(AccessTools.Method(typeof(itemdatabase), "ItemInit"), postfix: new HarmonyMethod(typeof(CarBuild), nameof(AfterDatabase)));
            WearLayer.Install(h);
            h.Patch(AccessTools.Method(typeof(mainscript), "Awake"), postfix: new HarmonyMethod(typeof(CarBuild), nameof(AfterDatabase)));
            h.Patch(AccessTools.Method(typeof(itemdatabase), "Item", new[] { typeof(int) }), prefix: new HarmonyMethod(typeof(CarBuild), nameof(Resolve)));
            h.Patch(AccessTools.Method(typeof(itemdatabase), "Item", new[] { typeof(int), typeof(GameObject).MakeByRefType() }),
                prefix: new HarmonyMethod(typeof(CarBuild), nameof(TryResolve)));
            h.Patch(AccessTools.Method(typeof(itemdatabase), "CheckType"), prefix: new HarmonyMethod(typeof(CarBuild), nameof(CheckType)));
        }

        private static int Int(object o) => Convert.ToInt32(o, IC);
        private static Dictionary<string, object> Obj(object o, string k) => (Dictionary<string, object>)((Dictionary<string, object>)o)[k];
        private static List<object> Arr(object o, string k) => (List<object>)((Dictionary<string, object>)o)[k];
        private static string Str(object o, string k) => ((Dictionary<string, object>)o).TryGetValue(k, out var v) ? v as string : null;
        private static Vector3 Vec(object o) { var l = (List<object>)o; return new Vector3(Convert.ToSingle(l[0], IC), Convert.ToSingle(l[1], IC), Convert.ToSingle(l[2], IC)); }
        private static bool Ours(int id) => cars.Any(c => id >= c.IdMin && id < c.IdMax);

        private static void AfterDatabase() { if (itemdatabase.s && mainscript.s) Register(itemdatabase.s); }

        private static bool Register(itemdatabase db)
        {
            if (built) { Append(db); return prefabs.Count > 0; }
            if (building || !db || db.items == null || !mainscript.s) return false;
            building = true;
            try
            {
                if (!library) { library = new GameObject("Rossa Car07 library"); library.SetActive(false); Object.DontDestroyOnLoad(library); }
                foreach (var car in cars)
                {
                    var made = new Dictionary<int, GameObject>();
                    try { new Builder(db, car.Cfg).Build(made); foreach (var p in made) prefabs[p.Key] = p.Value; }
                    catch (Exception e)
                    {
                        Log.Info("car07: " + car.File + ": build failed: " + e);
                        foreach (var p in made.Values) if (p) Object.Destroy(p);
                    }
                }
                built = true;
                Append(db);
                return prefabs.Count > 0;
            }
            finally { building = false; }
        }

        private static void Append(itemdatabase db)
        {
            var add = prefabs.Values.Where(p => !db.items.Contains(p)).ToArray();
            if (add.Length == 0) return;
            db.items = db.items.Concat(add).ToArray();
            foreach (var pair in prefabs) Log.Info("car07: " + pair.Value.name + " id " + pair.Key + " at F1 index " + Array.IndexOf(db.items, pair.Value));
        }

        private static bool Resolve(itemdatabase __instance, int id, ref GameObject __result)
        { if (!Ours(id)) return true; __result = Register(__instance) && prefabs.TryGetValue(id, out var p) ? p : __instance.gerror; return false; }
        private static bool TryResolve(itemdatabase __instance, int id, ref GameObject _obj, ref bool __result)
        { if (!Ours(id)) return true; __result = Register(__instance) && prefabs.TryGetValue(id, out var p); _obj = __result ? prefabs[id] : __instance.gerror; return false; }
        private static bool CheckType(itemdatabase __instance, int id, ref bool __result)
        { if (!Ours(id)) return true; __result = Register(__instance) && prefabs.ContainsKey(id); return false; }

        private sealed class Builder
        {
            private readonly itemdatabase db;
            private readonly Dictionary<string, object> cfg;
            private Transform model;
            private Vector3 offset;
            private LinearMap map;
            private float cabinRise;
            private Vector3[] seatR;
            private readonly Dictionary<string, Vector3> slotMoves = new Dictionary<string, Vector3>(), snapMoves = new Dictionary<string, Vector3>();

            internal Builder(itemdatabase db, Dictionary<string, object> cfg) { this.db = db; this.cfg = cfg; }

            private string ModelFile => Str(cfg, "model") ?? "rossa88-parts.glb";
            private Transform Part(string name) => model.Find(name) ?? throw new InvalidOperationException(ModelFile + " has no " + name);
            private Transform OptionalPart(string name) => model.Find(name);

            internal void Build(Dictionary<int, GameObject> output)
            {
                var template = new Material(Shader.Find("Standard"));
                model = Gltf.Load(Path.Combine(Plugin.CarsFolder, ModelFile), template, out var loader).transform;
                model.SetParent(library.transform, false);
                foreach (string w in loader.Warnings) Log.Info("car07: GLB: " + w);
                var ids = Obj(cfg, "ids");
                int shellId = Int(ids["shell"]), fullId = Int(ids["full"]);
                string name = Str(cfg, "name") ?? "RossaC07";
                var range = cfg.ContainsKey("idRange") ? (List<object>)cfg["idRange"] : new List<object> { 107600, 107700 };
                bool Mine(int id) => id >= Int(range[0]) && id < Int(range[1]);

                // The Full car first: its items give each slot's move (hinge onto the Rossa's), which the shell repeats.
                var full = Clone(db.items[Int(cfg["donorFull"])], name + "Full");
                Landmarks(full.transform);
                Apply(full.transform, true);
                var shell = Clone(db.items[Int(cfg["donorShell"])], name);
                Apply(shell.transform, false);
                // Saved like the game's Full cars: under the shell's id, parts inside.
                full.GetComponent<tosaveitemscript>().id = shellId;
                shell.GetComponent<tosaveitemscript>().id = shellId;
                var tags = cfg.ContainsKey("tags") ? ((List<object>)cfg["tags"]).Cast<string>().ToList() : new List<string> { "Rossa", "Mercedes", "car" };
                foreach (var go in new[] { full, shell }) go.GetComponent<tosaveitemscript>().searchtags = new List<string>(tags);
                output[shellId] = shell; output[fullId] = full;

                // Each fitted part as its own catalogue item (a copy of the one in the Full car, nested items removed).
                foreach (var save in full.GetComponentsInChildren<tosaveitemscript>(true))
                {
                    if (save.transform == full.transform || !Mine(save.id) || output.ContainsKey(save.id)) continue;
                    var item = Object.Instantiate(save.gameObject, library.transform, false);
                    item.name = save.name;
                    foreach (var nested in item.GetComponentsInChildren<tosaveitemscript>(true))
                        if (nested.transform != item.transform) Object.DestroyImmediate(nested.gameObject);
                    output[save.id] = item;
                }
                // What the game walks when the car spawns (tosaveitemscript.FStart): every listed slot and part must still exist.
                foreach (var go in output.Values.ToArray())
                    foreach (var s in go.GetComponentsInChildren<tosaveitemscript>(true))
                    {
                        if (s.partslotscripts != null)
                        {
                            int dead = s.partslotscripts.RemoveAll(p => !p);
                            if (dead > 0) Log.Info("car07: " + s.name + ": dropped " + dead + " removed slots from its slot list");
                            foreach (var ps in s.partslotscripts)
                            {
                                int gone = ps.parts.RemoveAll(p => !p || !p.tosave);
                                if (gone > 0) Log.Info("car07: " + s.name + "/" + ps.name + ": dropped " + gone + " removed parts");
                            }
                        }
                        if (s.randomScales != null && s.randomScales.RemoveAll(r => !r) > 0) Log.Info("car07: " + s.name + ": dropped removed random scales");
                        // Every other list or array of game objects it keeps (switches, random variants, seats, caps).
                        foreach (var field in typeof(tosaveitemscript).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                        {
                            var value = field.GetValue(s);
                            if (value is System.Collections.IList list && !(value is Array) && field.FieldType.IsGenericType && typeof(Object).IsAssignableFrom(field.FieldType.GetGenericArguments()[0]))
                            {
                                int before = list.Count;
                                for (int i = list.Count - 1; i >= 0; i--) if (!(list[i] as Object)) list.RemoveAt(i);
                                if (list.Count != before) Log.Info("car07: " + s.name + ": dropped " + (before - list.Count) + " removed entries from " + field.Name);
                            }
                            else if (value is Array arr && typeof(Object).IsAssignableFrom(field.FieldType.GetElementType()) && arr.Cast<object>().Any(x => !(x as Object)))
                            {
                                var kept = arr.Cast<object>().Where(x => (x as Object)).ToArray();
                                var fresh = Array.CreateInstance(field.FieldType.GetElementType(), kept.Length); Array.Copy(kept, fresh, kept.Length);
                                field.SetValue(s, fresh);
                                Log.Info("car07: " + s.name + ": dropped " + (arr.Length - kept.Length) + " removed entries from " + field.Name);
                            }
                        }
                    }
                // Wear/paint scripts list their child scripts and renderers; removed ones there threw when the car spawned.
                foreach (var go in output.Values.ToArray())
                    foreach (var pc in go.GetComponentsInChildren<partconditionscript>(true))
                    {
                        int gone = (pc.childs?.RemoveAll(c => !c) ?? 0) + (pc.renderers?.RemoveAll(r => !r) ?? 0);
                        if (pc.randomChildsSet != null && pc.randomChildsSet.Any(c => !c)) { pc.randomChildsSet = pc.randomChildsSet.Where(c => c).ToArray(); gone++; }
                        if (gone > 0) Log.Info("car07: " + pc.name + ": dropped " + gone + " removed wear entries");
                    }
                Object.Destroy(model.gameObject);
                Log.Info("car07: built " + name + " on Car07: shell, Full and " + (output.Count - 2) + " part items");
            }

            private GameObject Clone(GameObject donor, string name)
            {
                var go = Object.Instantiate(donor, library.transform, false);
                go.name = name;
                return go;
            }

            // Car07's landmarks (bumpers, axles, driver's seat, ground) against the Rossa's, giving the model offset and
            // the map along the car.
            private void Landmarks(Transform root)
            {
                var wheels = root.GetComponentsInChildren<WheelCollider>(true).Where(w => !w.name.Contains("KeepAwake")).ToArray();
                WheelCollider Wheel(string n) => wheels.Single(w => w.name == n);
                float Rest(WheelCollider w) => root.InverseTransformPoint(w.transform.position).y + w.center.y - w.suspensionDistance * (1 - w.suspensionSpring.targetPosition);
                // The wheel's real radius comes from the fitted tyre (wheelgraphicsscript.GetRadius: 0.345 m measured on a
                // settled Car07, against 0.30 on its bare WheelCollider); with the bare one the Rossa sat ~5 cm too high.
                var tyres = root.GetComponentsInChildren<gumiscript>(true).Where(g => g.radius > 0).Select(g => g.radius).ToArray();
                float wheelRadius = tyres.Length > 0 ? tyres.Max() : Wheel("FL").radius;
                float ground07 = Rest(Wheel("FL")) - wheelRadius;
                Log.Info("car07: Car07 wheel radius " + wheelRadius.ToString("F3") + (tyres.Length > 0 ? " (tyre item)" : " (bare WheelCollider)"));
                float front07 = Local(root, Wheel("FL").transform).z, rear07 = Local(root, Wheel("RL").transform).z;
                float bumperF07 = Local(root, Find(root, "Slots/FrontBumperSlot/FrontBumperSlotCollider")).z;
                float bumperR07 = Local(root, Find(root, "Slots/RearBumperSlot/RearBumperSlotCollider")).z;
                var tire = Part("Tire"); var tireBounds = Bounds(tire);
                float groundR = tireBounds.center.y - tireBounds.extents.y, frontR = tire.localPosition.z;
                // "align": "wheelCentre" puts the model's wheel centres on the game wheels' (its tyres are smaller than
                // Car07's: standing them on the ground would sink the game tyre into the arches); default: tyres on the ground.
                offset = Str(cfg, "align") == "wheelCentre"
                    ? new Vector3(0, Rest(Wheel("FL")) - tireBounds.center.y, front07 - frontR)
                    : new Vector3(0, ground07 - groundR, front07 - frontR);
                float wheelbase = 2 * frontR;   // the Rossa model's axles sit symmetric about its root
                seatR = Arr(cfg, "seats").Select(s => Vec(s) + offset).ToArray();
                var driver07 = Find(root, "Seats").GetComponentsInChildren<seatscript>(true).Single(s => s.driverSeat0);
                var seat07 = Local(root, driver07.sitPos);
                map = new LinearMap(new[] { bumperR07, rear07, seat07.z, front07, bumperF07 },
                    new[] { Part("BumperRear").localPosition.z + offset.z, frontR - wheelbase + offset.z, seatR[0].z, front07, Part("BumperFront").localPosition.z + offset.z });
                cabinRise = seatR[0].y - seat07.y;
                Log.Info("car07: Car07 ground " + ground07.ToString("F3") + " axles " + front07.ToString("F3") + "/" + rear07.ToString("F3") + " seat " + seat07.ToString("F3") +
                         "; Rossa ground " + groundR.ToString("F3") + " wheelbase " + wheelbase.ToString("F3") + " seat " + seatR[0].ToString("F3") +
                         "; model offset " + offset.ToString("F3") + "; cabin rise " + cabinRise.ToString("F3"));
            }

            private void Apply(Transform root, bool full)
            {
                var palette = Palette(root);
                // "follow": {node: leader} keeps a node where it sat relative to its leader on Car07 (the filler square, whose
                // origin is mid-car while it draws under the fuel flap, would otherwise move with the middle of the car).
                var follow = new Dictionary<Transform, (Transform leader, Vector3 rel, Quaternion relRot, Vector3 own)>();
                if (cfg.ContainsKey("follow"))
                    foreach (var kv in Obj(cfg, "follow"))
                    {
                        var t = Find(root, kv.Key); var l = Find(root, (string)kv.Value);
                        Vector3 tc = root.TransformPoint(CarBounds(root, t).center), lc = root.TransformPoint(CarBounds(root, l).center);
                        follow[t] = (l, Quaternion.Inverse(l.rotation) * (tc - lc), Quaternion.Inverse(l.rotation) * t.rotation, Quaternion.Inverse(t.rotation) * (tc - t.position));
                    }
                MoveAlong(root);
                Track(root);
                Seats(root);
                foreach (var path in Arr(cfg, "remove").Cast<string>()) { var t = root.Find(path); if (t) t.gameObject.SetActive(false); }
                // Hidden means gone: no drawing and no solid (the exhaust's colliders, left on, held the car up off its wheels).
                foreach (var path in Arr(cfg, "hide").Cast<string>())
                {
                    var t = root.Find(path); if (!t) continue;
                    foreach (var r in t.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                    foreach (var c in t.GetComponentsInChildren<Collider>(true)) c.enabled = false;
                }

                // Shell visuals: the Rossa's meshes on Car07's visual nodes; Car07's other body meshes hidden.
                var car07 = Find(root, "car07");
                var shown = new HashSet<Renderer>();
                foreach (var role in Obj(cfg, "shell"))
                {
                    var target = Find(car07, role.Key);
                    var parts = ((List<object>)role.Value).Cast<string>().Select(Part).ToArray();
                    Swap(parts, root, target, palette, own: role.Key == "tail light");
                    shown.Add(target.GetComponent<Renderer>());
                    // Swap's per-material child renderers (chrome, trim, glass) belong to the role too.
                    foreach (Transform c in target) if (c.name.StartsWith("Rossa ") && c.GetComponent<Renderer>()) shown.Add(c.GetComponent<Renderer>());
                }
                foreach (var r in car07.GetComponentsInChildren<Renderer>(true)) if (!shown.Contains(r)) r.enabled = false;
                Steering(root, palette);

                // Slots onto the Rossa: each item's hinge (or root) to the Rossa hinge / mount; Rossa meshes on Car07's items.
                foreach (Dictionary<string, object> fit in Arr(cfg, "fit"))
                {
                    var slot = root.Find(Str(fit, "slot"));
                    if (!slot) throw new InvalidOperationException("Car07 has no " + Str(fit, "slot"));
                    string part = Str(fit, "part");
                    Transform item = full ? slot.Find(Str(fit, "item")) : null;
                    // "turnX" / "scale": the slot (and what sits in it) tipped about the car's width and resized, for a fitting
                    // that goes into a differently angled or smaller opening (the radio into a tilted console slot).
                    if (fit.ContainsKey("turnX")) slot.rotation = Quaternion.AngleAxis(Convert.ToSingle(fit["turnX"], IC), root.right) * slot.rotation;
                    if (fit.ContainsKey("scale")) slot.localScale *= Convert.ToSingle(fit["scale"], IC);
                    // "upAxis": the slot turned so its local y (the axis Car07's lid swings about) runs that way in the car (the
                    // glovebox lid swung about the car's length on this dash; across the car it drops open).
                    if (fit.ContainsKey("upAxis")) slot.rotation = Quaternion.FromToRotation(slot.up, root.TransformDirection(Vec(fit["upAxis"]))) * slot.rotation;
                    if (full)
                    {
                        if (!item) throw new InvalidOperationException("Car07 Full has no " + Str(fit, "slot") + "/" + Str(fit, "item"));
                        if (fit.ContainsKey("centre"))
                        {
                            // The drawn centre of the item (or of its "ref" renderer) onto the point (a mirror's glass).
                            var b = CarBounds(root, Str(fit, "ref") != null ? Find(item, Str(fit, "ref")) : item);
                            slotMoves[Str(fit, "slot")] = Vec(fit["centre"]) + offset - b.center;
                        }
                        else if (fit.ContainsKey("bottom"))
                        {
                            // The item's drawn shape: its bottom-centre onto the point (an engine onto its bay floor).
                            var b = CarBounds(root, item);
                            slotMoves[Str(fit, "slot")] = Vec(fit["bottom"]) + offset - new Vector3(b.center.x, b.min.y, b.center.z);
                        }
                        else
                        {
                            var pivot = string.IsNullOrEmpty(Str(fit, "pivot")) ? item : Find(item, Str(fit, "pivot"));
                            var target = (part != null ? Part(part).localPosition : Vec(fit["target"])) + offset;
                            slotMoves[Str(fit, "slot")] = target - Local(root, pivot);
                        }
                    }
                    if (slotMoves.TryGetValue(Str(fit, "slot"), out var move)) slot.position += root.TransformVector(move);
                    if (!full || part == null) continue;
                    var under = string.IsNullOrEmpty(Str(fit, "pivot")) ? item : Find(item, Str(fit, "pivot"));
                    var renderers = under.GetComponentsInChildren<MeshRenderer>(true);
                    // The painted panel, never a window (window renderers get glass/random materials: invisible doors). The game
                    // paints and wears the item's painted renderer (a door's panel, not its handles), so that one carries the part.
                    var main = Str(fit, "renderer") != null ? Find(under, Str(fit, "renderer")).GetComponent<MeshRenderer>()
                        : renderers.Where(r => !Glassy(r)).OrderByDescending(r => r.sharedMaterial == palette["paint"] ? 1 : 0)
                            .ThenByDescending(r => r.GetComponent<MeshFilter>()?.sharedMesh?.vertexCount ?? 0).First();
                    Swap(new[] { Part(part) }, root, main.transform, palette, own: Str(fit, "renderer") != null, lamp: Str(fit, "lamp"));
                    foreach (var r in renderers) if (r != main) r.enabled = false;
                    // "extra": more model parts on other renderers of the item (a mirror's base, head and glass); "node" first
                    // moves that node (the mirror head's turning point) onto the part's origin.
                    if (fit.ContainsKey("extra"))
                        foreach (Dictionary<string, object> x in (List<object>)fit["extra"])
                        {
                            if (Str(x, "node") != null) Find(item, Str(x, "node")).position = root.TransformPoint(Part(Str(x, "part")).localPosition + offset);
                            var r = Find(item, Str(x, "renderer"));
                            Swap(new[] { Part(Str(x, "part")) }, root, r, palette, own: x.ContainsKey("own") && (bool)x["own"]);
                        }
                    // "twins": renderers the item's random-variant picker shows instead (Car07's mirrors come chrome or black,
                    // chosen when spawned): they get the same model meshes, or the part vanished whenever the other was chosen.
                    if (fit.ContainsKey("twins"))
                        foreach (var kv in Obj(fit, "twins")) Copy(Find(item, kv.Key), Find(item, (string)kv.Value));
                    else OneVariant(item, main.transform);
                    // "paintLike": the item wears and takes the car colour like that part (Car07's bumpers and mirrors are
                    // "metals", chrome; a body-coloured model part gets the door's paint type instead).
                    if (fit.ContainsKey("paintLike"))
                    {
                        CopyCondition(Find(root, Str(fit, "paintLike")).GetComponent<partconditionscript>(), item.GetComponent<partconditionscript>());
                    }
                    // "limit": how far the item's turnable controls go each way (a mirror head; Car07's swing 60 degrees).
                    if (fit.ContainsKey("limit"))
                        foreach (var u in item.GetComponentsInChildren<usablescript>(true).Where(u => u.rotateAble))
                        { float l = Convert.ToSingle(fit["limit"], IC); u.minX = -l; u.maxX = l; u.minY = -l; u.maxY = l; }
                    item.GetComponent<tosaveitemscript>().id = Int(fit["id"]);
                    // "cells": the part's collision as an N x N grid of boxes following its panel (one box round a curved lid
                    // reached 30 cm into the car), and the space they take kept clear of the body's boxes (a held lid, door or
                    // bumper was pushed off by them before it came near enough to attach: frunk lid 64 cm, Car07's 14).
                    var cells = FitColliders(item, main, root, Floor(), fit.ContainsKey("cells") ? Int(fit["cells"]) : 0);
                    if (cells != null) partRoom.AddRange(cells);
                }
                // "nodes": a node's origin onto the point, or with "ref" the centre of that child's drawn shape (the tow
                // hitch's ball just behind the bumper).
                foreach (Dictionary<string, object> n in Arr(cfg, "nodes"))
                {
                    var node = Find(root, Str(n, "node"));
                    var at = root.TransformPoint(Vec(n["target"]) + offset);
                    var was = node.position;
                    if (Str(n, "ref") != null) node.position += at - root.TransformPoint(CarBounds(root, Find(node, Str(n, "ref"))).center);
                    else node.position = at;
                    // "with": nodes that go along by the same amount (the fuel flap's open/closed markers, cap and filler square).
                    if (n.ContainsKey("with")) foreach (var w in ((List<object>)n["with"]).Cast<string>()) Find(root, w).position += node.position - was;
                }
                // "show": Car07 body pieces the model lacks and keeps (the black filler square under the fuel flap).
                if (cfg.ContainsKey("show"))
                    foreach (var path in Arr(cfg, "show").Cast<string>()) foreach (var r in Find(root, path).GetComponentsInChildren<Renderer>(true)) r.enabled = true;
                Snap(root, full);
                FuelFlap(root, palette);
                // Followers: same place and turn relative to the leader's drawn centre as on Car07.
                foreach (var kv in follow)
                {
                    var l = kv.Value.leader; var lc = root.TransformPoint(CarBounds(root, l).center);
                    kv.Key.rotation = l.rotation * kv.Value.relRot;
                    kv.Key.position = lc + l.rotation * kv.Value.rel - kv.Key.rotation * kv.Value.own;
                }
                // "fuelFlap.filler": the dark filler square right under the closed flap's surface (Car07's sat in a 5 cm deep
                // recess this body does not have, so it was hidden inside the panel).
                if (cfg.ContainsKey("fuelFlap") && Obj(cfg, "fuelFlap").ContainsKey("filler") && flapNormal != Vector3.zero)
                {
                    var fill = Find(root, Str(Obj(cfg, "fuelFlap"), "filler"));
                    var at = CarBounds(root, fill).center;
                    var surface = Hit(flapCentre + flapNormal * .25f, -flapNormal) ?? flapCentre - flapNormal * Convert.ToSingle(Obj(cfg, "fuelFlap")["fillerDepth"], IC);
                    fill.position += root.TransformVector(surface + flapNormal * .0015f - at);
                    Log.Info("car07: filler square on the body surface " + ((surface - flapCentre).magnitude * 1000).ToString("F1") + " mm under the flap");
                }
                Mounts(root);
                // "strip": Car07 items the model has its own of (radio, gauge pod, visors, rear-view mirror): out of the Full car.
                if (full && cfg.ContainsKey("strip"))
                    foreach (var path in Arr(cfg, "strip").Cast<string>()) { var t = root.Find(path); if (t) Remove(root, t); }
                // "hideDraw": Car07 controls kept working but not drawn (pedals and levers the model draws itself).
                if (cfg.ContainsKey("hideDraw"))
                    foreach (var path in Arr(cfg, "hideDraw").Cast<string>()) { var t = root.Find(path); if (t) foreach (var r in t.GetComponentsInChildren<Renderer>(true)) r.enabled = false; }
                if (full) Wheels(root, palette);
                Gauges(root, full);
                PopUps(root, palette);
                // "inertia": the body's inertia per kg (and its axes' turn) fixed, not guessed from the colliders
                if (cfg.ContainsKey("inertia"))
                {
                    var spec = Obj(cfg, "inertia"); var pin = root.gameObject.AddComponent<InertiaPin>();
                    pin.perKg = Vec(spec["perKg"]); pin.rotationEuler = spec.ContainsKey("rotation") ? Vec(spec["rotation"]) : Vector3.zero;
                }
                Collision(root);
                ClipToFloor(root);
            }

            // The game material for a model primitive: the config's "materials" table (source-name substring -> palette kind,
            // or "own" to keep the model's textured material) first, then the name guess the Rossa uses.
            private Material Pick(Dictionary<string, Material> palette, Material source)
            {
                string n = source ? source.name : "";
                if (cfg.ContainsKey("materials"))
                    foreach (var kv in Obj(cfg, "materials"))
                        if (n.Contains(kv.Key))
                            return (string)kv.Value == "own" && source ? Shine(source) : palette.TryGetValue((string)kv.Value, out var m) && m ? m : palette["paint"];
                return CarBuild.Pick(palette, n);
            }

            // "shineLike": {"<source-name substring>": <catalogue id>}: the model's own textured material, as shiny as that
            // game item's (metallic and smoothness copied). The model's rims came out matte (metallic 0, smoothness 0.5)
            // next to the game's rims (Metal0New: 0.95 / 0.8); the texture and its baked shading stay.
            private readonly Dictionary<Material, Material> shined = new Dictionary<Material, Material>();
            private Material Shine(Material source)
            {
                if (!cfg.ContainsKey("shineLike")) return source;
                if (shined.TryGetValue(source, out var done)) return done;
                Material result = source;
                foreach (var kv in Obj(cfg, "shineLike"))
                {
                    if (kv.Key.StartsWith("_") || !source.name.Contains(kv.Key)) continue;   // "_" is the note
                    // the item's new-condition material, looked up by its condition type as the game does when the part starts
                    // (partconditionscript.FStart: mainscript.conditionmaterials; a new rim is Metal0New). The prefab's own
                    // stored material is not the one drawn (the rim item's was "fem3", 0.85 / 0.2).
                    var pcs = db.items[Int(kv.Value)].GetComponentsInChildren<partconditionscript>(true);
                    var like = pcs.Select(pc => NewMaterial(pc)).Concat(pcs.Select(pc => pc.defNew))
                        .FirstOrDefault(m => m && m.HasProperty("_Metallic") && m.HasProperty("_Glossiness"));
                    result = new Material(source) { name = source.name + " (shine)" };
                    result.SetFloat("_Metallic", like ? like.GetFloat("_Metallic") : .95f);
                    result.SetFloat("_Glossiness", like ? like.GetFloat("_Glossiness") : .8f);
                    Log.Info("car07: " + source.name + " as shiny as " + (like ? like.name : "the game's rims") + " (metallic " +
                             result.GetFloat("_Metallic").ToString("0.##", IC) + ", smoothness " + result.GetFloat("_Glossiness").ToString("0.##", IC) + ")");
                    break;
                }
                shined[source] = result;
                return result;
            }

            private static Material NewMaterial(partconditionscript pc)
            {
                if (!mainscript.s || mainscript.s.conditionmaterials == null) return null;
                string t = pc.useOnlyMaterialTipusForMaterial ? pc.materialTipus : pc.tipus;
                foreach (var cm in mainscript.s.conditionmaterials) if (cm.tipus == t) return cm.New;
                return null;
            }

            // "wearLike": {"<material-name substring>": "<game condition type>"}: which of the game's worn looks a part takes
            // (the dash and trim wear like the game's dark metal, seats like its leather, rims like its metal).
            private string WearLike(string material)
            {
                if (!cfg.ContainsKey("wearLike")) return null;
                foreach (var kv in Obj(cfg, "wearLike")) if (!kv.Key.StartsWith("_") && material.Contains(kv.Key)) return (string)kv.Value;
                return null;
            }

            // Materials that stay clear at any condition: glass, lenses and lamps.
            private static bool Clear(string material)
            {
                string n = material.ToLowerInvariant();
                return n.Contains("glass") || n.Contains("window") || n.Contains("light") || n.Contains("lens") || n.Contains("lamp");
            }

            private static bool Glassy(Renderer r)
            {
                string n = (r.name + " " + (r.sharedMaterial ? r.sharedMaterial.name : "")).ToLowerInvariant();
                return n.Contains("glass") || n.Contains("window") || n.Contains("sunglass");
            }

            // A part item's own solid colliders (what you grab it by and what it bumps with when loose) take the Rossa
            // part's shape. One collider does it: the solid box whose frame lines up best with the part's mesh (a box
            // fitted in a tilted frame swells: Car07's angled bumper-end and door-glass boxes reached the ground and held
            // the car up). It gets the mesh's bounds; every other solid collider of the part shrinks to a 1 cm box inside
            // it, so the item's own collider lists stay valid. Interaction colliders (usable/slot scripts) stay Car07's.
            // "collisionFloor" (model height): nothing of the body or its parts collides below it (a low car's skirts and
            // spoiler lips otherwise caught the ground and threw it upwards on bumps); wheels are not affected.
            private float? Floor() => cfg.ContainsKey("collisionFloor") ? Convert.ToSingle(cfg["collisionFloor"], IC) + offset.y : (float?)null;

            private readonly List<BoxCollider> partRoom = new List<BoxCollider>();

            private static List<BoxCollider> FitColliders(Transform item, MeshRenderer main, Transform root, float? floor, int cells = 0)
            {
                var meshes = main.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh && f.GetComponent<MeshRenderer>().enabled).ToArray();
                var solids = item.GetComponentsInChildren<Collider>(true)
                    .Where(c => !c.isTrigger && !c.GetComponent<usablescript>() && !c.GetComponent<partslotscript>() && Owner(c.transform) == item).ToList();
                if (solids.Count == 0) return null;
                Bounds Fit(Transform frame)
                {
                    var b = new Bounds(); bool any = false;
                    foreach (var f in meshes)
                    {
                        var m = frame.worldToLocalMatrix * f.transform.localToWorldMatrix;
                        foreach (var v in f.sharedMesh.vertices)
                        {
                            if (floor.HasValue && root.InverseTransformPoint(f.transform.TransformPoint(v)).y < floor.Value) continue;
                            var p = m.MultiplyPoint3x4(v); if (!any) { b = new Bounds(p, Vector3.zero); any = true; } else b.Encapsulate(p);
                        }
                    }
                    return b;
                }
                // Alignment: 1 when the collider's axes are the renderer's axes up to 90-degree turns.
                float Aligned(Transform t)
                {
                    var q = Quaternion.Inverse(t.rotation) * main.transform.rotation;
                    return new[] { q * Vector3.right, q * Vector3.up, q * Vector3.forward }.Sum(a => Mathf.Max(Mathf.Abs(a.x), Mathf.Abs(a.y), Mathf.Abs(a.z))) / 3;
                }
                var primary = solids.OfType<BoxCollider>().Where(b => b.gameObject.layer != 23).OrderByDescending(b => Aligned(b.transform)).FirstOrDefault()
                    ?? solids.OfType<BoxCollider>().OrderByDescending(b => Aligned(b.transform)).FirstOrDefault();
                if (!primary) { foreach (var mc in solids.OfType<MeshCollider>()) mc.sharedMesh = main.GetComponent<MeshFilter>().sharedMesh; return null; }
                // A plain collision box (nothing but the box on it) is squared to the car first: fitted in Car07's tilted frame
                // it ballooned (the front lid's reached 2 m and swallowed the small box you click to open it).
                if (primary.GetComponents<Component>().All(x => x is Transform || x is BoxCollider || x.GetType().Name == "layerScript"))
                    primary.transform.rotation = root.rotation;
                var fit = Fit(primary.transform);
                primary.center = fit.center; primary.size = Vector3.Max(fit.size, Vector3.one * .01f);
                var middle = primary.transform.TransformPoint(fit.center);
                foreach (var c in solids)
                {
                    if (c == primary) continue;
                    var at = c.transform.InverseTransformPoint(middle);
                    var scale = c.transform.lossyScale; var tiny = new Vector3(.01f / Mathf.Abs(scale.x), .01f / Mathf.Abs(scale.y), .01f / Mathf.Abs(scale.z));
                    if (c is BoxCollider b) { b.center = at; b.size = tiny; }
                    else if (c is SphereCollider s) { s.center = at; s.radius = tiny.x / 2; }
                    else if (c is CapsuleCollider k) { k.center = at; k.radius = tiny.x / 2; k.height = tiny.x; }
                    else if (c is MeshCollider mc) mc.enabled = false;
                }
                Log.Info("car07: " + item.name + ": collider " + primary.name + " fitted (" + Aligned(primary.transform).ToString("F2") + " aligned), " + (solids.Count - 1) + " others shrunk");
                if (cells < 2) return null;
                // the grid: over the two longest sides of the fitted box, each cell a box round the mesh points in it
                var pts = new List<Vector3>();
                foreach (var mf in meshes)
                {
                    var mm = primary.transform.worldToLocalMatrix * mf.transform.localToWorldMatrix;
                    foreach (var v in mf.sharedMesh.vertices)
                    {
                        if (floor.HasValue && root.InverseTransformPoint(mf.transform.TransformPoint(v)).y < floor.Value) continue;
                        pts.Add(mm.MultiplyPoint3x4(v));
                    }
                }
                var size = fit.size; int thin = size.x <= size.y && size.x <= size.z ? 0 : size.y <= size.z ? 1 : 2;
                int a1 = (thin + 1) % 3, a2 = (thin + 2) % 3;
                var boxes = new Dictionary<int, Bounds>();
                foreach (var p in pts)
                {
                    int i = Mathf.Clamp((int)((p[a1] - fit.min[a1]) / Mathf.Max(size[a1], 1e-4f) * cells), 0, cells - 1);
                    int j = Mathf.Clamp((int)((p[a2] - fit.min[a2]) / Mathf.Max(size[a2], 1e-4f) * cells), 0, cells - 1);
                    int key = i * cells + j;
                    if (boxes.TryGetValue(key, out var bb)) { bb.Encapsulate(p); boxes[key] = bb; } else boxes[key] = new Bounds(p, Vector3.zero);
                }
                var made = new List<BoxCollider>(); bool firstCell = true;
                // at least 2 cm on every side in world terms, per axis (one axis's scale for all three made thin boxes on an
                // unevenly scaled part tall: the front bumper's hung under the car and hit the ground)
                var ls = primary.transform.lossyScale; var minSide = new Vector3(.02f / Mathf.Abs(ls.x), .02f / Mathf.Abs(ls.y), .02f / Mathf.Abs(ls.z));
                foreach (var cb in boxes.Values)
                {
                    var box = firstCell ? primary : primary.gameObject.AddComponent<BoxCollider>();
                    if (!firstCell) box.sharedMaterial = primary.sharedMaterial;
                    box.center = cb.center; box.size = Vector3.Max(cb.size, minSide); firstCell = false;
                    made.Add(box);
                }
                Log.Info("car07: " + item.name + ": collision as " + made.Count + " boxes following the panel (" + cells + " x " + cells + " grid), part scale " + ls.ToString("F3"));
                return made;
            }

            // The Rossa's outer skin (body and doors) in car space, for snapping things onto it.
            private List<Vector3> skin;
            private List<Vector3> Skin()
            {
                if (skin != null) return skin;
                skin = new List<Vector3>();
                foreach (var name in new[] { "body", "DoorLeft", "DoorRight" })
                    foreach (var f in Part(name).GetComponentsInChildren<MeshFilter>(true))
                    {
                        var m = model.worldToLocalMatrix * f.transform.localToWorldMatrix;
                        skin.AddRange(f.sharedMesh.vertices.Select(v => m.MultiplyPoint3x4(v) + offset));
                    }
                return skin;
            }

            // The outer skin as triangles in car space (body, doors, bumpers), and the nearest one a ray meets.
            private List<Vector3> skinTris;
            private List<Vector3> cabinTris;
            private Vector3? Hit(Vector3 origin, Vector3 dir, bool cabin = false)
            {
                if (cabin && cabinTris == null)
                {
                    cabinTris = new List<Vector3>();
                    foreach (var name in new[] { "cockpit", "body" })
                    {
                        var part = OptionalPart(name); if (!part) continue;
                        foreach (var f in part.GetComponentsInChildren<MeshFilter>(true))
                        {
                            var m = model.worldToLocalMatrix * f.transform.localToWorldMatrix; var v = f.sharedMesh.vertices;
                            foreach (int i in f.sharedMesh.triangles) cabinTris.Add(m.MultiplyPoint3x4(v[i]) + offset);
                        }
                    }
                }
                if (skinTris == null)
                {
                    skinTris = new List<Vector3>();
                    foreach (var name in new[] { "body", "DoorLeft", "DoorRight", "BumperFront", "BumperRear", "Hood", "Trunk", "black", "chrome" })
                    {
                        var part = OptionalPart(name); if (!part) continue;
                        foreach (var f in part.GetComponentsInChildren<MeshFilter>(true))
                        {
                            var m = model.worldToLocalMatrix * f.transform.localToWorldMatrix; var v = f.sharedMesh.vertices;
                            foreach (int i in f.sharedMesh.triangles) skinTris.Add(m.MultiplyPoint3x4(v[i]) + offset);
                        }
                    }
                }
                float best = float.MaxValue; var tris = cabin ? cabinTris : skinTris;
                for (int i = 0; i + 2 < tris.Count; i += 3)
                {
                    // Moller-Trumbore, both faces
                    Vector3 a = tris[i], e1 = tris[i + 1] - a, e2 = tris[i + 2] - a, p = Vector3.Cross(dir, e2);
                    float det = Vector3.Dot(e1, p); if (Mathf.Abs(det) < 1e-9f) continue;
                    Vector3 s = origin - a; float u = Vector3.Dot(s, p) / det; if (u < 0 || u > 1) continue;
                    Vector3 q = Vector3.Cross(s, e1); float w = Vector3.Dot(dir, q) / det; if (w < 0 || u + w > 1) continue;
                    float t = Vector3.Dot(e2, q) / det; if (t > 0 && t < best) best = t;
                }
                return best < float.MaxValue ? origin + dir * best : (Vector3?)null;
            }

            private Bounds CarBounds(Transform root, Transform t)
            {
                var rs = t.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.GetComponent<MeshFilter>()?.sharedMesh).ToArray();
                if (rs.Length == 0) return new Bounds(Local(root, t), Vector3.one * .05f);
                Bounds? b = null;
                foreach (var r in rs)
                {
                    var m = root.worldToLocalMatrix * r.transform.localToWorldMatrix; var mb = r.GetComponent<MeshFilter>().sharedMesh.bounds;
                    for (int i = 0; i < 8; i++)
                    {
                        var p = m.MultiplyPoint3x4(mb.center + Vector3.Scale(mb.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1)));
                        if (b == null) b = new Bounds(p, Vector3.zero); else { var x = b.Value; x.Encapsulate(p); b = x; }
                    }
                }
                return b.Value;
            }

            // Things that sat on Car07's surfaces, put on the Rossa's: mirror feet onto the door tops, the fuel flap and cap
            // flush on the body side, visor rods just under the windscreen frame's top edge.
            private void Snap(Transform root, bool full)
            {
                var points = Skin();
                var frame = new List<Vector3>();
                foreach (var f in new[] { "glass", "chrome" }.Select(OptionalPart).Where(p => p).SelectMany(p => p.GetComponentsInChildren<MeshFilter>(true)))
                {
                    var m = model.worldToLocalMatrix * f.transform.localToWorldMatrix;
                    frame.AddRange(f.sharedMesh.vertices.Select(v => m.MultiplyPoint3x4(v) + offset));
                }
                foreach (Dictionary<string, object> s in Arr(cfg, "snap"))
                {
                    var node = root.Find(Str(s, "node")); if (!node) continue;
                    // Measured on the Full car (its items have the shapes), repeated on the shell like the slot moves.
                    if (!full)
                    {
                        if (snapMoves.TryGetValue(Str(s, "node"), out var again))
                        {
                            node.position += root.TransformVector(again);
                            if (s.ContainsKey("with")) foreach (var w in ((List<object>)s["with"]).Cast<string>()) Find(root, w).position += root.TransformVector(again);
                        }
                        continue;
                    }
                    Transform shape = node;
                    if (Str(s, "item") != null) { var item = node.Find(Str(s, "item")); if (!item) continue; shape = item; }
                    if (!string.IsNullOrEmpty(Str(s, "renderer"))) { var r = shape.Find(Str(s, "renderer")); if (r) shape = r; }
                    var b = CarBounds(root, shape);
                    Vector3 move = Vector3.zero;
                    switch (Str(s, "onto"))
                    {
                        case "down":
                        {
                            var under = points.Where(p => Mathf.Abs(p.x - b.center.x) < b.extents.x + .03f && Mathf.Abs(p.z - b.center.z) < b.extents.z + .03f && p.y < b.max.y + .3f).ToList();
                            if (under.Count > 0) move.y = under.Max(p => p.y) - b.min.y;
                            break;
                        }
                        case "side":
                        {
                            // A ray from outside straight in at the shape's middle; the first skin surface it meets.
                            float side = Mathf.Sign(b.center.x);
                            var hit = Hit(new Vector3(side * 3, b.center.y, b.center.z), new Vector3(-side, 0, 0));
                            if (hit != null) move.x = hit.Value.x - (side > 0 ? b.min.x : b.max.x) + side * .002f;
                            break;
                        }
                        case "face":
                        {
                            // Front or back of the car (plates): flush on the first surface met coming in along the car.
                            float end = Mathf.Sign(b.center.z);
                            var hit = Hit(new Vector3(b.center.x, b.center.y, end * 5), new Vector3(0, 0, -end));
                            if (hit != null) move.z = hit.Value.z - (end > 0 ? b.min.z : b.max.z) + end * .002f;
                            break;
                        }
                        case "roof":
                        {
                            // Straight up from the shape onto the first surface above it (the roof lining), top just under it.
                            var hit = Hit(new Vector3(b.center.x, b.max.y - .02f, b.center.z), Vector3.up, cabin: true);
                            if (hit != null) move.y = hit.Value.y - .003f - b.max.y;
                            break;
                        }
                        case "frame":
                        {
                            var near = frame.Where(p => Mathf.Abs(p.x - b.center.x) < .25f).ToList();
                            if (near.Count == 0) break;
                            var top = near.OrderByDescending(p => p.y).First();
                            move = new Vector3(0, top.y - .01f - b.max.y, top.z - .02f - b.center.z);
                            break;
                        }
                    }
                    snapMoves[Str(s, "node")] = move;
                    node.position += root.TransformVector(move);
                    // "with": other nodes that go along (the black filler square under the fuel flap).
                    if (s.ContainsKey("with")) foreach (var w in ((List<object>)s["with"]).Cast<string>()) Find(root, w).position += root.TransformVector(move);
                    Log.Info("car07: snap " + Str(s, "node") + " " + Str(s, "onto") + " moved " + move.ToString("F3"));
                }
            }

            // Every node along the car (parents first), world rotations kept; the cabin fittings rise with the seats;
            // long pieces under the car (exhaust, grass box, mount volumes) shrink along the car.
            private void MoveAlong(Transform root)
            {
                var all = root.GetComponentsInChildren<Transform>(true).Where(t => t != root).ToArray();
                var before = all.ToDictionary(t => t, t => (pos: Local(root, t), rot: t.rotation));
                foreach (var t in all)
                {
                    var p = before[t].pos;
                    t.SetPositionAndRotation(root.TransformPoint(new Vector3(p.x, p.y + (InCabin(root, t) ? cabinRise : 0), map.Map(p.z))), before[t].rot);
                }
                var car07 = Find(root, "car07");
                foreach (var r in root.GetComponentsInChildren<MeshRenderer>(true))
                {
                    var f = r.GetComponent<MeshFilter>();
                    if (!f || !f.sharedMesh || r.transform.IsChildOf(car07) || Owner(r.transform) != root) continue;
                    var b = f.sharedMesh.bounds; var t = r.transform;
                    var original = Matrix4x4.TRS(before[t].pos, Quaternion.Inverse(root.rotation) * before[t].rot, t.lossyScale);
                    float zMin = float.MaxValue, zMax = float.MinValue;
                    for (int i = 0; i < 8; i++)
                    {
                        var c = original.MultiplyPoint3x4(b.center + Vector3.Scale(b.extents, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1)));
                        zMin = Mathf.Min(zMin, c.z); zMax = Mathf.Max(zMax, c.z);
                    }
                    if (zMax - zMin < .8f) continue;
                    float factor = (map.Map(zMax) - map.Map(zMin)) / (zMax - zMin);
                    var forward = t.InverseTransformDirection(root.forward);
                    int axis = Mathf.Abs(forward.x) > Mathf.Abs(forward.y) ? (Mathf.Abs(forward.x) > Mathf.Abs(forward.z) ? 0 : 2) : (Mathf.Abs(forward.y) > Mathf.Abs(forward.z) ? 1 : 2);
                    var s = t.localScale; s[axis] *= factor; t.localScale = s;
                    float wanted = map.Map((zMin + zMax) / 2), now = root.InverseTransformPoint(r.bounds.center).z;
                    t.position += root.forward * (wanted - now);
                }
            }

            // "track": {"front": x, "rear": x}: the game wheels (colliders and wheel graphics) set that far either side of the
            // middle, for a model whose axles are wider or narrower than Car07's (Car07 has 0.75 m both ends).
            private void Track(Transform root)
            {
                if (!cfg.ContainsKey("track")) return;
                var track = Obj(cfg, "track");
                foreach (var t in root.GetComponentsInChildren<Transform>(true))
                {
                    bool collider = t.GetComponent<WheelCollider>() && !t.name.Contains("KeepAwake");
                    bool graphics = t.parent && t.parent.name == "CarStuff" && t.name.Length == 6 && t.name.StartsWith("T") && t.name.EndsWith("ROT");
                    if (!collider && !graphics) continue;
                    bool front = collider ? t.name.StartsWith("F") : t.name[1] == 'F';
                    float x = Convert.ToSingle(track[front ? "front" : "rear"], IC);
                    var p = Local(root, t);
                    // "rearRaise": rear wheels that much higher (a model whose rear tyres are bigger than its front ones).
                    float raise = front || !track.ContainsKey("rearRaise") ? 0 : Convert.ToSingle(track["rearRaise"], IC);
                    t.position = root.TransformPoint(new Vector3(Mathf.Sign(p.x) * x, p.y + raise, p.z));
                }
                // "suspension": travel, spring and damper of the game wheels (a low car on Car07's 40 cm of soft travel bottomed
                // out and dragged its body on bumps). The wheel's rest point stays where it was, so the ride height is kept.
                if (!cfg.ContainsKey("suspension")) return;
                var sus = Obj(cfg, "suspension");
                foreach (var w in root.GetComponentsInChildren<WheelCollider>(true).Where(w => !w.name.Contains("KeepAwake")))
                {
                    float before = w.suspensionDistance * (1 - w.suspensionSpring.targetPosition);
                    w.suspensionDistance = Convert.ToSingle(sus["distance"], IC);
                    var spring = w.suspensionSpring;
                    spring.spring = Convert.ToSingle(sus["spring"], IC); spring.damper = Convert.ToSingle(sus["damper"], IC);
                    w.suspensionSpring = spring;
                    float after = w.suspensionDistance * (1 - w.suspensionSpring.targetPosition);
                    w.transform.position -= root.up * (before - after);
                }
            }

            // Front seats onto the Rossa's seat points; Car07's middle front seat and rear bench go.
            private void Seats(Transform root)
            {
                var seats = Find(root, "Seats");
                var driver = seats.GetComponentsInChildren<seatscript>(true).Single(s => s.driverSeat0);
                foreach (Transform s in seats)
                {
                    var script = s.GetComponentsInChildren<seatscript>(true).FirstOrDefault(x => x.sitPos);
                    if (s.name.StartsWith("RearSeat") || s.name == "FrontSeat (2)" || !script) { s.gameObject.SetActive(false); continue; }
                    s.position += root.TransformPoint(seatR[script == driver ? 0 : 1]) - script.sitPos.position;
                }
            }

            private void Steering(Transform root, Dictionary<string, Material> palette)
            {
                var steering = Find(root, "SteeringWheelRot");
                var renderers = steering.GetComponentsInChildren<MeshRenderer>(true);
                var main = renderers.OrderByDescending(r => r.GetComponent<MeshFilter>()?.sharedMesh?.vertexCount ?? 0).First();
                steering.position = root.TransformPoint(Bounds(Part("steering")).center + offset);
                Swap(new[] { Part("steering") }, root, main.transform, palette, own: false);
                foreach (var r in renderers) if (r != main) r.enabled = false;
            }

            // Rossa rims and tyres on Car07's wheel items: one mesh made on the front-left wheel, shared by all four (the
            // right wheels are the same item turned round, as in the game); the tyre's radius drives the wheel.
            // Wheels stay the game's: Car07's rim and tyre untouched. The Rossa wheel face goes on Car07's hubcap item,
            // scaled to Car07's rim, centred on it and flush with its outer face; made on the front-left wheel and shared
            // by all four, as the game shares one hubcap item (the right wheels are the same item turned round).
            private void Wheels(Transform root, Dictionary<string, Material> palette)
            {
                if (Obj(cfg, "wheels").ContainsKey("front")) { OwnWheels(root, palette); return; }
                var spec = Obj(cfg, "wheels"); var h = Obj(spec, "hubcap");
                var caps = root.GetComponentsInChildren<tosaveitemscript>(true).Where(x => x.name == Str(h, "item")).ToArray();
                var fl = caps.Single(x => x.transform.IsChildOf(Find(root, "CarStuff/TFLROT")));
                var face = Find(fl.transform, Str(h, "renderer"));
                var rimItem = Owner(fl.transform.parent);
                var rim = CarBounds(root, Find(rimItem, (string)spec["rim"]));
                var cap = CarBounds(root, face);
                var rossa = Bounds(Part(Str(h, "part")));
                float scale = rim.extents.y / rossa.extents.y;
                // Left wheel: outward is -x. Centre on the rim, outer face where Car07's rim/hubcap face is.
                float outer = Mathf.Min(rim.min.x, cap.min.x);
                var centre = new Vector3(outer + rossa.extents.x * scale, rim.center.y, rim.center.z);
                var place = Matrix4x4.Translate(centre - offset) * Matrix4x4.Scale(Vector3.one * scale) * Matrix4x4.Translate(-rossa.center);
                Swap(new[] { Part(Str(h, "part")) }, root, face, palette, own: false, place: place);
                var extras = face.Cast<Transform>().Where(c => c.name.StartsWith("Rossa ")).ToArray();
                foreach (var item in caps)
                {
                    var target = Find(item.transform, Str(h, "renderer"));
                    if (target != face)
                    {
                        target.GetComponent<MeshFilter>().sharedMesh = face.GetComponent<MeshFilter>().sharedMesh;
                        target.GetComponent<MeshRenderer>().sharedMaterials = face.GetComponent<MeshRenderer>().sharedMaterials;
                        foreach (var e in extras) { var copy = Object.Instantiate(e.gameObject, target, false); copy.name = e.name; }
                    }
                    target.GetComponent<MeshRenderer>().enabled = true;
                    item.id = Int(h["id"]);
                }
                Log.Info("car07: Rossa wheel face on Car07's hubcap, scale " + scale.ToString("F3") + ", rim radius " + rim.extents.y.ToString("F3"));
            }

            // shared with the right ones, which are the same items turned round), each wheel centred on the game wheel; the
            // tyre's radius (what the game drives on) set to the model tyre's; Car07's hubcaps taken off. Rim and tyre items
            // get ids of their own (front and rear differ).
            private void OwnWheels(Transform root, Dictionary<string, Material> palette)
            {
                var spec = Obj(cfg, "wheels");
                var made = new Dictionary<string, (Transform rim, Transform tyre)>();
                foreach (var name in new[] { "TFLROT", "TRLROT", "TFRROT", "TRRROT" })
                {
                    bool front = name[1] == 'F', left = name[2] == 'L';
                    var s = Obj(spec, front ? "front" : "rear");
                    var rot = Find(root, "CarStuff/" + name);
                    var wheel = rot.GetComponentsInChildren<wheelscript>(true).First().transform;
                    var tyre = wheel.GetComponentsInChildren<gumiscript>(true).First();
                    foreach (var cap in wheel.GetComponentsInChildren<tosaveitemscript>(true).Where(x => x.transform != wheel && !x.GetComponent<gumiscript>()).ToArray())
                        Remove(root, cap.transform);
                    // The rim and tyre items' own collision shapes were made for Car07's tyre: scaled to the new one, or they
                    // reach below the smaller wheel and drag on the road (the car was thrown about at speed).
                    float k = Convert.ToSingle(s["radius"], IC) / tyre.radius;
                    foreach (var col in wheel.GetComponentsInChildren<Collider>(true))
                    {
                        if (col is WheelCollider) continue;
                        if (col is CapsuleCollider cap) { cap.radius *= k; cap.height *= k; cap.center *= k; }
                        else if (col is SphereCollider sph) { sph.radius *= k; sph.center *= k; }
                        else if (col is BoxCollider box) { box.size *= k; box.center *= k; }
                    }
                    tyre.radius = Convert.ToSingle(s["radius"], IC);
                    // Every tyre (left ones made here, right ones copied from them) wears like the game's tyre item, and Car07's
                    // whitewall rule (a child wear script painting the tyre white whenever the car is painted) goes.
                    if (spec.ContainsKey("tyreFrom"))
                    {
                        var stockPc = db.items[Int(spec["tyreFrom"])].GetComponent<partconditionscript>(); var tyrePc = tyre.GetComponent<partconditionscript>();
                        foreach (var child in tyre.GetComponentsInChildren<partconditionscript>(true).Where(x => x.transform != tyre.transform).ToArray())
                        { if (tyrePc && tyrePc.childs != null) tyrePc.childs.Remove(child); Object.DestroyImmediate(child); }
                        CopyCondition(stockPc, tyrePc);
                    }
                    wheel.GetComponent<tosaveitemscript>().id = Int(s["rimId"]);
                    tyre.GetComponent<tosaveitemscript>().id = Int(s["tyreId"]);
                    var rimR = Find(wheel, Str(spec, "rimRenderer"));
                    var tyreR = Find(tyre.transform, Str(spec, "tyreRenderer"));
                    foreach (var r in wheel.GetComponentsInChildren<MeshRenderer>(true)) if (r.transform != rimR && r.transform != tyreR) r.enabled = false;
                    if (spec.ContainsKey("tyreFrom"))
                    {
                        // the tyre's wear script draws the tyre we show (it listed Car07's renderers, so ours kept the
                        // prefab's untinted material and looked grey next to every other car's tyre)
                        var tyrePc = tyre.GetComponent<partconditionscript>();
                        if (tyrePc) { Log.Info("car07: tyre wear script drew " + (tyrePc.renderers == null ? "nothing" : string.Join(", ", tyrePc.renderers.Select(x => x ? x.name : "-"))) + ", now " + tyreR.name);
                            tyrePc.renderers = new List<Renderer> { tyreR.GetComponent<MeshRenderer>() }; }
                    }
                    var key = front ? "front" : "rear";
                    if (left)
                    {
                        // The model wheel's centre onto the game wheel's.
                        var centre = Bounds(Part(Str(s, "tyre"))).center + offset;
                        var place = Matrix4x4.Translate(Local(root, wheel) - centre);
                        Swap(new[] { Part(Str(s, "rim")) }, root, rimR, palette, own: false, place: place);
                        if (spec.ContainsKey("tyreFrom")) StockTyre(Int(spec["tyreFrom"]), Part(Str(s, "tyre")), Part(Str(s, "rim")), tyre.transform, tyreR);
                        else Swap(new[] { Part(Str(s, "tyre")) }, root, tyreR, palette, own: spec.ContainsKey("gameTyre") && (bool)spec["gameTyre"], place: place);
                        made[key] = (rimR, tyreR);
                    }
                    else
                    {
                        Copy(made[key].rim, rimR); Copy(made[key].tyre, tyreR);
                    }
                }
                Log.Info("car07: own wheels: front r " + Obj(spec, "front")["radius"] + ", rear r " + Obj(spec, "rear")["radius"]);
            }

            // An item out of the car for good: off every slot's part list first (the game starts each listed part when the
            // car spawns, and a destroyed one there threw on spawn), then destroyed.
            private static void Remove(Transform root, Transform item)
            {
                foreach (var ps in root.GetComponentsInChildren<partslotscript>(true))
                    if (ps.parts != null) ps.parts.RemoveAll(p => !p || p.transform == item || p.transform.IsChildOf(item));
                Object.DestroyImmediate(item.gameObject);
            }

            // "tyreFrom": the game's own tyre (that catalogue item's mesh, materials and wear settings), scaled to the model
            // tyre's radius and width, so the tyres look like every other car's.
            private void StockTyre(int id, Transform modelTyre, Transform modelRim, Transform tyreItem, Transform target)
            {
                var stock = db.items[id];
                var sr = stock.GetComponentsInChildren<MeshRenderer>(true).Where(r => r.GetComponent<MeshFilter>()?.sharedMesh)
                    .OrderByDescending(r => r.GetComponent<MeshFilter>().sharedMesh.vertexCount).First();
                var sm = sr.GetComponent<MeshFilter>().sharedMesh;
                var toItem = stock.transform.worldToLocalMatrix * sr.transform.localToWorldMatrix;
                var pts = sm.vertices.Select(v => toItem.MultiplyPoint3x4(v)).ToArray();
                var sb = new Bounds(pts[0], Vector3.zero); foreach (var q in pts) sb.Encapsulate(q);
                int axle = sb.size.x <= sb.size.y && sb.size.x <= sb.size.z ? 0 : sb.size.y <= sb.size.z ? 1 : 2;
                var mb = Bounds(modelTyre);   // model space: the axle is along x
                // as wide as the model tyre and never narrower than the rim (85% of the tyre left the rim lips sticking out past it)
                var rb = Bounds(modelRim);
                float radius = mb.extents.y, width = Mathf.Max(mb.size.x, rb.size.x * 1.04f), stockRadius = Mathf.Max(sb.extents[(axle + 1) % 3], sb.extents[(axle + 2) % 3]);
                var scale = Vector3.one * (radius / stockRadius); scale[axle] = width / sb.size[axle];
                // Radially the stock tyre is stretched so its hole matches the model tyre's (scaling it whole left the game tyre's
                // hole smaller than this rim: the rim lip cut through the sidewall as a jagged ring, user's picture 2026-11-10).
                int a1 = (axle + 1) % 3, a2 = (axle + 2) % 3;
                float Rad(Vector3 q) => Mathf.Sqrt(q[a1] * q[a1] + q[a2] * q[a2]);
                float stockIn = pts.Min(q => Rad(q - sb.center)), stockOut = pts.Max(q => Rad(q - sb.center));
                var (modelIn, modelOut) = RadialRange(modelTyre, mb.center);
                var (rimIn, rimOut) = RadialRange(modelRim, mb.center);
                float inner = modelIn;
                Vector3 Place(Vector3 q)
                {
                    var d = q - sb.center; float r0 = Rad(d);
                    float r1 = inner + (r0 - stockIn) / Mathf.Max(1e-5f, stockOut - stockIn) * (radius - inner);
                    var p = d * (r0 > 1e-6f ? r1 / r0 : 1f); p[axle] = d[axle] * scale[axle];
                    return p;
                }
                Log.Info("car07: game tyre radial: stock hole " + (stockIn / stockOut).ToString("F3") + " of its radius; model tyre " + modelIn.ToString("F3") + "-" + modelOut.ToString("F3") +
                    ", rim out to " + rimOut.ToString("F3") + "; tyre now " + inner.ToString("F3") + "-" + radius.ToString("F3") + " (whole scaling gave a hole of " + (stockIn * radius / stockOut).ToString("F3") + ")");
                var toTarget = target.worldToLocalMatrix * tyreItem.localToWorldMatrix;
                var mesh = new Mesh { name = "Rossa game tyre", indexFormat = sm.indexFormat };
                mesh.SetVertices(pts.Select(q => toTarget.MultiplyPoint3x4(Place(q))).ToList());
                mesh.SetNormals(sm.normals.Select(n => toTarget.MultiplyVector(toItem.MultiplyVector(n)).normalized).ToList());
                if (sm.uv.Length == sm.vertexCount) mesh.SetUVs(0, sm.uv.ToList());
                mesh.subMeshCount = sm.subMeshCount;
                bool flip = (toTarget * toItem).determinant < 0 != (sr.transform.localToWorldMatrix.determinant < 0);
                for (int i = 0; i < sm.subMeshCount; i++)
                {
                    var tri = sm.GetTriangles(i);
                    if (flip) for (int k = 0; k + 2 < tri.Length; k += 3) { int x = tri[k + 1]; tri[k + 1] = tri[k + 2]; tri[k + 2] = x; }
                    mesh.SetTriangles(tri, i);
                }
                mesh.RecalculateBounds();
                target.GetComponent<MeshFilter>().sharedMesh = mesh;
                var r = target.GetComponent<MeshRenderer>(); r.sharedMaterials = sr.sharedMaterials; r.enabled = true;
                Log.Info("car07: game tyre " + stock.name + " scaled to radius " + radius.ToString("F3") + " width " + width.ToString("F3") + " (model tyre " + mb.size.x.ToString("F3") + ", rim " + rb.size.x.ToString("F3") + ")");
            }

            // A wear/paint script set up exactly like another (every setting, not its renderer and child lists): a part then
            // takes colour, wear and inheritance from its car the way that part does (copying only the paint type left
            // bumpers with an empty colour, drawn fully transparent, when a car was spawned normally).
            private static void CopyCondition(partconditionscript src, partconditionscript dst)
            {
                if (!src || !dst) return;
                var skip = new HashSet<string> { "renderers", "childs", "randomChildsSet", "headlight", "loaded", "started", "refresh",
                    "mNew", "mUsed", "mMiddle", "mOld", "mRusty", "selectedMaterial" };
                foreach (var f in typeof(partconditionscript).GetFields(System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.Public))
                {
                    if (skip.Contains(f.Name)) continue;
                    var v = f.GetValue(src);
                    if (v is System.Collections.IList list && !(v is Array))
                    {
                        var fresh = (System.Collections.IList)Activator.CreateInstance(f.FieldType);
                        foreach (var x in list) fresh.Add(x);
                        v = fresh;
                    }
                    else if (v is Array arr) v = arr.Clone();
                    f.SetValue(dst, v);
                }
                // Materials a prefab ships already made for a wear state win over the game's material for that state: Car07's
                // tyre had TireRubber at full brightness there, so our tyres drew grey instead of the game's tinted Tire0New.
                // Cleared, the part builds them from the game's table like the source does.
                dst.mNew = dst.mUsed = dst.mMiddle = dst.mOld = dst.mRusty = null;
            }

            // An item's random-variant picker (randomTypeSelector) whose variants don't all show the renderer carrying the
            // model part: every variant becomes the one that does. Car07's bumpers come chrome ("front bumper") or black
            // ("front bumper black"), chosen at random on spawn; the model part is on the chrome one, so every other spawn
            // had no bumper (user 2026-11-10). The variant count stays, so saved and synced variant numbers still work.
            private static void OneVariant(Transform item, Transform main)
            {
                foreach (var sel in item.GetComponentsInChildren<randomTypeSelector>(true))
                {
                    if (sel.tipusok == null || sel.tipusok.Length < 2) continue;
                    int keep = Array.FindIndex(sel.tipusok, tp => tp != null && tp.objects != null && tp.objects.Any(o => o && main.IsChildOf(o.transform)));
                    if (keep < 0) continue;
                    var k = sel.tipusok[keep];
                    for (int i = 0; i < sel.tipusok.Length; i++)
                    {
                        if (i == keep) continue;
                        var tp = sel.tipusok[i];
                        tp.objects = k.objects; tp.Move = k.Move; tp.Target = k.Target; tp.R = k.R; tp.M = k.M;
                        tp.partslots = k.partslots; tp.partss = k.partss; tp.tank = k.tank; tp.fluid = k.fluid;
                    }
                    Log.Info("car07: " + item.name + ": all " + sel.tipusok.Length + " random variants of " + sel.name + " now show " + main.name + " (variant " + keep + ")");
                }
            }

            private static void Copy(Transform from, Transform to)
            {
                to.GetComponent<MeshFilter>().sharedMesh = from.GetComponent<MeshFilter>().sharedMesh;
                to.GetComponent<MeshRenderer>().sharedMaterials = from.GetComponent<MeshRenderer>().sharedMaterials;
                to.GetComponent<MeshRenderer>().enabled = true;
                foreach (Transform c in from) if (c.name.StartsWith("Rossa ")) { var copy = Object.Instantiate(c.gameObject, to, false); copy.name = c.name; }
            }

            // "fuelFlap": the model's own flap on Car07's fuel door: the door (and its closed/open markers, which the game
            // moves it between) set at the flap's front edge, turned so the flap's outward face is the door's -x and its
            // front edge the door's turning axis, then opened "angle" degrees about that axis like Car07's.
            private Vector3 flapCentre, flapNormal;
            private void FuelFlap(Transform root, Dictionary<string, Material> palette)
            {
                if (!cfg.ContainsKey("fuelFlap")) return;
                var spec = Obj(cfg, "fuelFlap");
                var part = Part(Str(spec, "part"));
                var pts = new List<Vector3>(); var n = Vector3.zero;
                foreach (var f in part.GetComponentsInChildren<MeshFilter>(true))
                {
                    var m = model.worldToLocalMatrix * f.transform.localToWorldMatrix; var v = f.sharedMesh.vertices; var t = f.sharedMesh.triangles;
                    pts.AddRange(v.Select(x => m.MultiplyPoint3x4(x) + offset));
                    for (int i = 0; i + 2 < t.Length; i += 3)
                    {
                        Vector3 a = m.MultiplyPoint3x4(v[t[i]]), b = m.MultiplyPoint3x4(v[t[i + 1]]), c = m.MultiplyPoint3x4(v[t[i + 2]]);
                        var cr = Vector3.Cross(b - a, c - a); n += Vector3.Dot(cr, n) < 0 ? -cr : cr;
                    }
                }
                var centre = pts.Aggregate(Vector3.zero, (s, p) => s + p) / pts.Count;
                n = n.normalized; if (n.x * centre.x < 0) n = -n;   // outward: away from the middle
                var fwd = Vector3.ProjectOnPlane(Vector3.forward, n).normalized;
                var hinge = centre + fwd * pts.Max(p => Vector3.Dot(p - centre, fwd));
                var turn = root.rotation * Quaternion.LookRotation(fwd, Vector3.Cross(fwd, -n));
                var door = Find(root, Str(spec, "door"));
                foreach (var t in new[] { door, Find(root, Str(spec, "closed")) }) t.SetPositionAndRotation(root.TransformPoint(hinge), turn);
                Find(root, Str(spec, "open")).SetPositionAndRotation(root.TransformPoint(hinge), turn * Quaternion.Euler(0, Convert.ToSingle(spec["angle"], IC), 0));
                Swap(new[] { part }, root, door, palette, own: false);
                flapCentre = centre; flapNormal = n;
                Log.Info("car07: fuel flap at " + hinge.ToString("F3") + " facing " + n.ToString("F2"));
            }

            // Every solid collider of the car and its parts (seats, lamps, doors, hitch, body boxes; not the wheels) kept above
            // "collisionFloor": a box reaching below it is cut off at the floor, one wholly below it is switched off. Car07's
            // seat boxes, lowered into the low cabin, poked out under the floor and struck the ground hardest of all.
            private void ClipToFloor(Transform root)
            {
                if (!Floor().HasValue) return;
                float floor = Floor().Value; int cut = 0, off = 0, lifted = 0;
                foreach (var c in root.GetComponentsInChildren<Collider>(true))
                {
                    if (c.isTrigger || c is WheelCollider) continue;   // also ones the game switches on later (seat boxes)
                    bool wheel = false;
                    for (var x = c.transform; x && x != root; x = x.parent) if (x.parent && x.parent.name == "CarStuff" && x.name.Length == 6 && x.name.EndsWith("ROT")) { wheel = true; break; }
                    if (wheel) continue;
                    var m = root.worldToLocalMatrix * c.transform.localToWorldMatrix;
                    bool interact = c.GetComponent<seatscript>() || c.GetComponent<usablescript>() || c.GetComponent<partslotscript>() || c.GetComponent<tankcapscript>();
                    if (c is BoxCollider b)
                    {
                        // the box's corners in car space
                        float lo = float.MaxValue, hi = float.MinValue;
                        for (int i = 0; i < 8; i++)
                        {
                            var y = m.MultiplyPoint3x4(b.center + Vector3.Scale(b.size / 2, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1))).y;
                            lo = Mathf.Min(lo, y); hi = Mathf.Max(hi, y);
                        }
                        if (lo >= floor) continue;
                        if (interact)
                        {
                            // what you aim at to sit down or open something keeps its size, lifted to the floor; only the
                            // box moves (moving its object also lifted the seat's sitting point: heads through the roof)
                            b.center += c.transform.InverseTransformVector(root.up * (floor - lo)); lifted++;
                            continue;
                        }
                        if (hi <= floor + .01f) { b.enabled = false; off++; continue; }
                        // which local axis is the car's up (boxes are mostly square to the car)
                        var up = root.InverseTransformDirection(c.transform.up); var right = root.InverseTransformDirection(c.transform.right); var fwd = root.InverseTransformDirection(c.transform.forward);
                        int axis = Mathf.Abs(up.y) >= Mathf.Abs(right.y) && Mathf.Abs(up.y) >= Mathf.Abs(fwd.y) ? 1 : Mathf.Abs(right.y) >= Mathf.Abs(fwd.y) ? 0 : 2;
                        var dir = axis == 1 ? up : axis == 0 ? right : fwd;
                        float scale = c.transform.lossyScale[axis];
                        if (Mathf.Abs(dir.y) < .9f) { b.enabled = false; off++; continue; }   // tilted: no clean cut, leave it out
                        float keep = (hi - floor) / (hi - lo);
                        var size = b.size; var centre = b.center;
                        float newSize = size[axis] * keep, shift = (size[axis] - newSize) / 2 * Mathf.Sign(dir.y);
                        size[axis] = newSize; centre[axis] += shift;
                        b.size = size; b.center = centre; cut++;
                    }
                    else
                    {
                        var bb = c.bounds; float lo = root.InverseTransformPoint(bb.min).y;
                        if (lo < floor) { c.enabled = false; off++; }
                    }
                }
                Log.Info("car07: " + root.name + ": collision floor " + floor.ToString("F2") + ": " + cut + " colliders cut, " + off + " switched off, " + lifted + " click boxes lifted");
            }

            // "gauges": working needles on the model's printed dials. Car07's gauge item stays (the game feeds its meter script
            // speed, revs, fuel and temperature) but draws nothing of its own; each dial gets a plain needle the same meter
            // script drives, turning about the dial's centre in the dial's face (clockwise as the driver sees it).
            private void Gauges(Transform root, bool full)
            {
                if (!full || !cfg.ContainsKey("gauges")) return;
                var g = Obj(cfg, "gauges");
                var item = Find(root, Str(g, "item"));
                var meter = item.GetComponent<meterscript>();
                var proto = meter.mutatok.Select(x => x.mutato).First(x => x);
                foreach (var r in item.GetComponentsInChildren<Renderer>(true)) r.enabled = false;
                foreach (var old in item.GetComponentsInChildren<mutatoscript>(true)) old.enabled = false;
                var normal = root.TransformDirection(Vec(g["normal"])).normalized;   // the dial face, towards the driver
                var list = new List<meterscript.meterstuff>();
                foreach (Dictionary<string, object> d in Arr(g, "dials"))
                {
                    var pivot = new GameObject("Rossa dial " + Str(d, "tipus")).transform;
                    pivot.SetParent(item, false);
                    pivot.SetPositionAndRotation(root.TransformPoint(Vec(d["at"]) + offset) + normal * .004f, Quaternion.LookRotation(-normal, root.up));
                    var turn = new GameObject("needle pivot").transform; turn.SetParent(pivot, false);
                    float length = Convert.ToSingle(d["length"], IC), w = Convert.ToSingle(g["width"], IC);
                    var needle = new GameObject("needle"); needle.transform.SetParent(turn, false); needle.layer = item.gameObject.layer;
                    var mesh = new Mesh { name = "Rossa needle" };
                    mesh.vertices = new[] { new Vector3(-w / 2, -.18f * length, 0), new Vector3(w / 2, -.18f * length, 0), new Vector3(w / 4, length, 0), new Vector3(-w / 4, length, 0) };
                    mesh.triangles = new[] { 0, 2, 1, 0, 3, 2, 0, 1, 2, 0, 2, 3 };
                    mesh.RecalculateNormals(); mesh.RecalculateBounds();
                    needle.AddComponent<MeshFilter>().sharedMesh = mesh;
                    var mr = needle.AddComponent<MeshRenderer>(); mr.sharedMaterial = proto.OffM;
                    var ms = turn.gameObject.AddComponent<mutatoscript>();
                    ms.OffM = proto.OffM; ms.OnM = proto.OnM; ms.R = mr; ms.P = meter; ms.i = list.Count; ms.mutato = turn;
                    ms.Z = true; ms.minAngle = Convert.ToSingle(d["minAngle"], IC); ms.maxAngle = Convert.ToSingle(d["maxAngle"], IC);
                    ms.minValue = Convert.ToSingle(d["minValue"], IC); ms.maxValue = Convert.ToSingle(d["maxValue"], IC); ms.lerpSpeedMultiplier = 1;
                    ms.z = ms.minAngle; turn.localEulerAngles = new Vector3(0, 0, ms.minAngle);
                    list.Add(new meterscript.meterstuff { tipus = Str(d, "tipus"), mutato = ms, value = 0 });
                }
                meter.mutatok = list;
                item.GetComponent<tosaveitemscript>().id = Int(g["id"]);
                Log.Info("car07: " + list.Count + " gauge needles on the model's dials");
            }

            // "popups": each headlight slot (with its lamp item) and its painted cover hang from a new pivot at the pod's rear
            // edge; PopUpLights swings them up while the light switch is on. The covers are painted with the body.
            private void PopUps(Transform root, Dictionary<string, Material> palette)
            {
                if (!cfg.ContainsKey("popups")) return;
                var spec = Obj(cfg, "popups");
                var pods = new List<Transform>(); var down = new List<Quaternion>();
                var paint = root.GetComponent<partconditionscript>();
                foreach (Dictionary<string, object> p in Arr(spec, "pods"))
                {
                    var slot = Find(root, Str(p, "slot"));
                    var cover = Part(Str(p, "cover")); var b = Bounds(cover); b.center += offset;
                    var pivot = new GameObject("Rossa popup " + Str(p, "cover")).transform;
                    pivot.SetParent(slot.parent, false);
                    pivot.SetPositionAndRotation(root.TransformPoint(new Vector3(b.center.x, b.max.y - .01f, b.min.z)), root.rotation);
                    slot.SetParent(pivot, true);
                    // The lamp's click/collision box is Car07's (a metre across, over most of the front lid, so aiming at
                    // the lid grabbed the headlight): shrunk to the lamp itself, 2 cm proud of it.
                    var lampMeshes = slot.GetComponentsInChildren<MeshFilter>().Where(f => f.sharedMesh && f.sharedMesh.isReadable && f.GetComponent<Renderer>() && f.GetComponent<Renderer>().enabled).ToArray();
                    foreach (var box in slot.GetComponentsInChildren<BoxCollider>(true).Where(x => !x.isTrigger && !x.GetComponent<partslotscript>()))
                    {
                        if (lampMeshes.Length == 0) break;
                        Vector3 lo = Vector3.positiveInfinity, hi = Vector3.negativeInfinity;
                        foreach (var f in lampMeshes)
                            foreach (var v in f.sharedMesh.vertices)
                            { var q = box.transform.InverseTransformPoint(f.transform.TransformPoint(v)); lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q); }
                        var s = box.transform.lossyScale; var pad = new Vector3(.02f / Mathf.Abs(s.x), .02f / Mathf.Abs(s.y), .02f / Mathf.Abs(s.z));
                        box.center = (lo + hi) / 2; box.size = hi - lo + pad * 2;
                        Log.Info("car07: " + root.name + ": lamp box " + box.name + " in " + slot.name + " fitted to the lamp: centre " + box.center.ToString("F3") + " size " + box.size.ToString("F3") + " scale " + box.transform.lossyScale.ToString("F3") + " from " + lampMeshes.Length + " meshes, local lo " + lo.ToString("F3") + " hi " + hi.ToString("F3"));
                    }
                    var lid = new GameObject("Rossa popup cover") { layer = root.gameObject.layer }.transform;
                    lid.SetParent(pivot, false); lid.localPosition = Vector3.zero; lid.localRotation = Quaternion.identity;
                    Swap(new[] { cover }, root, lid, palette, own: false);
                    var r = lid.GetComponent<MeshRenderer>();
                    if (paint && paint.renderers != null && r) paint.renderers.Add(r);
                    pods.Add(pivot); down.Add(pivot.localRotation);
                }
                var lights = root.gameObject.AddComponent<PopUpLights>();
                lights.car = root.GetComponent<carscript>(); lights.pods = pods.ToArray(); lights.down = down.ToArray();
                lights.angle = Convert.ToSingle(spec["angle"], IC);
            }

            // "mounts": Car07's storage volumes moved and resized (box in model space) and tied to another lid's slot (the
            // trunk storage into the front trunk under the hood, the engine-bay storage under the rear lid).
            private void Mounts(Transform root)
            {
                if (!cfg.ContainsKey("mounts")) return;
                foreach (Dictionary<string, object> m in Arr(cfg, "mounts"))
                {
                    var node = Find(root, Str(m, "node")); var ms = node.GetComponent<mountStuff>();
                    Vector3 lo = Vec(m["min"]) + offset, hi = Vec(m["max"]) + offset;
                    node.SetPositionAndRotation(root.TransformPoint((lo + hi) / 2), root.rotation);
                    if (Str(m, "slot") != null) ms.slot = new[] { Find(root, Str(m, "slot")).GetComponent<partslotscript>() };
                    if (ms.col)
                    {
                        var c = ms.col.transform; c.localPosition = Vector3.zero; c.localRotation = Quaternion.identity; c.localScale = hi - lo;
                        ms.col.center = Vector3.zero; ms.col.size = Vector3.one;
                    }
                }
            }


            // Bodywork collision: Car07's plain body boxes (layer 0) and its glass boxes (roof and side windows of a
            // saloon) go; boxes fitted to the Rossa's body and tub take their place on the same layer and physic material,
            // with the cockpit, seats and wheels cut out (the native kit's fitter and the Rossa definition's settings), plus
            // one glass box for the windscreen. Car07's interior, tank-cap, help-text, seat and slot colliders stay.
            private void Collision(Transform root)
            {
                // Settings: the config's "collision" block.
                var settings = Obj(cfg, "collision");
                float Num(string k) => Convert.ToSingle(settings[k], IC);
                Vector3 Point(string k) => Vec(settings[k]);
                float[] Grid() => ((List<object>)settings["collider_grid"]).Select(x => Convert.ToSingle(x, IC)).ToArray();
                var colliders = Find(root, "Colliders");
                var plain = colliders.GetComponentsInChildren<BoxCollider>(true)
                    .Where(b => !b.isTrigger && (b.gameObject.layer == 0 || b.gameObject.layer == 23) && b.GetComponents<Component>().Length == 2).ToList();
                var bodyLeaf = plain.FirstOrDefault(b => b.gameObject.layer == 0) ?? throw new InvalidOperationException("Car07 has no plain body box");
                var glassLeaf = plain.FirstOrDefault(b => b.gameObject.layer == 23);
                int bodyLayer = bodyLeaf.gameObject.layer; var bodyMaterial = bodyLeaf.sharedMaterial;
                int glassLayer = glassLeaf ? glassLeaf.gameObject.layer : 23; var glassMaterial = glassLeaf ? glassLeaf.sharedMaterial : null;
                foreach (var b in plain) Object.DestroyImmediate(b.gameObject);

                // The Rossa body and tub as triangles in car space (glass and chrome trim excluded: not solid bodywork).
                var triangles = new List<BodyColliderGeometry.Triangle>();
                var glass = new List<Vector3>();
                var glassPaint = Palette(root)["glass"];
                foreach (var f in Find(root, "car07/body").GetComponentsInChildren<MeshFilter>(true).Concat(Find(root, "car07/cockpit").GetComponentsInChildren<MeshFilter>(true)))
                {
                    var r = f.GetComponent<MeshRenderer>(); if (!r || !r.enabled || !f.sharedMesh) continue;
                    var material = r.sharedMaterial; var m = root.worldToLocalMatrix * f.transform.localToWorldMatrix;
                    var v = f.sharedMesh.vertices; var t = f.sharedMesh.triangles;
                    bool isGlass = material && (material == glassPaint || material.name.ToLowerInvariant().Contains("glass"));
                    for (int i = 0; i + 2 < t.Length; i += 3)
                    {
                        Vector3 a = m.MultiplyPoint3x4(v[t[i]]), b = m.MultiplyPoint3x4(v[t[i + 1]]), c = m.MultiplyPoint3x4(v[t[i + 2]]);
                        if (!isGlass) triangles.Add(new BodyColliderGeometry.Triangle(a, b, c));
                    }
                }
                // The windscreen box from the model's "glass" part alone (a closed car's body also carries rear and side
                // glass; one box round all of it would fill the cabin).
                foreach (var f in Part("glass").GetComponentsInChildren<MeshFilter>(true))
                {
                    var m = model.worldToLocalMatrix * f.transform.localToWorldMatrix;
                    glass.AddRange(f.sharedMesh.vertices.Select(v => m.MultiplyPoint3x4(v) + offset));
                }
                var all = triangles.SelectMany(x => new[] { x.A, x.B, x.C }).ToList();
                var bounds = BodyColliderGeometry.BoundsOf(all);
                if (Floor().HasValue) { var lo = bounds.min; lo.y = Mathf.Max(lo.y, Floor().Value); bounds.SetMinMax(lo, bounds.max); }
                var holes = new List<Bounds> { BodyColliderGeometry.Box(Point("cockpit_min") + offset, Point("cockpit_max") + offset) };
                // Seats left in use (the library is inactive, so "active" means active under the Seats group).
                bool InUse(Transform t) { for (; t && t.name != "Seats"; t = t.parent) if (!t.gameObject.activeSelf) return false; return true; }
                foreach (var seat in Find(root, "Seats").GetComponentsInChildren<BoxCollider>(true).Where(b => b.GetComponent<seatscript>() && InUse(b.transform)))
                {
                    var c = new Bounds(root.InverseTransformPoint(seat.transform.TransformPoint(seat.center)), Vector3.Scale(seat.size, seat.transform.lossyScale));
                    c.Expand(Num("seat_clearance")); holes.Add(c);
                }
                var wheelClearance = Point("wheel_clearance");
                float radius = Bounds(Part("Tire")).extents.y;
                foreach (var w in root.GetComponentsInChildren<WheelCollider>(true).Where(w => !w.name.Contains("KeepAwake")))
                {
                    var p = Local(root, w.transform); p.y += w.center.y - w.suspensionDistance * (1 - w.suspensionSpring.targetPosition);
                    holes.Add(new Bounds(p, new Vector3(wheelClearance.x, radius * 2 + wheelClearance.y, radius * 2 + wheelClearance.z)));
                }
                // the space the removable panels take ("cells" fits): no body box there
                foreach (var pb in partRoom.Where(x => x && x.transform.IsChildOf(root)))
                {
                    var lo = Vector3.positiveInfinity; var hi = Vector3.negativeInfinity;
                    for (int i = 0; i < 8; i++)
                    {
                        var q = root.InverseTransformPoint(pb.transform.TransformPoint(pb.center + Vector3.Scale(pb.size * .5f, new Vector3((i & 1) * 2 - 1, (i & 2) - 1, (i & 4) / 2 - 1))));
                        lo = Vector3.Min(lo, q); hi = Vector3.Max(hi, q);
                    }
                    var room = BodyColliderGeometry.Box(lo, hi); room.Expand(.01f); holes.Add(room);
                }
                if (partRoom.Any(x => x && x.transform.IsChildOf(root))) Log.Info("car07: " + root.name + ": " + partRoom.Count + " panel boxes kept clear of the body boxes");
                var parent = new GameObject("Rossa body boxes") { layer = bodyLayer }.transform;
                parent.SetParent(colliders, false);
                var grid = Grid(); int count = 0;
                for (int x = 0; x < grid[0]; x++)
                    for (int y = 0; y < grid[1]; y++)
                        for (int z = 0; z < grid[2]; z++)
                        {
                            Vector3 lo = bounds.min + Vector3.Scale(bounds.size, new Vector3(x / grid[0], y / grid[1], z / grid[2]));
                            Vector3 hi = bounds.min + Vector3.Scale(bounds.size, new Vector3((x + 1) / grid[0], (y + 1) / grid[1], (z + 1) / grid[2]));
                            var cells = new List<Bounds> { BodyColliderGeometry.Box(lo, hi) };
                            foreach (var hole in holes) cells = cells.SelectMany(c => BodyColliderGeometry.Subtract(c, hole)).ToList();
                            foreach (var cell in cells)
                            {
                                if (cell.size.x < Num("collider_min") || cell.size.y < Num("collider_min") || cell.size.z < Num("collider_min")) continue;
                                var points = new List<Vector3>();
                                foreach (var triangle in triangles)
                                {
                                    var polygon = BodyColliderGeometry.Clip(triangle, cell);
                                    for (int i = 1; i + 1 < polygon.Count; i++) { points.Add(polygon[0]); points.Add(polygon[i]); points.Add(polygon[i + 1]); }
                                }
                                if (points.Count < 3) continue;
                                AddBox(parent, "Rossa body " + count++, ColliderBoxGeometry.Fit(points, cell, Num("collider_skin"), holes), bodyLayer, bodyMaterial, root);
                            }
                        }
                if (glass.Count >= 3)
                {
                    var cell = BodyColliderGeometry.BoundsOf(glass); cell.Expand(.02f);
                    AddBox(parent, "Rossa windscreen", ColliderBoxGeometry.Fit(glass, cell, Num("collider_skin")), glassLayer, glassMaterial, root);
                }
                Log.Info("car07: " + root.name + ": " + plain.Count + " Car07 body/glass boxes replaced by " + count + " fitted Rossa boxes" + (glass.Count >= 3 ? " + windscreen" : ""));
            }

            private static void AddBox(Transform parent, string name, ColliderBoxGeometry.Shape shape, int layer, PhysicMaterial material, Transform root)
            {
                var go = new GameObject(name) { layer = layer };
                go.transform.SetParent(parent, false);
                go.transform.position = root.TransformPoint(shape.Center);
                go.transform.rotation = root.rotation * Quaternion.LookRotation(shape.Forward, shape.Up);
                var box = go.AddComponent<BoxCollider>(); box.size = shape.Size; box.sharedMaterial = material;
            }

            // Smallest and largest distance of a part's vertices from the axle (model x) through centre.
            private (float, float) RadialRange(Transform part, Vector3 centre)
            {
                float lo = float.MaxValue, hi = 0;
                foreach (var f in part.GetComponentsInChildren<MeshFilter>(true))
                {
                    var m = model.worldToLocalMatrix * f.transform.localToWorldMatrix;
                    foreach (var v in f.sharedMesh.vertices)
                    {
                        var p = m.MultiplyPoint3x4(v) - centre; float r = Mathf.Sqrt(p.y * p.y + p.z * p.z);
                        lo = Mathf.Min(lo, r); hi = Mathf.Max(hi, r);
                    }
                }
                return (lo, hi);
            }

            private Bounds Bounds(Transform part)
            {
                Bounds? b = null;
                foreach (var f in part.GetComponentsInChildren<MeshFilter>(true))
                {
                    var m = model.worldToLocalMatrix * f.transform.localToWorldMatrix;
                    foreach (var v in f.sharedMesh.vertices)
                    {
                        var p = m.MultiplyPoint3x4(v);
                        if (b == null) b = new Bounds(p, Vector3.zero); else { var x = b.Value; x.Encapsulate(p); b = x; }
                    }
                }
                return b ?? throw new InvalidOperationException("Empty part " + part.name);
            }

            // One mesh with a submesh per source primitive, in the target node's space, the Rossa model moved onto Car07's
            // ground and front axle. Materials: Car07's of each primitive's kind, or the target's own (lamps, whose
            // scripts swap that material on and off).
            private void Swap(Transform[] parts, Transform carRoot, Transform target, Dictionary<string, Material> palette, bool own, Matrix4x4? place = null, string lamp = null)
            {
                var targetRenderer = target.GetComponent<MeshRenderer>();
                var ownMaterial = targetRenderer ? targetRenderer.sharedMaterial : null;
                var toTarget = target.worldToLocalMatrix * carRoot.localToWorldMatrix * Matrix4x4.Translate(offset) * (place ?? Matrix4x4.identity) * model.worldToLocalMatrix;
                var vertices = new List<Vector3>(); var normals = new List<Vector3>(); var uvs = new List<Vector2>();
                var subs = new List<int[]>(); var materials = new List<Material>();
                var bothSides = new HashSet<string>(Arr(cfg, "bothSides").Cast<string>());
                foreach (var part in parts)
                foreach (var f in part.GetComponentsInChildren<MeshFilter>(true).Where(f => f.sharedMesh))
                {
                    var m = toTarget * f.transform.localToWorldMatrix;
                    var mesh = f.sharedMesh; int start = vertices.Count, count = mesh.vertexCount;
                    var v = mesh.vertices.Select(x => m.MultiplyPoint3x4(x)).ToList();
                    var n = mesh.normals; var nn = (n.Length == count ? n.Select(x => m.MultiplyVector(x).normalized) : Enumerable.Repeat(Vector3.up, count)).ToList();
                    var uv = mesh.uv; var uu = (uv.Length == count ? uv : new Vector2[count]).ToList();
                    // Single-layer skins get a reversed copy with its own flipped normals, so the inside is drawn too.
                    bool both = bothSides.Contains(part.name);
                    vertices.AddRange(v); normals.AddRange(nn); uvs.AddRange(uu);
                    if (both) { vertices.AddRange(v); normals.AddRange(nn.Select(x => -x)); uvs.AddRange(uu); }
                    for (int sub = 0; sub < mesh.subMeshCount; sub++)
                    {
                        var t = mesh.GetTriangles(sub);
                        var tris = t.Select(i => i + start);
                        if (both) tris = tris.Concat(Enumerable.Range(0, t.Length / 3).SelectMany(k => new[] { t[k * 3] + start + count, t[k * 3 + 2] + start + count, t[k * 3 + 1] + start + count }));
                        subs.Add(tris.ToArray());
                        var source = f.GetComponent<Renderer>()?.sharedMaterials.ElementAtOrDefault(sub);
                        // "lamp": only primitives of that source material take the lamp's own material (the rest map as usual).
                        bool lampSub = lamp != null && source && source.name.Contains(lamp);
                        materials.Add((own && lamp == null || lampSub) && ownMaterial ? ownMaterial : Pick(palette, source));
                    }
                }
                string name = "Rossa " + string.Join("+", parts.Select(p => p.name));
                Mesh Build(List<int[]> tris)
                {
                    var mesh = new Mesh { name = name, indexFormat = vertices.Count > 65535 ? UnityEngine.Rendering.IndexFormat.UInt32 : UnityEngine.Rendering.IndexFormat.UInt16 };
                    mesh.SetVertices(vertices); mesh.SetNormals(normals); mesh.SetUVs(0, uvs);
                    // A mirrored node (Car07's left door is the right one scaled by -1) draws its triangles turned round:
                    // the mesh is built in its space from car-space points, so its winding is reversed to stay outward.
                    var index = tris.SelectMany(t => t).ToArray();
                    if (target.localToWorldMatrix.determinant < 0) for (int k = 0; k + 2 < index.Length; k += 3) { int x = index[k + 1]; index[k + 1] = index[k + 2]; index[k + 2] = x; }
                    mesh.subMeshCount = 1; mesh.SetTriangles(index, 0);
                    mesh.RecalculateBounds(); mesh.RecalculateTangents();
                    return mesh;
                }
                // One material per renderer: the game's condition code replaces a renderer's whole material list with
                // one material (partconditionscript.cs), which would drop every other submesh. The biggest group stays on
                // the game's node (paint, condition); each other material gets a child renderer of its own.
                // The node keeps the lamp material (lamps) or, with "paintFirst", the paint (the game paints and wears that renderer).
                bool paintFirst = cfg.ContainsKey("paintFirst") && (bool)cfg["paintFirst"] || ownMaterial && ownMaterial == palette["paint"];
                var groups = Enumerable.Range(0, subs.Count).GroupBy(i => materials[i])
                    .OrderByDescending(g => lamp != null && g.Key == ownMaterial && ownMaterial ? 2 : paintFirst && g.Key == palette["paint"] ? 1 : 0)
                    .ThenByDescending(g => g.Sum(i => subs[i].Length)).ToList();
                // A model's own textured material never goes on the game's node: spawning resets that renderer's material
                // (wear/paint), which turned a textured interior into plain paint. The node then draws nothing itself.
                bool modelOwn = cfg.ContainsKey("materials") && !palette.Values.Contains(groups[0].Key) && groups[0].Key != ownMaterial;
                var filter = target.GetComponent<MeshFilter>() ?? target.gameObject.AddComponent<MeshFilter>();
                filter.sharedMesh = modelOwn ? new Mesh { name = name + " (children draw)" } : Build(groups[0].Select(i => subs[i]).ToList());
                var renderer = targetRenderer ?? target.gameObject.AddComponent<MeshRenderer>();
                renderer.sharedMaterials = new[] { groups[0].Key }; renderer.enabled = true;
                foreach (var g in modelOwn ? groups : groups.Skip(1))
                {
                    var child = new GameObject("Rossa " + (g.Key ? g.Key.name : "none")) { layer = target.gameObject.layer };
                    child.transform.SetParent(target, false);
                    child.AddComponent<MeshFilter>().sharedMesh = Build(g.Select(i => subs[i]).ToList());
                    var r = child.AddComponent<MeshRenderer>();
                    r.sharedMaterials = new[] { g.Key }; r.shadowCastingMode = renderer.shadowCastingMode;
                    // it wears with the part it sits on (glass and lamps stay clear, as the game's do)
                    if (g.Key && g.Key != palette["glass"] && !Clear(g.Key.name)) { var w = child.AddComponent<WearLayer>(); w.own = g.Key; w.tipus = WearLike(g.Key.name); }
                }
            }
        }

        // Car07's own materials by kind, so the swapped meshes take the game's condition and paint materials.
        private static Dictionary<string, Material> Palette(Transform root)
        {
            Material M(string node) => root.Find(node)?.GetComponent<Renderer>()?.sharedMaterial;
            return new Dictionary<string, Material> {
                { "paint", M("car07/body") }, { "leather", M("car07/seats") }, { "dark", M("car07/Cube.020") },
                { "chrome", M("car07/chrome front") }, { "glass", M("car07/window outer") }, { "tire", null } };
        }

        private static Material Pick(Dictionary<string, Material> palette, string source)
        {
            string s = source.ToLowerInvariant();
            string kind = s.Contains("car07") || s.Contains("paint") ? "paint" : s.Contains("leather") ? "leather"
                : s.Contains("windshield") || s.Contains("glass") ? "glass" : s.Contains("chrome") || s.Contains("grille") || s.Contains("reflector") || s.Contains("rim") ? "chrome"
                : "dark";
            return palette[kind] ?? palette["paint"];
        }

        // The driver's surroundings: seats, controls, dash slots, cabin mounts and the interior/light sensors.
        private static readonly string[] Cabin = { "Seats", "SteeringWheelRot", "BrakeRot", "GasRot", "ClutchRot", "HandBrakeRot",
            "TransmissionRot", "Transmission", "LLPos", "RLPos", "DefLLPos", "MiddleLLPos", "MiddleRLPos", "PassengerMount", "GloveStoreMount",
            "Slots/RadioSlot", "Slots/KMHSlot", "Slots/KesztyutartoRot", "Slots/FrontMirrorCraft", "Slots/Napellenzo",
            "CarStuff/InteriorLight", "CarStuff/interior", "CarStuff/InteriorSens", "CarStuff/HelpText", "CarStuff/Interact" };
        private static bool InCabin(Transform root, Transform t)
        {
            string path = t.name;
            for (var x = t.parent; x && x != root; x = x.parent) path = x.name + "/" + path;
            return Cabin.Any(c => path.StartsWith(c));
        }

        // The item a node belongs to (GetComponentInParent skips the inactive library this is built in).
        private static Transform Owner(Transform t)
        {
            for (; t; t = t.parent) if (t.GetComponent<tosaveitemscript>()) return t;
            return null;
        }

        private static Transform Find(Transform root, string path) => root.Find(path) ?? throw new InvalidOperationException(root.name + " has no " + path);
        private static Vector3 Local(Transform root, Transform t) => root.InverseTransformPoint(t.position);

        // Piecewise-linear map through matching landmarks (ascending), slope 1 outside them.
        private sealed class LinearMap
        {
            private readonly float[] from, to;
            internal LinearMap(float[] from, float[] to) { this.from = from; this.to = to; }
            internal float Map(float z)
            {
                if (z <= from[0]) return to[0] + (z - from[0]);
                for (int i = 1; i < from.Length; i++)
                    if (z <= from[i]) return Mathf.Lerp(to[i - 1], to[i], Mathf.InverseLerp(from[i - 1], from[i], z));
                return to[to.Length - 1] + (z - from[from.Length - 1]);
            }
        }
    }
}
