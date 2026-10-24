# Crop a 8x8 face region out of a Minecraft skin and zoom it for eyeballing.
# Minecraft skin layout: the head front face occupies (8,8)-(16,16).
# usage: python skin_face.py <skin.png> <out.png> [scale]
import sys
from PIL import Image

src, out = sys.argv[1], sys.argv[2]
scale = int(sys.argv[3]) if len(sys.argv) > 3 else 20

im = Image.open(src).convert("RGBA")
print("skin size:", im.size)
face = im.crop((8, 8, 16, 16))
face = face.resize((face.width * scale, face.height * scale), Image.NEAREST)
face.save(out)
print(f"{out} {face.width}x{face.height}")

# print the face as hex so the palette is inspectable without looking at pixels
for y in range(8, 16):
    row = []
    for x in range(8, 16):
        r, g, b, a = im.getpixel((x, y))
        row.append(f"{r:02x}{g:02x}{b:02x}")
    print(f"y={y:2d} " + " ".join(row))