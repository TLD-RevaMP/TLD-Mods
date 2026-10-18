using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using BepInEx;
using TMPro;
using UnityEngine;

namespace TerrainChoice
{
    /// Per-frame work on the mod's own object: puts the terrain dropdown into the game-mode settings whenever the
    /// game makes them (each scene load), shows a small fallback panel if that ever fails, and runs the unattended
    /// test (-tcdiag) used to check a build.
    internal class Runner : MonoBehaviour
    {
        private gamemodesettingsscript _doneFor;
        private TMP_Dropdown _dropdown;
        private bool _fallback;
        private float _next;

        private void Update()
        {
            if (Time.unscaledTime >= _next)
            {
                _next = Time.unscaledTime + 0.5f;
                var g = gamemodesettingsscript.s;
                if (g != null && g != _doneFor && g.dropdrops != null && g.dropdrops.Count > 1 && menuhandler.isStarted())
                {
                    _doneFor = g;
                    try { _fallback = !NewGameUi.Inject(g, out _dropdown, out _); }
                    catch (Exception e) { _fallback = true; Plugin.Log.LogWarning("New-game UI: " + e + "\nUsing the fallback panel."); }
                }
                // the config may change elsewhere (config manager, the file): keep the dropdown showing it
                if (_dropdown != null && _dropdown.value != (int)Plugin.NewGameTerrain.Value) _dropdown.SetValueWithoutNotify((int)Plugin.NewGameTerrain.Value);
            }
            Diag.Tick();
        }

        private void OnGUI()
        {
            if (!_fallback) return;
            var g = gamemodesettingsscript.s;
            if (g == null || g.dropdrops == null || g.dropdrops.Count < 2 || g.dropdrops[1] == null || !g.dropdrops[1].gameObject.activeInHierarchy) return;
            var r = new Rect(Screen.width / 2f - 170f, 10f, 340f, 60f);
            GUI.Box(r, "Terrain for new games (TerrainChoice)");
            for (int i = 0; i < NewGameUi.Options.Length; i++)
            {
                bool on = (int)Plugin.NewGameTerrain.Value == i;
                if (GUI.Toggle(new Rect(r.x + 10f + i * 165f, r.y + 28f, 160f, 24f), on, NewGameUi.Options[i], "button") && !on)
                    Plugin.NewGameTerrain.Value = (TerrainMode)i;
            }
        }
    }

    /// Unattended check of a build (test copies only): TheLongDrive.exe -tcdiag <crossroads|mainroadonly|oldterrain|load> [-tcseed N]
    /// Menu: dumps the settings UI and screenshots it. crossroads/mainroadonly: starts a new game with that terrain and
    /// the seed, waits, writes road statistics, takes screenshots, autosaves, quits. load: continues the last save,
    /// writes the same statistics and quits. Results: BepInEx/TerrainChoice-diag.txt and BepInEx/TerrainChoice-*.png.
    internal static class Diag
    {
        private static string _mode;
        private static int _seed = 12345;
        private static int _state;
        private static float _at;
        private static string Out => Path.Combine(Paths.BepInExRootPath, "TerrainChoice-diag.txt");

        static Diag()
        {
            var args = Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length - 1; i++)
            {
                if (args[i] == "-tcdiag") _mode = args[i + 1].ToLowerInvariant();
                if (args[i] == "-tcseed") int.TryParse(args[i + 1], out _seed);
            }
        }

        private static void Write(string s)
        {
            Plugin.Log.LogInfo("[diag] " + s);
            File.AppendAllText(Out, DateTime.Now.ToString("HH:mm:ss") + " " + s + Environment.NewLine);
        }

        public static void Tick()
        {
            if (_mode == null) return;
            try { Step(); }
            catch (Exception e) { Write("ERROR " + e); _mode = null; Application.Quit(); }
        }

        private static void Step()
        {
            float now = Time.realtimeSinceStartup;
            var d = DataFromMenuScript.s;
            switch (_state)
            {
                case 0:   // the main menu is up
                    if (d == null || !d.mainmenu || !menuhandler.isStarted() || gamemodesettingsscript.s == null) return;
                    _state = 1; _at = now + 6f;
                    Write("---- run " + _mode + " seed " + _seed + ", TerrainChoice " + Plugin.Version);
                    return;
                case 1:
                    if (now < _at) return;
                    if (_mode == "roadcheck") { RoadCheck(); _mode = null; Application.Quit(); return; }
                    MenuReport();
                    _state = 2; _at = now + 3f;
                    return;
                case 2:
                    if (now < _at) return;
                    ScreenCapture.CaptureScreenshot(Path.Combine(Paths.BepInExRootPath, "TerrainChoice-menu.png"));
                    _state = 3; _at = now + 2f;
                    return;
                case 3:
                    if (now < _at) return;
                    if (_mode == "load")
                    {
                        Write("continuing the last save: " + mainscript.GetLastSavePath());
                        menuhandler.s.AutoContinue();
                    }
                    else
                    {
                        Plugin.NewGameTerrain.Value = _mode == "mainroadonly" ? TerrainMode.MainRoadOnly : _mode == "oldterrain" ? TerrainMode.OldTerrain : TerrainMode.Crossroads;
                        d.seed = _seed;
                        Write("new game, terrain " + Plugin.NewGameTerrain.Value);
                        menuhandler.s.PressedStart();
                    }
                    _state = 4; _at = now + 600f;
                    return;
                case 4:   // in the world
                    if (d != null && !d.mainmenu && mainscript.s != null && mainscript.s.player != null && menuhandler.s != null && menuhandler.s.currentMainMap != null)
                    {
                        if (OldRoad.Path != null) Write("old road: start " + OldRoad.Path.Start + ", at z 0/1000/5000: " + OldRoad.Path.At(0) + " " + OldRoad.Path.At(1000) + " " + OldRoad.Path.At(5000));
                        Write("world loaded: terrain " + WorldPatches.Current + " (" + WorldPatches.CurrentSource + "), map '" + menuhandler.s.currentMainMap.mapClass.map.mapName + "'");
                        _state = 5; _at = now + 45f;
                    }
                    else if (now > _at) { Write("the world never loaded"); Application.Quit(); _mode = null; }
                    return;
                case 5:
                    if (now < _at) return;
                    RoadReport("after 45 s");
                    ScreenCapture.CaptureScreenshot(Path.Combine(Paths.BepInExRootPath, "TerrainChoice-" + _mode + "-ground.png"));
                    _state = 6; _at = now + 2f;
                    return;
                case 6:
                    if (now < _at) return;
                    LookFromAbove();
                    _state = 7; _at = now + (_aim != null ? 25f : 8f);
                    return;
                case 7:
                    if (now < _at) return;
                    if (_aim != null && _state == 7 && !_aimed) { _aimed = true; AimNow(); _at = now + 3f; return; }
                    ScreenCapture.CaptureScreenshot(Path.Combine(Paths.BepInExRootPath, "TerrainChoice-" + _mode + "-above.png"));
                    _state = 8; _at = now + 2f;
                    return;
                case 8:
                    if (now < _at) return;
                    if (_mode != "load")
                    {
                        savedatascript.s.saveScreen.AutoSave();
                        Write("autosaved");
                    }
                    _state = 9; _at = now + 5f;
                    return;
                case 9:
                    if (now < _at) return;
                    Write("done, quitting");
                    _mode = null;
                    Application.Quit();
                    return;
            }
        }

        /// Compares the ported 2023 terrain and road with a dump of the real public game (tools\OldRoadDump) copied
        /// to BepInEx\OldRoadDump.txt: terrain height samples and every dumped road segment's start position.
        private static void RoadCheck()
        {
            var file = Path.Combine(Paths.BepInExRootPath, "OldRoadDump.txt");
            if (!File.Exists(file)) { Write("roadcheck: no " + file); return; }
            var inv = System.Globalization.CultureInfo.InvariantCulture;
            var lines = File.ReadAllLines(file);
            int seed = int.Parse(lines[0].Split(' ')[2], inv);
            var noise = new OldNoise(seed);
            foreach (var l in lines)
                if (l.StartsWith("heights (local x z h):"))
                {
                    var sb = new StringBuilder("roadcheck seed " + seed + " terrain (x z old ported):");
                    foreach (var part in l.Split('|'))
                    {
                        var f = part.Trim().Split(' ');
                        if (f.Length != 3 || !float.TryParse(f[0], System.Globalization.NumberStyles.Float, inv, out float x)) continue;
                        float z = float.Parse(f[1], inv), h = float.Parse(f[2], inv);
                        sb.Append(" | " + x + " " + z + " " + h.ToString("R", inv) + " " + noise.Height(x, z).ToString("R", inv));
                    }
                    Write(sb.ToString());
                }
            var path = new OldRoadPath(noise);
            int start = Array.IndexOf(lines, "== roads (index posX posY posZ | bone rots x y z ... | bone coords x y z ...)");
            double maxErr = 0; int worst = -1, n = 0;
            var firstBad = "";
            for (int i = start + 1; i < lines.Length; i++)
            {
                var f = lines[i].Split(' ');
                if (f.Length < 4 || !int.TryParse(f[0], out int idx)) continue;
                var old = new Vector3d(double.Parse(f[1], inv), double.Parse(f[2], inv), double.Parse(f[3], inv));
                path.Between(0, old.z + 200);   // computes far enough
                if (idx >= path.ForwardSegments) break;
                var mine = path.ForwardSegmentPos(idx);
                double err = (mine - old).magnitude;
                n++;
                if (err > maxErr) { maxErr = err; worst = idx; }
                if (err > 0.01 && firstBad.Length == 0) firstBad = "first > 1 cm at segment " + idx + ": old " + old + " ported " + mine;
            }
            // the building plan against the old game's buildinginmemory list (old frame: road start at the origin)
            int b0 = Array.IndexOf(lines, "== roadBuildingScript JSON"), b1 = start;
            var oldB = new List<double[]>();
            double[] cur = null;
            for (int i = b0 + 1; i < b1 && i > 0; i++)
            {
                var t = lines[i].Trim().TrimEnd(',');
                int c = t.IndexOf(':');
                if (c < 0) continue;
                string key = t.Substring(0, c).Trim('"', ' '), val = t.Substring(c + 1).Trim();
                if (key == "px") { cur = new double[5]; oldB.Add(cur); cur[0] = double.Parse(val, inv); }
                else if (cur != null && key == "py") cur[1] = double.Parse(val, inv);
                else if (cur != null && key == "pz") cur[2] = double.Parse(val, inv);
                else if (cur != null && key == "ry") cur[3] = double.Parse(val, inv);
                else if (cur != null && key == "tipus") { cur[4] = double.Parse(val, inv); cur = null; }
            }
            var mine2 = OldBuildingPlan.Generate(path, noise, seed, back: false);
            int same = 0; double maxPos = 0, maxRot = 0; string bad = "";
            for (int i = 0; i < Math.Min(oldB.Count, mine2.Count); i++)
            {
                var o = oldB[i]; var m = mine2[i];
                double dp = Math.Sqrt((o[0] - m.X) * (o[0] - m.X) + (o[2] - m.Z) * (o[2] - m.Z)), dr = Math.Abs(o[3] - m.Ry), dy = Math.Abs(o[1] - m.Y);
                if ((int)o[4] == m.OldType && dp < 0.05 && dr < 0.01 && dy < 0.05) same++;
                else if (bad.Length == 0) bad = "; first difference at building " + i + ": old type " + o[4] + " (" + o[0] + ", " + o[1] + ", " + o[2] + ") ry " + o[3] + ", ported type " + m.OldType + " (" + m.X + ", " + m.Y + ", " + m.Z + ") ry " + m.Ry;
                maxPos = Math.Max(maxPos, dp); maxRot = Math.Max(maxRot, dr);
            }
            Write("roadcheck buildings: old " + oldB.Count + ", ported " + mine2.Count + ", identical " + same + ", max position error " + maxPos.ToString("F3") + " m, max rotation error " + maxRot.ToString("F4") + bad);
            Write("roadcheck: " + n + " segments compared, max error " + maxErr.ToString("F4") + " m at segment " + worst + (firstBad.Length > 0 ? "; " + firstBad : "; all within 1 cm"));
        }

        private static void MenuReport()
        {
            var g = gamemodesettingsscript.s;
            var row = NewGameUi.LastCopy;
            Write("menu: terrain row " + (row != null ? "present at " + NewGameUi.Path(row.transform) + ", active " + row.activeInHierarchy : "MISSING") + "; " + NewGameUi.LastReport);
            // the settings panel is a closed menu window: open the chain above it for the screenshot
            var t = g.dropdrops[1].transform;
            Transform common = t;
            foreach (var x in g.toggles) while (common != null && x != null && !x.transform.IsChildOf(common)) common = common.parent;
            if (common != null)
            {
                for (var p = common; p != null; p = p.parent) if (!p.gameObject.activeSelf) p.gameObject.SetActive(true);
                File.WriteAllText(Path.Combine(Paths.BepInExRootPath, "TerrainChoice-ui.txt"), NewGameUi.Dump(common, 0));
                Write("UI dump of " + NewGameUi.Path(common) + " -> TerrainChoice-ui.txt");
            }
            if (row != null)
            {
                var rt = row.transform as RectTransform;
                var corners = new Vector3[4];
                rt.GetWorldCorners(corners);
                var mine = new Rect(corners[0], corners[2] - corners[0]);
                var overlaps = new List<string>();
                foreach (var s in row.transform.parent.GetComponentsInChildren<RectTransform>(false))
                {
                    if (s == rt || s.IsChildOf(rt) || rt.IsChildOf(s) || s.GetComponent<TMP_Text>() == null) continue;
                    s.GetWorldCorners(corners);
                    var other = new Rect(corners[0], corners[2] - corners[0]);
                    if (other.Overlaps(mine)) overlaps.Add(s.name + "='" + s.GetComponent<TMP_Text>().text + "'");
                }
                Write("terrain row rect " + mine + ", overlapping texts: " + (overlaps.Count == 0 ? "none" : string.Join(", ", overlaps.ToArray())));
            }
        }

        private static void RoadReport(string when)
        {
            var ms = menuhandler.s.currentMainMap;
            var sb = new StringBuilder("roads " + when + ":");
            foreach (var g in WorldPatches.RoadGens(ms))
            {
                int off = 0, roads = 0, main = 0, side = 0, empty = 0;
                foreach (var kv in g.chunks)
                {
                    if (kv.Key.x != 0.0) off++;
                    foreach (var r in kv.Value.roads)
                    {
                        roads++;
                        if (r.roadType == roadGenScript.roadTypeEmu.mainRoad) main++;
                        else if (r.roadType == roadGenScript.roadTypeEmu.sideRoad) side++;
                        else empty++;
                    }
                }
                if (OldRoad.Path != null)
                {
                    sb.Append(" {chunks z:");
                    foreach (var kv in g.chunks)
                    {
                        double ymin = double.MaxValue, ymax = double.MinValue; int pieces = 0;
                        foreach (var r in kv.Value.roads) foreach (var el in r.roadElements) { pieces++; foreach (var p in el.bonePos) { ymin = Math.Min(ymin, p.y); ymax = Math.Max(ymax, p.y); } }
                        sb.Append(" " + kv.Key.z + "(" + kv.Value.dots.Count + " dots, " + kv.Value.roads.Count + " roads" + (kv.Value.insideConnected ? "" : ", not connected") + ", " + pieces + " pieces" + (pieces > 0 ? ", y " + ymin.ToString("F0") + ".." + ymax.ToString("F0") : "") + ")");
                    }
                    sb.Append(" chunkSize " + g.chunkSize + "}");
                }
                sb.Append(" [" + g.name + ": hasMainRoad " + g.hasMainRoad + ", hasSideRoad " + g.hasSideRoad + ", chunks " + g.chunks.Count +
                          " (off the main column " + off + "), roads " + roads + " = main " + main + " + side " + side + " + empty side " + empty + "]");
            }
            var pl = mainscript.s.player;
            var gp = mainscript.GlobalFromUnityPos(pl.transform.position);
            sb.Append(" player global " + gp);
            if (OldBuildings.Count > 0)
            {
                sb.Append("; old buildings: plan " + OldBuildings.Count + ", queued " + OldBuildings.Queued + ", placed " + OldBuildings.Placed + ", missing " + OldBuildings.Missing + "; POIs within 15 km:");
                foreach (var pg in ms.poiGens)
                    if (pg != null)
                        foreach (var ch in pg.chunks.Values)
                            foreach (var poi in ch.pois.Values)
                            {
                                var d = poi.pos - gp; d.y = 0;
                                if (d.magnitude > 15000) continue;
                                var road = OldRoad.Path != null ? OldRoad.Path.At(poi.pos.z) : poi.pos;
                                sb.Append(" " + poi.poiName + "@" + poi.pos.z.ToString("F0") + "(" + (poi.pos.x - road.x).ToString("+0;-0") + "m, ry " + poi.rot.y.ToString("F0") + (poi.pobj != null ? ", spawned" : "") + ")");
                            }
            }
            if (OldRoad.Path != null) sb.Append("; old road here x " + OldRoad.Path.At(gp.z).x.ToString("F1") + " (chunks " + OldRoad.Chunks + ", roads " + OldRoad.Roads + ", pieces " + OldRoad.Elements + ")");
            var ts = ms.terrainGenSettings != null && ms.terrainGenSettings.Count > 0 ? ms.terrainGenSettings[0] : null;
            if (ts != null)
            {
                sb.Append("; terrain height here " + ts.Height(gp).ToString("F1") + ", biome " + ts.GetBiomeInt(gp) + ", old terrain " + (OldTerrain.Active ? "on" : "off") +
                          " (old heights " + OldTerrain.Heights + ", colours " + OldTerrain.Colors + ")");
                var hs = new StringBuilder();
                for (int i = -2; i <= 2; i++) hs.Append(ts.Height(new Vector3d(gp.x + i * 1000.0, 0, gp.z + i * 1000.0)).ToString("F0")).Append(i < 2 ? "/" : "");
                sb.Append("; heights on a 4 km diagonal " + hs);
            }
            Write(sb.ToString());
            if (ts != null) ObjReport(ms, ts);
        }

        /// The beta's object scatter per category: what it placed here and how far the objects sit from the ground.
        private static void ObjReport(mapSettingScript ms, terrainGenSettings ts)
        {
            if (ms.objGens == null) return;
            var sb = new StringBuilder("objects:");
            foreach (var og in ms.objGens)
            {
                if (og == null || og.categories == null) continue;
                foreach (var cat in og.categories)
                {
                    int n = 0, placed = 0; double off = 0, maxOff = 0;
                    foreach (var ch in cat.generatedChunks.Values)
                        foreach (var o in ch.objs.Values)
                        {
                            n++;
                            if (o.obj == null) continue;
                            placed++;
                            if (placed <= 30 && cat.useTerrainHeight)
                            {
                                double d = Math.Abs(o.gpos.y - ts.Height(o.gpos));
                                off += d; if (d > maxOff) maxOff = d;
                            }
                        }
                    if (n == 0) continue;
                    var names = new List<string>();
                    if (cat.prefabs != null) foreach (var pf in cat.prefabs) if (pf != null && pf.prefab != null && names.Count < 3) names.Add(pf.prefab.name);
                    sb.Append("\n  " + cat.name + ": " + n + " (" + placed + " placed), road " + cat.minDistFromRoad + "-" + cat.maxDistFromRoad +
                              (cat.useTerrainHeight ? ", ground offset avg " + (placed > 0 ? off / Math.Min(placed, 30) : 0).ToString("F1") + " max " + maxOff.ToString("F1") : ", own height") +
                              ", biome x" + (cat.biomeMultipliers != null ? string.Join("/", cat.biomeMultipliers.ConvertAll(f => f.ToString("0.##")).ToArray()) : "-") +
                              " [" + string.Join(", ", names.ToArray()) + "]");
                }
            }
            Write(sb.ToString());
        }

        private static poiGenScript.poiClass _aim;
        private static bool _aimed;

        private static void AimNow()
        {
            var cam = mainscript.s.player.Cam;
            if (_aim == null || cam == null) return;
            var target = mainscript.UnityPosFromGlobal(_aim.pos) + Vector3.up * 4f;
            mainscript.s.player.enabled = false;
            cam.transform.SetParent(null, true);
            cam.transform.position = target + new Vector3(-45f, 18f, -45f);
            cam.transform.LookAt(target);
            var road = OldRoad.Path != null ? OldRoad.Path.At(_aim.pos.z) : _aim.pos;
            Write("camera: " + _aim.poiName + " at " + _aim.pos + ", road there x " + road.x.ToString("F1") + " y " + road.y.ToString("F1") + ", building ground y " + _aim.pos.y.ToString("F1"));
        }

        private static void LookFromAbove()
        {
            var cam = mainscript.s.player.Cam;
            if (cam == null) return;
            mainscript.s.player.enabled = false;   // stop the controller from moving the camera back
            cam.transform.SetParent(null, true);
            if (OldBuildings.Count > 0)
            {
                // old terrain: look at the nearest spawned old building (not the start house) from 60 m, 20 m up
                poiGenScript.poiClass best = null; double bestD = double.MaxValue;
                var me = mainscript.GlobalFromUnityPos(cam.transform.position);
                foreach (var pg in menuhandler.s.currentMainMap.poiGens)
                    if (pg != null)
                        foreach (var ch in pg.chunks.Values)
                            foreach (var poi in ch.pois.Values)
                            {
                                if (poi.pobj == null || poi.pos.z == 0 || Math.Abs(poi.pos.z) < 100) continue;
                                double d = (poi.pos - me).magnitude;
                                if (d < bestD) { bestD = d; best = poi; }
                            }
                if (best != null)
                {
                    // stand next to it so the road and terrain there load, then look (AimNow, after the wait)
                    mainscript.s.player.Teleport(mainscript.UnityPosFromGlobal(best.pos) + new Vector3(-40f, 3f, -40f));
                    _aim = best;
                    Write("teleported next to " + best.poiName + " at " + best.pos);
                    return;
                }
            }
            cam.transform.position += new Vector3(0f, 350f, 0f);
            cam.transform.rotation = Quaternion.Euler(35f, 0f, 0f);   // along the road (+z), looking down
            cam.farClipPlane = Mathf.Max(cam.farClipPlane, 5000f);
        }
    }
}
