"""图标第三版：放弃 bbox 自动居中，改用显式比例裁脸。

前两版都失败在同一个地方 —— 自动检测主体边界：
  · 第 1 版按包围盒+6%边距 → 人物太小，16px 下是灰点
  · 第 2 版按不透明像素重心 → 左侧蓝色涂鸦也是不透明的，
    bbox 被撑到整张图，裁出来还是一团
本版不再依赖任何检测：直接按目测比例裁「头 + 脸」这一块。
源图 1260x1243，人物头部约在 x 380~880、y 330~790。
"""
from PIL import Image
from collections import deque
import os

SRC = r"C:\Users\yuwei\.dsh\attachments\v1\objects\41\416c9412ddc3a19b43c349d5da8a04b407ff0deb5c722ff243dae6ef2791c559"
OUT_DIR = r"C:\Users\yuwei\Documents\pcl2-like\src\PclLike.Gui\Assets"
ICO = os.path.join(OUT_DIR, "pclike.ico")
PNG = os.path.join(OUT_DIR, "pclike.png")
os.makedirs(OUT_DIR, exist_ok=True)

img = Image.open(SRC).convert("RGBA")
w, h = img.size
px = img.load()

def is_bg(p):
    r, g, b, a = p
    # 阈值放宽到 225：JPEG 在白背景边缘会产生 226~238 的过渡像素，
    # 按 238 判会漏掉它们，残留成图标外围的浅色方框（实测在深色标题栏上很明显）。
    # 同时用「接近中性灰」来判，避免把黄色的头顶物体边缘误删（它有明显色偏）。
    if r < 225 or g < 225 or b < 225:
        return False
    return max(r, g, b) - min(r, g, b) <= 12

# 去背景：只删与边界连通的白色，主体内部的白色（眼睛高光/衣服）保留
visited = [[False] * w for _ in range(h)]
q = deque()
for x in range(w):
    for y in (0, h - 1):
        if not visited[y][x] and is_bg(px[x, y]):
            visited[y][x] = True; q.append((x, y))
for y in range(h):
    for x in (0, w - 1):
        if not visited[y][x] and is_bg(px[x, y]):
            visited[y][x] = True; q.append((x, y))
while q:
    x, y = q.popleft()
    for dx, dy in ((1,0),(-1,0),(0,1),(0,-1)):
        nx, ny = x + dx, y + dy
        if 0 <= nx < w and 0 <= ny < h and not visited[ny][nx] and is_bg(px[nx, ny]):
            visited[ny][nx] = True; q.append((nx, ny))

cleared = 0
for y in range(h):
    for x in range(w):
        if visited[y][x]:
            px[x, y] = (0, 0, 0, 0); cleared += 1
print(f"去背景 {cleared} px ({cleared*100.0/(w*h):.1f}%)")

# 边缘收尾：把紧贴透明区的"半白"像素也抹掉。
# 泛洪只能删纯背景，主体轮廓外侧那一圈抗锯齿产生的浅色像素会留下，
# 缩小后拼成一条淡边 —— 图标四周那圈方框就是这么来的。
for _ in range(2):   # 两遍，处理 1~2 像素宽的抗锯齿带
    edge = []
    for y in range(h):
        for x in range(w):
            if px[x, y][3] == 0:
                continue
            r, g, b, a = px[x, y]
            # 已经很淡，或者颜色很接近白 → 且四邻里有透明 → 判为边缘残留
            near_white = r >= 218 and g >= 218 and b >= 218 and (max(r, g, b) - min(r, g, b) <= 20)
            if not near_white:
                continue
            for dx, dy in ((1,0),(-1,0),(0,1),(0,-1)):
                nx, ny = x + dx, y + dy
                if 0 <= nx < w and 0 <= ny < h and px[nx, ny][3] == 0:
                    edge.append((x, y)); break
    if not edge:
        break
    for x, y in edge:
        px[x, y] = (0, 0, 0, 0)
    print(f"  边缘清理 {len(edge)} px")

# 显式裁剪框：头 + 脸（含头顶黄色物体和耳机）
# 数值按源图目测调过三轮：
#   · (330,300,940,910) 太紧 —— 下巴贴边、黄色物体只露一角
#   · (315,265,955,955) 构图平衡，但头发被切在框边，外围留了一圈深色边
#   · 现在放大到完整包住头发（含左侧被白背景包围的发梢），
#     这样缩下去四周是干净的透明，不会出现"方形底座"
BOX = (285, 240, 990, 990)
print(f"裁剪框 {BOX}  尺寸 {BOX[2]-BOX[0]}x{BOX[3]-BOX[1]}")
crop = img.crop(BOX)
cw, ch = crop.size

# 补正方
sq = max(cw, ch)
canvas = Image.new("RGBA", (sq, sq), (0, 0, 0, 0))
canvas.paste(crop, ((sq - cw) // 2, (sq - ch) // 2), crop)
print(f"正方形 {sq}x{sq}")

# 注意：不再缩小后存 ico。Windows 在 256 档会直接用大图，
# 而小尺寸档由我们显式提供 LANCZOS 结果，避免系统用低质量缩放。
imgs = [(s, canvas.resize((s, s), Image.LANCZOS)) for s in (256,128,64,48,32,24,16)]
imgs.sort(key=lambda t: -t[0])
imgs[0][1].save(ICO, format="ICO", sizes=[(s, s) for s, _ in imgs])
imgs[0][1].save(PNG, format="PNG")
print(f"已写出 {ICO} ({os.path.getsize(ICO)} bytes)")

for s, im in imgs:
    p = os.path.join(OUT_DIR, f"_preview_{s}.png")
    im.save(p)
print("已导出各尺寸预览")