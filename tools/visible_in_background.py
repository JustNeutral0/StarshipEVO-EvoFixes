"""Find (and with --apply, set) PlayerSettings.visibleInBackground in Starship EVO's globalgamemanagers.

The game ships with visibleInBackground = False, so Unity hides a Borderless (FullScreenWindow) game whenever it loses
focus: alt-tabbing drops it to a small window behind everything else. The setting is read once at startup, so no mod
can change it at runtime; it's one bool byte in the file. This finds that byte by flipping candidate zero bytes in
memory and re-reading PlayerSettings until visibleInBackground alone reads True, then (with --apply, game closed)
writes it, keeping a .bak copy. A game update or Steam's "verify files" puts the original back; run it again then.

usage: python visible_in_background.py [path to globalgamemanagers] [--apply]
"""
import shutil
import sys

import UnityPy
from UnityPy.streams import EndianBinaryReader

DEFAULT = r"E:\SteamLibrary\steamapps\common\Starship EVO\Starship EVO_Data\globalgamemanagers"


def player_settings(env):
    return next(o for o in env.objects if o.type.name == "PlayerSettings")


def read(obj, reader):
    obj.reader = reader
    # The game's PlayerSettings is 4 bytes longer than UnityPy's type tree; the fields up to the window settings
    # still read right, so the size check is skipped.
    return obj.read_typetree(check_read=False)


def main():
    args = [a for a in sys.argv[1:] if not a.startswith("--")]
    path = args[0] if args else DEFAULT
    apply = "--apply" in sys.argv
    data = bytearray(open(path, "rb").read())
    env = UnityPy.load(bytes(data))
    obj = player_settings(env)
    original = obj.reader
    endian = original.endian
    base = read(obj, EndianBinaryReader(bytes(data), endian))
    print("visibleInBackground =", base["visibleInBackground"])
    if base["visibleInBackground"]:
        print("already on; nothing to do")
        return
    start, size = obj.byte_start, obj.byte_size
    found = None
    for offset in range(start, start + size):
        if data[offset] != 0:
            continue
        data[offset] = 1
        try:
            tree = read(obj, EndianBinaryReader(bytes(data), endian))
        except Exception:
            tree = None
        data[offset] = 0
        if tree is None or not tree["visibleInBackground"]:
            continue
        changed = [k for k in base if k != "visibleInBackground" and tree.get(k) != base[k]]
        if not changed:
            found = offset
            break
    if found is None:
        print("couldn't find the byte safely; file left alone")
        sys.exit(1)
    print(f"visibleInBackground is the byte at file offset {found}")
    if not apply:
        print("dry run; pass --apply (with the game closed) to write it")
        return
    shutil.copyfile(path, path + ".bak")
    data[found] = 1
    open(path, "wb").write(data)
    check = read(player_settings(UnityPy.load(bytes(data))), EndianBinaryReader(bytes(data), endian))
    print("written; visibleInBackground now reads", check["visibleInBackground"], "(backup:", path + ".bak)")


if __name__ == "__main__":
    main()
