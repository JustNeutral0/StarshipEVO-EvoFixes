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
                    if (show) MakeRoom(button as RectTransform);
                    // The menu outlives the session, and the game's click handler leaves a pressed button disabled.
                    var click = button.GetComponent<Selectable>();
                    if (click != null) click.enabled = true;
                    if (show && !logged) { logged = true; Log.Msg("Showing the Escape menu's Exit To Title button"); }
                }
                catch (System.Exception e) { Log.Error("Couldn't show the Exit To Title button: " + e); }
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
