using System.Collections.Generic;
using HarmonyLib;
using EvoFixes;
using UnityEngine;
using World;
using World.Starfield;

namespace GalaxyHalo
{
    public static class Mod
    {
        public static EvoCore.ModLog Log;
        static EvoCore.Setting<bool> enabled;
        static EvoCore.Setting<int> count;
        static EvoCore.Setting<bool> midplane;
        static EvoCore.Setting<float> thickness;
        static EvoFixes.Mod.Part part;
        public static bool On => part == null || part.On;
        public static bool Enabled => On && (enabled == null || enabled.Value);
        public static bool Midplane => On && (midplane == null || midplane.Value);
        public static float Thickness => !On || thickness == null ? 1f : Mathf.Clamp(thickness.Value, 1f, 100f);
        public static int Count => count == null ? 6000 : Mathf.Clamp(count.Value, 0, 50000);
        public static string Status = "No starfield built yet.";

        public static void Init()
        {
            Log = EvoCore.Log.For("GalaxyHalo");
            var category = EvoCore.Settings.For("GalaxyHalo", "Galaxy halo");
            enabled = category.Add("Enabled", true, "Add faint stars above and below the galaxy's disk, so looking out of it fades out instead of ending at a hard edge");
            count = category.Add("Stars", 6000, "How many halo stars to add to the sky");
            midplane = category.Add("FromMidplane", true, "Draw the sky as seen from the middle of the galaxy's disk (height 0), so it isn't lopsided near the disk's top or bottom");
            thickness = category.Add("SkyThickness", 15f, "Stretch star heights by this much when placing them in the sky (1 = stock), so stars appear all around, densest along the galactic band");
            part = EvoFixes.Mod.AddPart("GalaxyHalo", "Galaxy sky", "Stars all around the sky, not half the sky empty", DrawTab);
        }

        static void DrawTab()
        {
            var mid = GUILayout.Toggle(midplane.Value, " Sky as seen from the middle of the galaxy's disk");
            if (mid != midplane.Value) { midplane.Value = mid; EvoCore.Settings.SaveAll(); }
            GUILayout.Label(Thickness <= 1f ? "Sky thickness: stock (a thin band)" : $"Sky thickness: x{Thickness:0} (stars all around, densest along the band)");
            var t = Mathf.Round(GUILayout.HorizontalSlider(Thickness, 1f, 40f, GUILayout.Width(300f)));
            if (!Mathf.Approximately(t, Thickness)) { thickness.Value = t; EvoCore.Settings.SaveAll(); }
            var on = GUILayout.Toggle(enabled.Value, " Faint halo stars outside the galaxy's disk");
            if (on != enabled.Value) { enabled.Value = on; EvoCore.Settings.SaveAll(); }
            GUILayout.Label($"Halo stars: {Count}");
            var n = Mathf.RoundToInt(GUILayout.HorizontalSlider(Count, 0f, 20000f, GUILayout.Width(300f)) / 500f) * 500;
            if (n != Count) { count.Value = n; EvoCore.Settings.SaveAll(); }
            if (GUILayout.Button("Rebuild the sky now", GUILayout.Width(200f))) Halo.Rebuild();
            GUILayout.Label(Status);
            GUILayout.Label("Otherwise it applies the next time a system's sky is built (jumping, or loading a save).");
        }
    }

    // The game's galaxy is a very thin disk: every star in its distribution sits within about 0.01 of the galactic
    // plane (galaxy y), and nothing exists above or below it. From a system near the disk's top or bottom, half the
    // sky looks straight out of the galaxy and is empty, cut off from the starry half by a hard straight line (the
    // disk's surface seen from just outside it). Real galaxies thin out gradually. Two changes, both sky only:
    // The sky's stars come from StarfieldParticle.GenerateStars (on a worker thread): every star in the galaxy's
    // distribution bright enough from here, placed at the direction from this system to it. With FromMidplane the
    // directions are taken from this system's spot on the map at height 0, and with SkyThickness each star's height
    // is stretched before taking the direction, as if the disk were that much thicker: stars then fill the whole sky,
    // still densest along the galactic band. Brightness (which stars show) still comes from the true distances from
    // that point. Only the sky changes: the map, travel and systems' positions don't.
    [HarmonyPatch(typeof(StarfieldParticle), "GenerateStars")]
    static class Spread
    {
        static readonly AccessTools.FieldRef<StarfieldParticle, List<Vector4>> positions = AccessTools.FieldRefAccess<StarfieldParticle, List<Vector4>>("positions");
        static readonly AccessTools.FieldRef<StarfieldParticle, List<Vector4>> sizes = AccessTools.FieldRefAccess<StarfieldParticle, List<Vector4>>("sizes");
        static readonly AccessTools.FieldRef<StarfieldParticle, List<Vector4>> colors = AccessTools.FieldRefAccess<StarfieldParticle, List<Vector4>>("colors");
        static readonly AccessTools.FieldRef<StarfieldParticle, float> distance = AccessTools.FieldRefAccess<StarfieldParticle, float>("d");
        static readonly System.Reflection.MethodInfo initParticle = AccessTools.Method(typeof(StarfieldParticle), "InitParticle");

        static bool Prefix(StarfieldParticle __instance)
        {
            if (!Mod.Midplane && Mod.Thickness <= 1f) return true;
            var pos = positions(__instance);
            var size = sizes(__instance);
            var color = colors(__instance);
            var d = distance(__instance);
            var from = PlanetarySystem.CurrentStarData.GalaxyPosition;
            if (Mod.Midplane) from.y = 0f;
            var stretch = Mod.Thickness;
            foreach (var star in Manager.AssetRepoGalaxy.Distribution.StarDatas)
            {
                var delta = star.GalaxyPosition - from;
                // The game's own brightness rule.
                var f = delta.magnitude * 10f;
                var brightness = 0.2f * Mathf.Pow(star.size / Mathf.Pow(f, 2f), 1f / 3f);
                if (!(brightness > 0.3f && brightness < 1000000f)) continue;
                var dir = new Vector3(delta.x, delta.y * stretch, delta.z).normalized;
                pos.Add(d * dir);
                size.Add(new Vector4(brightness, 0f, 30f * dir.x, 30f * dir.y + 50f * dir.z));
                color.Add(new Vector3(star.color.r, star.color.g, star.color.b));
            }
            var sky = __instance;
            Manager.JobManager.AddFastJob(new JobAction(() => initParticle.Invoke(sky, null)));
            return false;
        }
    }

    // GenerateStars fills position, size and colour lists, which InitParticle uploads. Before that upload, add a halo
    // of faint stars: directions dense near the galactic plane and thinning towards its poles, so the edge fades
    // instead of stopping. Seeded by the system's galaxy position, so a system's halo is the same every visit. Sky
    // directions are galaxy axes (the game places stars at the plain galaxy-space direction), so the galactic poles
    // are world up and down.
    [HarmonyPatch(typeof(StarfieldParticle), "InitParticle")]
    static class Halo
    {
        // Density by galactic latitude: Floor of the plane's density at the poles, falling off over Spread of sin(b).
        const float Floor = 0.08f, Spread = 0.22f;
        static readonly AccessTools.FieldRef<StarfieldParticle, List<Vector4>> positions = AccessTools.FieldRefAccess<StarfieldParticle, List<Vector4>>("positions");
        static readonly AccessTools.FieldRef<StarfieldParticle, List<Vector4>> sizes = AccessTools.FieldRefAccess<StarfieldParticle, List<Vector4>>("sizes");
        static readonly AccessTools.FieldRef<StarfieldParticle, List<Vector4>> colors = AccessTools.FieldRefAccess<StarfieldParticle, List<Vector4>>("colors");
        static readonly AccessTools.FieldRef<StarfieldParticle, float> distance = AccessTools.FieldRefAccess<StarfieldParticle, float>("d");

        static void Prefix(StarfieldParticle __instance)
        {
            if (!Mod.Enabled || Mod.Count == 0) return;
            var pos = positions(__instance);
            if (pos == null || pos.Count == 0) return;
            var size = sizes(__instance);
            var color = colors(__instance);
            var d = distance(__instance);
            var at = PlanetarySystem.CurrentStarData.GalaxyPosition;
            var random = new System.Random(at.GetHashCode());
            float Next() => (float)random.NextDouble();
            int added = 0;
            for (int tries = 0; added < Mod.Count && tries < Mod.Count * 40; tries++)
            {
                // Uniform direction on the sphere, kept with the latitude falloff's probability.
                var z = Next() * 2f - 1f;
                var angle = Next() * 2f * Mathf.PI;
                var r = Mathf.Sqrt(1f - z * z);
                var dir = new Vector3(r * Mathf.Cos(angle), z, r * Mathf.Sin(angle));
                if (Next() > Floor + (1f - Floor) * Mathf.Exp(-Mathf.Abs(dir.y) / Spread)) continue;
                // Mostly just above the visibility threshold (0.3), a few brighter; same twinkle terms as the game's.
                var brightness = 0.31f + 0.45f * Mathf.Pow(Next(), 3f);
                pos.Add(d * dir);
                size.Add(new Vector4(brightness, 0f, 30f * dir.x, 30f * dir.y + 50f * dir.z));
                // Old halo stars: warm white to pale orange.
                var warmth = Next();
                color.Add(new Vector4(1f, Mathf.Lerp(0.95f, 0.78f, warmth), Mathf.Lerp(0.9f, 0.6f, warmth), 0f));
                added++;
            }
            Mod.Status = $"Added {added} halo stars to {pos.Count - added} of the game's own in this system's sky.";
        }

        // Rebuild the current system's sky the way arriving in it does.
        public static void Rebuild()
        {
            var sky = StarfieldParticle.instance;
            if (sky == null) { Mod.Status = "No starfield to rebuild (not in a world?)."; return; }
            AccessTools.Method(typeof(StarfieldParticle), "OnNewSystem").Invoke(sky, null);
            Mod.Status = "Rebuilding the sky...";
        }
    }
}
