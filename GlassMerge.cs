using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using HarmonyLib;
using UnityEngine;
using VoxelEngine;

namespace GlassMerge
{
    public static class Mod
    {
        public static EvoCore.ModLog Log;
        static EvoCore.Setting<bool> enabled;
        public static bool Enabled => enabled == null || enabled.Value;
        static bool wasEnabled = true;

        public static void Init()
        {
            Log = EvoCore.Log.For("GlassMerge");
            var category = EvoCore.Settings.For("GlassMerge", "Glass panes");
            enabled = category.Add("Enabled", true, "Glass blocks that touch draw as one pane: the faces pressed together are hidden");
            Clarity.Init(category);
            EvoFixes.Mod.AddPart("GlassMerge", "Glass panes", "touching glass blocks draw as one pane", DrawTab, enabled);
        }

        public static void Update()
        {
            if (Enabled != wasEnabled)
            {
                wasEnabled = Enabled;
                Merge.MarkAll();
            }
            Merge.Update();
            Clarity.Update();
        }

        static void DrawTab()
        {
            GUILayout.Label(Merge.Status);
            GUILayout.Label("Each glass block hides the parts of its faces that lie flat against another glass block, so a canopy shows only its inside and outside surfaces.");
            Clarity.Draw();
        }
    }

    // Every glass brick is its own mesh, and the transparent pass shades every face, so the two faces pressed together
    // between neighbouring glass bricks each add a grey reflective layer; a canopy of many bricks reads as a stack of
    // panels. The bricks draw from shared triangle lists (BrickMeshResources.shapeMeshDatas, one 12-triangle cluster
    // per 36 entries), picked per brick by RenderData.clusterStart/clusterCount. So: for each glass brick, cut away
    // the parts of its triangles that lie against opposite-facing triangles of other glass bricks, write the
    // remaining triangles (once per distinct result) into a reserved tail of the shape buffer, and point the brick at
    // them. Cutting, not just dropping whole triangles, matters where shapes differ: a tetra covers half of a wedge's
    // square face, a small cube part of a big one.
    static class Merge
    {
        // A glass brick shape: its transparent triangles in the shape buffer.
        class Shape
        {
            public int Start, Tris, ClusterCount;   // first triangle in shapeMeshDatas, triangle count, clusters
            public Vector3[] Local;                 // 3 per triangle, mesh space
        }

        class EntityState
        {
            public int DirtyFrame = -1;
            public readonly Dictionary<int, (ushort brickId, Vector2Int variant)> Applied = new Dictionary<int, (ushort, Vector2Int)>();
        }

        class Item
        {
            public int Index;
            public BrickInstanceData Data;
            public Shape Shape;
            public Vector3[] World;                 // 3 per triangle, entity space
            public Vector3[] Normal;                // per triangle, zero when degenerate
            public Bounds Box;
        }

        // What happened to one brick's triangles.
        struct Counts
        {
            public int Tris, Hidden, Cut, Uncut;    // Uncut: partly covered, but the cut can't be drawn (stretched part of a resized brick)
        }

        const int CHUNK = 36 * 512;                 // shape entries reserved at a time: room for 512 one-cluster variants
        const float PLANE_EPS = 0.002f;
        const float AREA_EPS = 1e-6f;

        static readonly Dictionary<ushort, Shape> shapes = new Dictionary<ushort, Shape>();
        static readonly Dictionary<string, Vector2Int> variants = new Dictionary<string, Vector2Int>();
        static readonly Dictionary<EntityBricks, EntityState> entities = new Dictionary<EntityBricks, EntityState>();
        static int chunkNext = -1, chunkEnd = -1;
        static Counts last;
        static int lastGlass;
        public static string Status = "No ship with glass seen yet.";

        static bool IsGlass(Brick brick)
        {
            // Plain bricks only: functional ones (screens are transparent too) may tie their drawing to the stock mesh.
            if (brick == null || brick.GetType() != typeof(Brick) || brick.VoxelMesh == null || brick.VoxelMaterials == null) return false;
            if (brick.childrenBrickID != 0 || brick.repeatBrickId != 0 || brick.repeatEvenBrickId != 0 || brick.repeatOddBrickId != 0) return false;
            var vm = brick.VoxelMesh;
            if (vm.renderData1.pass != RenderablePass.transparent || vm.renderData1.clusterCount == 0) return false;
            if (vm.renderData2.clusterCount != 0 || vm.renderData3.clusterCount != 0) return false;
            foreach (var m in brick.VoxelMaterials)
                if (m.AlphaMode != AlphaMode.transparent || m.dynDataPixel != DynData_Pixel.none) return false;
            return true;
        }

        static Shape GetShape(ushort brickId)
        {
            if (shapes.TryGetValue(brickId, out var shape)) return shape;
            shape = null;
            var brick = brickId < Brick.BRICK_INDEX.Length ? Brick.BRICK_INDEX[brickId] : null;
            if (IsGlass(brick))
            {
                var data = brick.VoxelMesh.transparentData;
                var list = BrickMeshResources.shapeMeshDatas;
                int tris = data.count / 3;
                // Curved pieces (the rounded arch corner has 120 triangles) are left alone.
                if (tris > 0 && tris <= 64 && (data.startTriangle + tris) * 3 <= list.Count)
                {
                    shape = new Shape { Start = data.startTriangle, Tris = tris, ClusterCount = data.ClusterCount, Local = new Vector3[tris * 3] };
                    for (int i = 0; i < tris * 3; i++) shape.Local[i] = list[data.startTriangle * 3 + i].position;
                }
            }
            shapes[brickId] = shape;
            if (shape != null) Mod.Log.Msg($"Glass shape {brickId} {brick.name}: {shape.Tris} triangles");
            return shape;
        }

        // Specular slices of every glass brick's materials (glass cube, wedges, arches, the transparent pipe).
        public static List<int> GlassSpecularSlices()
        {
            var slices = new List<int>();
            foreach (var brick in Brick.BRICK_INDEX)
            {
                if (!IsGlass(brick)) continue;
                foreach (var m in brick.VoxelMaterials)
                    if (m.specularID >= 0 && !slices.Contains(m.specularID)) slices.Add(m.specularID);
            }
            return slices;
        }

        // ---- shape buffer tail ----

        static readonly FieldInfo bufferField = AccessTools.Field(typeof(BrickMeshResources), "shapeMeshBuffer");
        static ComputeBuffer ShapeBuffer
        {
            get => (ComputeBuffer)bufferField.GetValue(null);
            set => bufferField.SetValue(null, value);
        }

        // Room for 'entries' more shape entries; grows the list and rebuilds the GPU buffer when a new chunk is needed.
        static int Reserve(int entries)
        {
            var list = BrickMeshResources.shapeMeshDatas;
            if (chunkNext < 0 || chunkNext + entries > chunkEnd || chunkEnd != list.Count)
            {
                while (list.Count % 3 != 0) list.Add(default);
                chunkNext = list.Count;
                int size = Mathf.Max(CHUNK, entries);
                for (int i = 0; i < size; i++) list.Add(default);
                chunkEnd = list.Count;
                var old = ShapeBuffer;
                var buffer = new ComputeBuffer(list.Count, Marshal.SizeOf(typeof(BrickMeshResources.ShapeMeshData)));
                buffer.SetData(list);
                ShapeBuffer = buffer;
                Shader.SetGlobalBuffer("ShapeMeshData", buffer);
                old?.Release();
                Mod.Log.Msg($"Shape buffer grown to {list.Count} entries for glass variants");
            }
            int start = chunkNext;
            chunkNext += entries;
            return start;
        }

        // The triangles (3 entries each) as a variant: one copy per distinct key, padded to whole clusters.
        static Vector2Int GetVariant(string key, List<BrickMeshResources.ShapeMeshData> entries)
        {
            if (variants.TryGetValue(key, out var v)) return v;
            var list = BrickMeshResources.shapeMeshDatas;
            // Always at least one cluster: a brick with everything hidden draws one empty (all-zero) triangle.
            int clusters = Mathf.Max(1, Mathf.CeilToInt(entries.Count / 36f));
            int start = Reserve(clusters * 36);
            for (int i = 0; i < clusters * 36; i++) list[start + i] = i < entries.Count ? entries[i] : default;
            ShapeBuffer.SetData(list, start, start, clusters * 36);
            v = new Vector2Int(start / 3, clusters);
            variants[key] = v;
            return v;
        }

        // ---- tracking ----

        public static void MarkDirty(EntityBricks bricks)
        {
            if (bricks == null) return;
            if (!entities.TryGetValue(bricks, out var st)) entities[bricks] = st = new EntityState();
            st.DirtyFrame = Time.frameCount;
        }

        public static void MarkAll()
        {
            foreach (var st in entities.Values) st.DirtyFrame = Time.frameCount;
        }

        public static void Forget(EntityBricks bricks)
        {
            if (bricks != null) entities.Remove(bricks);
        }

        static readonly List<EntityBricks> due = new List<EntityBricks>();

        public static void Update()
        {
            due.Clear();
            // Wait two quiet frames so a ship loading over several frames is merged once, at the end.
            foreach (var kv in entities)
                if (kv.Value.DirtyFrame >= 0 && kv.Value.DirtyFrame <= Time.frameCount - 2) due.Add(kv.Key);
            foreach (var bricks in due)
            {
                var st = entities[bricks];
                st.DirtyFrame = -1;
                try { Recompute(bricks, st); }
                catch (System.Exception e)
                {
                    Mod.Log.Error($"Glass merge failed on {bricks.entity?.EntityName}: {e}");
                    entities.Remove(bricks);
                }
            }
            if (due.Count > 0)
                Status = $"Last part merged: {lastGlass} glass bricks, {last.Tris} triangles: {last.Hidden} hidden, {last.Cut} cut to their uncovered part, {last.Uncut} partly covered but left whole. "
                    + $"{variants.Count} shape variants built, {entities.Count} parts tracked.";
        }

        // Puts the brick's render element on the wanted variant (vanilla UpdateRenderData resets it to the full mesh).
        static void Apply(EvoRenderer renderer, int renderId, Vector2Int v)
        {
            if (renderId == 0) return;
            var rd = renderer.GetElement(renderId);
            if (!rd.IsValid || rd.pass != RenderablePass.transparent) return;
            if (rd.clusterStart == v.x && rd.clusterCount == v.y) return;
            rd.clusterStart = v.x;
            rd.clusterCount = v.y;
            renderer.UpdateRenderData(renderId, rd);
        }

        public static void Reapply(BrickInstanceData bd, EvoRenderer renderer, BrickIds ids, VoxelEntity entity)
        {
            if (renderer != GlobalRenderer.renderer || entity?.Bricks == null) return;
            if (!entities.TryGetValue(entity.Bricks, out var st) || !st.Applied.TryGetValue(bd.instanceId, out var a) || a.brickId != bd.brickId) return;
            Apply(renderer, ids.renderId1, a.variant);
        }

        static void Recompute(EntityBricks bricks, EntityState st)
        {
            if (!bricks.brickDatas.IsCreated || !bricks.brickIds.IsCreated) { entities.Remove(bricks); return; }
            var renderer = GlobalRenderer.renderer;
            var items = Collect(bricks);
            var total = new Counts();
            var seen = new HashSet<int>();
            foreach (var a in items)
            {
                var v = new Vector2Int(a.Shape.Start, a.Shape.ClusterCount);
                if (Mod.Enabled)
                {
                    var key = Build(a, items, out var entries, ref total);
                    if (key != null) v = GetVariant(key, entries);
                }
                Apply(renderer, bricks.brickIds[a.Index].renderId1, v);
                st.Applied[a.Index] = (a.Data.brickId, v);
                seen.Add(a.Index);
            }
            var stale = new List<int>();
            foreach (var k in st.Applied.Keys) if (!seen.Contains(k)) stale.Add(k);
            foreach (var k in stale) st.Applied.Remove(k);
            if (items.Count == 0) entities.Remove(bricks);
            lastGlass = items.Count;
            last = total;
        }

        static List<Item> Collect(EntityBricks bricks)
        {
            var items = new List<Item>();
            int n = Mathf.Min(bricks.brickDatas.Length, bricks.brickIds.Length);
            for (int i = 0; i < n; i++)
            {
                var bd = bricks.brickDatas[i];
                if (!bd.IsValid) continue;
                var shape = GetShape(bd.brickId);
                if (shape != null) items.Add(Place(i, bd, shape));
            }
            return items;
        }

        static Item Place(int index, BrickInstanceData bd, Shape shape)
        {
            var vm = bd.Brick.VoxelMesh;
            var scale = bd.Scale;
            var q = bd.Quaternion;
            float grid = bd.GridSize;
            var pos = bd.Position;
            var it = new Item { Index = index, Data = bd, Shape = shape, World = new Vector3[shape.Local.Length], Normal = new Vector3[shape.Tris] };
            for (int i = 0; i < shape.Local.Length; i++) it.World[i] = pos + q * (grid * vm.ResizePoint(scale, shape.Local[i]));
            var box = new Bounds(it.World[0], Vector3.zero);
            for (int t = 0; t < shape.Tris; t++)
            {
                Vector3 a = it.World[3 * t], b = it.World[3 * t + 1], c = it.World[3 * t + 2];
                box.Encapsulate(a); box.Encapsulate(b); box.Encapsulate(c);
                var nrm = Vector3.Cross(b - a, c - a);
                it.Normal[t] = nrm.sqrMagnitude < 1e-12f ? Vector3.zero : nrm.normalized;
            }
            box.Expand(2 * PLANE_EPS);
            it.Box = box;
            return it;
        }

        // ---- cutting ----

        // The brick's triangles with the parts lying against other glass cut away. Returns null when nothing changed
        // (the stock mesh is used), else a key naming the result so equal results share one copy.
        static string Build(Item a, List<Item> items, out List<BrickMeshResources.ShapeMeshData> entries, ref Counts counts)
        {
            entries = null;
            var near = new List<Item>();
            foreach (var b in items) if (b != a && b.Box.Intersects(a.Box)) near.Add(b);
            var list = BrickMeshResources.shapeMeshDatas;
            var key = new StringBuilder().Append(a.Data.brickId);
            bool changed = false;
            var output = new List<BrickMeshResources.ShapeMeshData>();
            for (int t = 0; t < a.Shape.Tris; t++)
            {
                int src = (a.Shape.Start + t) * 3;
                counts.Tris++;
                Basis basis = default;
                var pieces = a.Normal[t] == Vector3.zero ? null : Uncovered(a, t, near, out basis);
                if (pieces == null)
                {
                    // Not against any glass: keep as is.
                    for (int k = 0; k < 3; k++) output.Add(list[src + k]);
                    key.Append(',').Append(t);
                    continue;
                }
                changed = true;
                if (pieces.Count == 0) { counts.Hidden++; continue; }
                // Partly covered: draw the uncovered pieces, as fans, with every vertex mapped back into mesh space.
                var cut = new List<BrickMeshResources.ShapeMeshData>();
                bool ok = true;
                foreach (var poly in pieces)
                    for (int i = 1; i + 1 < poly.Count && ok; i++)
                        foreach (var p in new[] { poly[0], poly[i], poly[i + 1] })
                        {
                            if (!MakeVertex(a, t, basis.origin + basis.u * p.x + basis.w * p.y, list, out var e)) { ok = false; break; }
                            cut.Add(e);
                        }
                if (ok)
                {
                    counts.Cut++;
                    output.AddRange(cut);
                    key.Append(",c").Append(t);
                    foreach (var e in cut) key.Append(':').Append(Mathf.RoundToInt(e.position.x * 4096)).Append('/').Append(Mathf.RoundToInt(e.position.y * 4096)).Append('/').Append(Mathf.RoundToInt(e.position.z * 4096));
                }
                else
                {
                    counts.Uncut++;
                    for (int k = 0; k < 3; k++) output.Add(list[src + k]);
                    key.Append(',').Append(t);
                }
            }
            if (!changed) return null;
            entries = output;
            return key.ToString();
        }

        struct Basis { public Vector3 origin, u, w; }

        // Triangle t of a, minus every opposite-facing triangle of the nearby glass that lies in its plane, as convex
        // polygons in the triangle's plane. Null when nothing touches it (empty list: fully covered).
        static List<List<Vector2>> Uncovered(Item a, int t, List<Item> near, out Basis basis)
        {
            Vector3 p0 = a.World[3 * t], p1 = a.World[3 * t + 1], p2 = a.World[3 * t + 2], n = a.Normal[t];
            basis = new Basis { origin = p0, u = (p1 - p0).normalized };
            basis.w = Vector3.Cross(n, basis.u);
            var bs = basis;
            System.Func<Vector3, Vector2> flat = p => new Vector2(Vector3.Dot(p - bs.origin, bs.u), Vector3.Dot(p - bs.origin, bs.w));
            var tb = new Bounds(p0, Vector3.zero); tb.Encapsulate(p1); tb.Encapsulate(p2); tb.Expand(2 * PLANE_EPS);
            var pieces = new List<List<Vector2>> { new List<Vector2> { flat(p0), flat(p1), flat(p2) } };
            float full = Area(pieces[0]);
            foreach (var b in near)
            {
                if (!b.Box.Intersects(tb)) continue;
                for (int s = 0; s < b.Shape.Tris; s++)
                {
                    if (Vector3.Dot(b.Normal[s], n) > -0.999f) continue;
                    Vector3 c0 = b.World[3 * s], c1 = b.World[3 * s + 1], c2 = b.World[3 * s + 2];
                    if (Mathf.Abs(Vector3.Dot(n, c0 - p0)) > PLANE_EPS) continue;
                    // Opposite facing, so its winding reads clockwise here: reverse it.
                    var cut = new List<Vector2> { flat(c0), flat(c2), flat(c1) };
                    var next = new List<List<Vector2>>();
                    foreach (var poly in pieces) Subtract(poly, cut, next);
                    pieces = next;
                    if (pieces.Count == 0) return pieces;
                }
            }
            float left = 0;
            foreach (var poly in pieces) left += Area(poly);
            // Only edges or corners touching: leave the triangle alone.
            if (left > full - AREA_EPS * Mathf.Max(1, full) * 100) return null;
            if (left < AREA_EPS) return new List<List<Vector2>>();
            return pieces;
        }

        // poly minus the convex counter-clockwise triangle cut, as convex pieces added to output.
        static void Subtract(List<Vector2> poly, List<Vector2> cut, List<List<Vector2>> output)
        {
            var rest = poly;
            for (int k = 0; k < 3 && rest.Count >= 3; k++)
            {
                Vector2 e0 = cut[k], e1 = cut[(k + 1) % 3];
                var outside = Clip(rest, e0, e1, keepLeft: false);
                if (outside.Count >= 3 && Area(outside) > AREA_EPS) output.Add(outside);
                rest = Clip(rest, e0, e1, keepLeft: true);
            }
        }

        // Sutherland-Hodgman against the line e0->e1: keeps the left (inside of a counter-clockwise shape) or right side.
        static List<Vector2> Clip(List<Vector2> poly, Vector2 e0, Vector2 e1, bool keepLeft)
        {
            var result = new List<Vector2>();
            var d = e1 - e0;
            float Side(Vector2 p) { float s = d.x * (p.y - e0.y) - d.y * (p.x - e0.x); return keepLeft ? s : -s; }
            for (int i = 0; i < poly.Count; i++)
            {
                Vector2 a = poly[i], b = poly[(i + 1) % poly.Count];
                float sa = Side(a), sb = Side(b);
                if (sa >= 0) result.Add(a);
                if ((sa >= 0) != (sb >= 0)) result.Add(a + (b - a) * (sa / (sa - sb)));
            }
            return result;
        }

        static float Area(List<Vector2> poly)
        {
            float s = 0;
            for (int i = 0; i < poly.Count; i++) { var a = poly[i]; var b = poly[(i + 1) % poly.Count]; s += a.x * b.y - b.x * a.y; }
            return s / 2;
        }

        // ---- vertices ----

        static readonly FieldInfo fPosition = AccessTools.Field(typeof(BrickMeshResources.ShapeMeshData), "position");
        static readonly FieldInfo fUv = AccessTools.Field(typeof(BrickMeshResources.ShapeMeshData), "uv");
        static readonly FieldInfo fVertOffset = AccessTools.Field(typeof(BrickMeshResources.ShapeMeshData), "vertOffset");
        static readonly FieldInfo fNormal = AccessTools.Field(typeof(BrickMeshResources.ShapeMeshData), "normal");

        // A shape entry at world point p on triangle t of a: the corner entries blended by barycentric weight, with the
        // mesh-space position the brick's resize turns into p. False when no mesh-space point does (a resized brick
        // stretches the middle of its mesh, and the GPU only moves corners, so points there have no source).
        static bool MakeVertex(Item a, int t, Vector3 p, List<BrickMeshResources.ShapeMeshData> list, out BrickMeshResources.ShapeMeshData entry)
        {
            entry = default;
            var bd = a.Data;
            var vm = bd.Brick.VoxelMesh;
            var local = bd.QuaternionInv * (p - bd.Position) / bd.GridSize;
            if (!Unresize(vm, bd.Scale, local, out var mesh)) return false;
            if ((vm.ResizePoint(bd.Scale, mesh) - local).magnitude * bd.GridSize > PLANE_EPS) return false;

            Vector3 w0 = a.World[3 * t], w1 = a.World[3 * t + 1], w2 = a.World[3 * t + 2];
            Vector3 v0 = w1 - w0, v1 = w2 - w0, v2 = p - w0;
            float d00 = Vector3.Dot(v0, v0), d01 = Vector3.Dot(v0, v1), d11 = Vector3.Dot(v1, v1), d20 = Vector3.Dot(v2, v0), d21 = Vector3.Dot(v2, v1);
            float den = d00 * d11 - d01 * d01;
            float bv = (d11 * d20 - d01 * d21) / den, bw = (d00 * d21 - d01 * d20) / den, bu = 1 - bv - bw;

            int src = (a.Shape.Start + t) * 3;
            BrickMeshResources.ShapeMeshData e0 = list[src], e1 = list[src + 1], e2 = list[src + 2];
            object boxed = e0;
            fPosition.SetValue(boxed, mesh);
            fUv.SetValue(boxed, bu * e0.uv + bv * e1.uv + bw * e2.uv);
            fNormal.SetValue(boxed, (bu * (Vector3)fNormal.GetValue(e0) + bv * (Vector3)fNormal.GetValue(e1) + bw * (Vector3)fNormal.GetValue(e2)).normalized);
            fVertOffset.SetValue(boxed, (bu * e0.vertOffset + bv * e1.vertOffset + bw * e2.vertOffset).normalized);
            entry = (BrickMeshResources.ShapeMeshData)boxed;
            return true;
        }

        // Inverse of VoxelMesh.ResizePoint, per axis: points past the high threshold moved by the full stretch, between
        // the thresholds by half of it, below the low one not at all.
        static bool Unresize(VoxelMesh vm, Vector3i scale, Vector3 x, out Vector3 v)
        {
            var ext = vm.ResizeMode.unit * scale.ToVector3();
            var c = vm.bounds.center;
            float lo = vm.ResizeMode.thresholdLow, hi = vm.ResizeMode.thresholdHigh;
            v = Vector3.zero;
            for (int i = 0; i < 3; i++)
            {
                float e = ext[i], r = x[i] - c[i];
                float tol = PLANE_EPS;
                float o;
                if (e == 0) o = r;
                else if (r <= lo + tol) o = Mathf.Min(r, lo);
                else if (r > hi + e - tol) o = Mathf.Max(r - e, hi + 1e-5f);
                else if (r > lo + e / 2 - tol && r <= hi + e / 2 + tol) o = Mathf.Clamp(r - e / 2, lo + 1e-5f, hi);
                else return false;
                v[i] = o + c[i];
            }
            return true;
        }

        // ---- DevBridge report ----

        // Per part of the named ship: what happened to its glass triangles.
        public static string Report(string shipName)
        {
            var sb = new StringBuilder();
            foreach (var ship in BlockEntityManager.Entities)
            {
                if (ship == null || ship.EntityName != shipName) continue;
                foreach (VoxelEntity part in ship.Tree.ChildrensAndSelf)
                {
                    if (part.Bricks == null || !part.Bricks.brickDatas.IsCreated) continue;
                    var items = Collect(part.Bricks);
                    if (items.Count == 0) continue;
                    var counts = new Counts();
                    int scaled = 0;
                    foreach (var a in items)
                    {
                        Build(a, items, out _, ref counts);
                        if (a.Data.Scale.x != 0 || a.Data.Scale.y != 0 || a.Data.Scale.z != 0) scaled++;
                    }
                    sb.AppendLine($"{part.EntityName} #{part.ID.indexID}: {items.Count} glass ({scaled} resized), {counts.Tris} triangles: {counts.Hidden} hidden, {counts.Cut} cut, {counts.Uncut} partly covered but left whole");
                }
            }
            return sb.Length == 0 ? "no ship named " + shipName : sb.ToString();
        }
    }

    // Glass look: the transparent pass reads the glass's base colour (alpha = opacity) from MAIN_TEX and its
    // specular from SPECULAR_TEX, both DXT5 texture arrays that the game keeps readable. Rescale one slice's
    // channels from its original pixels, recompress, and copy it back in, every mip.
    // Measured on Glass Cube (MAIN_TEX 183 is plain grey with alpha 0; SPECULAR_TEX 154 is r 68, g 150, b 255, a 23):
    // specular r is the reflectivity, which also sets most of the opacity (Unity's premultiplied transparent:
    // alpha = a * (1 - reflectivity) + reflectivity); a is the base opacity; g is smoothness, which also sets how
    // strongly faces reflect when seen edge-on (Unity's grazing term is smoothness + reflectivity). Clarity scales
    // r and a down; Edge glare scales g, because faces seen nearly edge-on (the inside of a squared canopy edge)
    // otherwise turn into bright, almost opaque mirrors. With the stock reflectivity, low smoothness only smears
    // the reflection into flat grey, so the two go together.
    public static class Clarity
    {
        static readonly Dictionary<(string, int), Color32[]> originals = new Dictionary<(string, int), Color32[]>();
        static EvoCore.Setting<float> amount, glare;
        static Vector2 applied = new Vector2(-1, -1);
        static Texture applyTo;
        static int nextCheck;
        public static float Amount => amount?.Value ?? 1f;
        public static float Glare => glare?.Value ?? 0f;

        public static void Init(EvoCore.Settings category)
        {
            amount = category.Add("Clarity", 1f, "Glass clarity, 0 (stock) to 1 (no tint or base reflectivity)");
            glare = category.Add("EdgeGlare", 0f, "Glass edge glare: reflection on faces seen edge-on, 1 (stock) to 0 (none)");
        }

        // Applies the setting once the textures exist and again if they're replaced; then only on change.
        public static void Update()
        {
            if (Time.frameCount < nextCheck) return;
            nextCheck = Time.frameCount + 30;
            var spec = Array("SPECULAR_TEX");
            if (spec == null) return;
            var want = GlassMerge.Mod.Enabled ? new Vector2(Mathf.Clamp01(Amount), Mathf.Clamp01(Glare)) : new Vector2(0, 1);
            if (spec == applyTo && want == applied) return;
            if (spec != applyTo) originals.Clear();
            applyTo = spec;
            applied = want;
            foreach (int slice in Merge.GlassSpecularSlices())
            {
                try { GlassMerge.Mod.Log.Msg(Scale("SPECULAR_TEX", slice, 1 - want.x, want.y, 1, 1 - want.x)); }
                catch (System.Exception e) { GlassMerge.Mod.Log.Error($"Glass clarity on specular slice {slice} failed: {e.Message}"); }
            }
        }

        public static void Draw()
        {
            Slider(amount, "Clarity", "0 is the stock grey tint; 1 removes the tint and base reflection.");
            Slider(glare, "Edge glare", "1 is stock: faces seen edge-on turn into near-solid mirrors. Lower keeps them see-through, and the glass a little less glossy.");
        }

        static void Slider(EvoCore.Setting<float> entry, string name, string about)
        {
            GUILayout.BeginHorizontal();
            GUILayout.Label($"{name} {entry.Value:0.00}", GUILayout.Width(110));
            float v = Mathf.Round(GUILayout.HorizontalSlider(entry.Value, 0f, 1f, GUILayout.Width(200)) * 20) / 20;
            if (!Mathf.Approximately(v, entry.Value)) { entry.Value = v; EvoCore.Settings.SaveAll(); }
            GUILayout.Label(about);
            GUILayout.EndHorizontal();
        }

        static Texture2DArray Array(string global) => Shader.GetGlobalTexture(global) as Texture2DArray;

        static Color32[] Original(string global, int slice)
        {
            if (!originals.TryGetValue((global, slice), out var px)) originals[(global, slice)] = px = Array(global).GetPixels32(slice, 0);
            return px;
        }

        public static string Stats(string global, int slice)
        {
            var px = Original(global, slice);
            double r = 0, g = 0, b = 0, a = 0;
            int amin = 255, amax = 0;
            foreach (var c in px) { r += c.r; g += c.g; b += c.b; a += c.a; amin = Mathf.Min(amin, c.a); amax = Mathf.Max(amax, c.a); }
            int n = px.Length;
            return $"{global}[{slice}] {Array(global).width}px: mean rgba {r / n:F1} {g / n:F1} {b / n:F1} {a / n:F1}, alpha {amin}..{amax}";
        }

        public static string Scale(string global, int slice, float r, float g, float b, float a)
        {
            var arr = Array(global);
            var src = Original(global, slice);
            var px = new Color32[src.Length];
            for (int i = 0; i < px.Length; i++)
                px[i] = new Color32((byte)Mathf.Clamp(src[i].r * r, 0, 255), (byte)Mathf.Clamp(src[i].g * g, 0, 255), (byte)Mathf.Clamp(src[i].b * b, 0, 255), (byte)Mathf.Clamp(src[i].a * a, 0, 255));
            var tex = new Texture2D(arr.width, arr.height, TextureFormat.RGBA32, true, true);
            tex.SetPixels32(px);
            tex.Apply(true);
            tex.Compress(true);
            if (tex.format != arr.format) { Object.Destroy(tex); return $"recompressed to {tex.format}, array is {arr.format}: not copied"; }
            int mips = Mathf.Min(tex.mipmapCount, arr.mipmapCount);
            for (int m = 0; m < mips; m++) Graphics.CopyTexture(tex, 0, m, arr, slice, m);
            Object.Destroy(tex);
            return $"{global}[{slice}] scaled by {r} {g} {b} {a}, {mips} mips";
        }
    }

    [HarmonyPatch(typeof(EntityBricks), nameof(EntityBricks.AddBrickAt))]
    static class OnAdd
    {
        static void Postfix(EntityBricks __instance, BrickInstanceData brickData)
        {
            if (brickData.Brick?.VoxelMesh?.renderData1.pass == RenderablePass.transparent) Merge.MarkDirty(__instance);
        }
    }

    [HarmonyPatch(typeof(EntityBricks), nameof(EntityBricks.Remove))]
    static class OnRemove
    {
        static void Postfix(EntityBricks __instance, BrickInstanceData brickData)
        {
            if (brickData.Brick?.VoxelMesh?.renderData1.pass == RenderablePass.transparent) Merge.MarkDirty(__instance);
        }
    }

    [HarmonyPatch(typeof(EntityBricks), nameof(EntityBricks.OnWillUnspawnEntity))]
    static class OnUnspawn
    {
        static void Prefix(EntityBricks __instance) => Merge.Forget(__instance);
    }

    // Damage, the cutout toggle and the like re-send the brick's stock render data; put the variant back.
    [HarmonyPatch(typeof(Brick), nameof(Brick.UpdateRenderData), new[] { typeof(BrickInstanceData), typeof(EvoRenderer), typeof(int), typeof(BrickIds), typeof(VoxelEntity), typeof(bool), typeof(bool) })]
    static class OnUpdateRender
    {
        static void Postfix(BrickInstanceData bd, EvoRenderer renderer, BrickIds ids, VoxelEntity entity) => Merge.Reapply(bd, renderer, ids, entity);
    }
}
