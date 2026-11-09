using System.Collections.Generic;
using System.Linq;
using Game;
using HarmonyLib;
using EvoFixes;
using UnityEngine;
using VoxelEngine;

namespace NpcAvoidFix
{
    public static class Mod
    {
        public static EvoCore.ModLog Log;
        static EvoCore.Setting<bool> enabled, steer;
        static EvoFixes.Mod.Part part;
        public static bool Enabled => part.On && (enabled == null || enabled.Value);
        public static bool Steer => part.On && (steer == null || steer.Value);
        public static int Rescues, Detours;

        public static void Init()
        {
            Log = EvoCore.Log.For("NpcAvoidFix");
            var category = EvoCore.Settings.For("NpcAvoidFix", "NPC avoidance fix");
            enabled = category.Add("Enabled", true, "Free ships stuck spinning in avoidance");
            steer = category.Add("SteerAroundBlocks", true, "Ships flying straight at a target look ahead for blocks and steer around them");
            part = EvoFixes.Mod.AddPart("NpcAvoidFix", "NPC avoidance", "NPC ships stop spinning in place and steer around blocks", DrawTab);
        }

        static void DrawTab()
        {
            Toggle(enabled, " Free ships stuck spinning in avoidance (turn off to compare with the stock game)");
            GUILayout.Label($"Ships freed this session: {Rescues}");
            GUILayout.Label("A ship counts as stuck after 3 s at avoidance distance 0; it then ignores avoidance for 20 s.");
            GUILayout.Space(8);
            Toggle(steer, " Ships flying at a target steer around blocks in the way");
            GUILayout.Label($"Detours started this session: {Detours}   (ships detouring now: {FlyAround.Active})");
            GUILayout.Label("The game's fly-to-target steering has no avoidance at all, so ships rammed stations between them and their target.");
        }

        static void Toggle(EvoCore.Setting<bool> entry, string label)
        {
            var on = GUILayout.Toggle(entry.Value, label);
            if (on == entry.Value) return;
            entry.Value = on;
            EvoCore.Settings.SaveAll();
        }
    }

    // The game measures obstacle distance from the obstacle's bounding sphere, so a ship inside a station's
    // or habitat's sphere always reads 0. Avoidance then orders a fixed turn with zero thrust, and an orbiting
    // ship spins in place forever. A real approach never sits at exactly 0 for long, so treat that as a false
    // alarm and let orbit steering fly the ship out for a while.
    [HarmonyPatch(typeof(VehicleControlInput), nameof(VehicleControlInput.NeedAvoidanceInput))]
    static class AvoidanceTrap
    {
        const float StuckRatio = 0.01f;
        const float StuckSeconds = 3f;
        const float IgnoreSeconds = 20f;

        class State { public float stuckSince = -1f, ignoreUntil; }
        static readonly Dictionary<int, State> states = new Dictionary<int, State>();

        static bool Prefix(ParentEntity entity, ref bool __result)
        {
            if (!Mod.Enabled) return true;
            var navigation = entity == null ? null : entity.NpcNavigation;
            if (navigation == null) return true;
            var state = Get(entity.GetHashCode());
            var now = Time.time;

            if (now < state.ignoreUntil)
            {
                __result = false; // skip avoidance; orbit steering sets the input instead
                return false;
            }
            if (!navigation.isCollidingExpected || navigation.collisionDistanceRatio > StuckRatio)
            {
                state.stuckSince = -1f;
                return true;
            }
            if (state.stuckSince < 0f) state.stuckSince = now;
            if (now - state.stuckSince < StuckSeconds) return true;

            state.stuckSince = -1f;
            state.ignoreUntil = now + IgnoreSeconds;
            Mod.Rescues++;
            Mod.Log.Msg($"{entity.EntityName} was stuck avoiding at distance 0; ignoring avoidance for {IgnoreSeconds:F0} s");
            __result = false;
            return false;
        }

        static State Get(int id)
        {
            if (states.Count > 512) states.Clear(); // ships come and go; old entries hold nothing important
            if (!states.TryGetValue(id, out var state)) states[id] = state = new State();
            return state;
        }
    }

    // FlyToTarget points the ship at its target and thrusts, with no avoidance at all, so a station between
    // the two gets rammed. A few times a second this casts rays ahead through the game's block physics, from
    // the ship's centre and its four sides; if any hits, it looks for the nearest clear direction on rings
    // around the target direction and steers at a point that way instead, thrusting only once roughly facing it.
    [HarmonyPatch(typeof(FlyToTarget), nameof(FlyToTarget.GetVehicleInput))]
    static class FlyAround
    {
        const float Interval = 0.25f;
        const float MinLook = 40f, LookSeconds = 3f;
        const float FacingAngle = 45f;
        const float ClearHold = 1.5f; // keep a detour this long after the way looks clear, so ships don't wobble
        static readonly float[] Rings = { 25f, 50f, 80f, 115f, 160f };
        const int Spokes = 8;

        class State { public float nextCheck, clearSince = -1f; public Vector3 detour; public bool detouring; }
        static readonly Dictionary<int, State> states = new Dictionary<int, State>();
        static readonly VoxelRaycast caster = new VoxelRaycast();

        public static int Active => states.Values.Count(s => s.detouring);

        static void Postfix(FlyToTarget __instance, ref VehicleControlInput __result)
        {
            if (!Mod.Steer) return;
            var ship = __instance.spaceship?.value;
            var entity = ship == null ? null : ship.entity;
            if (entity == null) return;
            var tracking = entity.TargetingSystem.TrackingData;
            if (!tracking.IsValid) return;
            var state = Get(entity.GetHashCode());
            // ParentBounds is in the ship's own space; rays, the target and steering all work in world space.
            // Using it as is only worked for ships sitting near the world origin (near the player).
            var origin = entity.TransformData.LocalToWorld(entity.ParentBounds.center);

            if (Time.time >= state.nextCheck)
            {
                state.nextCheck = Time.time + Interval;
                try { Plan(entity, tracking, origin, state); }
                catch (System.Exception e)
                {
                    state.detouring = false;
                    Mod.Log.Error("Steering check failed: " + e);
                }
            }
            if (!state.detouring) return;

            var orient = ship.OrientController;
            __result.orient = orient.OrientAxis(state.detour);
            var facing = Vector3.Angle(orient.WorldForward, state.detour - origin);
            __result.translation = facing <= FacingAngle ? Vector3.forward * __instance.speedFactor : Vector3.zero;
        }

        static void Plan(ParentEntity entity, UI.TrackingData tracking, Vector3 origin, State state)
        {
            var toTarget = tracking.WorldPosition - origin;
            var distance = toTarget.magnitude;
            if (distance < 1f) { state.detouring = false; return; }
            var direction = toTarget / distance;
            var radius = Mathf.Max(entity.ParentRadius, 2f);
            var speed = entity.Rigidbody != null ? entity.Rigidbody.LinearVelocity.magnitude : 0f;
            var look = Mathf.Max(MinLook, speed * LookSeconds) + radius;

            if (Clear(entity, tracking, origin, direction, Mathf.Min(look, distance), radius))
            {
                if (!state.detouring) return;
                if (state.clearSince < 0f) state.clearSince = Time.time;
                if (Time.time - state.clearSince >= ClearHold) state.detouring = false;
                return;
            }
            state.clearSince = -1f;

            // Try directions on widening rings around the target direction, starting each ring from the side
            // the current detour is on, so a ship keeps going round the way it started.
            var side = Vector3.Cross(direction, Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
            if (state.detouring)
            {
                var current = Vector3.ProjectOnPlane(state.detour - origin, direction);
                if (current.sqrMagnitude > 0.01f) side = current.normalized;
            }
            foreach (var ring in Rings)
            for (int i = 0; i < Spokes; i++)
            {
                var spoke = Quaternion.AngleAxis(i * 360f / Spokes, direction) * side;
                var candidate = Quaternion.AngleAxis(ring, Vector3.Cross(direction, spoke)) * direction;
                if (!Clear(entity, tracking, origin, candidate, look, radius)) continue;
                if (!state.detouring)
                {
                    Mod.Detours++;
                    Mod.Log.Msg($"{entity.EntityName}: blocks between it and its target, steering {ring:F0}° off course");
                }
                state.detouring = true;
                state.detour = origin + candidate * look;
                return;
            }
            // Boxed in on every side: back straight away from the target until something opens up.
            state.detouring = true;
            state.detour = origin - direction * look;
        }

        // A ray from the centre and one from each side, a ship's radius out. Hits on the target itself don't count.
        static bool Clear(ParentEntity entity, UI.TrackingData tracking, Vector3 origin, Vector3 direction, float length, float radius)
        {
            var right = Vector3.Cross(direction, Mathf.Abs(Vector3.Dot(direction, Vector3.up)) > 0.9f ? Vector3.right : Vector3.up).normalized;
            var up = Vector3.Cross(right, direction);
            return Ray(entity, tracking, origin, direction, length)
                && Ray(entity, tracking, origin + right * radius, direction, length)
                && Ray(entity, tracking, origin - right * radius, direction, length)
                && Ray(entity, tracking, origin + up * radius, direction, length)
                && Ray(entity, tracking, origin - up * radius, direction, length);
        }

        static bool Ray(ParentEntity entity, UI.TrackingData tracking, Vector3 from, Vector3 direction, float length)
        {
            VoxelRaycastHit hit;
            if (!caster.Raycast(from, direction, out hit, length, entity, 0) || !hit.isHit) return true;
            return tracking.transformId != 0 && hit.transformId == tracking.transformId;
        }

        static State Get(int id)
        {
            if (states.Count > 512) states.Clear();
            if (!states.TryGetValue(id, out var state)) states[id] = state = new State();
            return state;
        }
    }
}
