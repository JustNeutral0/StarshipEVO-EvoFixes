using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;
using VoxelEngine;

namespace LifeSupportRecheck
{
    // A life support that once found a leak stays "leaking" for good: the game only re-checks the room when the
    // block is switched on or the ship is edited, and the mode is saved with the ship. A leak that was fixed since,
    // or one seen while the ship was still loading, outlives every reload. 15 s after a world loads and every
    // 2 minutes after that, wake every leaking life support so it checks its room again (the game's own
    // ForceWakeup, as a ship edit does). A real leak just finds the hole again; after 3 tries in a row it is left
    // alone until its mode changes.
    public static class Mod
    {
        public static readonly EvoCore.ModLog Log = EvoCore.Log.For("LifeSupportRecheck");
        public static int Woken, Cleared;
        const float FirstCheck = 15f, Interval = 120f;
        const int MaxTries = 3;
        static float nextCheck = FirstCheck, lastLevelTime;
        static readonly Dictionary<int, int> tries = new Dictionary<int, int>();
        // The mode lives in a private field; its Mode property is private too.
        static readonly FieldInfo SaveDataField = AccessTools.Field(typeof(LifeSupportCommandData), "saveData");
        static EvoFixes.Mod.Part part;

        public static void Init() =>
            part = EvoFixes.Mod.AddPart("LifeSupportRecheck", "Life support recheck", "Life supports stuck on \"leaking\" check their room again", DrawTab);

        static void DrawTab()
        {
            GUILayout.Label($"Every {Interval / 60f:0} minutes, each life support still marked leaking re-checks its room, up to {MaxTries} times in a row.");
            GUILayout.Label($"Re-checks this session: {Woken}, leaks found fixed: {Cleared}");
        }

        public static void Update()
        {
            if (!part.On) return;
            var now = Time.timeSinceLevelLoad;
            if (now < lastLevelTime) { nextCheck = FirstCheck; tries.Clear(); } // a new world or the menu
            lastLevelTime = now;
            if (now < nextCheck) return;
            nextCheck = now + Interval;
            var datas = LifeSupportCommandData.datas;
            if (!datas.IsCreated) return;
            for (int id = 1; id < datas.LengthFast; id++)
            {
                var data = datas[id];
                var ship = data.brickCoord.Parent;
                if (ship == null) continue; // a free slot
                var mode = ((LifeSupportCommandData.LifeSupportSaveData)SaveDataField.GetValue(data)).mode;
                tries.TryGetValue(id, out var count);
                if (mode != LifeSupportCommandData.LifeSupportMode.leaking)
                {
                    if (count > 0 && mode == LifeSupportCommandData.LifeSupportMode.stable && Cleared++ < 50)
                        Log.Msg($"{ship.EntityName}: the leak is gone, the room is sealed");
                    tries.Remove(id);
                    continue;
                }
                // The game's wake-up skips a block with no oxygen left, so that needs no check here.
                if (count >= MaxTries) continue;
                tries[id] = count + 1;
                LifeSupportCommandData.ForceWakeup(id, now);
                Woken++;
            }
        }
    }
}
