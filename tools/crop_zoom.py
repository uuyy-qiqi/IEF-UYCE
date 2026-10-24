# Crop + nearest-neighbour zoom regions of a screenshot for close inspection.
# usage: python crop_zoom.py <src.png> <out.png> x0 y0 x1 y1 [scale]
import sys
from PIL import Image

src, out = sys.argv[1], sys.argv[2]
x0, y0, x1, y1 = (int(v) for v in sys.argv[3:7])
scale = int(sys.argv[7]) if len(sys.argv) > 7 else 3

im = Image.open(src).convert("RGB")
box = im.crop((x0, y0, x1, y1))
box = box.resize((box.width * scale, box.height * scale), Image.NEAREST)
box.save(out)
print(f"{out} {box.width}x{box.height} from ({x0},{y0})-({x1},{y1}) x{scale}")