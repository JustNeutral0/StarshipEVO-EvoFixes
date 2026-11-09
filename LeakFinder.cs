using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using EvoFixes;
using HarmonyLib;
using Manager;
using UI;
using UnityEngine;
using VoxelEngine;

namespace LeakFinder
{
    // The game decides a room is leaking when one of five random particles from the life support escapes the ship,
    // so it can't say where the hole is or how big the room is. This finder fills the room with a grid of small cells
    // instead, growing out from the vent. Two cells connect when the game's own airtight raycast finds no wall
    // between them (cast both ways, at the centre and four offset points of the face, so a thin slit still counts).
    //  - Sealed: the cells it reached are the room. Each cell's clearance (distance to the nearest wall) splits them
    //    into living space, where a person-sized capsule fits, and cavities: gaps behind panels, under floors, inside
    //    walls.
    //  - Leaking: the fill reaches the edge of the ship. The route it took leads out through the hole; the hole is
    //    marked where the route starts being able to see outside.
    // The scan only reads the ship. It never changes the game's own oxygen.
    public static class Mod
    {
        public static EvoCore.ModLog Log;
        static EvoCore.Setting<float> cellSize, personRadius;
        static EvoCore.Setting<bool> showCavities;
        public static Scan Current;

        public static void Init()
        {
            Log = EvoCore.Log.For("LeakFinder");
            var category = EvoCore.Settings.For("LeakFinder", "Leak finder");
            cellSize = category.Add("CellSize", 0.125f, "Size of a scan cell in metres; smaller finds thinner gaps but takes longer");
            personRadius = category.Add("PersonRadius", 0.26f, // the game's NPC capsule radius (NpcMovementHelpers.CAPSULE_RADIUS, internal)
 "Space counts as living space where a capsule this wide (radius, m) fits");
            showCavities = category.Add("ShowCavities", true, "Mark cavity cells in the world after a scan");
            EvoFixes.Mod.AddPart("LeakFinder", "Leak finder", "Find where a room leaks air, and how much of it is living space", DrawTab, tool: true);
        }

        public static void Update()
        {
            Current?.Step();
            if (Current != null && Current.Done) Markers.Draw(Current, showCavities.Value);
        }

        static void DrawTab()
        {
            GUILayout.Label("Fills the room around a life support with small cells: shows where air gets out, and how much of the room is living space and how much is wall cavities.");
            GUILayout.Space(4);
            var aimed = ActionActivate.CommandOver.Value is LifeSupportCommandData over ? (LifeSupportCommandData?)over : null;
            if (aimed.HasValue)
            {
                if (GUILayout.Button($"Scan the life support you're looking at ({aimed.Value.brickCoord.Parent?.EntityName})", GUILayout.Width(460)))
                    Start(aimed.Value);
            }
            else GUILayout.Label("Look at a life support block to scan it, or pick one nearby:");
            foreach (var (data, distance) in Nearby().Take(8))
            {
                GUILayout.BeginHorizontal();
                GUILayout.Label($"{data.brickCoord.Parent.EntityName}, {distance:F0} m, at {data.brickCoord.Position}", GUILayout.Width(380));
                if (GUILayout.Button("Scan", GUILayout.Width(70))) Start(data);
                GUILayout.EndHorizontal();
            }

            GUILayout.Space(6);
            GUILayout.BeginHorizontal();
            GUILayout.Label($"Cell size {cellSize.Value * 100:F1} cm", GUILayout.Width(140));
            foreach (var size in new[] { 0.0625f, 0.125f, 0.25f })
                if (GUILayout.Button($"{size * 100:F1} cm", GUILayout.Width(70))) { cellSize.Value = size; EvoCore.Settings.SaveAll(); }
            GUILayout.EndHorizontal();
            var cavities = GUILayout.Toggle(showCavities.Value, " Mark cavities (yellow) as well as the way out (orange) and the hole (red)");
            if (cavities != showCavities.Value) { showCavities.Value = cavities; EvoCore.Settings.SaveAll(); }

            if (Current == null) return;
            GUILayout.Space(6);
            GUILayout.Label("<b>" + Current.Ship?.EntityName + "</b>: " + Current.Status);
            foreach (var line in Current.Report) GUILayout.Label(line);
            if (GUILayout.Button("Clear", GUILayout.Width(90))) Current = null;
        }

        static IEnumerable<(LifeSupportCommandData data, float distance)> Nearby()
        {
            var datas = LifeSupportCommandData.datas;
            if (!datas.IsCreated) yield break;
            var found = new List<(LifeSupportCommandData, float)>();
            for (int id = 1; id < datas.LengthFast; id++)
            {
                var data = datas[id];
                var ship = data.brickCoord.Parent;
                if (ship == null || ship.isDestroyed) continue;
                var distance = ship.TransformData.LocalToWorld(data.brickCoord.Position).magnitude; // the world is centred on the player
                if (distance < 200f) found.Add((data, distance));
            }
            foreach (var item in found.OrderBy(f => f.Item2)) yield return item;
        }

        static void Start(LifeSupportCommandData data)
        {
            Current = new Scan(data, cellSize.Value, personRadius.Value);
            Log.Msg($"Scanning {Current.Ship?.EntityName} at {cellSize.Value * 100:F1} cm cells");
        }
    }

    public class Scan
    {
        // The game keeps these private; they are plain fields.
        static readonly FieldInfo TreeField = AccessTools.Field(typeof(LifeSupportCommandData), "childrenTreeId");
        static readonly FieldInfo ParentTransformField = AccessTools.Field(typeof(LifeSupportCommandData), "parentTransformId");
        static readonly FieldInfo TransformField = AccessTools.Field(typeof(LifeSupportCommandData), "transformId");
        static readonly FieldInfo UpField = AccessTools.Field(typeof(LifeSupportCommandData), "localUp");

        const double BudgetMs = 6;
        const int MaxCells = 400000;
        static readonly Vector3Int[] Steps =
        {
            new Vector3Int(1, 0, 0), new Vector3Int(-1, 0, 0), new Vector3Int(0, 1, 0),
            new Vector3Int(0, -1, 0), new Vector3Int(0, 0, 1), new Vector3Int(0, 0, -1),
        };
        static readonly Vector3[] Around = Steps.Select(s => (Vector3)s)
            .Concat(from x in new[] { -1, 1 } from y in new[] { -1, 1 } from z in new[] { -1, 1 } select new Vector3(x, y, z).normalized)
            .ToArray();

        public readonly ParentEntity Ship;
        readonly int tree, parentTransform, transform;
        readonly float cell, radius;
        readonly Vector3 vent;
        readonly Bounds inside;      // the ship's bounds plus a cell: a cell beyond them is outside
        readonly float reach;        // longer than any ray inside the ship

        // Cells, in the order the fill reached them.
        public readonly List<Vector3Int> Cells = new List<Vector3Int>();
        readonly List<int> parent = new List<int>();
        readonly Dictionary<long, int> index = new Dictionary<long, int>();
        public readonly HashSet<int> Living = new HashSet<int>(), Cavity = new HashSet<int>();
        public readonly List<int> Route = new List<int>();   // vent to outside, when leaking
        public int Hole = -1;

        public bool Done, Leaking;
        public string Status = "starting";
        public readonly List<string> Report = new List<string>();
        readonly IEnumerator work;
        readonly Stopwatch clock = new Stopwatch();
        SkyPhysics.QueryContext context;
        SkyTransformData frame;

        public Scan(LifeSupportCommandData data, float cellSize, float personRadius)
        {
            Ship = data.brickCoord.Parent;
            tree = (int)TreeField.GetValue(data);
            parentTransform = (int)ParentTransformField.GetValue(data);
            transform = (int)TransformField.GetValue(data);
            var up = (Vector3)UpField.GetValue(data);
            cell = cellSize;
            radius = personRadius;
            // Near where the game's own particles start, but off the brick grid (1/32 m): a cell centre lying on a
            // brick face sees through it, since a ray starting on a surface, or ending exactly at one, hits nothing.
            // Snapping must not pull it back toward the block: the game's start is a hair inside the vent's own face,
            // and a fill starting inside the block only explores its collider.
            var start = data.brickCoord.Position + 0.12f * up;
            vent = new Vector3(OffGrid(start.x), OffGrid(start.y), OffGrid(start.z));
            if (Vector3.Dot(vent - start, up) < 0f) vent += up / 32f;
            vent += up / 32f; // and clear of the face itself
            inside = Ship.ParentBounds;
            inside.Expand(2f * cell);
            reach = inside.size.magnitude + 1f;
            work = Run();
        }

        static float OffGrid(float v) => (Mathf.Floor(v * 32f) + 0.5f) / 32f;

        public float CellSize => cell;
        public Vector3 Centre(int i) => vent + (Vector3)Cells[i] * cell;

        // World position of a cell, for markers; the ship moves, so ask every frame.
        public bool ToWorld(Vector3 local, out Vector3 world, out Quaternion rotation)
        {
            world = default; rotation = default;
            if (Ship == null || Ship.isDestroyed) return false;
            var td = Ship.TransformData;
            world = td.LocalToWorld(local);
            rotation = td.rotation;
            return true;
        }

        public void Step()
        {
            if (Done) return;
            clock.Restart();
            context = SkyPhysics.CONTEXT;
            frame = SkyTransformData.datas[transform];
            try
            {
                while (clock.Elapsed.TotalMilliseconds < BudgetMs)
                    if (!work.MoveNext()) { Done = true; break; }
            }
            catch (Exception e)
            {
                Done = true; Status = "failed: " + e.Message;
                Mod.Log.Error("Scan failed: " + e);
            }
            if (Done) Mod.Log.Msg($"{Ship?.EntityName}: {Status}. " + string.Join(" ", Report));
        }

        // True when an airtight surface lies within `length` of `from` along `dir` (ship-local).
        bool Hits(Vector3 from, Vector3 dir, float length, out float distance)
        {
            distance = length;
            return SkyPhysics.QueryRaycastOnParentEntity.Execute(frame.LocalToSystem(from), frame.rotation * dir, tree, parentTransform,
                ref distance, out _, out _, context, hitForceField: true, hitOnlyAirTight: true);
        }

        // Air passes from a cell to its neighbour when some sample line through their shared face is clear both ways.
        bool Passes(Vector3 centre, Vector3Int step)
        {
            var dir = (Vector3)step;
            var side1 = step.x != 0 ? Vector3.up : Vector3.right;
            var side2 = Vector3.Cross(dir, side1);
            for (int k = 0; k < 5; k++)
            {
                var offset = k == 0 ? Vector3.zero : 0.35f * cell * ((k & 1) == 0 ? side1 : -side1) + 0.35f * cell * (k < 3 ? side2 : -side2);
                // Each way overlaps the other end by a centimetre, so a wall at either centre still counts.
                var from = centre + offset;
                var to = from + dir * cell;
                if (Hits(from, dir, cell + 0.01f, out _) || Hits(to, -dir, cell + 0.01f, out _)) continue;
                // An offset line only counts if both its ends are in the same open air as their cells' centres: a point
                // inside a block sees nothing around it, and would make a gap out of solid wall.
                if (k == 0 || (Open(centre, from) && Open(centre + dir * cell, to))) return true;
            }
            return false;
        }

        // No wall between two points, looking both ways.
        bool Open(Vector3 a, Vector3 b)
        {
            var d = b - a;
            var length = d.magnitude + 0.01f;
            return !Hits(a, d.normalized, length, out _) && !Hits(b, -d.normalized, length, out _);
        }

        // Distance to the nearest wall around a cell, and whether any line from it gets out of the ship.
        float Clearance(Vector3 centre, out bool seesOut)
        {
            seesOut = false;
            var nearest = reach;
            foreach (var dir in Around)
            {
                if (Hits(centre, dir, reach, out var d)) nearest = Mathf.Min(nearest, d);
                else seesOut = true;
            }
            return nearest;
        }

        static long Key(Vector3Int c) => ((long)(c.x + (1 << 20)) << 42) | ((long)(c.y + (1 << 20)) << 21) | (long)(c.z + (1 << 20));

        int Add(Vector3Int c, int from)
        {
            index[Key(c)] = Cells.Count;
            Cells.Add(c); parent.Add(from);
            return Cells.Count - 1;
        }

        IEnumerator Run()
        {
            // 1. Fill from the vent until the room is full or the air gets out of the ship.
            Status = "filling the room";
            Add(Vector3Int.zero, -1);
            int escaped = -1;
            for (int head = 0; head < Cells.Count && escaped < 0; head++)
            {
                if (Cells.Count > MaxCells)
                {
                    Status = $"stopped: over {MaxCells:N0} cells. Try a bigger cell size.";
                    yield break;
                }
                var centre = Centre(head);
                foreach (var step in Steps)
                {
                    var next = Cells[head] + step;
                    if (index.ContainsKey(Key(next)) || !Passes(centre, step)) continue;
                    var added = Add(next, head);
                    if (!inside.Contains(Centre(added))) { escaped = added; break; }
                }
                yield return null;
            }
            float m3 = cell * cell * cell;

            if (escaped >= 0)
            {
                // 2a. Leaking: walk back from the outside to the vent, then find where the route starts seeing outside.
                Leaking = true;
                Status = "leaking: tracing the way out";
                for (int i = escaped; i >= 0; i = parent[i]) Route.Add(i);
                Route.Reverse();
                int lastClosed = 0;
                var clearances = new float[Route.Count];
                for (int r = 0; r < Route.Count; r++)
                {
                    clearances[r] = Clearance(Centre(Route[r]), out var seesOut);
                    if (!seesOut) lastClosed = r;
                    yield return null;
                }
                var holeAt = Mathf.Min(lastClosed + 1, Route.Count - 1);
                Hole = Route[holeAt];
                // The narrowest point from there out is roughly how wide the opening is.
                var narrowest = clearances.Skip(lastClosed).Take(4).DefaultIfEmpty(0f).Min();
                var hole = Centre(Hole);
                var cavityRun = clearances.Take(holeAt + 1).Count(c => c < radius);
                Status = "LEAKING";
                Report.Add($"The air gets out {(hole - vent).magnitude:F1} m from the vent, at ship position ({hole.x:F2}, {hole.y:F2}, {hole.z:F2}). It's marked red; the orange line is the way out.");
                Report.Add($"The opening is roughly {narrowest * 200:F0} cm across" + (narrowest < radius ? " — a gap, not a doorway." : " — big enough to be an open door or a missing block."));
                if (cavityRun > 0)
                    Report.Add($"On the way it squeezes through {cavityRun * cell * 100:F0} cm of cavity (space too tight for a person), so the hole may be behind a panel rather than in the room itself.");
                Report.Add($"Scanned {Cells.Count:N0} cells ({Cells.Count * m3:F1} m³ of air) before it got out.");
                yield break;
            }

            // 2b. Sealed: split the room into living space and cavities by clearance.
            Status = "sealed: measuring the room";
            var clear = new float[Cells.Count];
            for (int i = 0; i < Cells.Count; i++)
            {
                clear[i] = Clearance(Centre(i), out _);
                if ((i & 7) == 7) yield return null;
            }
            // Living space is everywhere a person-sized capsule can be, so grow the roomy cells back out by its radius.
            var grow = Mathf.CeilToInt(radius / cell);
            var queue = new Queue<(int cell, int depth)>();
            for (int i = 0; i < Cells.Count; i++)
                if (clear[i] >= radius) { Living.Add(i); queue.Enqueue((i, 0)); }
            while (queue.Count > 0)
            {
                var (i, depth) = queue.Dequeue();
                if (depth >= grow) continue;
                foreach (var step in Steps)
                    if (index.TryGetValue(Key(Cells[i] + step), out var j) && Living.Add(j)) queue.Enqueue((j, depth + 1));
            }
            for (int i = 0; i < Cells.Count; i++) if (!Living.Contains(i)) Cavity.Add(i);

            Status = "SEALED";
            Report.Add($"Room volume {Cells.Count * m3:F1} m³: living space {Living.Count * m3:F1} m³, cavities {Cavity.Count * m3:F1} m³ (gaps too tight for a person, marked yellow).");
            if (!Living.Contains(0))
                Report.Add("The vent itself opens into a cavity, not the living space: the air reaches the room through gaps.");
            if (Living.Count == 0)
                Report.Add("No living space at all: the vent only fills a cavity. Move it, or open the gap between it and the room.");
        }
    }

    // One mesh of small cubes in the ship's own coordinates, drawn every frame on top of everything: cavities are
    // inside walls by definition, so markers that the walls hid would be no use. Nothing is added to the scene.
    static class Markers
    {
        static Material material;
        static Mesh mesh;
        static Scan builtFor;
        static bool builtWithCavities;
        const int MaxCubes = 6000;
        static readonly Color Hole = new Color(1f, 0.1f, 0.1f, 0.9f), Route = new Color(1f, 0.55f, 0.1f, 0.8f), Cavity = new Color(1f, 0.9f, 0.2f, 0.45f);

        public static void Draw(Scan scan, bool cavities)
        {
            if (material == null)
            {
                // Built into every Unity player; colours come from the vertices.
                material = new Material(Shader.Find("Hidden/Internal-Colored")) { hideFlags = HideFlags.HideAndDontSave };
                material.SetInt("_ZTest", (int)UnityEngine.Rendering.CompareFunction.Always);
                material.SetInt("_ZWrite", 0);
                material.SetInt("_Cull", (int)UnityEngine.Rendering.CullMode.Off);
                material.SetInt("_SrcBlend", (int)UnityEngine.Rendering.BlendMode.SrcAlpha);
                material.SetInt("_DstBlend", (int)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha);
            }
            if (builtFor != scan || builtWithCavities != cavities) Build(scan, cavities);
            if (!scan.ToWorld(Vector3.zero, out var origin, out var rotation)) return;
            Graphics.DrawMesh(mesh, Matrix4x4.TRS(origin, rotation, Vector3.one), material, 0);
        }

        static void Build(Scan scan, bool cavities)
        {
            builtFor = scan; builtWithCavities = cavities;
            var cubes = new List<(Vector3 centre, float size, Color color)>();
            if (scan.Leaking)
            {
                cubes.AddRange(scan.Route.Select(i => (scan.Centre(i), scan.CellSize * 0.35f, Route)));
                if (scan.Hole >= 0) cubes.Add((scan.Centre(scan.Hole), scan.CellSize * 1.6f, Hole));
            }
            else if (cavities) cubes.AddRange(scan.Cavity.Take(MaxCubes).Select(i => (scan.Centre(i), scan.CellSize * 0.3f, Cavity)));

            var vertices = new List<Vector3>(cubes.Count * 8);
            var colors = new List<Color>(cubes.Count * 8);
            var triangles = new List<int>(cubes.Count * 36);
            int[] faces = { 0, 2, 1, 1, 2, 3, 4, 5, 6, 5, 7, 6, 0, 1, 4, 1, 5, 4, 2, 6, 3, 3, 6, 7, 0, 4, 2, 2, 4, 6, 1, 3, 5, 3, 7, 5 };
            foreach (var (centre, size, color) in cubes)
            {
                int first = vertices.Count;
                for (int corner = 0; corner < 8; corner++)
                {
                    vertices.Add(centre + 0.5f * size * new Vector3((corner & 1) == 0 ? -1 : 1, (corner & 2) == 0 ? -1 : 1, (corner & 4) == 0 ? -1 : 1));
                    colors.Add(color);
                }
                foreach (var f in faces) triangles.Add(first + f);
            }
            if (mesh == null) mesh = new Mesh { hideFlags = HideFlags.HideAndDontSave, indexFormat = UnityEngine.Rendering.IndexFormat.UInt32 };
            mesh.Clear();
            mesh.SetVertices(vertices);
            mesh.SetColors(colors);
            mesh.SetTriangles(triangles, 0);
            mesh.RecalculateBounds();
        }
    }
}
