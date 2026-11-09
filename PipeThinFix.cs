using System.Collections.Generic;
using HarmonyLib;
using EvoFixes;
using UnityEngine;
using VoxelEngine;

namespace PipeThinFix
{
    public static class Mod
    {
        public static EvoCore.ModLog Log;
        static EvoCore.Setting<bool> enabled;
        public static bool Enabled => enabled == null || enabled.Value;

        public static void Init()
        {
            Log = EvoCore.Log.For("PipeThinFix");
            var category = EvoCore.Settings.For("PipeThinFix", "Pipe Thin fix");
            enabled = category.Add("Enabled", true, "Put Pipe Thin's far connection point at the real end of its model");
            EvoFixes.Mod.AddPart("PipeThinFix", "Pipe Thin", "Pipe Thin's far end connects", DrawTab, enabled);
        }

        static void DrawTab()
        {
            GUILayout.Label(ThinPoints.Status);
            GUILayout.Label("Pipes link when placed or when a ship loads, so re-place the pipe or reload after changing this.");
        }
    }

    // The game gives Pipe Thin (brick 43) the Conveyor Tube Short points: the far end sits a quarter block up.
    // Pipes only link when two points meet exactly, so if the model is longer than that the far end never
    // connects and the pipe carries nothing. Measure the model against the plain Pipe (80, far end at 1.0)
    // and put the far point where the model actually ends.
    [HarmonyPatch(typeof(StorageAttachmentPoint), nameof(StorageAttachmentPoint.GetPoints))]
    static class ThinPoints
    {
        const int PIPE_THIN = 43, PIPE = 80;
        const float STOCK_TOP = 0.25f;
        static List<StorageAttachmentPoint> points;
        static bool measured;
        public static string Status = "Not measured yet (happens the first time a Pipe Thin links).";

        static void Postfix(int brickId, ItemStorageType type, ref List<StorageAttachmentPoint> __result)
        {
            if (brickId != PIPE_THIN || type != ItemStorageType.fuelPipe || !Mod.Enabled) return;
            if (!measured) Measure(__result);
            if (points != null) __result = points;
        }

        static void Measure(List<StorageAttachmentPoint> stock)
        {
            measured = true;
            var thin = Brick.BRICK_INDEX[PIPE_THIN];
            var pipe = Brick.BRICK_INDEX[PIPE];
            if (thin?.VoxelMesh == null || pipe?.VoxelMesh == null || stock == null || stock.Count != 2)
            {
                measured = false;
                return;
            }
            Bounds tb = thin.Bounds, pb = pipe.Bounds;
            float pipeLength = pb.size.y;
            if (pipeLength <= 0f)
            {
                measured = false;
                return;
            }
            float top = tb.size.y / pipeLength;
            Mod.Log.Msg($"Pipe Thin bounds {tb.min} .. {tb.max}, Pipe bounds {pb.min} .. {pb.max}; Pipe Thin length = {top:0.###} of a Pipe (stock far point {STOCK_TOP})");
            if (Mathf.Abs(top - STOCK_TOP) < 0.02f)
            {
                Status = $"Model length {top:0.###} matches the stock far point {STOCK_TOP}: nothing to fix, left alone.";
                Mod.Log.Msg(Status);
                return;
            }
            // Snap to the nearest 1/8 so two pipes still meet within the game's 0.01 tolerance.
            top = Mathf.Round(top * 8f) / 8f;
            points = new List<StorageAttachmentPoint>
            {
                stock[0],
                new StorageAttachmentPoint { point = top * Vector3.up, normal = Vector3.up },
            };
            Status = $"Far connection point moved from {STOCK_TOP} to {top} (model measured against Pipe).";
            Mod.Log.Msg(Status);
        }
    }
}
