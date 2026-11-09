using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Manager;
using EvoFixes;
using UnityEngine;

namespace NpcDebug
{
    // Shows why NPC ships behave as they do. It only reads game state and switches the game's own debug views.
    public static class Mod
    {
        const float Interval = 0.5f;
        const float MaxDistance = 10000f;
        const int MaxRows = 15;
        const float SpinLogThreshold = 20f; // degrees per second

        class Row
        {
            public string name, controller;
            public float distance, spin, speed, ratio;
            public bool avoiding;
        }

        static readonly Dictionary<int, Quaternion> lastRotation = new Dictionary<int, Quaternion>();
        static readonly Dictionary<int, float> lastLogged = new Dictionary<int, float>();
        static List<Row> rows = new List<Row>();
        static EvoCore.Setting<bool> hudPref, logPref;
        static bool showHud, logSpinners = true;
        static float nextSample, lastSample, tabSeen = -10f;
        static EvoCore.ModLog log;
        static GUIStyle style;

        public static void Init()
        {
            log = EvoCore.Log.For("NpcDebug");
            var category = EvoCore.Settings.For("NpcDebug", "NPC ships debug");
            hudPref = category.Add("ShipList", false, "Ship list on screen while Mod Settings is closed");
            logPref = category.Add("LogSpinners", true, $"Log ships spinning over {SpinLogThreshold:F0} degrees/s to Latest.log");
            showHud = hudPref.Value;
            logSpinners = logPref.Value;
            hudPref.Changed += (_, value) => showHud = value;
            logPref.Changed += (_, value) => logSpinners = value;
            EvoFixes.Mod.AddPart("NpcDebug", "NPC ships", "Why NPC ships fly as they do: a live list and the game's collision views", DrawTab, tool: true);
        }

        // Sampling only runs while someone is looking, through the HUD or the panel tab.
        public static void Update()
        {
            var watching = showHud || Time.time - tabSeen < 1f;
            if (!watching || Time.time < nextSample) return;
            nextSample = Time.time + Interval;
            try { Sample(); }
            catch (Exception e) { log.Error(e.ToString()); }
        }

        static void DrawTab()
        {
            tabSeen = Time.time;
            var boxes = DebugToggleRepo.ViewNPCCollision.isActive;
            if (GUILayout.Toggle(boxes, " Collision boxes in the world (cyan = clear, red = avoiding)") != boxes)
            {
                DebugToggleRepo.ViewNPCCollision.EnforceState(!boxes);
                DebugToggleRepo.ViewNPCBehaviours.EnforceState(!boxes);
            }
            Toggle(hudPref, " Ship list on screen while this window is closed");
            Toggle(logPref, $" Log ships spinning over {SpinLogThreshold:F0}°/s to Latest.log");
            GUILayout.Space(8);
            GUILayout.Label(Table(false));
        }

        static void Toggle(EvoCore.Setting<bool> entry, string label)
        {
            var value = GUILayout.Toggle(entry.Value, label);
            if (value == entry.Value) return;
            entry.Value = value;
            EvoCore.Settings.SaveAll();
        }

        static void Sample()
        {
            var dt = Time.time - lastSample;
            lastSample = Time.time;
            var result = new List<Row>();
            foreach (var entity in NPCManager.NpcEntities)
            {
                if (entity == null) continue;
                var id = entity.GetHashCode();
                var transform = entity.TransformData;
                var distance = transform.LocalToWorld(Vector3.zero).magnitude;
                var spin = 0f;
                if (lastRotation.TryGetValue(id, out var previous) && dt > 0f && dt < 2f)
                    spin = Quaternion.Angle(previous, transform.rotation) / dt;
                lastRotation[id] = transform.rotation;
                if (distance > MaxDistance) continue;

                var navigation = entity.NpcNavigation;
                var ship = entity.GetComponent<Spaceship>();
                var row = new Row
                {
                    name = entity.EntityName,
                    distance = distance,
                    spin = spin,
                    speed = entity.Rigidbody != null ? entity.Rigidbody.LinearVelocity.magnitude : 0f,
                    avoiding = navigation != null && navigation.isCollidingExpected,
                    ratio = navigation != null ? navigation.collisionDistanceRatio : 0f,
                    controller = ship == null ? "no ship" : ship.aiController == null ? "none" : ship.aiController.GetType().Name,
                };
                result.Add(row);
                if (logSpinners) LogSpinner(id, row);
            }
            rows = result.OrderBy(r => r.distance).Take(MaxRows).ToList();
        }

        // Writes spinning ships to the log, at most every 10 seconds each, so the cause survives after the game closes.
        static void LogSpinner(int id, Row row)
        {
            if (row.spin < SpinLogThreshold) return;
            if (lastLogged.TryGetValue(id, out var time) && Time.time - time < 10f) return;
            lastLogged[id] = Time.time;
            log.Msg($"Spinning: {row.name}, {row.spin:F0} deg/s, speed {row.speed:F1} m/s, " +
                $"avoiding {row.avoiding} ratio {row.ratio:F2}, controller {row.controller}, {row.distance:F0} m away");
        }

        static string Table(bool rich)
        {
            var sb = new StringBuilder();
            if (rows.Count == 0) sb.Append("No NPC ships within 10 km.\n");
            foreach (var r in rows)
            {
                var spun = r.spin >= SpinLogThreshold;
                var spin = rich && spun ? $"<color=orange>{r.spin:F0}°/s</color>" : $"{r.spin:F0}°/s{(spun ? " !" : "")}";
                var avoid = !r.avoiding ? "clear" : rich ? $"<color=red>AVOIDING {r.ratio:F2}</color>" : $"AVOIDING {r.ratio:F2}";
                sb.Append($"{r.name}  {r.distance:F0} m  spin {spin}  {r.speed:F0} m/s  {avoid}  {r.controller}\n");
            }
            return sb.ToString();
        }

        public static void Gui()
        {
            if (!showHud) return;
            if (style == null) style = new GUIStyle(GUI.skin.label) { fontSize = 14, richText = true };
            var text = "<b>NPC ships</b>\n" + Table(true);
            var size = style.CalcSize(new GUIContent(text));
            var x = Screen.width - size.x - 24;
            GUI.Box(new Rect(x - 8, 8, size.x + 16, size.y + 8), GUIContent.none);
            GUI.Label(new Rect(x, 12, size.x + 8, size.y), text, style);
        }
    }
}
