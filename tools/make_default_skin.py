#!/usr/bin/env python3
"""
生成内置默认皮肤（Steve）。

为什么要自己画：查不到皮肤的玩家需要一个能看的回退。用一张灰方块最省事，
但在 3D 头部渲染里灰方块看起来像坏了 —— 所以这里按 Mojang 官方 Steve 的
配色手绘一份 64×64 标准皮肤，只有头部是必需的，但把全身也填上，
以后要做全身预览就不用再补。

跑法：
    py tools/make_default_skin.py

输出：
    src/PclLike.Gui/Assets/default_skin.png  (64×64 RGBA)
"""
from pathlib import Path
import struct
import zlib

W = H = 64

# ── Steve 官方配色（取色自 Mojang 默认皮肤）──
SKIN      = (196, 138, 105, 255)   # 脸/身体肤色
SKIN_DARK = (150, 101,  76, 255)   # 侧面/阴影
HAIR      = ( 44,  28,  20, 255)   # 头发
HAIR_LIT  = ( 74,  49,  36, 255)   # 头发高光
EYE_WHITE = (255, 255, 255, 255)
EYE_BLUE  = ( 74,  87, 158, 255)
BROW      = ( 56,  38,  27, 255)
MOUTH     = (150, 101,  76, 255)
MOUTH_DK  = (110,  73,  55, 255)
SHIRT     = ( 62, 148, 191, 255)   # 蓝色上衣
SHIRT_DK  = ( 44, 108, 143, 255)
PANTS     = ( 74,  72, 105, 255)   # 深蓝裤子
PANTS_DK  = ( 52,  50,  76, 255)
SHOE      = ( 56,  56,  56, 255)
CLEAR     = (  0,   0,   0,   0)

img = [[CLEAR for _ in range(W)] for _ in range(H)]


def fill(x, y, w, h, color):
    for yy in range(y, y + h):
        for xx in range(x, x + w):
            if 0 <= xx < W and 0 <= yy < H:
                img[yy][xx] = color


def px(x, y, color):
    if 0 <= x < W and 0 <= y < H:
        img[y][x] = color


# ── 头部正面 (8,8) 8×8 ──────────────────────────────
# 布局对照真正的 Steve（脸部内坐标 0..7 → 图内 x=8..15, y=8..15）：
#   行 0..1  头发（整齐的刘海，不要左右缺口）
#   行 2     头发 + 两侧鬓角
#   行 3     额头（纯肤色）
#   行 4     眉毛（只在眼睛上方两段，不是整条）
#   行 5     眼睛：每只 2 像素，蓝瞳在内、眼白在外
#   行 6     脸颊 + 鼻影
#   行 7     嘴（一小段）+ 下巴
fill(8, 8, 8, 2, HAIR)                 # 行0-1 刘海
# 行2：两侧各留 1 像素鬓角，中间是额头
fill(8, 10, 8, 1, SKIN)
px(8, 10, HAIR); px(15, 10, HAIR)
# 行3：整行额头
fill(8, 11, 8, 1, SKIN)
# 行4：眉毛 —— 左眉 x=10,11；右眉 x=13,14（正中留缝）
px(10, 12, BROW); px(11, 12, BROW)
px(13, 12, BROW); px(14, 12, BROW)
# 行5：眼睛 —— 左眼 x=10,11（白,蓝）；右眼 x=13,14（蓝,白）
px(10, 13, EYE_WHITE); px(11, 13, EYE_BLUE)
px(13, 13, EYE_BLUE);   px(14, 13, EYE_WHITE)
# 行6：鼻影两像素居中（x=11,12）
px(11, 14, SKIN_DARK); px(12, 14, SKIN_DARK)
# 行7：嘴（居中 3 像素）+ 两侧下巴
px(10, 15, MOUTH_DK); px(11, 15, MOUTH_DK); px(12, 15, MOUTH_DK)

# ── 头部侧面 (0,8) 8×8 ──────────────────────────────
fill(0, 8, 8, 2, HAIR)
fill(0, 10, 8, 6, SKIN_DARK)
# 鬓角
px(0, 10, HAIR); px(0, 11, HAIR)
px(0, 12, HAIR)

# ── 头部背面 (24,8) 8×8 ─────────────────────────────
fill(24, 8, 8, 3, HAIR)
fill(24, 11, 8, 5, HAIR_LIT)

# ── 头部顶面 (8,0) 8×8 ──────────────────────────────
fill(8, 0, 8, 8, HAIR)

# ── 头部底面 (16,0) 8×8 ─────────────────────────────
fill(16, 0, 8, 8, SKIN_DARK)

# ── 帽檐层（第二层）(32..56, 0..16)：全透明 ─────────
# Steve 没有帽檐，保持透明即可；SkinHead 会检测到全透明从而不绘制。

# ── 身体：躯干 (16,16) 8×12 ─────────────────────────
fill(16, 16, 8, 4, SHIRT)          # 正面
fill(16, 20, 8, 8, SHIRT_DK)       # 正面下半
# 手臂与躯干的边界阴影
for y in range(16, 32):
    px(16, y, SHIRT_DK)

# 躯干背面 (32,16)
fill(32, 16, 8, 12, SHIRT)

# ── 右臂 (40,16) 4×12 / 左臂 (32,48) ────────────────
fill(40, 16, 4, 12, SHIRT)
fill(32, 48, 4, 12, SHIRT)
fill(44, 16, 4, 12, SHIRT_DK)
fill(44, 48, 4, 12, SHIRT_DK)

# ── 腿部 (0,16) 4×12 / (16,48) ──────────────────────
fill(0, 16, 4, 12, PANTS)
fill(16, 48, 4, 12, PANTS)
fill(4, 16, 4, 12, PANTS_DK)
fill(20, 48, 4, 12, PANTS_DK)
# 鞋
fill(0, 26, 4, 2, SHOE)
fill(16, 58, 4, 2, SHOE)

# ── 其余空白区域填透明（保持默认）───────────────────


def write_png(path: Path):
    raw = bytearray()
    for row in img:
        raw.append(0)                      # filter type 0
        for r, g, b, a in row:
            raw += bytes((r, g, b, a))

    def chunk(tag: bytes, data: bytes) -> bytes:
        return (struct.pack(">I", len(data)) + tag + data
                + struct.pack(">I", zlib.crc32(tag + data) & 0xFFFFFFFF))

    png = b"\x89PNG\r\n\x1a\n"
    png += chunk(b"IHDR", struct.pack(">IIBBBBB", W, H, 8, 6, 0, 0, 0))
    png += chunk(b"IDAT", zlib.compress(bytes(raw), 9))
    png += chunk(b"IEND", b"")

    path.parent.mkdir(parents=True, exist_ok=True)
    path.write_bytes(png)
    print(f"wrote {path}  ({len(png):,} bytes, {W}x{H})")


if __name__ == "__main__":
    root = Path(__file__).resolve().parent.parent
    write_png(root / "src" / "PclLike.Gui" / "Assets" / "default_skin.png")