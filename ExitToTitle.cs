using System;
using System.Diagnostics;
using System.Linq;
using System.Text;
using HarmonyLib;
using UI;
using UnityEngine;
using UnityEngine.UI;

namespace ExitToTitle
{
    // The game's Escape menu already has an "Exit To Title" button (Canvas_MainMenu / Main Menu / Exit To Title),
    // wired to UI_MainMenu.QuitToTitleScreen, which the developers left switched off: it saves and stops the session,
    // but the network manager has no offline scene, so nothing goes back to the title screen; and the game keeps world
    // state in statics set up once at start (habitats, loading tasks, systems), so a second load in one session hangs
    // on the loading screen (tested 2026-11-09). So this shows the button (single-player and host only) and makes it
    // save and quit exactly like Exit To Desktop, after starting a small helper that waits for the game to close and
    // starts it again: the title screen comes up in a fresh game.
    public static class Mod
    {
        public static readonly EvoCore.ModLog Log = EvoCore.Log.For("ExitToTitle");
        const string ButtonName = "Exit To Title";
        static EvoFixes.Mod.Part part;
        static bool logged;
        public static bool On => part == null || part.On;

        public static void Init() =>
            part = EvoFixes.Mod.AddPart("ExitToTitle", "Exit to title", "Shows the game's hidden Exit To Title button in the Escape menu: saves, then restarts the game to its title screen", DrawTab);

        static void DrawTab()
        {
            GUILayout.Label("The button is the game's own, left switched off by the developers: going back to the title inside one game session leaves the next load stuck.");
            GUILayout.Label("So it saves and quits like Exit To Desktop, then starts the game again. Single-player and host only.");
        }

        // Starts a hidden PowerShell that waits for this game to exit, then starts the same exe with the same
        // arguments. Encoded so paths with spaces or quotes need no escaping.
        static bool StartRelauncher()
        {
            try
            {
                var self = Process.GetCurrentProcess();
                var exe = self.MainModule.FileName;
                var dir = System.IO.Path.GetDirectoryName(exe);
                var args = Environment.GetCommandLineArgs().Skip(1).ToArray();
                string Q(string s) => "'" + s.Replace("'", "''") + "'";
                var argList = args.Length == 0 ? "" : " -ArgumentList @(" + string.Join(",", args.Select(Q).ToArray()) + ")";
                var script = $"Wait-Process -Id {self.Id} -ErrorAction SilentlyContinue; Start-Process -FilePath {Q(exe)} -WorkingDirectory {Q(dir)}{argList}";
                var encoded = Convert.ToBase64String(Encoding.Unicode.GetBytes(script));
                var start = new ProcessStartInfo("powershell.exe", "-NoProfile -WindowStyle Hidden -EncodedCommand " + encoded)
                {
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                // Doorstop (which loads BepInEx) marks this process DOORSTOP_INITIALIZED so child processes skip it;
                // inherited, that mark would start the new game without mods.
                foreach (var key in start.EnvironmentVariables.Keys.Cast<string>().Where(k => k.StartsWith("DOORSTOP", StringComparison.OrdinalIgnoreCase)).ToList())
                    start.EnvironmentVariables.Remove(key);
                Process.Start(start);
                Log.Msg("Exit to title: restarting " + exe);
                return true;
            }
            catch (Exception e)
            {
                Log.Error("Exit to title: couldn't start the game again, so nothing was done: " + e);
                return false;
            }
        }

        [HarmonyPatch(typeof(UI_MainMenu), nameof(UI_MainMenu.QuitToTitleScreen))]
        static class Restart
        {
            static bool Prefix(UI_MainMenu __instance)
            {
                if (!On) return true;
                if (StartRelauncher()) __instance.QuitApplication();
                return false;
            }
        }

        [HarmonyPatch(typeof(UI_MainMenu), nameof(UI_MainMenu.SetActive))]
        static class ShowButton
        {
            static void Postfix(UI_MainMenu __instance, bool active)
            {
                if (!active) return;
                try
                {
                    var button = __instance.GetComponentsInChildren<Transform>(true).FirstOrDefault(t => t.name == ButtonName);
                    if (button == null)
                    {
                        if (!logged) { logged = true; Log.Warning("No \"" + ButtonName + "\" button in the Escape menu; the game must have changed"); }
                        return;
                    }
                    var show = On && PlayerState.IsHostOrSolo;
                    if (button.gameObject.activeSelf != show) button.gameObject.SetActive(show);
                    if (show) MakeRoom(button as RectTransform);
                    // The menu outlives the session, and the game's click handler leaves a pressed button disabled.
                    var click = button.GetComponent<Selectable>();
                    if (click != null) click.enabled = true;
                    if (show && !logged) { logged = true; Log.Msg("Showing the Escape menu's Exit To Title button"); }
                }
                catch (Exception e) { Log.Error("Couldn't show the Exit To Title button: " + e); }
            }

            // The prefab has the button in the same spot as Exit To Desktop (the menu has no layout group), so move
            // Exit To Desktop and anything under it down a row and make the box a row taller. Once: after that
            // nothing shares its spot.
            static void MakeRoom(RectTransform ours)
            {
                if (ours == null || ours.parent == null) return;
                var rows = ours.parent.Cast<Transform>().OfType<RectTransform>()
                    .Where(r => r != ours && r.gameObject.activeSelf && r.GetComponent<UITemplateButton>() != null).ToList();
                var under = rows.FirstOrDefault(r => Mathf.Abs(r.anchoredPosition.y - ours.anchoredPosition.y) < 1f);
                if (under == null) return;
                var gaps = rows.Select(r => Mathf.Abs(r.anchoredPosition.y - ours.anchoredPosition.y)).Where(g => g > 1f).ToList();
                var step = gaps.Count > 0 ? gaps.Min() : ours.rect.height + 4f;
                GrowDown(ours.parent as RectTransform, step);
                foreach (var row in rows)
                    if (row.anchoredPosition.y <= ours.anchoredPosition.y + 1f)
                        row.anchoredPosition -= new Vector2(0f, step);
                Log.Msg($"Moved {under.name} and the rows under it down {step:F0} to make room for Exit To Title");
            }

            // One row taller at the bottom; children pinned to a point keep their place on screen (as Mod Settings
            // does for its button).
            static void GrowDown(RectTransform box, float step)
            {
                if (box == null) return;
                var pinned = box.Cast<Transform>().OfType<RectTransform>()
                    .Where(r => r.anchorMin.y == r.anchorMax.y).ToDictionary(r => r, r => r.position);
                box.sizeDelta += new Vector2(0f, step);
                box.anchoredPosition -= new Vector2(0f, step * (1f - box.pivot.y));
                foreach (var pair in pinned) pair.Key.position = pair.Value;
            }
        }
    }
}
