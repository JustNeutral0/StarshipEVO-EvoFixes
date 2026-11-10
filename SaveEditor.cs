using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Common.SkyApplication;
using Manager.Save;
using MessagePack;
using UI;
using UnityEngine;

namespace SaveEditor
{
    // Changes what the game only lets you pick when making a character: the game mode (Creative or Survival) and the
    // name. A pencil button on each save slot (character select) opens a small window. Each change first
    // copies the save file to <name>\save-editor-backups, then writes it the way the game does (MessagePack,
    // SaveTools.WriteAtomically). Renaming moves the save folder and its <name>.mpc file; everything else in the folder
    // (the world, ships, every mod's data under mods\) goes with it. Appearance and the save's mod list are already in
    // the game: load the save, then Escape > Options > Player or Mods; both are written into the save.
    public static class Mod
    {
        public static readonly EvoCore.ModLog Log = EvoCore.Log.For("SaveEditor");
        static EvoFixes.Mod.Part part;
        public static bool On => part == null || part.On;

        static bool open;
        static Rect window = new Rect(0, 0, 560, 440);
        static Vector2 scroll;
        static List<string> names = new List<string>();
        static string picked, newName = "", message = "";
        static PlayerSave_mp save;

        public static void Init() =>
            part = EvoFixes.Mod.AddPart("SaveEditor", "Save editor", "Change a save's game mode or name: the pencil on each save in character select", DrawTab);

        static void DrawTab()
        {
            GUILayout.Label("On the character select screen, press the pencil next to a save (left of its trash can). A backup of the save file is made before each change.");
            GUILayout.Label("Appearance and the save's mods: load the save, then Escape > Options > Player or Mods.");
        }

        // A solid window: the stock IMGUI window is see-through, and the title logo showed through it.
        static GUIStyle windowStyle;
        static GUIStyle WindowStyle()
        {
            if (windowStyle != null) return windowStyle;
            var back = new Texture2D(1, 1) { hideFlags = HideFlags.HideAndDontSave };
            back.SetPixel(0, 0, new Color(0.13f, 0.14f, 0.15f, 0.98f));
            back.Apply();
            windowStyle = new GUIStyle(GUI.skin.window) { padding = new RectOffset(12, 12, 30, 12), fontSize = 16 };
            windowStyle.normal.background = windowStyle.onNormal.background = back;
            windowStyle.normal.textColor = windowStyle.onNormal.textColor = new Color(0.9f, 0.9f, 0.9f);
            return windowStyle;
        }

        static bool OnSelectScreen => UI_CharacterSelection.IsActive != null && UI_CharacterSelection.IsActive.Value;

        public static void Gui()
        {
            if (!On) { open = false; return; }
            if (!open && !OnSelectScreen) return;
            var oldSkin = GUI.skin;
            GUI.skin = EvoCore.GameLook.Skin();
            try
            {
                if (open)
                {
                    if (window.x == 0) window.center = new Vector2(Screen.width / 2f, Screen.height / 2f);
                    window = GUI.Window(0x5A7E, window, DrawWindow, "Edit saves", WindowStyle());
                }
            }
            finally { GUI.skin = oldSkin; }
        }

        // The character list is hidden while the window is open, so a click here can't also start a save.
        static void Open(string name)
        {
            open = true;
            message = "";
            Refresh();
            if (name != null && names.Contains(name)) Pick(name);
            UI_CharacterSelection.IsActive.Value = false;
        }

        // A pencil button on each save slot, left of its Delete (trash) button: a copy of Delete with the game's own
        // "edit" icon. The date and game mode text move left by a button's width to make room.
        [HarmonyLib.HarmonyPatch(typeof(UI_CharacterSlot), nameof(UI_CharacterSlot.Initialize))]
        static class SlotButton
        {
            static Sprite pencil;

            static void Postfix(UI_CharacterSlot __instance, FileInfo fileInfo)
            {
                if (!On || !__instance.gameObject.activeSelf || fileInfo?.Directory == null) return;
                try { Add(__instance.transform, fileInfo.Directory.Name); }
                catch (Exception e) { Log.Error("Couldn't add the Edit button to a save slot: " + e); }
            }

            static void Add(Transform slot, string name)
            {
                var delete = slot.Find("Delete") as RectTransform;
                if (delete == null || slot.Find("Edit") != null) return;
                var edit = (RectTransform)UnityEngine.Object.Instantiate(delete.gameObject, slot, false).transform;
                edit.name = "Edit";
                var step = delete.sizeDelta.x + 4f;
                edit.anchoredPosition = delete.anchoredPosition - new Vector2(step, 0f);
                if (pencil == null) pencil = Resources.FindObjectsOfTypeAll<Sprite>().FirstOrDefault(s => s.name == "edit");
                var icon = edit.Find("Image")?.GetComponent<UnityEngine.UI.Image>();
                if (icon != null && pencil != null) icon.sprite = pencil;
                var button = edit.GetComponent<UnityEngine.UI.Button>();
                if (button != null)
                {
                    // A fresh event drops the delete call copied with the button.
                    button.onClick = new UnityEngine.UI.Button.ButtonClickedEvent();
                    button.onClick.AddListener(() => Open(name));
                }
                edit.GetComponent<UITemplateButton>()?.SetTooltip("Edit save: game mode, name");
                foreach (var label in new[] { "Mode", "Last Time" })
                    if (slot.Find(label) is RectTransform t) t.anchoredPosition -= new Vector2(step, 0f);
            }
        }

        static void Close()
        {
            open = false;
            save = null;
            picked = null;
            if (UI_CharacterSelection.IsActive != null) UI_CharacterSelection.IsActive.Value = true;   // rebuilds the slots
        }

        static void Refresh()
        {
            names = Directory.Exists(Paths.SAVE_CHARACTER)
                ? new DirectoryInfo(Paths.SAVE_CHARACTER).GetDirectories()
                    .Where(d => File.Exists(PlayerSave_mp.GetPath(d.Name)))
                    .OrderByDescending(d => File.GetLastWriteTime(PlayerSave_mp.GetPath(d.Name)))
                    .Select(d => d.Name).ToList()
                : new List<string>();
        }

        static void Pick(string name)
        {
            picked = name;
            save = PlayerSave_mp.LoadFromDisk(PlayerSave_mp.GetPath(name));
            newName = name;
            message = save == null ? "This save couldn't be read, so it can't be edited." : "";
        }

        static void DrawWindow(int id)
        {
            GUILayout.BeginHorizontal();
            GUILayout.BeginVertical(GUILayout.Width(190));
            scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(330));
            foreach (var name in names)
                if (GUILayout.Toggle(name == picked, name, GUI.skin.button)) { if (name != picked) Pick(name); }
            GUILayout.EndScrollView();
            GUILayout.EndVertical();

            GUILayout.BeginVertical();
            if (save == null) GUILayout.Label(picked == null ? "Pick a save." : "");
            else
            {
                GUILayout.Label("Game mode");
                GUILayout.BeginHorizontal();
                var creative = save.gameMode == PlayerState.GameMode.creative;
                if (GUILayout.Toggle(creative, "Creative", GUI.skin.button) && !creative) SetMode(PlayerState.GameMode.creative);
                if (GUILayout.Toggle(!creative && save.gameMode == PlayerState.GameMode.survival, "Survival", GUI.skin.button) && save.gameMode != PlayerState.GameMode.survival) SetMode(PlayerState.GameMode.survival);
                GUILayout.EndHorizontal();
                GUILayout.Space(14);
                GUILayout.Label("Name");
                newName = GUILayout.TextField(newName ?? "", 48);
                if (GUILayout.Button("Rename") && newName != picked) Rename(newName);
                GUILayout.Space(14);
                GUILayout.Label("Appearance and mods: load the save, then\nEscape > Options > Player or Mods.");
            }
            GUILayout.FlexibleSpace();
            if (message != "") GUILayout.Label(message);
            GUILayout.EndVertical();
            GUILayout.EndHorizontal();
            if (GUILayout.Button("Done")) Close();
            GUI.DragWindow();
        }

        static void SetMode(PlayerState.GameMode mode)
        {
            var old = save.gameMode;
            save.gameMode = mode;
            if (Write(picked)) message = $"{picked} is now {(mode == PlayerState.GameMode.creative ? "Creative" : "Survival")}.";
            else save.gameMode = old;
        }

        static void Rename(string name)
        {
            name = (name ?? "").Trim();
            // The same rules as the game's new character box.
            if (string.IsNullOrWhiteSpace(name) || name.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0 || name.IndexOfAny(Path.GetInvalidPathChars()) >= 0 || string.Equals(name, "con", StringComparison.OrdinalIgnoreCase))
            { message = "That name can't be used for a save folder."; return; }
            var from = PlayerSave_mp.GetDirectoryPath(picked);
            var to = PlayerSave_mp.GetDirectoryPath(name);
            var caseOnly = string.Equals(from, to, StringComparison.OrdinalIgnoreCase);
            if (Directory.Exists(to) && !caseOnly) { message = $"There's already a save called {name}."; return; }
            if (!Backup(picked)) return;
            try
            {
                // A change of case only needs a stop in between on Windows.
                if (caseOnly) { var temp = from + ".rename"; Directory.Move(from, temp); from = temp; }
                Directory.Move(from, to);
                File.Move(Path.Combine(to, picked + ".mpc"), PlayerSave_mp.GetPath(name));
                save.characterName = name;
                var old = picked;
                picked = name;
                if (!Write(name)) return;
                Log.Msg($"Renamed save \"{old}\" to \"{name}\"");
                message = $"Renamed to {name}.";
                Refresh();
            }
            catch (Exception e)
            {
                message = "Renaming failed: " + e.Message;
                Log.Error($"Renaming \"{picked}\" to \"{name}\" failed: {e}");
            }
        }

        static bool Backup(string name)
        {
            try
            {
                var dir = Path.Combine(PlayerSave_mp.GetDirectoryPath(name), "save-editor-backups");
                Directory.CreateDirectory(dir);
                File.Copy(PlayerSave_mp.GetPath(name), Path.Combine(dir, $"{name} {DateTime.Now:yyyy-MM-dd HHmmss}.mpc"), true);
                return true;
            }
            catch (Exception e)
            {
                message = "Couldn't back up the save, so nothing was changed: " + e.Message;
                Log.Error($"Backing up \"{name}\" failed: {e}");
                return false;
            }
        }

        static bool Write(string name)
        {
            try
            {
                if (!Backup(name)) return false;
                SaveTools.WriteAtomically(PlayerSave_mp.GetPath(name), MessagePackSerializer.Serialize(save));
                Log.Msg($"Wrote save \"{name}\" (mode {save.gameMode})");
                return true;
            }
            catch (Exception e)
            {
                message = "Writing the save failed: " + e.Message;
                Log.Error($"Writing \"{name}\" failed: {e}");
                return false;
            }
        }
    }
}
