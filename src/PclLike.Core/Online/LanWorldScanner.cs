using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;

namespace PclLike.Core.Online;

/// <summary>
/// 局域网上发现的一个世界。
///
/// <paramref name="Address"/> 是**发广播的那台机器的 IP**（从 UDP 包的来源地址取），
/// 不是包里的内容 —— 包里的 [AD] 只有端口号，没有地址。所以同一个包从不同的网卡
/// 发过来就是不同的 Address，这正是我们想要的。
/// </summary>
public sealed record LanWorld(string Motd, string Address, int Port)
{
    /// <summary>可以直接喂给"添加服务器"输入框的 `host:port`。</summary>
    public string Endpoint => $"{Address}:{Port}";
}

/// <summary>
/// 监听 Minecraft 的局域网世界广播。
///
/// 游戏在"对局域网开放"之后，会每 1.5 秒往 UDP 组播组 224.0.2.60:4445 发一段
/// 纯文本，格式固定为 `[MOTD]消息[/MOTD][AD]端口号[/AD]`。这是游戏自带的、不需要
/// 任何握手和认证的发现机制，启动器只要跟着听就行。
///
/// **为什么必须设 ReuseAddress**：同一台机器上跑着的游戏自己也在 `0.0.0.0:4445`
/// 收这个组播（它要显示"局域网世界"列表），我们的进程跟着绑同一个端口。
/// 不设地址复用，后绑的那个直接 WSAEADDRINUSE 失败 —— 而"启动器和游戏同时开着"
/// 恰恰是最常见的场景。设上以后两边都能收到组播（组播是一对多的，
/// 与单播端口共享的语义不同）。
///
/// **不抛异常**：绑定失败（别的程序占了端口且没设复用、防火墙挡了组播、
/// 没有可用网卡）都只写进 <see cref="LastError"/> 并返回空列表。
/// 界面在"没人开世界"和"我们收不到广播"两种情况下应该长得不一样，
/// 但都不该弹异常框。
/// </summary>
public sealed class LanWorldScanner : IDisposable
{
    /// <summary>Minecraft 写死的组播组地址，改了就收不到任何东西。</summary>
    public const string MulticastGroup = "224.0.2.60";

    /// <summary>Minecraft 写死的组播端口。</summary>
    public const int DiscoveryPort = 4445;

    private const string MotdOpen = "[MOTD]";
    private const string MotdClose = "[/MOTD]";
    private const string AdOpen = "[AD]";
    private const string AdClose = "[/AD]";

    /// <summary>
    /// 严格 UTF-8：遇到非法字节就抛，好让我们退回 Latin-1。
    /// 默认的 UTF8Encoding 会把坏字节悄悄换成 U+FFFD，那样连"这份包是乱码"
    /// 都判断不出来。
    /// </summary>
    private static readonly UTF8Encoding StrictUtf8 = new(false, true);

    private UdpClient? _client;
    private bool _disposed;

    /// <summary>上一次扫描失败的原因，供界面显示一行提示；成功或未扫描时为 null。</summary>
    public string? LastError { get; private set; }

    /// <summary>
    /// 构造不做任何 IO。
    ///
    /// 绑端口推迟到 <see cref="ScanAsync"/>：构造一个"扫描器对象"却因为端口被占
    /// 直接失败，会让调用方不得不把 new 包在 try 里，而真正需要错误提示的时机
    /// 是"用户点了刷新"那一刻。
    /// </summary>
    public LanWorldScanner() { }

    /// <summary>
    /// 解析一个广播包。合法返回世界信息，不合法返回 null（不抛）——
    /// 4445 端口上什么都可能收到（别的程序的杂包、游戏旧版本的格式）。
    ///
    /// 单独公开是为了能脱离网卡单独验证：组播能不能收到取决于防火墙和网卡，
    /// 但解析逻辑对不对可以在任何机器上用一段字节测出来。
    /// </summary>
    /// <param name="data">UDP 负载原始字节。</param>
    /// <param name="senderAddress">发送方 IPv4 点分地址，作为世界的 Address。</param>
    public static LanWorld? ParsePacket(byte[] data, string senderAddress)
    {
        if (data is null || data.Length == 0) return null;

        var text = DecodeText(data);

        var motdStart = text.IndexOf(MotdOpen, StringComparison.Ordinal);
        if (motdStart < 0) return null;

        var motdEnd = text.IndexOf(MotdClose, motdStart + MotdOpen.Length, StringComparison.Ordinal);
        if (motdEnd < 0) return null;

        var motd = text[(motdStart + MotdOpen.Length)..motdEnd];

        // [AD] 必须出现在 [/MOTD] 之后：MOTD 是用户自己填的自由文本，
        // 里面完全可能写着 "[AD]" 这几个字符，从 MOTD 开头搜就会搜错地方
        var adStart = text.IndexOf(AdOpen, motdEnd, StringComparison.Ordinal);
        if (adStart < 0) return null;

        var adEnd = text.IndexOf(AdClose, adStart + AdOpen.Length, StringComparison.Ordinal);
        if (adEnd < 0) return null;

        var portText = text[(adStart + AdOpen.Length)..adEnd].Trim();

        // NumberStyles.None 只认纯数字：带符号或空格的都不是游戏发的包
        if (!int.TryParse(portText, NumberStyles.None, CultureInfo.InvariantCulture, out var port)) return null;
        if (port is < 1 or > 65535) return null;

        return new LanWorld(motd, senderAddress, port);
    }

    /// <summary>
    /// 监听 <paramref name="window"/> 这段时间，返回期间发现的全部世界。
    ///
    /// 按 Endpoint（地址:端口）去重，同一个世界后发的包覆盖前面的 —— 世界刚开时
    /// MOTD 常常是"正在生成世界"，几秒后才变成真正的名字，保留最后一次才是对的。
    ///
    /// 窗口过期、被 <paramref name="ct"/> 取消、或者根本绑不上端口，都会正常返回
    /// （空列表或已收到的部分），不会抛异常。取消也一样：用户关窗口时不该看到异常。
    /// </summary>
    public async Task<IReadOnlyList<LanWorld>> ScanAsync(TimeSpan window, CancellationToken ct = default)
    {
        // 每次扫描重新记错误。上一次的失败原因留着会误导界面（"端口被占"其实已经好了）
        LastError = null;

        var found = new Dictionary<string, LanWorld>(StringComparer.Ordinal);

        if (_disposed || window <= TimeSpan.Zero) return [];

        UdpClient client;
        try
        {
            client = new UdpClient();

            // 顺序不能变：ExclusiveAddressUse 和 ReuseAddress 都必须在 bind 之前设，
            // 绑完再设会抛 InvalidOperationException。游戏很可能正绑着这个端口，
            // 不设复用这里就必失败
            client.ExclusiveAddressUse = false;
            client.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);

            client.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));

            // 加入组播组。用不带网卡参数的重载：让系统挑默认的组播网卡，
            // 多网卡机器上逐个网卡 join 是更细的选择，但对"发现局域网世界"没必要
            client.JoinMulticastGroup(IPAddress.Parse(MulticastGroup));
        }
        catch (Exception ex)
        {
            LastError = $"无法监听 UDP {DiscoveryPort}：{ex.Message}";
            return [];
        }

        _client = client;

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(window);

        try
        {
            while (!timeout.IsCancellationRequested)
            {
                UdpReceiveResult packet;
                try
                {
                    packet = await client.ReceiveAsync(timeout.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // 窗口到了（或调用方取消）。这是正常的结束方式
                    break;
                }
                catch (ObjectDisposedException)
                {
                    // Dispose 被并发调用，收摊
                    break;
                }
                catch (SocketException ex)
                {
                    // Windows 上的经典坑：UDP 端点回过 ICMP port unreachable 之后，
                    // 这个 socket 的收包会抛 WSAECONNRESET(10054)。它不代表监听结束，
                    // 也不该让整次扫描失败，记下来接着等。
                    // 加一点点延时是为了万一错误持续复现时不至于空转烧 CPU
                    LastError = $"收包出错（{ex.SocketErrorCode}）：{ex.Message}";

                    try
                    {
                        await Task.Delay(25, timeout.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }

                    continue;
                }

                var world = ParsePacket(packet.Buffer, AddressOf(packet.RemoteEndPoint));

                // 解析不出来就是别人发的杂包，无声跳过（4445 是公共端口，不该为了
                // 一个无关的包让用户看到错误提示）
                if (world is not null) found[world.Endpoint] = world;
            }
        }
        catch (Exception ex)
        {
            // 兜底：这里不该再有别的异常，但"发现世界"失败绝不该让界面炸掉
            LastError ??= $"扫描中断：{ex.Message}";
        }
        finally
        {
            client.Dispose();
            if (ReferenceEquals(_client, client)) _client = null;
        }

        return found.Values.ToList();
    }

    /// <summary>丢掉正在监听的 socket。正在进行的扫描会尽快返回已经收到的部分。</summary>
    public void Dispose()
    {
        _disposed = true;
        _client?.Dispose();
        _client = null;
    }

    /// <summary>
    /// 取来源地址的 IPv4 文本。
    ///
    /// 双栈 socket 上报的可能是 IPv4-mapped IPv6（`::ffff:192.168.1.7`），
    /// 直接 ToString 会得到一坨带冒号的字符串，拼进 `host:port` 之后游戏解析不了。
    /// 先映射回 IPv4 再输出。
    /// </summary>
    private static string AddressOf(IPEndPoint remote)
    {
        var address = remote.Address;
        if (address.IsIPv4MappedToIPv6) address = address.MapToIPv4();
        return address.ToString();
    }

    /// <summary>
    /// 包体解码。
    ///
    /// 外层框架（[MOTD]…[AD]…）是 ASCII，但 MOTD 是用户自己填的，
    /// 中文、颜色符号（§）都可能是多字节 UTF-8 —— 按 ASCII 解会把它们全变成 '?'，
    /// 用户看到的世界名字就是一堆问号。所以优先按 UTF-8 解；
    /// 万一真收到非法字节序列，退回 Latin-1（它把每个字节一对一映射成字符，
    /// 永不失败，至少能保住 ASCII 框架让解析继续走）。
    /// </summary>
    private static string DecodeText(byte[] data)
    {
        try
        {
            return StrictUtf8.GetString(data);
        }
        catch (DecoderFallbackException)
        {
            return Encoding.Latin1.GetString(data);
        }
    }
}