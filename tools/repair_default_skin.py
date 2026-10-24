# Repair the bundled default skin.
#
# The file we had was a "feature overlay": only the detail pixels (hair, eyes,
# eyebrows, nose, beard, some cloth) were opaque, and the base skin tone was
# left fully transparent. Alpha 0 in a real skin means "not present", so the
# renderer composited those holes onto white and the head came out with a white
# face.
#
# This script fills every transparent pixel with the base colour of the region
# it sits in, which turns the overlay back into a normal opaque 64x64 skin.
# Opaque pixels are never touched. The hat layer region stays transparent on
# purpose (this skin has no hat layer - if we filled it with hair we would get
# a second dark cube wrapped around the head).
#
# usage: python repair_default_skin.py <in.png> <out.png>

import sys
from PIL import Image

HAIR = (44, 28, 20, 255)      # 2C1C14
SKIN = (196, 138, 105, 255)   # C48A69
SHIRT = (0, 175, 175, 255)    # 00AFAF
SHIRT_D = (0, 135, 143, 255)
PANTS = (74, 60, 145, 255)    # 4A3C91
PANTS_D = (56, 41, 110, 255)
SKIN_D = (150, 101, 76, 255)
SHOE = (75, 59, 47, 255)

# (x0, y0, x1, y1) -> base colour used for transparent pixels inside
REGIONS = [
    # ── head (all six faces) ────────────────────────────────────────────
    ((8, 8, 16, 16), SKIN),        # front: keep the face on skin
    ((0, 8, 8, 16), HAIR),         # right side
    ((16, 8, 24, 16), HAIR),       # left side
    ((8, 0, 16, 8), HAIR),         # top
    ((16, 0, 24, 8), HAIR),        # bottom
    ((24, 8, 32, 16), HAIR),       # back
    # ── body ────────────────────────────────────────────────────────────
    ((16, 16, 40, 32), SHIRT),
    ((28, 16, 32, 20), SHIRT_D),   # body bottom
    # ── right arm / right leg (the model's right, texture's left) ───────
    ((40, 16, 56, 32), SHIRT),
    ((44, 20, 48, 32), SKIN),      # forearm
    ((0, 16, 16, 32), PANTS),
    ((0, 28, 16, 32), SHOE),       # foot
    # ── left leg / left arm (lower half of the sheet) ───────────────────
    ((16, 48, 32, 64), PANTS),
    ((16, 60, 32, 64), SHOE),
    ((32, 48, 48, 64), SHIRT),
    ((36, 52, 40, 64), SKIN),
]

# Filled last so it always wins: the hat layer must stay empty.
KEEP_CLEAR = (32, 0, 64, 16)


def main() -> int:
    src, dst = sys.argv[1], sys.argv[2]
    im = Image.open(src).convert("RGBA")
    if im.size != (64, 64):
        print(f"refusing: expected a 64x64 skin, got {im.size}")
        return 1

    px = im.load()
    filled = 0

    for (x0, y0, x1, y1), colour in REGIONS:
        for y in range(y0, y1):
            for x in range(x0, x1):
                if px[x, y][3] == 0:
                    px[x, y] = colour
                    filled += 1

    hx0, hy0, hx1, hy1 = KEEP_CLEAR
    for y in range(hy0, hy1):
        for x in range(hx0, hx1):
            if px[x, y][3] == 0:
                continue
            px[x, y] = (0, 0, 0, 0)

    im.save(dst)

    left = sum(1 for y in range(64) for x in range(64) if im.getpixel((x, y))[3] == 0)
    print(f"filled={filled} still_transparent={left} -> {dst}")
    return 0


if __name__ == "__main__":
    raise SystemExit(main())