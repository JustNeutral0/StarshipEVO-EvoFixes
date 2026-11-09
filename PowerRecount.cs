using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VoxelEngine;

namespace PowerRecount
{
    // A ship's power output and capacity are running totals: each reactor, solar panel, wind turbine and power cell
    // takes its old share off and puts its new one on whenever it changes. On a joined player's machine, a ship that
    // player spawned has its totals reset to 0 after its reactors were counted (the spawner builds the ship from its
    // own blueprint file; everyone else gets it over the network). The reactors still remember their share, so
    // switching them off takes the total below 0, and the ship reads short by that much for good (issue tracker
    // #8031, reproduced with two copies). Every 2 s on a joined player's machine, add up every power source per ship
    // and put a total that is off back to that sum, once the same difference has been seen twice in a row.
    public static class Mod
    {
        public static readonly EvoCore.ModLog Log = EvoCore.Log.For("PowerRecount");
        public static int Fixed;
        const float Interval = 2f, Tolerance = 0.5f;
        static float nextCheck;
        // Ship index -> the difference seen last time, so a total is only corrected when it stays wrong.
        static readonly Dictionary<int, Vector2> pending = new Dictionary<int, Vector2>();
        static readonly Dictionary<int, Vector2> sums = new Dictionary<int, Vector2>();
        // Wind turbines and power cells keep their share in a private field; solar panels' is public.
        static readonly FieldInfo WindField = AccessTools.Field(typeof(WindTurbineCommandData), "powerGeneratorData");
        static readonly FieldInfo CellField = AccessTools.Field(typeof(PowerCellBaseCommandData), "powerGeneratorData");
        static EvoFixes.Mod.Part part;

        public static void Init() =>
            part = EvoFixes.Mod.AddPart("PowerRecount", "Power recount", "Joined players: ships they spawn no longer show wrong or negative power", DrawTab);

        static void DrawTab()
        {
            GUILayout.Label($"Only on a joined player's machine (the host's totals are right). Every {Interval:0} s each ship's power output and capacity are checked against the sum of its power sources.");
            GUILayout.Label($"Totals corrected this session: {Fixed}");
        }

        public static void Update()
        {
            if (!part.On || !Mirror.NetworkClient.active || Mirror.NetworkServer.active) return;
            var now = Time.unscaledTime;
            if (now < nextCheck) return;
            nextCheck = now + Interval;
            try { Check(); }
            catch (Exception e) { Log.Warning("check failed: " + e.Message); }
        }

        static void Add(int ship, float capacity, float output)
        {
            if (ship <= 0) return;
            sums.TryGetValue(ship, out var s);
            sums[ship] = s + new Vector2(capacity, output);
        }

        static void AddGenerator(SystemPowerCommon.PowerGeneratorData g) => Add(g.parentEntityId, g.powerCapacity, g.powerOutput);

        static void Check()
        {
            var totals = EntitySystemResources.datas;
            if (!totals.IsCreated || BlockEntityManager.instance == null || Manager.Dependency.PlayerManager == null) return; // still joining
            sums.Clear();
            // A released slot is reset to default, so its ship index is 0 and it adds nothing.
            var reactors = SystemsDataResources.ReactorDatas;
            if (reactors.IsCreated)
                for (int id = 1; id < reactors.LengthFast; id++) { var r = reactors[id]; Add(r.entityId, r.powerCapacity, r.powerOutput); }
            var solar = SystemSolarPanel.datas;
            if (solar.IsCreated)
                for (int id = 1; id < solar.LengthFast; id++) AddGenerator(solar[id].powerGeneratorData);
            var wind = WindTurbineCommandData.datas;
            if (wind.IsCreated)
                for (int id = 1; id < wind.LengthFast; id++) AddGenerator((SystemPowerCommon.PowerGeneratorData)WindField.GetValue(wind[id]));
            var cells = PowerCellBaseCommandData.datas;
            if (cells.IsCreated)
                for (int id = 1; id < cells.LengthFast; id++) AddGenerator((SystemPowerCommon.PowerGeneratorData)CellField.GetValue(cells[id]));

            foreach (var ship in BlockEntityManager.Entities)
            {
                if (ship == null || !ship.IsInitialized) continue;
                int index = ship.IndexID;
                if (index <= 0 || index >= totals.Length) continue;
                var data = totals[index];
                sums.TryGetValue(index, out var sum);
                var diff = new Vector2(data.powerCapacity - sum.x, data.powerOutput - sum.y);
                if (Mathf.Abs(diff.x) <= Tolerance && Mathf.Abs(diff.y) <= Tolerance) { pending.Remove(index); continue; }
                if (!pending.TryGetValue(index, out var seen) || (seen - diff).sqrMagnitude > Tolerance * Tolerance)
                {
                    pending[index] = diff; // first sighting: wait one more check
                    continue;
                }
                pending.Remove(index);
                if (Fixed++ < 50)
                    Log.Msg($"{ship.EntityName}: output {data.powerOutput:0} -> {sum.y:0}, capacity {data.powerCapacity:0} -> {sum.x:0}");
                data.powerCapacity = sum.x;
                data.powerOutput = sum.y;
                totals[index] = data;
            }
        }
    }
}
