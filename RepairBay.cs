using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using EvoFixes;
using Game;
using World;
using UnityEngine;
using VoxelEngine;

namespace RepairBayMod
{
    public static class Mod
    {
        public static readonly int BrickId = EvoCore.Ids.Claim(EvoCore.IdSpace.Brick, "RepairBay.RepairDock", 505);
        public const string MeshPath = "Shapes/modded/repair_bay";
        public const float ResizeThreshold = 0.12f;
        public static EvoCore.ModLog Log;
        static float nextTick;
        static EvoFixes.Mod.Part part;

        public static void Init()
        {
            Log = EvoCore.Log.For("RepairBay");
            part = EvoFixes.Mod.AddPart("RepairBay", "Repair Dock", "A base block that repairs the ship in its shipyard for plates", SystemRepairBay.DrawTab);
        }

        // Off, docks stay built (the block is always registered, so saves keep them) but don't repair.
        public static void Update()
        {
            if (!part.On || Time.time < nextTick) return;
            nextTick = Time.time + 1f;
            try { SystemRepairBay.TickAll(); }
            catch (Exception e) { Log.Error(e.ToString()); }
        }
    }

    // A base-side repair dock: works only on a base or station with a shipyard, and repairs the ship landed in that
    // shipyard once per second, while on and powered. It pays in plates from storage units connected to the
    // shipyard or anywhere on the base, for the damage actually repaired.
    public class SystemRepairBay : CommandSystem
    {
        const float HealPerBrick = -100f;       // same value as the stock Repair Tool
        const int MaxBricksPerTick = 8;
        // Plates per unit of the game's repair cost (what a station's repair kiosk charges in credits).
        const float PlatesPerCostUnit = 1f;
        static readonly List<SystemRepairBay> Active = new List<SystemRepairBay>();
        static readonly List<int> StorageIds = new List<int>();
        static readonly CraftingElement Plate = new CraftingElement(WorldPropCommon.WorldPropId.plate, 1f, 0);
        static string globalStatus;
        string status;
        ParentEntity lastShip;
        float lastCost = -1f, owed, platesUsed;

        protected override float initialState => 1f;

        protected override void OnInitialize(BrickIds ids, bool onLoad, object[] args)
        {
            pixelDataPointer = MaterialDynamicData.RegisterFloat(1f); // the glow slot reads this
            base.OnInitialize(ids, onLoad, args);
            Active.Add(this);
            Mod.Log.Msg($"Repair Dock spawned, {Active.Count} active, on load {onLoad}");
        }

        // Dims the glow when switched off and darkens it when destroyed. Stock systems use 3x, which blooms
        // too much across a whole cross, so this matches a stock glow tile instead.
        public override void OnStateUpdated(bool onLoad)
        {
            base.OnStateUpdated(onLoad);
            var destroyed = IsDestroyed || (entitySystems != null && entitySystems.IsDestroyed);
            var glow = destroyed ? 0f : Mathf.Max(0.05f, State);
            if (onLoad) MaterialDynamicData.FloatDynDatas[pixelDataPointer.id] = glow;
            else MaterialDynamicData.AddTargetFloat(pixelDataPointer.id, glow, 2f);
        }

        public override void OnDestroy(BrickUnspawnMode unspawnMode)
        {
            Active.Remove(this);
            Mod.Log.Msg($"Repair Dock removed, {Active.Count} active, mode {unspawnMode}");
            base.OnDestroy(unspawnMode);
        }

        public static void TickAll()
        {
            if (Active.Count == 0) return;
            if (!PlayerState.IsHostOrSolo)
            {
                Report(ref globalStatus, "not host or solo, session mode " + PlayerState.sessionMode);
                return;
            }
            foreach (var bay in Active.ToArray()) bay.Tick();
        }

        void Tick()
        {
            var blocked = BlockedReason(out var shipyard);
            ParentEntity ship = null;
            if (blocked == null)
            {
                ship = DockedShip(shipyard);
                if (ship == null) blocked = "no ship landed in the shipyard";
            }
            if (blocked != null) { Report(ref status, blocked); lastShip = null; return; }

            // Pay for what the last tick repaired, measured by how far the ship's repair cost dropped.
            var cost = ship.Systems.GetRepairCost();
            if (ship == lastShip && lastCost >= 0f && cost < lastCost) owed += (lastCost - cost) * PlatesPerCostUnit;
            lastShip = ship;
            lastCost = cost;
            FetchStorage(shipyard);
            var whole = Mathf.FloorToInt(owed);
            if (whole > 0)
            {
                var taken = TakePlates(whole);
                owed -= taken;
                platesUsed += taken;
            }
            if (cost <= 0f) { Report(ref status, $"{ship.EntityName} is fully repaired ({platesUsed:0} plates used)"); return; }
            if (owed >= 1f || CountPlates() < 1f)
            {
                Report(ref status, $"out of plates: put plates in a storage unit on this base ({ship.EntityName} still needs {cost:0} repair)");
                return;
            }
            Report(ref status, $"repairing {ship.EntityName}, repair cost {cost:0}");
            Repair(ship, Mathf.Clamp(Count, 1, MaxBricksPerTick));
        }

        // The stock repair only reaches 2 units from the point it's given, so aim each at a damaged block.
        static void Repair(ParentEntity ship, int bricks)
        {
            foreach (var entity in ship.Tree.ChildrensAndSelf)
            {
                var data = entity.Bricks.brickDatas;
                for (int i = 0; i < data.Length && bricks > 0; i++)
                {
                    var brick = data[i];
                    if (!brick.IsValid || brick.healthScore == 255) continue;
                    if (brick.Brick.IsDecal && !brick.Brick.IsDecalDamage) continue;
                    Manager.Dependency.PlayerNetwork.CmdRepair(entity.ID, brick.Position, HealPerBrick);
                    bricks--;
                }
                if (bricks == 0) return;
            }
        }

        string BlockedReason(out CommandShipyard shipyard)
        {
            shipyard = null;
            if (State <= 0f) return "switched off";
            if (entitySystems == null || parentEntity == null) return "no base systems";
            if (parentEntity.Type == EntityType.spaceship) return "only works on a base or station, next to a shipyard";
            if (IsDestroyed) return "dock is at half health or less: " + brickData.HealthFactor;
            if (entitySystems.IsPowerDepleted) return "base power is stalled";
            if (Manager.Dependency.PlayerNetwork == null) return "no player network";
            foreach (var coord in entitySystems.GetCommandsFor(CommandType.shipyard))
                if (coord.Command is CommandShipyard found) { shipyard = found; break; }
            return shipyard == null ? "needs a shipyard on the same base" : null;
        }

        // The shipyard keeps the landed ship by id; it only resolves the entity itself on load.
        static ParentEntity DockedShip(CommandShipyard shipyard)
        {
            var data = shipyard.shipyardData;
            if (!data.hasEntityDocked || string.IsNullOrEmpty(data.dockedSGUID)) return null;
            var ship = BlockEntityManager.instance.GetEntityBySGUID(data.dockedSGUID);
            return ship == null || ship.isDestroyed ? null : ship;
        }

        // Any storage unit or cargo space on the base, plus anything connected to the shipyard itself.
        void FetchStorage(CommandShipyard shipyard)
        {
            StorageIds.Clear();
            foreach (var coord in entitySystems.GetCommandsFor(StorageCommon.CommandTypesCargo))
                if (coord.Command is StorageCommandData storage && !StorageIds.Contains(storage.StorageId)) StorageIds.Add(storage.StorageId);
            var linked = new List<int>();
            StorageCommon.GetConnectedStorageIds(shipyard.StorageId, linked,
                storage => storage.type == ItemStorageType.storageUnit, shipyard.StorageId);
            foreach (var id in linked) if (!StorageIds.Contains(id)) StorageIds.Add(id);
        }

        static IEnumerable<(int storage, int item, ItemInstanceData data)> Plates()
        {
            var context = StorageCommon.CONTEXT;
            foreach (var storageId in StorageIds)
            {
                int id = context.GetStorageData(storageId).rootItemId, guard = 0;
                while (id != 0 && guard++ < 500)
                {
                    var data = context.GetItemData(id);
                    var next = data.nextElementId;
                    if (Plate.IsMatching(data, context)) yield return (storageId, id, data);
                    id = next;
                }
            }
        }

        static float CountPlates() => Plates().Sum(p => p.data.quantity);

        // The shipyard's own way of paying: split off what's needed and release it.
        static int TakePlates(int wanted)
        {
            var left = (float)wanted;
            foreach (var (storage, item, data) in Plates().ToList())
            {
                if (left <= 0f) break;
                var amount = Mathf.Min(left, data.quantity);
                StorageCommon.PopupItemQuantityAndRelease(storage, item, amount, StorageCommon.CONTEXT);
                left -= amount;
            }
            return wanted - Mathf.CeilToInt(left);
        }

        internal static void DrawTab()
        {
            GUILayout.Label($"{Active.Count} dock(s) loaded. A dock on a base or station repairs the ship landed in that base's shipyard, " +
                            $"up to {MaxBricksPerTick} blocks a second (one per unit of dock size), for {PlatesPerCostUnit:0.#} plate per unit of repair cost, " +
                            "taken from any storage unit or cargo space on the base.");
            if (globalStatus != null) GUILayout.Label("All docks: " + globalStatus);
            foreach (var bay in Active)
            {
                var where = bay.parentEntity == null ? "nowhere" : bay.parentEntity.EntityName;
                GUILayout.Label($"{where}: state {bay.State:F1}, {bay.status ?? "waiting for first tick"}");
            }
        }

        // Logs only when the message changes, so the log shows each step without repeating every second.
        static void Report(ref string last, string message)
        {
            if (last == message) return;
            last = message;
            Mod.Log.Msg("Repair Dock: " + message);
        }
    }

    [HarmonyPatch(typeof(Brick), nameof(Brick.LoadAllResourcesRuntime))]
    static class RegisterBlock
    {
        // An exception here would stop every block from loading, so failures are logged and contained.
        static void Prefix()
        {
            try { Register(); }
            catch (Exception e) { Mod.Log.Error("Repair Bay registration failed: " + e); }
        }

        static void Register()
        {
            var brick = new CommandBrick<SystemRepairBay>(Mod.BrickId, "Repair Dock", "modded/repair_bay",
                // Vertices within the thresholds of the centre stay centred, so the cross keeps its width.
                new ResizeMode(1f) { canResize = true, maxScale = new Vector3i(15, 15, 15), thresholdHigh = Mod.ResizeThreshold, thresholdLow = -Mod.ResizeThreshold },
                new object[] { "slot0", "slot1", "slot2" });
            brick.defaultPaint = TabSystem.THRUSTER_ROUNDED.defaultPaint;
            brick.PreviewPaint = TabSystem.THRUSTER_ROUNDED.defaultPaint;
            brick.cost = 8f;
            brick.massForUnitShape = 6f;
            brick.colliderShape = Manager.ColliderShapeCommon.Shape.box;
            brick.systemProfile = new SystemProfile { powerDrawIdle = 20f, heatGenerated = 5f };
            brick.Desc("Place on a base or station with a shipyard: repairs the ship landed there, paid in plates from the base's storage.")
                 .Tab(UI.Codex.CodexTab.BLOCK_SYSTEM, false);
            Mod.Log.Msg("Registered Repair Dock as brick " + Mod.BrickId);
        }
    }

    // Materials are consumed from a baked table in ID order; spare entries are empty, so build ours from stock ones.
    [HarmonyPatch(typeof(Brick), "LoadResourcesRuntime")]
    static class CopyMaterials
    {
        const int Hull = 0, Glow = 1, Panel = 2;

        static void Prefix(Brick __instance)
        {
            if (__instance != Brick.BRICK_INDEX[Mod.BrickId]) return;
            try { Build(); }
            catch (Exception e) { Mod.Log.Error("Repair Bay materials failed: " + e); }
        }

        static void Build()
        {
            var table = VoxelMaterialResources.materialParamDatas;
            var start = VoxelMaterialResources.MaterialCount;
            if (start + Panel >= table.Length)
            {
                Mod.Log.Error($"No room for Repair Bay materials: table {table.Length}, start {start}");
                return;
            }
            var hull = TabHull.HULL_A.VoxelMaterials[0];
            hull.BorderMode = BorderMode.noBorder;
            // Plain painted hull with no border overlay, so the panels show no seams or scratched raw texture.
            var panel = TabSystem.THRUSTER_SQUARE.VoxelMaterials[3];
            // A flat, uniformly glowing texture that takes the block's paint colour.
            var glow = TabConstructionBrick.GLOW_TILE.VoxelMaterials[1];
            glow.dynDataPixel = DynData_Pixel.glowFactor; // brightness follows the bay's on/off state
            glow.BorderMode = BorderMode.noStretch;       // keeps the texture still when resized
            Set(table, start, Hull, hull);
            Set(table, start, Glow, glow);
            Set(table, start, Panel, panel);
            Mod.Log.Msg($"Repair Bay materials at {start}..{start + Panel} of {table.Length}");
        }

        // The renderer reads resize behaviour from the material, and each block must use its own table entry.
        // The whole table reaches the GPU after every brick has loaded, so no extra upload is needed.
        static void Set(VoxelMaterial[] table, int start, int slot, VoxelMaterial material)
        {
            material.Id = start + slot;
            material.thresholdGeoHigh = Mod.ResizeThreshold;
            material.thresholdGeoLow = -Mod.ResizeThreshold;
            material.scalingDeformLinkMode = ResizeMode.LinkMode.free; // flat would stretch x and z past the thresholds
            table[start + slot] = material;
        }
    }

    [HarmonyPatch]
    static class MeshLoader
    {
        static Mesh mesh;

        // The collider loads its own copy of the mesh, so both constructors need patching.
        static IEnumerable<MethodBase> TargetMethods() => new MethodBase[]
        {
            AccessTools.Constructor(typeof(VoxelMesh), new[]
                { typeof(string), typeof(Brick), typeof(float), typeof(ResizeMode), typeof(bool), typeof(bool) }),
            AccessTools.Constructor(typeof(VoxelItemCollider), new[]
                { typeof(VoxelMesh), typeof(Manager.ColliderShapeCommon.Shape), typeof(Bounds), typeof(SubShape[]) })
        };

        static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> code)
        {
            var load = AccessTools.Method(typeof(MeshLoader), nameof(Load));
            foreach (var ins in code)
            {
                if (ins.operand is MethodInfo m && m.Name == "Load" && m.DeclaringType == typeof(Resources))
                    yield return new CodeInstruction(OpCodes.Call, load);
                else
                    yield return ins;
            }
        }

        public static Mesh Load(string path)
        {
            if (path != Mod.MeshPath) return Resources.Load<Mesh>(path);
            if (mesh == null)
            {
                try { mesh = ReadObj(); }
                catch (Exception e)
                {
                    // A broken model file must not stop every block from loading, so fall back to a stock shape.
                    Mod.Log.Error("Repair Bay model failed to load, using the reactor shape: " + e.Message);
                    return Resources.Load<Mesh>("Shapes/systems/reactor_small");
                }
                mesh.hideFlags = HideFlags.DontUnloadUnusedAsset; // survives the game's asset reloads
            }
            return mesh;
        }

        static Mesh ReadObj()
        {
            var file = Path.Combine(EvoCore.Paths.UserData, "RepairBay", "repair_bay.obj");
            var verts = new List<Vector3>();
            var uvs = new List<Vector2>();
            var tris = new List<int>();
            float F(string s) => float.Parse(s, CultureInfo.InvariantCulture);
            foreach (var line in File.ReadAllLines(file))
            {
                var p = line.Split(' ');
                if (p[0] == "v") verts.Add(new Vector3(F(p[1]), F(p[2]), F(p[3])));
                else if (p[0] == "vt") uvs.Add(new Vector2(F(p[1]), F(p[2])));
                else if (p[0] == "f") tris.AddRange(p.Skip(1).Select(x => int.Parse(x) - 1));
            }
            var result = new Mesh { name = "repair_bay" };
            result.SetVertices(verts);
            result.SetUVs(0, uvs);
            result.SetTriangles(tris, 0);
            result.RecalculateNormals();
            result.RecalculateTangents();
            result.RecalculateBounds();
            return result;
        }
    }
}
