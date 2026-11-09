using System.Linq;
using System.Collections.Generic;
using System.Reflection;
using Manager;
using UnityEngine;
using VoxelEngine;
using Game;
using HarmonyLib;
using UI;

namespace LightCruiseFix
{
    // An NPC ship out of light-cruise fuel (ThrustLightCruise under 0.1, the drive's own cut-off) still has its
    // autopilot engage light cruise for long legs. The drive never enters it, the autopilot never lets go, and the
    // ship sits at 0 m/s for good. For NPC ships, refuse the engagement, so they fly the leg at normal speed instead.
    // The player's own ship is left alone: the game tells the player "no fuel" and they can cancel.
    //
    // The same trap mid-flight: a long light cruise can drain an NPC ship's power, its light-cruise thrust drops to
    // zero, and the drive stays in light cruise waiting for a timer that never runs out. A watchdog frees any NPC ship
    // that sits under autopilot at about 0 m/s for 5 s: cancel the autopilot and put the drive back to normal.
    public static class Mod
    {
        public static readonly EvoCore.ModLog Log = EvoCore.Log.For("LightCruiseFix");
        public static int Refused, Freed;
        const float StuckSeconds = 5f;
        static readonly Dictionary<ParentEntity, float> stuckSince = new Dictionary<ParentEntity, float>();
        static float nextCheck;
        static bool warned;
        static EvoFixes.Mod.Part part;
        public static bool On => part == null || part.On;

        public static void Init() =>
            part = EvoFixes.Mod.AddPart("LightCruiseFix", "NPC light cruise", "NPC ships don't get stuck at 0 m/s in light cruise", DrawTab);

        static void DrawTab()
        {
            GUILayout.Label("NPC ships without light-cruise fuel fly long legs at normal speed; ships stuck under light-cruise autopilot at 0 m/s for 5 s are freed.");
            GUILayout.Label($"This session: {Refused} cruise(s) refused, {Freed} ship(s) freed");
        }

        public static void Update()
        {
            if (!On || Time.time < nextCheck || !PlayerState.IsHostOrSolo) return;
            nextCheck = Time.time + 1f;
            foreach (var entity in NPCManager.NpcEntities.ToList())
            {
                try { Check(entity); }
                catch (System.Exception e) { if (!warned) { warned = true; Log.Warning("Watchdog: " + e); } }
            }
            stuckSince.Keys.Where(e => e == null).ToList().ForEach(e => stuckSince.Remove(e));
        }

        static void Check(ParentEntity entity)
        {
            if (entity == null || entity.isDestroyed) return;
            var ship = entity.GetComponent<Spaceship>();
            var moving = entity.Rigidbody != null && entity.Rigidbody.LinearVelocity.magnitude > 1f;
            // Only light cruise: docking and landing also sit still under autopilot, and must be left alone.
            var cruising = ship != null && ship.autoPilot != null && ship.autoPilot.mode == SpaceshipAutoPilot.Mode.autoLightcruise;
            if (!cruising || ship.IsControlledByLocalPlayer || moving) { stuckSince.Remove(entity); return; }
            if (!stuckSince.TryGetValue(entity, out var since)) { stuckSince[entity] = Time.time; return; }
            if (Time.time - since < StuckSeconds) return;
            stuckSince.Remove(entity);
            ship.autoPilot.CancelCurrentMode();
            ship.Drive.ModeVariable.Value = 0;
            if (Freed++ < 50) Log.Msg($"{entity.EntityName}: stuck in light cruise at 0 m/s (light-cruise thrust {entity.Systems.ThrustLightCruise:0.##}); freed it");
        }
    }

    [HarmonyPatch(typeof(SpaceshipAutoPilot), nameof(SpaceshipAutoPilot.EngageAutoLightcruise))]
    static class NoCruiseWithoutFuel
    {
        static readonly FieldInfo ShipField = AccessTools.Field(typeof(SpaceshipAutoPilot), "spaceship");

        static bool Prefix(SpaceshipAutoPilot __instance, TrackingData trackingData)
        {
            var ship = ShipField.GetValue(__instance) as Spaceship;
            if (!Mod.On || ship == null || ship.IsControlledByLocalPlayer || !ship.entity.Identity.IsNPC) return true;
            if (ship.entity.Systems.ThrustLightCruise >= 0.1f) return true;
            if (Mod.Refused++ < 20) Mod.Log.Msg($"{ship.entity.EntityName}: no light-cruise fuel, flying at normal speed instead");
            return false;
        }
    }
}
