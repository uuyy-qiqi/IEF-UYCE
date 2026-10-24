"""用 PrintWindow 直接渲染 PclLike 窗口，不受 z-order 影响。

上一版用 ImageGrab 抓屏幕失败了：SetForegroundWindow 被 Windows 的前台窗口
保护挡掉，抓到的是桌面上其它程序。PrintWindow 让目标窗口自己往我们的
DC 上画，即使被遮挡也能拿到内容 —— 这是抓被测窗口的正确做法。
"""
import subprocess
import time
import ctypes
from ctypes import wintypes
from PIL import Image

EXE = r"C:\Users\yuwei\Documents\pcl2-like\src\PclLike.Gui\bin\Debug\net8.0-windows\PclLike.exe"
OUT_FULL = r"C:\Users\yuwei\Documents\pcl2-like\icon-window.png"
OUT_ZOOM = r"C:\Users\yuwei\Documents\pcl2-like\icon-titlebar.png"

user32 = ctypes.windll.user32
gdi32 = ctypes.windll.gdi32

proc = subprocess.Popen([EXE])
print(f"已启动 pid={proc.pid}")
time.sleep(9)

hwnd = user32.FindWindowW(None, "PclLike 启动器")
if not hwnd:
    print("没找到窗口")
    proc.terminate()
    raise SystemExit(1)

rect = wintypes.RECT()
user32.GetWindowRect(hwnd, ctypes.byref(rect))
w = rect.right - rect.left
h = rect.bottom - rect.top
print(f"窗口 {w}x{h}  hwnd={hwnd}")

# 截整窗口
hdc = user32.GetWindowDC(hwnd)
memdc = gdi32.CreateCompatibleDC(hdc)
bmp = gdi32.CreateCompatibleBitmap(hdc, w, h)
gdi32.SelectObject(memdc, bmp)

# PW_RENDERFULLCONTENT = 2，对 WPF 这类 DirectComposition 窗口必须用 2，
# 用 0 会得到全黑或空白
ok = user32.PrintWindow(hwnd, memdc, 2)
print(f"PrintWindow -> {ok}")

class BITMAPINFOHEADER(ctypes.Structure):
    _fields_ = [
        ("biSize", wintypes.DWORD), ("biWidth", wintypes.LONG),
        ("biHeight", wintypes.LONG), ("biPlanes", wintypes.WORD),
        ("biBitCount", wintypes.WORD), ("biCompression", wintypes.DWORD),
        ("biSizeImage", wintypes.DWORD), ("biXPelsPerMeter", wintypes.LONG),
        ("biYPelsPerMeter", wintypes.LONG), ("biClrUsed", wintypes.DWORD),
        ("biClrImportant", wintypes.DWORD),
    ]

bmi = BITMAPINFOHEADER()
bmi.biSize = ctypes.sizeof(BITMAPINFOHEADER)
bmi.biWidth = w
bmi.biHeight = -h          # 负值 = 自上而下，省一次翻转
bmi.biPlanes = 1
bmi.biBitCount = 32
bmi.biCompression = 0

buf = ctypes.create_string_buffer(w * h * 4)
gdi32.GetDIBits(memdc, bmp, 0, h, buf, ctypes.byref(bmi), 0)

img = Image.frombuffer("RGBA", (w, h), buf, "raw", "BGRA", 0, 1)
img.convert("RGB").save(OUT_FULL)
print(f"已保存整窗: {OUT_FULL}  {img.size}")

# 标题栏左侧放大：图标是 16x16 渲染，最容易暴露抠图缺陷
zone = img.crop((0, 0, 190, 32)).convert("RGB")
zone = zone.resize((190 * 5, 32 * 5), Image.NEAREST)
zone.save(OUT_ZOOM)
print(f"已保存标题栏 5x: {OUT_ZOOM}  {zone.size}")

gdi32.DeleteObject(bmp)
gdi32.DeleteDC(memdc)
user32.ReleaseDC(hwnd, hdc)

proc.terminate()
print("已关闭 GUI")