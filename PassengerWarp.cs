using System;
using System.Threading.Tasks;
using HarmonyLib;
using UnityEngine;
using VoxelEngine;
using Game;
using World;

namespace PassengerWarp
{
    // A warp jump only moves the pilot's machine to the new system. The pilot's ship tells everyone it left
    // (Transform_Sync.SetNewSystem -> RpcOnNewSystem), but what each machine should do with that,
    // Transform_Sync.SetNewSystemAllChildrens, is empty in the build. So a player riding along stays in the old system
    // and is left in the void, and the host keeps the ship filed under the old system when a joined player flew it
    // (it would be saved there). Seen in a real two-PC game, 2026-11-10.
    // Here, when the message arrives: the ship is filed under its new system on every machine, and a player standing
    // on it (or on a ship docked to it) loads that system the way the pilot's machine did, from the next frame.
    public static class Mod
    {
        public static readonly EvoCore.ModLog Log = EvoCore.Log.For("PassengerWarp");
        public static int Followed, Refiled;
        static readonly AccessTools.FieldRef<Transform_Sync, ParentEntity> ShipOf = AccessTools.FieldRefAccess<Transform_Sync, ParentEntity>("parentEntity");
        static EvoFixes.Mod.Part part;
        static ParentEntity pendingShip;
        static StarData pendingStar;
        static bool pending, loading;
        static string last = "nothing yet";

        public static void Init() =>
            part = EvoFixes.Mod.AddPart("PassengerWarp", "Passenger warp", "Multiplayer: players riding on a ship that warps go to the new system with it", DrawTab);

        static void DrawTab()
        {
            GUILayout.Label("When a ship warps, only the pilot's game loads the new system. With this on, a player standing on the ship follows it, and every game files the ship under its new system.");
            GUILayout.Label($"Followed a warp: {Followed} time(s). Ships refiled: {Refiled}. Last: {last}");
        }

        // From the patch below: a ship has just told this machine it is in a new system.
        internal static void OnShipNewSystem(Transform_Sync sync, StarData starData)
        {
            if (!part.On) return;
            var ship = ShipOf(sync);
            if (ship == null) return;
            if (ship.SkyTransform.CurrentSystem != starData)
            {
                ship.SkyTransform.SetCurrentSystem(starData);
                Refiled++;
            }
            // The pilot's own game is already there (it set the system before the message came back to it).
            if (starData == PlanetarySystem.CurrentStarData || WarpCommon.PlayerWarpStatus.state != WarpCommon.WarpState.none ||
                WarpCommon.WarpgateState.mode != WarpCommon.WarpgateMode.none) return;
            if (ParentEntity.PlayerUltimateParentEntity != ship) return;
            pendingShip = ship;
            pendingStar = starData;
            pending = true;
            last = $"{ship.EntityName} warped to {starData}; following";
            Log.Msg(last);
        }

        public static void Update()
        {
            if (loading || PlanetarySystem.InSystemTransition) return;
            if (!pending) { CheckStranded(); return; }
            pending = false;
            var ship = pendingShip;
            var star = pendingStar;
            pendingShip = null;
            if (!part.On || ship == null || star == PlanetarySystem.CurrentStarData) return;
            if (ParentEntity.PlayerUltimateParentEntity != ship) { last = "left the ship before the warp could be followed"; Log.Msg(last); return; }
            Follow(ship, star);
        }

        // A player whose ship is filed under another system than the one loaded here, seen twice a second apart: the
        // message was missed, or came while this was off (a player already stranded is brought back by switching it on).
        static float nextCheck;
        static ParentEntity strandedOn;

        static void CheckStranded()
        {
            if (!part.On || Time.unscaledTime < nextCheck) return;
            nextCheck = Time.unscaledTime + 1f;
            var ship = ParentEntity.PlayerUltimateParentEntity;
            if (ship == null || !Mirror.NetworkClient.active || WarpCommon.PlayerWarpStatus.state != WarpCommon.WarpState.none ||
                WarpCommon.WarpgateState.mode != WarpCommon.WarpgateMode.none ||
                ship.SkyTransform.CurrentSystem == PlanetarySystem.CurrentStarData || !ship.SkyTransform.CurrentSystem.IsValid)
            {
                strandedOn = null;
                return;
            }
            if (strandedOn != ship) { strandedOn = ship; return; }
            strandedOn = null;
            last = $"{ship.EntityName} is in {ship.SkyTransform.CurrentSystem} but {PlanetarySystem.CurrentStarData} is loaded; following";
            Log.Msg(last);
            Follow(ship, ship.SkyTransform.CurrentSystem);
        }

        // What PlanetarySystem.OnUpdate does on the pilot's machine, minus moving the ship: its position comes from the
        // pilot over the network, already in the new system's coordinates.
        static async void Follow(ParentEntity ship, StarData star)
        {
            loading = true;
            try
            {
                SaveManager.UpdateCurrentSystemInfoDate();
                await PlanetarySystem.instance.NewSystem(star, Vector3l.zero);
                PositionManager.ComputeFloatingOrigin();
                Followed++;
                last = $"followed {ship.EntityName} to {star}";
                Log.Msg(last);
            }
            catch (Exception e)
            {
                last = "failed: " + e.Message;
                Log.Error("Following the warp failed: " + e);
            }
            finally { loading = false; }
        }

        // Runs on every machine that gets the message, the host included (the game's own handler skips the host).
        [HarmonyPatch(typeof(Transform_Sync), "UserCode_RpcOnNewSystem")]
        static class Arrived
        {
            static void Postfix(Transform_Sync __instance, StarData starData)
            {
                try { OnShipNewSystem(__instance, starData); }
                catch (Exception e) { Log.Error("Passenger warp check failed: " + e); }
            }
        }
    }
}
