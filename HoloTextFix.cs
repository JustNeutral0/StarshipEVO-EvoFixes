using System.IO;
using HarmonyLib;
using EvoFixes;
using TMPro;
using UnityEngine;
using UnityEngine.TextCore;

namespace HoloTextFix
{
    public static class Mod
    {
        public static EvoCore.ModLog Log;
        static EvoCore.Setting<bool> enabled;
        public static bool Enabled => enabled == null || enabled.Value;

        public static void Init()
        {
            Log = EvoCore.Log.For("HoloTextFix");
            var category = EvoCore.Settings.For("HoloTextFix", "Holo text fix");
            enabled = category.Add("Enabled", true, "Draw in-world screen text (text screens, kiosks) as letters instead of squares");
            EvoFixes.Mod.AddPart("HoloTextFix", "Holo text", "Text screens and kiosks show letters instead of squares", DrawTab, enabled);
        }

        static void DrawTab()
        {
            GUILayout.Label(Atlas.Status);
            GUILayout.Label(Repack.Status);
            GUILayout.Label("Takes effect the next time the game starts.");
        }
    }

    // In-world text (text screen blocks, station kiosks, cockpit displays) uses the font "Microgramma D Extended
    // Bold SDF Holo". In the Unity 6 build its atlas texture is blank: every pixel 0, so the text has no letters to
    // draw and the holo shader's soft edges show each character as a grey box. The 2019 build's atlas is fine, but
    // the font was re-packed for Unity 6, so its glyphs sit elsewhere.
    // tools\holo_atlas.py rebuilds the atlas from the user's own 2019 install: each glyph (with its 5 px distance
    // field margin) moved to its Unity 6 rect. The result, raw Alpha8 bottom row first, lives in
    // UserData\EvoFixes\holo_atlas.alpha8 and is copied into the font's texture the first time it's drawn.
    [HarmonyPatch(typeof(TextMeshPro), "GenerateTextMesh")]
    static class Atlas
    {
        const string AtlasName = "Microgramma D Extended Bold SDF Holo Atlas";
        const int Size = 512;
        // 'A' in the Unity 6 packing the file was built for. A game update that re-packs the font changes this, and
        // the file would put letters in the wrong places, so it's left alone then.
        static readonly GlyphRect PackedA = new GlyphRect(268, 6, 62, 41);
        public static string Status = "The holo font hasn't been drawn yet.";
        static bool tried;

        static string FilePath => Path.Combine(EvoCore.Paths.UserData, "EvoFixes", "holo_atlas.alpha8");

        static void Prefix(TextMeshPro __instance)
        {
            if (tried || !Mod.Enabled) return;
            var font = __instance.font;
            var atlas = font != null ? font.atlasTexture : null;
            if (atlas == null || atlas.name != AtlasName) return;
            tried = true;
            Status = Repair(font, atlas);
            Mod.Log.Msg(Status);
        }

        static string Repair(TMP_FontAsset font, Texture2D atlas)
        {
            if (atlas.width != Size || atlas.height != Size || atlas.format != TextureFormat.Alpha8)
                return $"Left alone: the atlas is {atlas.width}x{atlas.height} {atlas.format}, not the {Size}x{Size} Alpha8 the fix was built for.";
            if (!font.characterLookupTable.TryGetValue('A', out var a) || !a.glyph.glyphRect.Equals(PackedA))
                return "Left alone: the font's glyph layout has changed (a game update?); rebuild holo_atlas.alpha8 with tools\\holo_atlas.py.";
            if (!File.Exists(FilePath)) return $"Missing {FilePath}; build it with tools\\holo_atlas.py.";
            var bytes = File.ReadAllBytes(FilePath);
            if (bytes.Length != Size * Size) return $"{FilePath} is {bytes.Length} bytes, expected {Size * Size}.";
            var fixedAtlas = new Texture2D(Size, Size, TextureFormat.Alpha8, false);
            fixedAtlas.LoadRawTextureData(bytes);
            fixedAtlas.Apply(false, false);
            Graphics.CopyTexture(fixedAtlas, 0, 0, atlas, 0, 0);
            Object.Destroy(fixedAtlas);
            return "Repaired the holo font's blank atlas: in-world screen text draws as letters.";
        }
    }

    // The holo shader ("Custom/TMP Holo Text") was written for the 2019 TextMeshPro, which packed each character's
    // face-texture UV into uv2.x (9 bits each) and its SDF scale into uv2.y. Unity 6's TextMeshPro puts the scale in
    // uv0.w and the raw face UV in uv2, so the shader reads a scale of 0 to 1 that varies across each character and
    // the letters smear into boxes. After each mesh is built, write the old layout back for every part of the text
    // whose shader isn't a stock one ("TextMeshPro/...", which read the new layout).
    [HarmonyPatch(typeof(TextMeshPro), "GenerateTextMesh")]
    static class Repack
    {
        public static string Status = "No in-world text drawn yet.";
        static bool logged;
        static readonly AccessTools.FieldRef<TextMeshPro, TMP_SubMesh[]> subMeshes =
            AccessTools.FieldRefAccess<TextMeshPro, TMP_SubMesh[]>("m_subTextObjects");

        static void Postfix(TextMeshPro __instance)
        {
            if (!Mod.Enabled) return;
            var info = __instance.textInfo;
            if (info?.meshInfo == null) return;
            for (int i = 0; i < info.materialCount && i < info.meshInfo.Length; i++)
            {
                Mesh mesh;
                Material material;
                if (i == 0) { mesh = __instance.mesh; material = __instance.fontSharedMaterial; }
                else
                {
                    var sub = subMeshes(__instance);
                    if (sub == null || i >= sub.Length || sub[i] == null) continue;
                    mesh = sub[i].mesh; material = sub[i].sharedMaterial;
                }
                var shader = material != null ? material.shader : null;
                if (mesh == null || shader == null || shader.name.StartsWith("TextMeshPro/")) continue;
                var uv0 = info.meshInfo[i].uvs0;
                var uv2 = info.meshInfo[i].uvs2;
                if (uv0 == null || uv2 == null) continue;
                for (int v = 0; v < uv2.Length && v < uv0.Length; v++)
                    uv2[v] = new Vector2(Pack(uv2[v].x - (int)uv2[v].x, uv2[v].y - (int)uv2[v].y), uv0[v].w);
                mesh.uv2 = uv2;
                if (!logged)
                {
                    logged = true;
                    Status = $"Writing the 2019 vertex layout for text drawn with {shader.name}.";
                    Mod.Log.Msg(Status);
                }
            }
        }

        // The 2019 TextMeshPro's PackUV; the shader unpacks it as floor(p / 4096) and p % 4096, each / 512.
        static float Pack(float x, float y) => (int)(x * 511) * 4096 + (int)(y * 511);
    }
}
