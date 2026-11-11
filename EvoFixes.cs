using System;
using System.Collections.Generic;
using UnityEngine;

#if !BEPINEX
[assembly: MelonLoader.MelonInfo(typeof(EvoFixes.Mod), "EvoFixes", "1.7.9", "Claude")]
[assembly: MelonLoader.MelonGame("Moonfire Entertainment", "Starship EVO")]
[assembly: MelonLoader.MelonAdditionalDependencies("EvoCore")]
#endif

namespace EvoFixes
{
    // Matched by name by the ModSettings mod, so this mod needs no reference to it.
    [AttributeUsage(AttributeTargets.Method)]
    class ModSettingsTabAttribute : Attribute
    {
        public ModSettingsTabAttribute(string title) { }
    }

    // Game fixes and quality-of-life changes in one mod. Each part keeps its own namespace, log name and
    // settings category from when it was a separate mod, so settings and the Repair Dock block (brick 505)
    // carry over. Each part registers itself (AddPart) in the one "EvoFixes" tab: a row with an on/off switch and a
    // fold-out for its own settings. The loader applies every part's Harmony patches; a part that's off returns
    // early from them.
#if BEPINEX
    [BepInEx.BepInPlugin("evo.evofixes", "EvoFixes", "1.7.9")]
    [BepInEx.BepInDependency("evo.core")]
#endif
    public class Mod : EvoCore.EvoMod
    {
        public class Part
        {
            public string Title, About;
            public EvoCore.Setting<bool> Switch; // null: a tool, which does nothing until used
            public Action Draw;
            public bool Open;
            public bool On => Switch == null || Switch.Value;
        }

        static readonly List<Part> parts = new List<Part>();
        static EvoCore.Settings switches;

        // A part's switch: its own existing entry (an "Enabled" it always had), or a new one under EvoFixes.
        public static Part AddPart(string key, string title, string about, Action draw, EvoCore.Setting<bool> existing = null, bool tool = false)
        {
            if (switches == null) switches = EvoCore.Settings.For("EvoFixes", "EvoFixes");
            var part = new Part
            {
                Title = title,
                About = about,
                Draw = draw,
                Switch = tool ? null : existing ?? switches.Add(key, true, $"{title}: {about}"),
            };
            parts.Add(part);
            parts.Sort((a, b) => string.Compare(a.Title, b.Title, StringComparison.OrdinalIgnoreCase));
            return part;
        }

        protected override void Init()
        {
            NpcAvoidFix.Mod.Init();
            NpcDebug.Mod.Init();
            RepairBayMod.Mod.Init();
            PipeThinFix.Mod.Init();
            LeakFinder.Mod.Init();
            HoloTextFix.Mod.Init();
            GalaxyHalo.Mod.Init();
            LifeSupportRecheck.Mod.Init();
            LifeSupportRecheck.FieldWake.Init();
            LightCruiseFix.Mod.Init();
            PowerRecount.Mod.Init();
            PassengerWarp.Mod.Init();
            GlassMerge.Mod.Init();
            ExitToTitle.Mod.Init();
            SaveEditor.Mod.Init();
            Borderless.Mod.Init();
        }

        protected override void Tick()
        {
            NpcDebug.Mod.Update();
            RepairBayMod.Mod.Update();
            LightCruiseFix.Mod.Update();
            LifeSupportRecheck.Mod.Update();
            LifeSupportRecheck.FieldWake.Update();
            PowerRecount.Mod.Update();
            PassengerWarp.Mod.Update();
            LeakFinder.Mod.Update();
            GlassMerge.Mod.Update();
        }

        protected override void Gui()
        {
            NpcDebug.Mod.Gui();
            SaveEditor.Mod.Gui();
        }

        [ModSettingsTab("EvoFixes")]
        static void DrawTab()
        {
            GUILayout.Label("Click a name for its settings. Switching a part off puts the game back the way it was for that part.");
            foreach (var part in parts)
            {
                GUILayout.BeginHorizontal();
                if (GUILayout.Button((part.Open ? "▾ " : "▸ ") + part.Title, GUI.skin.label, GUILayout.Width(220))) part.Open = !part.Open;
                if (part.Switch != null)
                {
                    var on = GUILayout.Toggle(part.Switch.Value, part.Switch.Value ? " On" : " Off", GUILayout.Width(60));
                    if (on != part.Switch.Value) { part.Switch.Value = on; EvoCore.Settings.SaveAll(); }
                }
                else GUILayout.Label("tool", GUILayout.Width(60));
                GUILayout.Label(part.About);
                GUILayout.EndHorizontal();
                if (!part.Open) continue;
                GUILayout.BeginHorizontal();
                GUILayout.Space(24);
                GUILayout.BeginVertical(GUI.skin.box);
                try { part.Draw?.Invoke(); }
                catch (Exception e) { GUILayout.Label("This part's settings failed to draw: " + e.Message); }
                GUILayout.EndVertical();
                GUILayout.EndHorizontal();
                GUILayout.Space(6);
            }
        }
    }
}
