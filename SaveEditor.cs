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
    // name. An "Edit saves" button shows on the character select screen and opens a small window. Each change first
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
            part = EvoFixes.Mod.AddPart("SaveEditor", "Save editor", "Change a save's game mode or name from the character select screen", DrawTab);

        static void DrawTab()
        {
            GUILayout.Label("On the character select screen, press Edit saves (bottom right). A backup of the save file is made before each change.");
            GUILayout.Label("Appearance and the save's mods: load the save, then Escape > Options > Player or Mods.");
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
                if (!open)
                {
                    if (GUI.Button(new Rect(Screen.width - 180, Screen.height - 56, 160, 36), "Edit saves")) Open();
                }
                else
                {
                    if (window.x == 0) window.center = new Vector2(Screen.width / 2f, Screen.height / 2f);
                    window = GUI.Window(0x5A7E, window, DrawWindow, "Edit saves");
                }
            }
            finally { GUI.skin = oldSkin; }
        }

        // The character list is hidden while the window is open, so a click here can't also start a save.
        static void Open()
        {
            open = true;
            message = "";
            Refresh();
            UI_CharacterSelection.IsActive.Value = false;
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
