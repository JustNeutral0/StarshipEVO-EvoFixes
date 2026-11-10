using System;
using System.IO;
using System.Text;
using UnityEngine;

namespace Borderless
{
    // The game ships with PlayerSettings.visibleInBackground = False, so a Borderless game is hidden (and minimizes)
    // whenever it loses focus: alt-tab or a click on another monitor (game issues #7266, #7316). Unity reads that once
    // at startup from Starship EVO_Data\globalgamemanagers, so no code can change it for the running game; this sets
    // the byte in the file, so it holds from the next launch on (the same patch as tools\visible_in_background.py).
    // A game update or Steam's "verify files" puts the original file back: the next launch patches it again.
    // Finding the byte: PlayerSettings' macAppStoreCategory string "public.app-category.games" comes just before it;
    // visibleInBackground is 13 bytes after the string's end, rounded up to 4 (checked with UnityPy on Unity 6000.6,
    // builds 26w44a and 26w45a). The string must be in the file exactly once and the byte must read 0 or 1, else
    // nothing is written. The first change keeps globalgamemanagers.evofixes.bak.
    public static class Mod
    {
        public static readonly EvoCore.ModLog Log = EvoCore.Log.For("Borderless");
        static EvoFixes.Mod.Part part;
        public static bool On => part == null || part.On;
        static string state = "not checked yet";
        static bool hooked;

        const string Anchor = "public.app-category.games";
        const int AfterAnchor = 13;

        public static void Init()
        {
            part = EvoFixes.Mod.AddPart("Borderless", "Borderless alt-tab", "Borderless no longer minimizes when you click away or alt-tab (from the next launch)", DrawTab);
            Apply(true);
        }

        static void DrawTab()
        {
            GUILayout.Label("Sets the game's \"visible in background\" setting in globalgamemanagers, so Borderless (Options > Graphics) stays up when you click away.");
            GUILayout.Label("Unity reads it at startup: a change shows from the next launch. Switching this off puts the game's setting back (also from the next launch).");
            GUILayout.Label("Now: " + state);
            if (GUILayout.Button("Check again", GUILayout.Width(140))) Apply(false);
        }

        static string FilePath => Path.Combine(Application.dataPath, "globalgamemanagers");

        // Writes the byte (1 with the part on, 0 with it off). If the file is in use, tries once more as the game quits.
        static void Apply(bool atStart)
        {
            if (!Application.unityVersion.StartsWith("6000.")) { state = $"not done: Unity {Application.unityVersion} isn't the version this was checked on"; return; }
            var want = (byte)(On ? 1 : 0);
            try
            {
                var data = File.ReadAllBytes(FilePath);
                var at = Find(data);
                if (at < 0) return;
                if (data[at] == want)
                {
                    state = want == 1 ? "set (Borderless stays up when you click away)" : "the game's own setting (off)";
                    return;
                }
                var backup = FilePath + ".evofixes.bak";
                if (!File.Exists(backup)) File.Copy(FilePath, backup);
                Write(at, want);
                state = (want == 1 ? "set" : "put back") + ": takes effect the next time you start the game";
                Log.Msg($"visibleInBackground set to {want} at offset {at} in globalgamemanagers; takes effect next launch");
            }
            catch (IOException e)
            {
                state = "the file was in use; trying again when the game quits";
                if (atStart && !hooked) { hooked = true; Application.quitting += () => Apply(false); }
                else { state = "couldn't write globalgamemanagers: " + e.Message; Log.Warning(state); }
            }
            catch (Exception e)
            {
                state = "failed: " + e.Message;
                Log.Error("Borderless patch failed: " + e);
            }
        }

        static int Find(byte[] data)
        {
            var anchor = Encoding.ASCII.GetBytes(Anchor);
            int found = -1, count = 0;
            for (int i = 0; i + anchor.Length <= data.Length; i++)
            {
                int k = 0;
                while (k < anchor.Length && data[i + k] == anchor[k]) k++;
                if (k == anchor.Length) { found = i; count++; }
            }
            if (count != 1) { state = $"not done: the settings marker is in the file {count} times (a game update changed it?)"; Log.Warning(state); return -1; }
            var end = found + anchor.Length;
            var at = ((end + 3) & ~3) + AfterAnchor;
            if (at >= data.Length || data[at] > 1) { state = "not done: the byte where the setting should be isn't a true/false (a game update changed it?)"; Log.Warning(state); return -1; }
            return at;
        }

        static void Write(int at, byte value)
        {
            using (var f = new FileStream(FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            {
                f.Position = at;
                f.WriteByte(value);
            }
        }
    }
}
