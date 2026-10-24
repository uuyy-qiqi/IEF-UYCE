using System.Collections.ObjectModel;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using PclLike.Core.Online;

namespace PclLike.Gui;

/// <summary>
/// 联机大厅这一屏。
///
/// 先说清楚这一屏**没有**什么：没有官方中继服务器。PCL 的大厅背后有一套
/// 托管服务，会给你分一个编号、把两个人撮合到一个通道里；我们没有那台机器，
/// 所以也不会假装有 —— 装个连不上的"大厅服务"比没有更糟。
///
/// 那能做的是什么？三件不需要服务器的事：
///
///   1. 局域网世界发现 —— 走 Minecraft 自己的组播协议（224.0.2.60:4445），
///      同一个网里有人"对局域网开放"就能直接看到。这个是真的自动发现。
///   2. 大厅编号 —— 编号本身就是把 ip:port 编进 11 个字符（<see cref="LobbyCode"/>）。
///      所以"加入大厅"= 把编号解回地址、写进这个实例的 servers.dat，
///      进游戏就能在多人游戏列表里看到；"创建大厅"= 拿本机地址编一个码发给朋友。
///      这在局域网、端口映射、虚拟局域网（Radmin/ZeroTier/蒲公英）下都能用。
///   3. 服务器列表 —— 直接读改游戏自己的 servers.dat，不另存一份。
///
/// 三件事都不依赖任何第三方服务，所以它们不会"明天就失效"。
/// </summary>
public sealed class LobbyViewModel : ObservableObject, IDisposable
{
    /// <summary>取当前实例的 GameDir —— servers.dat 就放在那儿。</summary>
    private readonly Func<string> _gameDir;

    private readonly Action<LogKind, string> _log;

    private LanWorldScanner? _scanner;
    private CancellationTokenSource? _scanCts;

    public LobbyViewModel(Func<string> gameDir, Action<LogKind, string> log)
    {
        _gameDir = gameDir;
        _log = log;

        foreach (var ip in LocalIpv4())
            LocalAddresses.Add(ip);

        SelectedAddress = LocalAddresses.FirstOrDefault() ?? "127.0.0.1";

        RefreshServers();
    }

    // ══════════════════════════════════════════════════════════
    //  局域网世界
    // ══════════════════════════════════════════════════════════

    public ObservableCollection<LanRow> Worlds { get; } = [];

    private bool _scanning;

    public bool IsScanning
    {
        get => _scanning;
        private set
        {
            if (Set(ref _scanning, value)) OnPropertyChanged(nameof(ScanButtonText));
        }
    }

    public string ScanButtonText => IsScanning ? "正在扫描…" : "扫描局域网";

    private string _scanNote = "";

    /// <summary>扫描结果的说明行：要么报错，要么说明为什么是空的。</summary>
    public string ScanNote
    {
        get => _scanNote;
        private set
        {
            if (Set(ref _scanNote, value)) OnPropertyChanged(nameof(HasScanNote));
        }
    }

    public bool HasScanNote => _scanNote.Length > 0;

    /// <summary>
    /// 收 5 秒组播。
    ///
    /// 5 秒不是拍的：Minecraft 每 1.5 秒广播一次，5 秒能覆盖 3 轮，
    /// 足够容忍一次丢包，又不会让用户觉得按了没反应。
    /// </summary>
    public async Task ScanLanAsync()
    {
        if (IsScanning) return;

        IsScanning = true;
        ScanNote = "正在听局域网广播（5 秒）…";
        Worlds.Clear();

        _scanCts?.Dispose();
        _scanCts = new CancellationTokenSource();

        try
        {
            // 每次扫描都新建一个 scanner：它绑 0.0.0.0:4445，重用同一个实例
            // 会让"上一轮的 LastError"串到这一轮的提示里。
            _scanner?.Dispose();
            _scanner = new LanWorldScanner();

            var found = await _scanner.ScanAsync(TimeSpan.FromSeconds(5), _scanCts.Token);

            foreach (var w in found)
                Worlds.Add(new LanRow(StripFormatting(w.Motd), w.Address, w.Port));

            ScanNote = found.Count > 0
                ? $"听到 {found.Count} 个世界。"
                : _scanner.LastError is { Length: > 0 } err
                    ? $"没听到任何广播：{err}（可能是防火墙挡了 4445 端口，或者本机占着这个端口）"
                    : "没听到任何广播。同一个局域网里得有人先在游戏里选择「对局域网开放」。";
        }
        catch (OperationCanceledException)
        {
            ScanNote = "扫描已取消。";
        }
        catch (Exception ex)
        {
            ScanNote = $"扫描失败：{ex.Message}";
        }
        finally
        {
            IsScanning = false;
        }
    }

    /// <summary>把扫到的世界写进 servers.dat，进游戏就能直接点。</summary>
    public void AddWorldToServers(LanRow row)
    {
        var n = ServerListFile.AddOrUpdate(ServersPath, new ServerEntry($"局域网 · {row.Motd}", row.Endpoint));
        RefreshServers();

        _log(LogKind.Ok, $"已把 {row.Endpoint} 加进服务器列表（共 {n} 条）");
        Status = $"已加入服务器列表：{row.Endpoint}。进游戏 → 多人游戏 就能看到。";
    }

    /// <summary>
    /// 把 Minecraft 的样式码（§a 之类）去掉再显示。
    ///
    /// 界面上不认这些码，原样显示会看到一堆"§a"；而服务器名里带色是很常见的，
    /// 不能过滤掉整条，只去标记。
    /// </summary>
    private static string StripFormatting(string text)
        => Regex.Replace(text, "§.", "").Trim();

    // ══════════════════════════════════════════════════════════
    //  加入大厅（编号 → 地址）
    // ══════════════════════════════════════════════════════════

    private string _joinCode = "";

    public string JoinCode
    {
        get => _joinCode;
        set
        {
            if (!Set(ref _joinCode, value)) return;

            OnPropertyChanged(nameof(JoinPreview));
            OnPropertyChanged(nameof(CanJoin));
        }
    }

    /// <summary>
    /// 一边打字一边把编号解出来给用户看。
    ///
    /// 这一行是这一屏最要紧的反馈：编号是一串长得差不多的字符，
    /// 用户需要立刻看到"它到底是哪个地址"，而不是点了加入才知道连错地方。
    /// </summary>
    public string JoinPreview
    {
        get
        {
            if (JoinCode.Trim().Length == 0) return "编号形如 60N0-0GER-YXG，朋友发给你的那串。";

            var decoded = LobbyCode.Decode(JoinCode);

            return decoded is null
                ? "这个编号不合法 —— 字符抄错了，或者少抄了几位。"
                : $"解析结果：{decoded.Value.Ip}:{decoded.Value.Port}";
        }
    }

    public bool CanJoin => LobbyCode.Decode(JoinCode) is not null;

    public void Join()
    {
        var decoded = LobbyCode.Decode(JoinCode);

        if (decoded is null)
        {
            Status = "编号不合法，没做任何改动。";
            return;
        }

        var (ip, port) = decoded.Value;
        var endpoint = $"{ip}:{port}";

        var n = ServerListFile.AddOrUpdate(ServersPath, new ServerEntry($"大厅 {JoinCode.Trim()}", endpoint));
        RefreshServers();

        _log(LogKind.Ok, $"大厅编号 {JoinCode.Trim()} → {endpoint}");
        Status = $"编号认出来了：{endpoint}。已写进服务器列表（共 {n} 条），进游戏 → 多人游戏 直接点它。";
    }

    // ══════════════════════════════════════════════════════════
    //  创建大厅（本机地址 → 编号）
    // ══════════════════════════════════════════════════════════

    public ObservableCollection<string> LocalAddresses { get; } = [];

    private string _selectedAddress = "";

    public string SelectedAddress
    {
        get => _selectedAddress;
        set
        {
            if (Set(ref _selectedAddress, value)) OnPropertyChanged(nameof(CanCreateCode));
        }
    }

    private string _portText = "25565";

    public string PortText
    {
        get => _portText;
        set
        {
            if (!Set(ref _portText, value)) return;

            OnPropertyChanged(nameof(CanCreateCode));
            OnPropertyChanged(nameof(PortHint));
        }
    }

    public bool CanCreateCode => LocalAddresses.Count > 0 && TryPort(out _);

    /// <summary>
    /// 重新检测本机地址。
    ///
    /// 插拔网线、连上 ZeroTier 之类的虚拟网卡之后地址会变，所以得能手动重来一次。
    /// 能保留的旧选择尽量保留 —— 用户刚才是看着那个地址在操作，别把他的手感打乱。
    /// </summary>
    public void RefreshLocalAddresses()
    {
        var keep = SelectedAddress;

        LocalAddresses.Clear();
        foreach (var ip in LocalIpv4())
            LocalAddresses.Add(ip);

        SelectedAddress = LocalAddresses.Contains(keep)
            ? keep
            : LocalAddresses.FirstOrDefault() ?? "";

        Status = LocalAddresses.Count > 0
            ? $"检测到 {LocalAddresses.Count} 个本机地址。"
            : "没检测到可用的本机 IPv4 地址 —— 网卡是不是断了？";
    }

    /// <summary>端口填错时立刻说清楚，别等点生成才拒绝。</summary>
    public string PortHint
    {
        get
        {
            if (PortText.Trim().Length == 0) return "游戏默认 25565；改了端口就得填改过的。";

            return TryPort(out var port)
                ? $"编号会指向 {port} 端口。"
                : "端口得是 1~65535 之间的整数。";
        }
    }

    private string _createdCode = "";

    public string CreatedCode
    {
        get => _createdCode;
        private set
        {
            if (Set(ref _createdCode, value)) OnPropertyChanged(nameof(HasCreatedCode));
        }
    }

    public bool HasCreatedCode => _createdCode.Length > 0;

    public void CreateCode()
    {
        if (!TryPort(out var port))
        {
            Status = "端口不合法，没生成编号。";
            return;
        }

        try
        {
            CreatedCode = LobbyCode.Encode(SelectedAddress, port);
        }
        catch (ArgumentException ex)
        {
            CreatedCode = "";
            Status = $"这个地址编不出编号：{ex.Message}";
            return;
        }

        Status = $"把 {CreatedCode} 发给朋友。它在局域网里能直接用；" +
                 "不在同一个网里，得先用虚拟局域网工具把两个人放进同一个网，或者在路由器上做端口映射。";
    }

    public string CreatedHint =>
        $"对应的地址是 {SelectedAddress}:{PortText.Trim()}。先把游戏里的世界「对局域网开放」再发码。";

    // ══════════════════════════════════════════════════════════
    //  服务器列表（游戏自己的 servers.dat）
    // ══════════════════════════════════════════════════════════

    public ObservableCollection<ServerEntry> Servers { get; } = [];

    public string ServersPath => Path.Combine(_gameDir(), "servers.dat");

    public string ServersEmptyText =>
        Servers.Count > 0 ? "" : "还没有服务器。加入大厅、或者扫到局域网世界之后就会出现在这里。";

    public bool HasServers => Servers.Count > 0;

    public void RefreshServers()
    {
        Servers.Clear();
        foreach (var s in ServerListFile.Read(ServersPath))
            Servers.Add(s);

        OnPropertyChanged(nameof(HasServers));
        OnPropertyChanged(nameof(ServersEmptyText));
    }

    public void RemoveServer(ServerEntry entry)
    {
        if (!ServerListFile.Remove(ServersPath, entry.Ip))
        {
            Status = $"服务器列表里没有 {entry.Ip}，没动它。";
            return;
        }

        RefreshServers();
        _log(LogKind.Info, $"已从服务器列表移除 {entry.Ip}");
        Status = $"已移除 {entry.Name}（{entry.Ip}）。";
    }

    // ══════════════════════════════════════════════════════════
    //  状态行
    // ══════════════════════════════════════════════════════════

    private string _status = "";

    public string Status
    {
        get => _status;
        private set
        {
            if (Set(ref _status, value)) OnPropertyChanged(nameof(HasStatus));
        }
    }

    public bool HasStatus => _status.Length > 0;

    public void SetStatus(string text) => Status = text;

    // ══════════════════════════════════════════════════════════
    //  工具
    // ══════════════════════════════════════════════════════════

    private bool TryPort(out int port)
        => int.TryParse(PortText.Trim(), out port) && port is >= 1 and <= 65535;

    /// <summary>
    /// 本机的 IPv4 候选地址。
    ///
    /// 私网地址排前面（192.168 / 10 / 172.16-31）—— 那些才是"发给朋友"能用的；
    /// 虚拟网卡的地址（VPN、WSL、VMware）排在后面，但一个都不藏：用户可能
    /// 正用着 ZeroTier，我要是不列出来他反而没法生成对的码。
    /// </summary>
    private static IEnumerable<string> LocalIpv4()
    {
        List<string> addrs;
        try
        {
            addrs = Dns.GetHostAddresses(Dns.GetHostName())
                .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                .Select(a => a.ToString())
                .Distinct()
                .ToList();
        }
        catch
        {
            return [];
        }

        return addrs.OrderByDescending(IsPrivate);
    }

    private static bool IsPrivate(string ip)
    {
        var parts = ip.Split('.');
        if (parts.Length != 4) return false;

        if (parts[0] == "10") return true;
        if (parts[0] == "192" && parts[1] == "168") return true;
        if (parts[0] == "172" && int.TryParse(parts[1], out var b)) return b is >= 16 and <= 31;

        return false;
    }

    public void Dispose()
    {
        _scanCts?.Cancel();
        _scanCts?.Dispose();
        _scanCts = null;

        _scanner?.Dispose();
        _scanner = null;
    }
}

/// <summary>局域网世界的一行。Motd 是去掉样式码之后的，Endpoint 是 host:port。</summary>
public sealed record LanRow(string Motd, string Address, int Port)
{
    public string Endpoint => $"{Address}:{Port}";

    public string DetailText => $"{Address}:{Port}";
}