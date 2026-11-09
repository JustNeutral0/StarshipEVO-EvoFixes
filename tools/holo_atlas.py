# Rebuilds the holo font atlas that the Unity 6 build ships blank, for EvoFixes' HoloTextFix.
#   python holo_atlas.py            (needs: pip install UnityPy)
# Reads the glyph pixels from the 2019 build (its atlas is intact) and the glyph positions from both builds' font
# assets, moves each glyph (with its 5 px distance-field margin) to its Unity 6 rect, and writes raw Alpha8, bottom
# row first, to the Unity 6 install's UserData\EvoFixes\holo_atlas.alpha8. Re-run it if a game update re-packs the
# font (the mod's tab says so). Neither asset file carries type trees, so the font assets are read from raw bytes:
# a list of glyph records (index, 5 metric floats, rect as 4 ints, scale, atlas index, and in Unity 6 one more int)
# followed by a list of 16-byte character records (type, unicode, glyph index, scale).
import os, struct
import UnityPy
from PIL import Image, ImageChops

OLD = r"D:\Games\sevo unity2019\Starship EVO_Data\resources.assets"
NEW = r"E:\SteamLibrary\steamapps\common\Starship EVO\Starship EVO_Data\resources.assets"
OUT = r"E:\SteamLibrary\steamapps\common\Starship EVO\UserData\EvoFixes\holo_atlas.alpha8"
FONT = b"Microgramma D Extended Bold SDF Holo"
ATLAS = FONT.decode() + " Atlas"
PAD, SIZE = 5, 512


def font_chars(env):
    """unicode -> (x, y, w, h) from the font asset's glyph and character tables."""
    for o in env.objects:
        if o.type.name != "MonoBehaviour": continue
        raw = o.get_raw_data()
        i = raw.find(FONT)
        if i < 0 or i > 64 or struct.unpack_from("<i", raw, i - 4)[0] != len(FONT): continue
        for stride in (48, 52):
            for off in range(0, len(raw) - 8, 4):
                cnt = struct.unpack_from("<i", raw, off)[0]
                end = off + 4 + cnt * stride
                if not 20 <= cnt <= 1000 or end + 4 + cnt * 16 > len(raw) or struct.unpack_from("<i", raw, end)[0] != cnt: continue
                glyphs = {}
                for k in range(cnt):
                    idx, *_, x, y, w, h, scale, atlas = struct.unpack_from("<I5f4ifi", raw, off + 4 + k * stride)
                    glyphs[idx] = (x, y, w, h)
                chars = {}
                for k in range(cnt):
                    kind, uni, gi, scale = struct.unpack_from("<iIIf", raw, end + 4 + k * 16)
                    if kind == 1 and gi in glyphs: chars[uni] = glyphs[gi]
                if 65 in chars and len(chars) == cnt: return chars
    raise SystemExit("font asset not found")


def atlas_alpha(env):
    for o in env.objects:
        if o.type.name == "Texture2D":
            tex = o.read()
            if tex.m_Name == ATLAS and tex.m_Width == SIZE: return tex.image.getchannel("A")
    raise SystemExit("atlas not found")


old_env, new_env = UnityPy.load(OLD), UnityPy.load(NEW)
old_chars, new_chars, old_atlas = font_chars(old_env), font_chars(new_env), atlas_alpha(old_env)
out = Image.new("L", (SIZE, SIZE), 0)
moved = 0
for uni, (nx, ny, nw, nh) in new_chars.items():
    if uni not in old_chars or nw <= 0 or nh <= 0: continue
    ox, oy, ow, oh = old_chars[uni]
    if ow <= 0 or oh <= 0: continue
    # glyph rects are bottom-up; PIL is top-down
    src = old_atlas.crop((ox - PAD, SIZE - (oy + oh + PAD), ox + ow + PAD, SIZE - (oy - PAD)))
    size = (nw + 2 * PAD, nh + 2 * PAD)
    if src.size != size: src = src.resize(size, Image.BILINEAR)
    at = (nx - PAD, SIZE - (ny + nh + PAD))
    out.paste(ImageChops.lighter(out.crop((*at, at[0] + size[0], at[1] + size[1])), src), at)
    moved += 1
os.makedirs(os.path.dirname(OUT), exist_ok=True)
open(OUT, "wb").write(out.transpose(Image.FLIP_TOP_BOTTOM).tobytes())
print(f"{moved} of {len(new_chars)} glyphs moved; 'A' at {new_chars[65]}; wrote {OUT}")
