using System.Net;
using System.Net.Http;
using System.Net.Sockets;

namespace PclLike.Core.Net;

/// <summary>代理这个地址是从哪儿读出来的 —— 界面上必须写清楚，不然用户不知道该去哪儿改。</summary>
public enum ProxySource
{
    /// <summary>不使用代理，直连。</summary>
    None,

    /// <summary>HTTPS_PROXY / HTTP_PROXY / ALL_PROXY 环境变量。</summary>
    EnvironmentVariable,

    /// <summary>Windows 的"局域网设置"（WinINET）。</summary>
    SystemSettings,

    /// <summary>启动器设置里手填的。</summary>
    Manual,

    /// <summary>系统里没开代理，但在本机常见端口上探到了一个。</summary>
    Detected,
}

/// <summary>最终生效的代理。</summary>
public sealed record ProxySetting(string Url, ProxySource Source, IReadOnlyList<string> Bypass)
{
    /// <summary>直连的表示。</summary>
    public static ProxySetting Direct { get; } = new("", ProxySource.None, Array.Empty<string>());

    public bool IsActive => Source != ProxySource.None && Url.Length > 0;

    public string SourceText => Source switch
    {
        ProxySource.EnvironmentVariable => "环境变量",
        ProxySource.SystemSettings => "系统设置",
        ProxySource.Manual => "手动填写",
        ProxySource.Detected => "探测到本机代理",
        _ => "直连",
    };

    public string DisplayText => IsActive ? $"{Url}（{SourceText}）" : "不使用代理（直连）";
}

/// <summary>
/// 找代理。
///
/// 为什么要有这个东西：.NET 的 <c>HttpClient</c> 默认确实会读系统代理，但只认
/// Windows"局域网设置"里那个开关（ProxyEnable）。而 Clash / v2ray 这类软件
/// 默认**不打开**那个开关 —— 于是启动器直连、超时，用户看着 "连接
/// maven.neoforged.net 超时" 一头雾水：他明明开着代理。
///
/// 所以这里的顺序是：
/// 1. 配置里写死了（手动 / 关闭）就听配置的；
/// 2. 环境变量（命令行用户常用）；
/// 3. 系统设置（开关开着才算）；
/// 4. 开关没开、但 ProxyServer 里留着地址，或者本机常见端口上有东西在听 ——
///    那就是代理客户端，用它（这一步才是真正解决问题的）。
/// </summary>
public static class ProxyResolver
{
    public const string ModeAuto = "auto";
    public const string ModeManual = "manual";
    public const string ModeOff = "off";

    /// <summary>本机代理软件的常见端口，按常见程度排。</summary>
    private static readonly int[] CommonLocalPorts =
    [
        7897,   // Clash Verge / Mihomo 默认
        7890,   // Clash for Windows 默认
        10809,  // v2rayN（http）
        10808,  // v2rayN（socks，也常当 http 用）
        1080,   // 通用 socks
        8889,   // 一些机场客户端
        8080,   // 通用
        1087,   // 老版本 v2rayN
    ];

    private static readonly object Gate = new();
    private static ProxySetting? _current;
    private static string? _currentMode;
    private static string? _currentManual;

    /// <summary>当前生效的代理（带缓存 —— 探测要花最多两秒，不能每次发请求都探）。</summary>
    public static ProxySetting Current
    {
        get
        {
            lock (Gate)
            {
                return _current ??= Resolve(null, null);
            }
        }
    }

    /// <summary>配置变了以后重新解析（设置页改完代理要调一次）。</summary>
    public static ProxySetting Refresh(string? mode, string? manualUrl)
    {
        lock (Gate)
        {
            _current = Resolve(mode, manualUrl);
            _currentMode = mode;
            _currentManual = manualUrl;
            return _current;
        }
    }

    /// <summary>
    /// 解析代理。
    /// </summary>
    /// <param name="mode">auto / manual / off；null 当 auto（再看一眼缓存里的配置）。</param>
    /// <param name="manualUrl">手动模式下的地址，允许只写 "127.0.0.1:7897"。</param>
    public static ProxySetting Resolve(string? mode, string? manualUrl)
    {
        mode = string.IsNullOrWhiteSpace(mode) ? (_currentMode ?? ModeAuto) : mode.Trim();
        manualUrl = string.IsNullOrWhiteSpace(manualUrl) ? _currentManual : manualUrl;

        if (mode.Equals(ModeOff, StringComparison.OrdinalIgnoreCase)) return ProxySetting.Direct;

        if (mode.Equals(ModeManual, StringComparison.OrdinalIgnoreCase))
        {
            var url = Normalize(manualUrl);
            return url.Length == 0 ? ProxySetting.Direct : new ProxySetting(url, ProxySource.Manual, Array.Empty<string>());
        }

        // ① 环境变量
        foreach (var key in new[] { "HTTPS_PROXY", "https_proxy", "HTTP_PROXY", "http_proxy", "ALL_PROXY", "all_proxy" })
        {
            var value = Environment.GetEnvironmentVariable(key);
            var url = Normalize(value);
            if (url.Length > 0) return new ProxySetting(url, ProxySource.EnvironmentVariable, Array.Empty<string>());
        }

        // ② 系统设置
        var winInet = ReadWinInet();
        if (winInet is not null)
        {
            if (winInet.Enabled && winInet.Server.Length > 0)
                return new ProxySetting(winInet.Server, ProxySource.SystemSettings, winInet.Bypass);

            // ③ 开关没开，但地址还留着 —— 那台机器上多半真有个代理客户端在跑，
            //    先试它写的那个端口（比盲猜常见端口准得多）。
            if (winInet.Server.Length > 0)
            {
                var host = HostOf(winInet.Server);
                var port = PortOf(winInet.Server);
                if (host.Length > 0 && port > 0 && CanConnect(host, port))
                    return new ProxySetting(winInet.Server, ProxySource.Detected, winInet.Bypass);
            }
        }

        // ④ 盲探本机常见端口
        foreach (var port in CommonLocalPorts)
        {
            if (!CanConnect("127.0.0.1", port)) continue;
            return new ProxySetting($"http://127.0.0.1:{port}", ProxySource.Detected, Array.Empty<string>());
        }

        return ProxySetting.Direct;
    }

    /// <summary>
    /// 把用户/环境里那种写法（"127.0.0.1:7897"、"socks5://…"）补全成
    /// 能直接喂给 <see cref="WebProxy"/> 的地址。
    /// </summary>
    public static string NormalizeUrl(string? raw) => Normalize(raw);

    /// <summary>把代理装进一个 HttpClientHandler。</summary>
    public static void Apply(HttpClientHandler handler, ProxySetting? setting)
    {
        setting ??= Current;

        if (!setting.IsActive)
        {
            handler.UseProxy = false;
            return;
        }

        handler.UseProxy = true;
        handler.Proxy = Build(setting);
        handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
    }

    /// <summary>把代理装进一个 SocketsHttpHandler。</summary>
    public static void Apply(SocketsHttpHandler handler, ProxySetting? setting)
    {
        setting ??= Current;

        if (!setting.IsActive)
        {
            handler.UseProxy = false;
            return;
        }

        handler.UseProxy = true;
        handler.Proxy = Build(setting);
        handler.DefaultProxyCredentials = CredentialCache.DefaultCredentials;
    }

    /// <summary>
    /// 给 WebView2 用的启动参数。
    ///
    /// WebView2 自己也会读系统设置，但它读的环境变量名和 .NET 不一样，而且
    /// 系统开关没开时它同样看不见代理 —— 所以显式告诉它一遍。
    /// </summary>
    public static string WebView2Argument(ProxySetting? setting = null)
    {
        setting ??= Current;
        return setting.IsActive ? $"--proxy-server={setting.Url}" : "--no-proxy-server";
    }

    /// <summary>把探测结果写成一行给人看的话。</summary>
    public static string Describe() => "网络代理：" + Current.DisplayText;

    // ────────────────────────────────────────────────────────────
    // 内部
    // ────────────────────────────────────────────────────────────

    private static IWebProxy Build(ProxySetting setting)
    {
        var bypass = new List<string> { "127\\.0\\.0\\.1", "localhost", "\\[::1\\]" };

        foreach (var raw in setting.Bypass)
        {
            var item = raw.Trim();
            if (item.Length == 0) continue;
            if (item.Equals("<local>", StringComparison.OrdinalIgnoreCase)) continue;

            // WinINET 的绕过项是通配写法（*.local、10.*），WebProxy.BypassList 要正则
            var pattern = item.Replace(".", "\\.").Replace("*", ".*");
            bypass.Add(pattern);
        }

        return new WebProxy(setting.Url) { BypassList = bypass.ToArray() };
    }

    /// <summary>补全 scheme；空/无效返回空串。</summary>
    private static string Normalize(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";

        var value = raw.Trim().Trim('"');

        // 系统设置里可能是按协议分开写的：http=1.2.3.4:8080;https=1.2.3.4:8443
        if (value.Contains('='))
        {
            foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;

                var scheme = part[..eq].Trim();
                if (!scheme.Equals("https", StringComparison.OrdinalIgnoreCase)) continue;

                var candidate = part[(eq + 1)..].Trim();
                if (candidate.Length > 0) return WithScheme(candidate);
            }

            // 没有 https 项就退而用 http 项
            foreach (var part in value.Split(';', StringSplitOptions.RemoveEmptyEntries))
            {
                var eq = part.IndexOf('=');
                if (eq <= 0) continue;

                var candidate = part[(eq + 1)..].Trim();
                if (candidate.Length > 0) return WithScheme(candidate);
            }

            return "";
        }

        return WithScheme(value);
    }

    private static string WithScheme(string value)
    {
        if (value.Length == 0) return "";

        // 已经写了 scheme 的，注意 socks5:// 这种 .NET 不认，换成 http（
        // 绝大多数本地代理软件的 http 口和 socks 口是分开的，用户填错就直说）
        if (value.Contains("://", StringComparison.Ordinal))
        {
            var lower = value.ToLowerInvariant();
            if (lower.StartsWith("socks5://", StringComparison.Ordinal))
                return "http://" + value["socks5://".Length..];
            if (lower.StartsWith("socks4://", StringComparison.Ordinal))
                return "http://" + value["socks4://".Length..];
            if (lower.StartsWith("socks://", StringComparison.Ordinal))
                return "http://" + value["socks://".Length..];

            return value;
        }

        return "http://" + value;
    }

    private static string HostOf(string server)
    {
        var url = WithScheme(server);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Host : "";
    }

    private static int PortOf(string server)
    {
        var url = WithScheme(server);
        return Uri.TryCreate(url, UriKind.Absolute, out var uri) ? uri.Port : 0;
    }

    /// <summary>半秒内能不能连上 —— 只用来判断"这儿有没有东西在听"。</summary>
    private static bool CanConnect(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var task = client.ConnectAsync(host, port);
            return task.Wait(TimeSpan.FromMilliseconds(500)) && client.Connected;
        }
        catch
        {
            return false;
        }
    }

    private sealed record WinInet(bool Enabled, string Server, IReadOnlyList<string> Bypass);

    /// <summary>
    /// 读 Windows"局域网设置"。
    ///
    /// 只看当前用户（HKCU）—— 系统级（HKLM）那份是策略下发的，改起来要管理员权限，
    /// 而且 .NET 自己也会读，这里重复一遍没意义。
    /// </summary>
    private static WinInet? ReadWinInet()
    {
        if (!OperatingSystem.IsWindows()) return null;

        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Internet Settings");

            if (key is null) return null;

            var enabled = key.GetValue("ProxyEnable") is int flag && flag != 0;
            var server = (key.GetValue("ProxyServer") as string ?? "").Trim();
            var overrides = (key.GetValue("ProxyOverride") as string ?? "").Trim();

            var bypass = overrides.Length == 0
                ? Array.Empty<string>()
                : overrides.Split(';', StringSplitOptions.RemoveEmptyEntries);

            return new WinInet(enabled, server, bypass);
        }
        catch
        {
            // 读注册表失败（权限、策略）就当没有 —— 不能因此让启动器起不来
            return null;
        }
    }
}