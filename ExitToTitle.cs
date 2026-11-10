using System.Linq;
using HarmonyLib;
using UI;
using UnityEngine;
using UnityEngine.UI;

namespace ExitToTitle
{
    // The game's Escape menu already has an "Exit To Title" button (Canvas_MainMenu / Main Menu / Exit To Title),
    // wired to UI_MainMenu.QuitToTitleScreen: save, release the session, stop the host, back to the title screen. The
    // developers left it switched off in the menu prefab. This switches it back on, for single-player and the host
    // only (a joined client's way out stays Exit To Desktop). Experimental: the developers may have hidden it for a
    // reason, and loading another save after it hasn't been tried with every mod.
    public static class Mod
    {
        public static readonly EvoCore.ModLog Log = EvoCore.Log.For("ExitToTitle");
        const string ButtonName = "Exit To Title";
        static EvoFixes.Mod.Part part;
        static bool logged;
        public static bool On => part == null || part.On;

        public static void Init() =>
            part = EvoFixes.Mod.AddPart("ExitToTitle", "Exit to title", "Shows the game's hidden Exit To Title button in the Escape menu (experimental)", DrawTab);

        static void DrawTab()
        {
            GUILayout.Label("The button is the game's own, left switched off by the developers. It saves, then goes back to the title screen.");
            GUILayout.Label("Single-player and host only. Experimental: if loading another save afterwards misbehaves, use Exit To Desktop.");
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
                    // The menu outlives the session, and the game's click handler leaves a pressed button disabled.
                    var click = button.GetComponent<Selectable>();
                    if (click != null) click.enabled = true;
                    if (show && !logged) { logged = true; Log.Msg("Showing the Escape menu's Exit To Title button"); }
                }
                catch (System.Exception e) { Log.Error("Couldn't show the Exit To Title button: " + e); }
            }
        }
    }
}
