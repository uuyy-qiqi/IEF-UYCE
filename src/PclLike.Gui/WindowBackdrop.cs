using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;

namespace PclLike.Gui;

/// <summary>
/// 窗口背后的"毛玻璃"（亚克力）效果。
///
/// 为什么要有这个文件：主题里的底色是带 alpha 的，窗口本身就是半透明的。
/// 但只做半透明不好看 —— 桌面上的图标和窗口会直接透过来，和启动器自己的内容
/// 糊成一团。亚克力会在窗口背后画一层模糊，透过来的是"被糊掉的桌面"，字就压得住了。
///
/// 手感：全部包在 try 里，任何一步失败都当没这回事。系统不支持（老 Win10、
/// 远程桌面、部分显卡驱动）时窗口就退回成普通的浅色面板 —— 还能用，只是不透明。
/// </summary>
internal static class WindowBackdrop
{
    /// <summary>给窗口打开亚克力。tint 是叠在模糊上的那层色调。</summary>
    public static bool TryEnableAcrylic(Window window, Color tint)
    {
        if (window is null) return false;

        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            var accent = new AccentPolicy
            {
                AccentState = AccentState.EnableAcrylicBlurBehind,
                // 2 = 四条边都画上这层 accent，否则最大化后边上会缺一圈
                AccentFlags = 2,
                GradientColor = ToAbgr(tint),
                AnimationId = 0,
            };

            var size = Marshal.SizeOf<AccentPolicy>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, buffer, false);

                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.AccentPolicy,
                    Data = buffer,
                    SizeOfData = size,
                };

                // 这个 API 没进公开文档，在某些系统版本上会直接失败（返回 0）。
                return SetWindowCompositionAttribute(hwnd, ref data) != 0;
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 让 DWM 给窗口画圆角（Windows 11 22000+ 才有）。
    ///
    /// 无边框窗口默认是直角，跟系统的观感对不上；这个属性是"最好有"，所以
    /// 失败就当没这回事 —— Win10 上它只会返回一个错误码。
    /// </summary>
    public static bool TryRoundCorners(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return false;

            // DWMWA_WINDOW_CORNER_PREFERENCE = 33，DWMWCP_ROUND = 2
            var preference = 2;
            return DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int)) == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>关掉亚克力（窗口要变成“真的不透明”时用得上）。</summary>
    public static void Disable(Window window)
    {
        try
        {
            var hwnd = new WindowInteropHelper(window).Handle;
            if (hwnd == IntPtr.Zero) return;

            var accent = new AccentPolicy { AccentState = AccentState.Disabled };
            var size = Marshal.SizeOf<AccentPolicy>();
            var buffer = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(accent, buffer, false);
                var data = new WindowCompositionAttributeData
                {
                    Attribute = WindowCompositionAttribute.AccentPolicy,
                    Data = buffer,
                    SizeOfData = size,
                };
                SetWindowCompositionAttribute(hwnd, ref data);
            }
            finally
            {
                Marshal.FreeHGlobal(buffer);
            }
        }
        catch
        {
            // 关不掉也无所谓 —— 调用方只是想让背景变回不透明而已
        }
    }

    /// <summary>GradientColor 在内存里是 0xAABBGGRR（和 WPF 的 #AARRGGBB 反着来）。</summary>
    private static int ToAbgr(Color c) => (c.A << 24) | (c.B << 16) | (c.G << 8) | c.R;

    private enum AccentState
    {
        Disabled = 0,
        EnableBlurBehind = 3,
        EnableAcrylicBlurBehind = 4,
        EnableHostBackdrop = 5,
    }

    private enum WindowCompositionAttribute
    {
        AccentPolicy = 19,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct AccentPolicy
    {
        public AccentState AccentState;
        public int AccentFlags;
        public int GradientColor;
        public int AnimationId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct WindowCompositionAttributeData
    {
        public WindowCompositionAttribute Attribute;
        public IntPtr Data;
        public int SizeOfData;
    }

    [DllImport("user32.dll")]
    private static extern int SetWindowCompositionAttribute(
        IntPtr hwnd, ref WindowCompositionAttributeData data);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(
        IntPtr hwnd, int attribute, ref int value, int size);
}