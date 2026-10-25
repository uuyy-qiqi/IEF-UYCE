using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace PclLike.Core.Mods;

/// <summary>一个可下载的 Mod 版本。</summary>
public sealed record ModDownloadCandidate
{
    /// <summary>平台：modrinth / curseforge。</summary>
    public required string Platform { get; init; }

    /// <summary>Mod 的项目 id 或 slug（Modrinth）／modId（CurseForge）。</summary>
    public required string ProjectId { get; init; }

    /// <summary>版本 id（Modrinth 的 version id）。</summary>
    public required string VersionId { get; init; }

    /// <summary>Mod 显示名（通常就是依赖 id）。</summary>
    public required string Name { get; init; }

    /// <summary>版本号。</summary>
    public required string Version { get; init; }

    /// <summary>文件名，写盘时用。</summary>
    public required string FileName { get; init; }

    public required string DownloadUrl { get; init; }

    /// <summary>文件大小（字节），0 表示未知。</summary>
    public long SizeBytes { get; init; }

    public string? Sha1 { get; init; }

    /// <summary>该版本支持的加载器。</summary>
    public IReadOnlyList<string> Loaders { get; init; } = [];

    /// <summary>该版本支持的 MC 版本。</summary>
    public IReadOnlyList<string> GameVersions { get; init; } = [];

    /// <summary>依赖 id 列表（Modrinth 会带，用于递归补依赖）。</summary>
    public IReadOnlyList<string> DependencyIds { get; init; } = [];
}

/// <summary>
/// 从公共 Mod 平台查找并下载 Mod。
///
/// 用 Modrinth 作为主源（完全公开，无需 API key），CurseForge 只做兜底
/// （要 key，未配置时直接跳过，不报错打断整个流程）。
///
/// 设计要点：**每一步失败都返回 null / 空列表，不抛异常**。
/// 自动补依赖会因为网络、限流、作者删包等各种原因失败，
/// 抛异常会把"补了 3 个、第 4 个没找到"变成"整个安装失败"，
/// 用户已经下好的那 3 个也白下了。
/// </summary>
public sealed class ModDownloader : IDisposable
{
    private const string ModrinthApi = "https://api.modrinth.com/v2";
    private const string CurseForgeApi = "https://api.curseforge.com/v1";

    private readonly HttpClient _http;
    private readonly SocketsHttpHandler _handler;
    private readonly Lazy<HttpClient> _fallback;

    /// <summary>
    /// 发一个 GET，自动在"直连"和"系统代理"两条通道之间切换。
    ///
    /// 网络环境千奇百怪：有代理的、没代理的、代理只对部分域名有效的。
    /// 与其让用户去改配置，不如两条路都试一遍，记住哪条通就用哪条。
    /// </summary>
    private async Task<HttpResponseMessage> GetWithFallbackAsync(string url, CancellationToken ct)
        => await GetWithFallbackAsync(url, HttpCompletionOption.ResponseContentRead, ct);

    private async Task<HttpResponseMessage> GetWithFallbackAsync(
        string url,
        HttpCompletionOption completion,
        CancellationToken ct)
    {
        // 已经知道代理好用就直接用
        if (_preferProxy)
        {
            try
            {
                return await _fallback.Value.GetAsync(url, completion, ct);
            }
            catch (Exception ex) when (IsTransportFailure(ex) && !ct.IsCancellationRequested)
            {
                _preferProxy = false;   // 代理又不通了，退回直连
            }
        }

        try
        {
            return await _http.GetAsync(url, completion, ct);
        }
        catch (Exception ex) when (IsTransportFailure(ex) && !ct.IsCancellationRequested)
        {
            // 直连挂了（典型：TLS 被中间设备切断），换代理再试一次。
            //
            // 注意：这里必须检查 ct，因为用户主动取消和"网络超时"都表现为
            // OperationCanceledException。不区分的话，用户点了取消还会傻等一轮代理。
            try
            {
                var resp = await _fallback.Value.GetAsync(url, completion, ct);
                _preferProxy = true;
                return resp;
            }
            catch when (!ct.IsCancellationRequested)
            {
                throw ex;   // 两条都不行，抛原始的直连错误更有诊断价值
            }
        }
    }

    /// <summary>判断是不是"传输层"故障 —— 这类才值得换通道重试。</summary>
    private static bool IsTransportFailure(Exception ex)
    {
        // 连接超时：DNS 污染 / 直连被墙的典型表现，换代理基本都能救回来
        if (ex is TaskCanceledException or OperationCanceledException) return true;

        if (ex is HttpRequestException) return true;
        if (ex is IOException) return true;

        // 有些 TLS 错误被包在 AggregateException 或多层 InnerException 里
        var inner = ex.InnerException;
        while (inner is not null)
        {
            if (inner is IOException
                or System.Security.Authentication.AuthenticationException
                or TimeoutException
                or TaskCanceledException)
                return true;
            inner = inner.InnerException;
        }

        return false;
    }

    /// <summary>
    /// 连接层回调：按可达性顺序尝试该主机名解析出的每个 IP。
    ///
    /// 作用范围要说清楚，别高估它：它只能解决"某个边缘 IP 的 TCP 握手挂死"，
    /// 也就是连接建立阶段就失败的故障。
    ///
    /// 2026-10 实测 cdn.modrinth.com 的故障**不是**这一类：
    ///   · DNS 只返回一个 IP（当时是 146.75.113.242 或 199.232.161.242），
    ///     因此下面的多 IP 轮换实际退化为单 IP 直连，基本不会触发；
    ///   · 该 IP 的 TCP 握手本身只要 0.08s，DNS 0.007s，TLS 0.17s —— 全都正常；
    ///   · 真正的问题是**响应体阶段**：同一条 URL 四次测得总耗时
    ///     1.3s / 45.8s / 59.0s / 64.9s，而完整下载成功那次只用了 1.58s
    ///     （4.4 MB/s）。即边缘节点会间歇性先挂几十秒再吐数据。
    ///
    /// 连接层看不到响应体，所以这种间歇性卡顿在这里**必然修不掉**；
    /// 只能靠足够大的超时（见 HttpClient.Timeout = 5 分钟）加上上层重试兜住，
    /// 重试通常会落到一个正常速度的节点上。调用方 ModsViewModel 就是这么做的。
    ///
    /// 保留这个回调的理由：单元仍然有价值 —— 主机名解析出多个 IP 且其中某个
    /// 握手挂死时，它能跳过坏的节点。只是它不再承担"修复 CDN 慢"的职责。
    ///
    /// 实现要点：ctx.DnsEndPoint 里的 Host 仍是原主机名，我们只替换"连到哪个
    /// IP"。SNI 与证书校验由上层继续使用原主机名完成，TLS 不受影响。
    /// 绝不能改写 URL 指向 IP 再手动设 Host 头 —— 那会让 SNI 跟着 IP 走，
    /// 证书是按主机名签的，握手必然失败。
    /// </summary>
    private static async ValueTask<Stream> ConnectRotatingAsync(
        System.Net.Http.SocketsHttpConnectionContext ctx, CancellationToken ct)
    {
        var host = ctx.DnsEndPoint.Host;
        var port = ctx.DnsEndPoint.Port;

        var ips = await OrderByReachabilityAsync(host, ct);

        // 解析不出来或只有一条：直接连，让系统按原样处理
        if (ips.Length == 0)
        {
            var sock0 = NewSocket(System.Net.Sockets.AddressFamily.InterNetwork);
            try
            {
                await sock0.ConnectAsync(ctx.DnsEndPoint, ct);
                return new System.Net.Sockets.NetworkStream(sock0, ownsSocket: true);
            }
            catch { sock0.Dispose(); throw; }
        }

        Exception? lastError = null;
        var tried = 0;

        // 最多 3 轮。每轮把候选表按光标顺序试一遍；一轮全败就**重新解析一次 DNS**
        // 再试下一轮。
        //
        // 为什么要重解析：最常见的失败形态是"解析的那一刻只给了一个死节点"
        // （实测系统解析器对 cdn.modrinth.com 反复只回一个地址），而 DNS 的答案
        // 会变 —— 该域名背后是一池由运营商缓存的边缘 IP。只试一轮，用户点一次
        // 「安装」就只会撞那一个死地址；多试几轮，常常第二轮就换到活的了。
        for (var round = 0; round < 3; round++)
        {
            // 从光标处开始试，并且每建成功一条连接就把光标往前挪一格。
            //
            // 为什么必须挪：连接层每次重试都会从候选表开头挑，于是同一个"能握手、
            // 但在响应体阶段挂死几十秒"的边缘节点会被反复选中 —— 重试三次等于
            // 白等两分钟。实测 cdn.modrinth.com 就有这种节点。挪一格，上层的
            // 25 秒静默重试才真的有机会换到别的节点上去。
            var start = 0;
            if (ips.Length > 0 && _ipCursor.TryGetValue(host, out var cursor))
                start = ((cursor % ips.Length) + ips.Length) % ips.Length;

            for (var step = 0; step < ips.Length; step++)
            {
                if (ct.IsCancellationRequested) break;

                var index = (start + step) % ips.Length;
                var ip = ips[index];
                tried++;

                var sock = NewSocket(ip.AddressFamily);
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    // 单个 IP 别等太久 —— 挂死的节点要快速跳过
                    linked.CancelAfter(TimeSpan.FromSeconds(6));

                    await sock.ConnectAsync(
                        new System.Net.DnsEndPoint(ip.ToString(), port), linked.Token).ConfigureAwait(false);

                    // 这个节点是活的，记下来给后面的排序当提示
                    _goodIpCache[host] = ip.ToString();
                    _ipCursor[host] = (index + 1) % ips.Length;

                    return new System.Net.Sockets.NetworkStream(sock, ownsSocket: true);
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    // 用户真的取消了，原样往上抛
                    sock.Dispose();
                    throw;
                }
                catch (Exception ex)
                {
                    sock.Dispose();

                    // 单个 IP 的 6 秒超时是我们自己掐的，不是用户取消。
                    // 必须换成别的异常类型再往外抛：上层到处都用
                    // catch (OperationCanceledException) 表示"用户点了取消"，
                    // 把连接超时伪装成取消会让它报"安装已取消"（用户看到会一头雾水），
                    // 更糟的是取消通常意味着"不要再重试了"，于是一次网络抖动就被
                    // 当成了用户意图。
                    lastError = ex is OperationCanceledException
                        ? new IOException($"连接 {ip} 超时（{host}:{port}）")
                        : ex;
                }
            }

            if (ct.IsCancellationRequested) break;

            // 一轮全败：重新解析 + 重新扩展候选，换个池子里的地址再来
            var refreshed = await RefreshCandidatesAsync(host, ips, ct);
            if (refreshed.Length == 0) break;
            ips = refreshed;
        }

        throw lastError is null
            ? new IOException($"无法连接 {host}:{port}")
            : new IOException(
                $"连不上 {host}:{port}（试了 {tried} 个地址都超时或被拒）。" +
                $"原因：{lastError.Message}。网络恢复后点「重新安装」再试。");
    }

    /// <summary>
    /// 重新解析主机名并把候选池再扩一遍（只追加没见过的新地址）。
    ///
    /// 和 <see cref="ExpandCandidatesAsync"/> 的区别：那个会因为"已经有 2 条以上"
    /// 就跳过，这里是要在一轮连接全部失败后强制刷新，所以总是去问。
    /// </summary>
    private static async Task<System.Net.IPAddress[]> RefreshCandidatesAsync(
        string host, System.Net.IPAddress[] current, CancellationToken ct)
    {
        var list = new List<System.Net.IPAddress>(current);

        try
        {
            var fresh = await System.Net.Dns.GetHostAddressesAsync(host, ct);
            foreach (var ip in fresh)
                if (!list.Contains(ip)) list.Add(ip);
        }
        catch { /* 解析失败就用原来的 */ }

        foreach (var server in ExtraDnsServers)
        {
            try
            {
                var found = await QueryARecordsAsync(server, host, ct);
                foreach (var ip in found)
                    if (!list.Contains(ip)) list.Add(ip);
            }
            catch { /* 单个解析器失败不影响其它 */ }
        }

        return [.. list];
    }

    /// <summary>
    /// 每个主机名"下次从哪个候选开始试"的光标（配合连接层的轮换）。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, int>
        _ipCursor = new(StringComparer.OrdinalIgnoreCase);

    private static System.Net.Sockets.Socket NewSocket(
        System.Net.Sockets.AddressFamily family) =>
        new(family, System.Net.Sockets.SocketType.Stream,
            System.Net.Sockets.ProtocolType.Tcp) { NoDelay = true };

    /// <summary>
    /// 探测该主机名各 IP 的可达性，好用的排前面。
    ///
    /// 注意它测的是 **TCP 握手**，只能筛掉"连都连不上"的节点。实测 cdn.modrinth.com
    /// 的间歇性卡顿发生在响应体阶段（握手 0.08s 正常，之后挂几十秒），本函数对此
    /// 无感知 —— 别指望它来兜住慢节点。
    ///
    /// 结果按主机名缓存 —— 不能每个请求都探一遍，那会把下载拖垮。
    /// 缓存的是"上次成功的那个 IP"，其余保持原序兜底。
    /// </summary>
    private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string>
        _goodIpCache = new(StringComparer.OrdinalIgnoreCase);

    private static async Task<System.Net.IPAddress[]> OrderByReachabilityAsync(
        string host, CancellationToken ct)
    {
        System.Net.IPAddress[] ips;
        try
        {
            ips = await System.Net.Dns.GetHostAddressesAsync(host, ct);
        }
        catch
        {
            ips = [];
        }

        // 系统解析器只给一两条时必须补一手。
        //
        // 实测证据（2026-10-24，本机）：默认解析器（10.199.199.199 / 114.114.114.114 /
        // 223.5.5.5）对 cdn.modrinth.com 都只回**同一个** 146.75.113.242，而那个
        // Fastly 边缘 TCP 是死的（21 秒连不上）；同一时刻问 119.29.29.29 拿到
        // 199.232.161.242（79ms 是活的）、问 8.8.8.8 拿到 151.101.x.242 四条。
        // 只拿到一个 IP 时，上面那个"逐个换 IP 重试"的循环根本无从换起，下载就整体
        // 失败 —— 补上其它解析器给出的兄弟节点，这个循环才真正有意义。
        ips = await ExpandCandidatesAsync(host, ips, ct);

        if (ips.Length <= 1) return ips;

        // 命中缓存：上次成功的 IP 排最前，其余保持原序
        if (_goodIpCache.TryGetValue(host, out var cachedLiteral) &&
            System.Net.IPAddress.TryParse(cachedLiteral, out var cachedIp) &&
            ips.Any(x => x.Equals(cachedIp)))
        {
            return [cachedIp, .. ips.Where(x => !x.Equals(cachedIp))];
        }

        // 并发探测，每个 IP 给 3 秒
        var probes = ips.Select(async ip =>
        {
            var sock = NewSocket(ip.AddressFamily);
            try
            {
                using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
                linked.CancelAfter(TimeSpan.FromSeconds(3));
                await sock.ConnectAsync(new System.Net.DnsEndPoint(ip.ToString(), 443), linked.Token);
                return (ip, ok: true);
            }
            catch
            {
                return (ip, ok: false);
            }
            finally
            {
                sock.Dispose();
            }
        }).ToArray();

        var results = await Task.WhenAll(probes);

        var good = results.Where(r => r.ok).Select(r => r.ip).ToList();
        var bad = results.Where(r => !r.ok).Select(r => r.ip).ToList();

        if (good.Count > 0)
            _goodIpCache[host] = good[0].ToString();

        // 好的在前；全挂了就保持原序（让上层自己报错，错误更直观）
        return good.Count > 0 ? [.. good, .. bad] : ips;
    }

    /// <summary>
    /// 额外问几个公共解析器，把同一个主机名的兄弟节点捞回来。
    ///
    /// 只在候选很少（≤2 条）或系统解析完全失败时才问 —— 正常网络下不能白花这几百毫秒。
    /// 主机的"拓扑感"还是系统解析器最准，所以补进来的永远排在它后面。
    /// </summary>
    private static readonly string[] ExtraDnsServers = ["119.29.29.29", "223.5.5.5", "8.8.8.8"];

    private static async Task<System.Net.IPAddress[]> ExpandCandidatesAsync(
        string host, System.Net.IPAddress[] current, CancellationToken ct)
    {
        // 已经是 IP 字面量，或者候选本来就够多 —— 不用问
        if (System.Net.IPAddress.TryParse(host, out _)) return current;
        if (current.Length > 2) return current;

        var batches = await Task.WhenAll(
            ExtraDnsServers.Select(server => QueryARecordsAsync(server, host, ct)));

        var merged = new List<System.Net.IPAddress>(current);
        foreach (var ip in batches.SelectMany(b => b))
            if (!merged.Any(x => x.Equals(ip)))
                merged.Add(ip);

        // 别贪多：候选太多会把"逐个试"拖成几十秒
        return [.. merged.Take(8)];
    }

    /// <summary>
    /// 用指定解析器查 A 记录。
    ///
    /// 自己发 UDP 查询是因为 .NET 的 <c>Dns</c> 类只能问系统配置的解析器，
    /// 而为一个补手引一个 DNS 库不值得。
    ///
    /// 超时、查不到、响应被截断一律返回空列表 —— 这层只负责"多给几个候选"，
    /// 它自己出错绝不能影响正常下载。
    /// </summary>
    private static async Task<List<System.Net.IPAddress>> QueryARecordsAsync(
        string server, string host, CancellationToken ct)
    {
        var found = new List<System.Net.IPAddress>();

        try
        {
            using var udp = new System.Net.Sockets.UdpClient();
            udp.Connect(server, 53);

            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct);
            linked.CancelAfter(TimeSpan.FromSeconds(2));

            var query = BuildDnsQuery(host);
            await udp.SendAsync(query, linked.Token);

            var datagram = await udp.ReceiveAsync(linked.Token);
            ParseDnsAnswers(datagram.Buffer, found);
        }
        catch
        {
            // 问不到就当这个解析器不存在
        }

        return found;
    }

    private static byte[] BuildDnsQuery(string host)
    {
        var buffer = new List<byte>
        {
            0x5A, 0x1B,                     // 事务 ID，随便
            0x01, 0x00,                     // 标准查询 + 期望递归
            0x00, 0x01,                     // QDCOUNT
            0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
        };

        foreach (var label in host.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var bytes = System.Text.Encoding.ASCII.GetBytes(label);
            var length = Math.Min(bytes.Length, 63);
            buffer.Add((byte)length);
            buffer.AddRange(bytes.Take(length));
        }

        buffer.Add(0x00);                                   // QNAME 结束
        buffer.AddRange(new byte[] { 0x00, 0x01 });         // QTYPE = A
        buffer.AddRange(new byte[] { 0x00, 0x01 });         // QCLASS = IN

        return [.. buffer];
    }

    /// <summary>把响应里所有 A 记录的 IPv4 抠出来。格式不对就当没有，不抛。</summary>
    private static void ParseDnsAnswers(byte[] data, List<System.Net.IPAddress> into)
    {
        if (data.Length < 12) return;

        var answerCount = (data[6] << 8) | data[7];
        if (answerCount <= 0) return;

        // 跳过 question 段：QNAME + QTYPE(2) + QCLASS(2)
        var offset = SkipDnsName(data, 12);
        if (offset < 0 || offset + 4 > data.Length) return;
        offset += 4;

        for (var i = 0; i < answerCount && offset + 10 <= data.Length; i++)
        {
            offset = SkipDnsName(data, offset);
            if (offset < 0 || offset + 10 > data.Length) return;

            var type = (data[offset] << 8) | data[offset + 1];
            var rdLength = (data[offset + 8] << 8) | data[offset + 9];
            offset += 10;

            if (offset + rdLength > data.Length) return;

            if (type == 1 && rdLength == 4)
                into.Add(new System.Net.IPAddress(data.AsSpan(offset, 4)));

            offset += rdLength;
        }
    }

    /// <summary>跳过一段域名。压缩指针只占 2 字节。返回下一段的偏移，越界返回 -1。</summary>
    private static int SkipDnsName(byte[] data, int offset)
    {
        while (true)
        {
            if (offset >= data.Length) return -1;

            var length = data[offset];

            if (length == 0) return offset + 1;
            if ((length & 0xC0) == 0xC0) return offset + 2;

            offset += 1 + length;
        }
    }

    /// <summary>CurseForge API key。未设置时 CurseForge 相关方法直接返回空。</summary>
    public string? CurseForgeApiKey { get; set; }

    public ModDownloader(string? curseForgeApiKey = null, string? userAgent = null)
    {
        CurseForgeApiKey = curseForgeApiKey;

        // 关键：用一个自定义的 SocketsHttpHandler，并且在遇到 TLS 故障时
        // 能自动切换到"直连"重试。
        //
        // 为什么需要这个：开着 Clash / v2ray 这类代理软件的用户，Windows 会把
        // 系统代理设成 127.0.0.1:7897，.NET 的 HttpClient 默认会走它。
        // 但部分代理对 Modrinth 这类大响应体（几 KB～几 MB 的 JSON）
        // 会中途断流，报 "Received an unexpected EOF or 0 bytes from the
        // transport stream" —— 而同样的代理访问小接口却没问题。
        // curl 不受影响是因为它不读 WinINET 的系统代理设置。
        //
        // 所以这里做两手准备：默认直连（绝大多数国内直连 Modrinth 是通的，
        // 且比走代理稳），失败再退回系统代理。
        // 代理：配置里明确指定 / 环境变量 / 系统设置里找到的，直接就用它 ——
        // 走代理时不能再用"换 IP 重试"那套：ConnectCallback 拿到的会是代理的
        // 地址，把代理的 IP 轮着换只会把连接搞坏。
        //
        // 唯独"探测到的本机代理"不直接接管：系统开关没开、只是本机某个端口
        // 有东西在听（可能根本不是一个 HTTP 代理），这种情况下保持直连优先、
        // 把它留给下面的备用通道 —— 别为了修一个超时把能下的变成下不了。
        var proxy = Net.ProxyResolver.Current;
        var proxyFirst = proxy.IsActive && proxy.Source != Net.ProxySource.Detected;

        _handler = new SocketsHttpHandler
        {
            UseProxy = proxyFirst,                  // 默认直连
            AutomaticDecompression =
                System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
            ConnectTimeout = TimeSpan.FromSeconds(8),
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            // Fastly 有些边缘 IP 会 TCP 挂死，在连接层换 IP 重试
            ConnectCallback = proxyFirst ? null : ConnectRotatingAsync,
        };

        if (proxyFirst) Net.ProxyResolver.Apply(_handler, proxy);

        _http = new HttpClient(_handler, disposeHandler: false)
        {
            Timeout = TimeSpan.FromMinutes(5),
        };

        // Modrinth 要求带可识别的 UA，否则可能被限流
        _http.DefaultRequestHeaders.UserAgent.ParseAdd(
            userAgent ?? "PclLike/1.0 (Minecraft launcher)");

        if (!string.IsNullOrWhiteSpace(CurseForgeApiKey))
            _http.DefaultRequestHeaders.Add("x-api-key", CurseForgeApiKey);

        // 备用通道：走代理（用户可能只有代理能出网）
        _fallback = new Lazy<HttpClient>(() =>
        {
            var h = new SocketsHttpHandler
            {
                AutomaticDecompression =
                    System.Net.DecompressionMethods.GZip | System.Net.DecompressionMethods.Deflate,
                ConnectTimeout = TimeSpan.FromSeconds(15),
                ConnectCallback = proxyFirst ? null : ConnectRotatingAsync,
            };

            // 没找到代理时这一步等于直连（以前用的是 WebRequest.GetSystemWebProxy()，
            // 系统开关没开的时候它给的是个"不用代理"的空壳，效果一样）
            Net.ProxyResolver.Apply(h, proxy);

            var client = new HttpClient(h)
            {
                Timeout = TimeSpan.FromMinutes(5),
            };

            client.DefaultRequestHeaders.UserAgent.ParseAdd(
                userAgent ?? "PclLike/1.0 (Minecraft launcher)");

            if (!string.IsNullOrWhiteSpace(CurseForgeApiKey))
                client.DefaultRequestHeaders.Add("x-api-key", CurseForgeApiKey);

            return client;
        });
    }

    /// <summary>直连失败过且代理成功过 —— 之后的请求优先走代理。</summary>
    private volatile bool _preferProxy;

    // ────────────────────────────────────────────────────────────
    // Modrinth
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 查找满足加载器 + MC 版本的一个下载候选。
    ///
    /// <paramref name="dependencyId"/> 可以是 Modrinth 的 slug 或 project id ——
    /// Fabric Mod 的 depends 里写的就是 slug（比如 `fabric-api`、`cloth-config`），
    /// 直接用 slug 查正好对上。
    /// </summary>
    public async Task<ModDownloadCandidate?> FindModrinthAsync(
        string dependencyId,
        string mcVersion,
        string loader,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(dependencyId)) return null;

        try
        {
            // 先按 slug 直接拿项目，命中率最高
            var project = await GetModrinthProjectAsync(dependencyId, ct);
            if (project is null)
            {
                // 不是 slug 就搜索一次，取最相关的
                project = await SearchModrinthAsync(dependencyId, ct);
                if (project is null) return null;
            }

            return await PickModrinthVersionAsync(project, mcVersion, loader, ct);
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<JsonObject?> GetModrinthProjectAsync(string idOrSlug, CancellationToken ct)
    {
        try
        {
            using var resp = await GetWithFallbackAsync($"{ModrinthApi}/project/{Uri.EscapeDataString(idOrSlug)}", ct);
            if (!resp.IsSuccessStatusCode) return null;
            return await resp.Content.ReadFromJsonAsync<JsonObject>(ct);
        }
        catch { return null; }
    }

    private async Task<JsonObject?> SearchModrinthAsync(string query, CancellationToken ct)
    {
        try
        {
            var facets = Uri.EscapeDataString("[[\"project_type:mod\"]]");
            var url = $"{ModrinthApi}/search?query={Uri.EscapeDataString(query)}&limit=5&facets={facets}";

            using var resp = await GetWithFallbackAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadFromJsonAsync<JsonObject>(ct);
            var hits = json?["hits"]?.AsArray();
            if (hits is null || hits.Count == 0) return null;

            var slug = hits[0]?["slug"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(slug)) return null;

            return await GetModrinthProjectAsync(slug, ct);
        }
        catch { return null; }
    }

    private async Task<ModDownloadCandidate?> PickModrinthVersionAsync(
        JsonObject project, string mcVersion, string loader, CancellationToken ct)
    {
        var id = project["id"]?.GetValue<string>();
        if (string.IsNullOrWhiteSpace(id)) return null;

        var title = project["title"]?.GetValue<string>() ?? id;

        // 用服务端的 loaders / game_versions 过滤，而不是自己拉 258 个版本再筛。
        //
        // 这不只是省流量：自己筛会在"一个都没匹配上"时退化成"取最新版"，
        // 而最新版可能是给别的 MC 版本用的（Sodium 有 258 个版本，最新的是
        // 给 26.3 的）。装错版本的 Mod 用户只会在启动时看到游戏崩溃，
        // 根本联想不到是启动器装错了。让服务端过滤 = 没有匹配就是没有。
        var qs = new List<string>();

        var loaderLower = (loader ?? "").Trim().ToLowerInvariant();
        if (loaderLower.Length > 0)
        {
            var names = LoaderQueryNames(loaderLower);
            qs.Add("loaders=" + Uri.EscapeDataString(
                "[" + string.Join(",", names.Select(n => $"\"{n}\"")) + "]"));
        }

        if (!string.IsNullOrWhiteSpace(mcVersion))
            qs.Add("game_versions=" + Uri.EscapeDataString($"[\"{mcVersion.Trim()}\"]"));

        var url = $"{ModrinthApi}/project/{Uri.EscapeDataString(id)}/version" +
                  (qs.Count > 0 ? "?" + string.Join("&", qs) : "");

        JsonArray? versions;
        try
        {
            using var resp = await GetWithFallbackAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;
            versions = await resp.Content.ReadFromJsonAsync<JsonArray>(ct);
        }
        catch { return null; }

        if (versions is null || versions.Count == 0) return null;

        // 服务端给的就是「匹配的版本」，取最新的一个。
        // Modrinth 按发布时间倒序返回，所以第一个就是最新的 —— 不用自己排序。
        foreach (var v in versions)
        {
            if (v is not JsonObject ver) continue;

            var files = ver["files"]?.AsArray();
            if (files is null || files.Count == 0) continue;

            // 优先选 primary 文件
            var file = files.FirstOrDefault(f => f?["primary"]?.GetValue<bool>() == true) ?? files[0];
            var dlUrl = file?["url"]?.GetValue<string>();
            var fname = file?["filename"]?.GetValue<string>();
            if (string.IsNullOrWhiteSpace(dlUrl) || string.IsNullOrWhiteSpace(fname)) continue;

            var loaders = (ver["loaders"]?.AsArray() ?? [])
                .Select(x => x?.GetValue<string>() ?? "").ToList();
            var games = (ver["game_versions"]?.AsArray() ?? [])
                .Select(x => x?.GetValue<string>() ?? "").ToList();

            // 递归依赖：只取 required 的
            var depIds = new List<string>();
            if (ver["dependencies"] is JsonArray deps)
            {
                foreach (var d in deps)
                {
                    if (d?["dependency_type"]?.GetValue<string>() != "required") continue;
                    var pid = d?["project_id"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(pid)) depIds.Add(pid);
                }
            }

            return new ModDownloadCandidate
            {
                Platform = "modrinth",
                ProjectId = id,
                VersionId = ver["id"]?.GetValue<string>() ?? "",
                Name = title,
                Version = ver["version_number"]?.GetValue<string>() ?? "",
                FileName = fname,
                DownloadUrl = dlUrl,
                SizeBytes = file?["size"]?.GetValue<long>() ?? 0,
                Sha1 = file?["hashes"]?["sha1"]?.GetValue<string>(),
                Loaders = loaders,
                GameVersions = games,
                DependencyIds = depIds,
            };
        }

        return null;
    }

    /// <summary>
    /// 加载器名 → Modrinth 查询里该用的名字。
    ///
    /// NeoForge 兼容 Forge 生态的 Mod，Quilt 兼容 Fabric 的，所以要带上
    /// 两个名字一起查。反过来不行 —— 给 Forge 实例装 NeoForge 专用包会崩。
    /// </summary>
    private static string[] LoaderQueryNames(string loaderLower) => loaderLower switch
    {
        "neoforge" => ["neoforge", "forge"],
        "quilt" => ["quilt", "fabric"],
        _ => [loaderLower],
    };

    private static bool LoaderMatches(string candidate, string wanted)
    {
        candidate = candidate.Trim().ToLowerInvariant();
        if (candidate == wanted) return true;

        // NeoForge 能从 Forge Mod 加载（1.20.1 生态），反之不行
        if (wanted == "neoforge" && candidate == "forge") return true;

        // quilt 兼容 fabric
        if (wanted == "quilt" && candidate == "fabric") return true;

        return false;
    }

    /// <summary>按 Modrinth project id 直接拿候选（用于递归依赖，id 比 slug 快且准）。</summary>
    public async Task<ModDownloadCandidate?> FindModrinthByIdAsync(
        string projectId, string mcVersion, string loader, CancellationToken ct = default)
    {
        var project = await GetModrinthProjectAsync(projectId, ct);
        return project is null ? null : await PickModrinthVersionAsync(project, mcVersion, loader, ct);
    }

    // ────────────────────────────────────────────────────────────
    // Modrinth 搜索（给 GUI 的列表用）
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 在 Modrinth 搜索 Mod，返回多条结果供用户挑选。
    ///
    /// 与 <see cref="FindModrinthAsync"/> 的区别：那个只取第一条且返回可下载候选，
    /// 是给"自动补依赖"用的——依赖 id 是确定的，不需要挑。这里是给人用的，
    /// 所以返回列表 + 图标 + 下载数 + 简介，让用户自己选。
    ///
    /// <paramref name="mcVersion"/> 和 <paramref name="loader"/> 会作为 facets 过滤，
    /// 传空表示不限。
    ///
    /// <paramref name="projectType"/> 是 Modrinth 的项目类型（mod / modpack / resourcepack /
    /// shader / datapack / plugin）。默认 mod 是为了不破坏原有调用点；资源包和光影包
    /// **不能**带加载器过滤 —— 它们没有加载器分类，带上会一个都搜不出来，
    /// 所以只有 mod 与 modpack 才追加加载器 facet。
    /// </summary>
    public async Task<IReadOnlyList<ModSearchResult>> SearchModrinthListAsync(
        string query,
        string? mcVersion = null,
        string? loader = null,
        int limit = 20,
        int offset = 0,
        CancellationToken ct = default,
        string projectType = "mod",
        string? category = null)
    {
        limit = Math.Clamp(limit, 1, 100);
        offset = Math.Max(0, offset);
        projectType = NormalizeProjectType(projectType);

        try
        {
            var facets = BuildModrinthFacets(mcVersion, loader, projectType, category);
            var url = $"{ModrinthApi}/search?query={Uri.EscapeDataString(query ?? "")}" +
                      $"&limit={limit}&offset={offset}&index=relevance" +
                      (facets.Length > 0 ? $"&facets={facets}" : "");

            using var resp = await GetWithFallbackAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return [];

            var json = await resp.Content.ReadFromJsonAsync<JsonObject>(ct);
            var hits = json?["hits"]?.AsArray();
            if (hits is null || hits.Count == 0) return [];

            var results = new List<ModSearchResult>();
            foreach (var h in hits)
            {
                if (h is not JsonObject hit) continue;

                var slug = hit["slug"]?.GetValue<string>() ?? "";
                var projectId = hit["project_id"]?.GetValue<string>() ?? slug;
                if (string.IsNullOrWhiteSpace(projectId)) continue;

                results.Add(new ModSearchResult
                {
                    ProjectId = projectId,
                    Slug = slug,
                    Name = hit["title"]?.GetValue<string>() ?? slug,
                    Summary = hit["description"]?.GetValue<string>() ?? "",
                    IconUrl = hit["icon_url"]?.GetValue<string>(),
                    Downloads = hit["downloads"]?.GetValue<long>() ?? 0,
                    Follows = hit["follows"]?.GetValue<long>() ?? 0,
                    Author = hit["author"]?.GetValue<string>() ?? "",
                    Loaders = (hit["categories"]?.AsArray() ?? [])
                        .Select(x => x?.GetValue<string>() ?? "")
                        .Where(IsLoaderName).ToList(),
                    GameVersions = (hit["versions"]?.AsArray() ?? [])
                        .Select(x => x?.GetValue<string>() ?? "").ToList(),
                    ProjectType = hit["project_type"]?.GetValue<string>() ?? "mod",
                });
            }

            return results;
        }
        catch (OperationCanceledException) { throw; }
        catch { return []; }
    }

    /// <summary>
    /// 组装 Modrinth facets。
    ///
    /// 格式是 JSON 数组的数组，外层 AND、内层 OR —— 比如
    /// `[["project_type:mod"],["categories:fabric"],["versions:1.20.1"]]`
    /// 意思是"是 mod" 且 "属于 fabric 分类" 且 "支持 1.20.1"。
    ///
    /// 加载器要按 Modrinth 的分类名传（全小写）；"forge" 在 Modrinth 上
    /// 同时覆盖 Forge 和 NeoForge 的旧包，所以 NeoForge 也传 forge 会漏掉
    /// 纯 NeoForge 的包，这里两者都带上用 OR 连。
    /// </summary>
    private static string BuildModrinthFacets(
        string? mcVersion,
        string? loader,
        string projectType = "mod",
        string? category = null)
    {
        var groups = new List<string> { $"[\"project_type:{projectType}\"]" };

        // 加载器只对 mod / modpack 有意义。资源包、光影、数据包在 Modrinth 上
        // 没有加载器分类，硬加 categories:fabric 会把结果清空。
        var loaderApplies = projectType is "mod" or "modpack";

        var l = (loader ?? "").Trim().ToLowerInvariant();
        if (loaderApplies && l.Length > 0)
        {
            var names = l switch
            {
                "neoforge" => new[] { "neoforge", "forge" },
                "quilt" => new[] { "quilt", "fabric" },
                _ => new[] { l },
            };
            groups.Add("[" + string.Join(",", names.Select(n => $"\"categories:{n}\"")) + "]");
        }

        // 附加分类。数据包就是靠这个区分的 —— Modrinth 上 datapack 不是
        // 一种 project_type，纯数据包（比如各种结构包）在 API 里一律是
        // project_type:mod + categories 里带 datapack。实测：
        // project_type:datapack 会被服务端**静默忽略**（不报错，返回的是不限
        // 类型的搜索结果）；project_type:mod + categories:datapack 才是 146 个
        // 纯数据包。
        var c = (category ?? "").Trim().ToLowerInvariant();
        if (c.Length > 0)
            groups.Add($"[\"categories:{c}\"]");

        if (!string.IsNullOrWhiteSpace(mcVersion))
            groups.Add($"[\"versions:{mcVersion.Trim()}\"]");

        return Uri.EscapeDataString("[" + string.Join(",", groups) + "]");
    }

    /// <summary>
    /// Modrinth 能接受的 project_type 白名单。传错会被服务端忽略（而不是报错），
    /// 结果就是"搜出来一堆 Mod"—— 所以这里收敛到已知值，未知的退回 mod。
    ///
    /// 注意 `datapack` **不在**白名单里：Modrinth 没有这个 project_type，
    /// 传过去只会被忽略。要筛数据包请用 mod + category="datapack"。
    /// </summary>
    private static string NormalizeProjectType(string? projectType)
    {
        var t = (projectType ?? "").Trim().ToLowerInvariant();
        return t switch
        {
            "mod" or "modpack" or "resourcepack" or "shader" or "plugin" => t,
            _ => "mod",
        };
    }

    // ────────────────────────────────────────────────────────────
    // CurseForge（需 key）
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 从 cfwidget 这个第三方只读镜像问文件名 —— **不需要任何 API key**。
    ///
    /// 为什么要它：CurseForge 唯一的"无需 key 也能拿到文件名"的路子（官方网页那个
    /// 公开下载端点）已经被 Cloudflare 的机器人挑战接管，程序侧拿不到 307 后面的
    /// 文件名。cfwidget 的 <c>/{projectId}</c> 会返回 <c>files[]</c>，每条带
    /// <c>id</c> 和 <c>name</c>，拿名字够用了。
    /// </summary>
    public async Task<string?> ResolveCurseFileNameViaMirrorAsync(
        int projectId, int fileId, CancellationToken ct = default)
    {
        if (projectId <= 0 || fileId <= 0) return null;

        foreach (var url in new[]
                 {
                     $"https://api.cfwidget.com/{projectId}?version={fileId}",
                     $"https://api.cfwidget.com/{projectId}",
                 })
        {
            try
            {
                using var resp = await _http.GetAsync(url, ct).ConfigureAwait(false);
                if (!resp.IsSuccessStatusCode) continue;

                using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: ct)
                    .ConfigureAwait(false);

                if (!doc.RootElement.TryGetProperty("files", out var files)
                    || files.ValueKind != JsonValueKind.Array)
                {
                    continue;
                }

                foreach (var f in files.EnumerateArray())
                {
                    if (!f.TryGetProperty("id", out var idEl) || !idEl.TryGetInt32(out var id)) continue;
                    if (id != fileId) continue;

                    var raw = f.TryGetProperty("name", out var n) ? n.GetString() : null;
                    var name = Path.GetFileName(raw ?? "");

                    if (!string.IsNullOrWhiteSpace(name)) return name;
                }
            }
            catch
            {
                // 镜像挂了就试下一个地址，都失败就返回 null，上层会退回老路
            }
        }

        return null;
    }

    /// <summary>
    /// 直连 CDN 的地址：<c>edge.forgecdn.net/files/{id/1000}/{id%1000}/{名字}</c>。
    ///
    /// 这一段不需要 key、也不经过 Cloudflare，只要有名字就能下。名字里的空格和
    /// 加号**必须转义** —— 真实文件名两种都有（`iterationT 3.2.0.zip` 和
    /// `iterationT+3.2.0.zip`），不转义直接连不上（curl 报 000）。
    /// </summary>
    public static string CurseForgeEdgeUrl(int fileId, string fileName)
    {
        var a = fileId / 1000;
        var b = fileId % 1000;

        return $"https://edge.forgecdn.net/files/{a}/{b}/{Uri.EscapeDataString(fileName)}";
    }

    /// <summary>
    /// 只问「这个 projectID/fileID 的文件叫什么、真实地址在哪」，不下载。
    ///
    /// 为什么要单独一步：CurseForge 整合包的 manifest.json 只给 id，不给文件名。
    /// 而直接拼 CDN 地址（<c>https://edge.forgecdn.net/files/{id/1000}/{id%1000}/名字</c>）
    /// 又必须知道名字 —— 没名字只能下成 <c>cf-6552097.jar</c>，mods 目录里堆上
    /// 两百多个这种名字，谁也认不出来。
    ///
    /// 这里走的是 CurseForge 网页上那个公开的下载端点：它 307 跳到
    /// edge.forgecdn.net 并在查询串里带上 api-key，**不需要用户申请 key**。
    /// 只发头不发体（ResponseHeadersRead + 立刻释放），所以代价接近一次 HEAD。
    /// </summary>
    public async Task<string?> ResolveCurseFileNameAsync(int projectId, int fileId, CancellationToken ct = default)
    {
        if (projectId <= 0 || fileId <= 0) return null;

        // 先问不需要 key 的镜像。CurseForge 自己那个"公开下载端点"现在被 Cloudflare
        // 的机器人挑战挡住了（响应体是 <title>Just a moment...</title>），程序拿不到
        // 307 后面的文件名 —— 这是"PCL2 能装完、我们装不完"的真正差别：PCL2 用官方
        // API（带 key）查名字，再直连 edge.forgecdn.net 取文件；关键就在"拿到名字"。
        var viaMirror = await ResolveCurseFileNameViaMirrorAsync(projectId, fileId, ct)
            .ConfigureAwait(false);

        if (!string.IsNullOrWhiteSpace(viaMirror)) return viaMirror;

        var url = $"https://www.curseforge.com/api/v1/mods/{projectId}/files/{fileId}/download";

        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            using var resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct)
                .ConfigureAwait(false);

            if (!resp.IsSuccessStatusCode) return null;

            // 跳转后的地址形如 .../files/6552/97/letsdocompat-forge-2.2.3.jar?api-key=xxx
            var final = resp.RequestMessage?.RequestUri;
            var name = final?.Segments.LastOrDefault()?.Trim('/');

            if (string.IsNullOrWhiteSpace(name)) return null;

            name = Uri.UnescapeDataString(name);

            // 去掉可能的查询残渣并挡掉路径穿越
            var slash = name.IndexOfAny(['?', '#']);
            if (slash >= 0) name = name[..slash];

            name = Path.GetFileName(name);

            return string.IsNullOrWhiteSpace(name) ? null : name;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 在 CurseForge 搜索。未配置 API key 时返回空列表 —— 这是预期行为，
    /// 调用方按"这个源不可用"处理即可，不要当成错误。
    /// </summary>
    public async Task<ModDownloadCandidate?> FindCurseForgeAsync(
        string query, string? mcVersion, string loader, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(CurseForgeApiKey)) return null;
        if (string.IsNullOrWhiteSpace(query)) return null;

        try
        {
            // classId 6 = Mods
            var gameVersion = Uri.EscapeDataString(mcVersion ?? "");
            var url = $"{CurseForgeApi}/mods/search?gameId=432&classId=6" +
                      $"&searchFilter={Uri.EscapeDataString(query)}&pageSize=5" +
                      (string.IsNullOrWhiteSpace(mcVersion) ? "" : $"&gameVersion={gameVersion}");

            using var resp = await GetWithFallbackAsync(url, ct);

            // CurseForge 对不合法 key 返回 403 —— 静默降级
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadFromJsonAsync<JsonObject>(ct);
            var data = json?["data"]?.AsArray();
            if (data is null || data.Count == 0) return null;

            foreach (var m in data)
            {
                if (m is not JsonObject mod) continue;

                var modId = mod["id"]?.GetValue<int>() ?? 0;
                if (modId == 0) continue;
                if (mod["allowModDistribution"]?.GetValue<bool>() == false) continue;  // 作者不允许分发

                var candidate = await PickCurseForgeFileAsync(modId, mcVersion, loader, ct);
                if (candidate is not null) return candidate;
            }

            return null;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    private async Task<ModDownloadCandidate?> PickCurseForgeFileAsync(
        int modId, string? mcVersion, string loader, CancellationToken ct)
    {
        try
        {
            var url = $"{CurseForgeApi}/mods/{modId}/files?pageSize=50";
            using var resp = await GetWithFallbackAsync(url, ct);
            if (!resp.IsSuccessStatusCode) return null;

            var json = await resp.Content.ReadFromJsonAsync<JsonObject>(ct);
            var files = json?["data"]?.AsArray();
            if (files is null) return null;

            var loaderLower = (loader ?? "").Trim().ToLowerInvariant();

            foreach (var f in files)
            {
                if (f is not JsonObject file) continue;

                var games = (file["gameVersions"]?.AsArray() ?? [])
                    .Select(x => x?.GetValue<string>() ?? "").ToList();

                if (!string.IsNullOrWhiteSpace(mcVersion) &&
                    !games.Contains(mcVersion, StringComparer.OrdinalIgnoreCase))
                    continue;

                // CurseForge 把加载器也塞在 gameVersions 里
                if (loaderLower.Length > 0 &&
                    !games.Any(g => g.Equals(loaderLower, StringComparison.OrdinalIgnoreCase) ||
                                    (loaderLower == "neoforge" && g.Equals("forge", StringComparison.OrdinalIgnoreCase))))
                    continue;

                var fileId = file["id"]?.GetValue<int>() ?? 0;
                if (fileId == 0) continue;

                var dl = file["downloadUrl"]?.GetValue<string>();

                // 没有直链就无法下载（作者关闭了第三方分发）
                if (string.IsNullOrWhiteSpace(dl)) continue;

                var deps = new List<string>();
                if (file["dependencies"] is JsonArray depArr)
                {
                    foreach (var d in depArr)
                    {
                        if (d?["relationType"]?.GetValue<int>() != 3) continue;  // 3 = RequiredDependency
                        var did = d?["modId"]?.GetValue<int>() ?? 0;
                        if (did != 0) deps.Add("cf:" + did);
                    }
                }

                return new ModDownloadCandidate
                {
                    Platform = "curseforge",
                    ProjectId = modId.ToString(),
                    VersionId = fileId.ToString(),
                    Name = file["displayName"]?.GetValue<string>() ?? modId.ToString(),
                    Version = file["displayName"]?.GetValue<string>() ?? "",
                    FileName = file["fileName"]?.GetValue<string>() ?? $"mod-{fileId}.jar",
                    DownloadUrl = dl,
                    SizeBytes = file["fileLength"]?.GetValue<long>() ?? 0,
                    Loaders = games.Where(g => IsLoaderName(g)).ToList(),
                    GameVersions = games.Where(g => !IsLoaderName(g)).ToList(),
                    DependencyIds = deps,
                };
            }

            return null;
        }
        catch { return null; }
    }

    private static bool IsLoaderName(string s)
    {
        s = s.ToLowerInvariant();
        return s is "forge" or "fabric" or "neoforge" or "quilt" or "liteloader" or "rift";
    }

    // ────────────────────────────────────────────────────────────
    // 下载
    // ────────────────────────────────────────────────────────────

    /// <summary>
    /// 单次"读不到任何字节"能容忍的最长时间。
    ///
    /// 为什么需要它：2026-10 实测 cdn.modrinth.com 会在握手早已完成（0.08s）之后，
    /// 让响应体静止 45~65 秒才开始吐数据，同一个 URL 换一次连接就只要 1.3 秒。
    /// 这种静默期只有 HttpClient.Timeout（5 分钟）能兜住，而 5 分钟对用户来说
    /// 长得像死机。
    ///
    /// 所以这里设一个更短的静默阈值：只要超过它还没读到新字节，就认为这条连接
    /// 卡住了，主动断开并让上层重试（重连通常会落到正常节点）。
    /// 注意这**不是**总时长上限 —— 慢速持续传输不会被它打断，因为那种情况下
    /// 每读一小块就会重置计时。
    /// </summary>
    private static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(25);

    public async Task<bool> DownloadAsync(
        ModDownloadCandidate candidate,
        string targetPath,
        IProgress<DownloadProgress>? progress = null,
        CancellationToken ct = default)
    {
        var partPath = targetPath + ".part";

        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);

            using var resp = await GetWithFallbackAsync(
                candidate.DownloadUrl, HttpCompletionOption.ResponseHeadersRead, ct);

            if (!resp.IsSuccessStatusCode) return false;

            var total = resp.Content.Headers.ContentLength ?? candidate.SizeBytes;

            await using (var src = await resp.Content.ReadAsStreamAsync(ct))
            await using (var dst = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, true))
            {
                var buffer = new byte[81920];
                long read = 0;
                int n;
                var lastReport = 0L;

                while (true)
                {
                    // 每一次读都挂一个静默上限：CDN 边缘会在传输中途静止几十秒，
                    // 靠 HttpClient.Timeout（5 分钟）兜的话用户会以为程序死了。
                    // 这里只限制"连续多久读不到新字节"，持续但缓慢的传输不受影响。
                    using var stall = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    stall.CancelAfter(StallTimeout);

                    try
                    {
                        n = await src.ReadAsync(buffer, stall.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // 用户没取消，是我们主动掐的静默连接 —— 当作失败上报，
                        // 让上层重新发起（重连通常就快了）。
                        TryDelete(partPath);
                        return false;
                    }

                    if (n <= 0) break;

                    await dst.WriteAsync(buffer.AsMemory(0, n), ct);
                    read += n;

                    // 每 128KB 报一次，避免 UI 线程被高频回调淹没
                    if (read - lastReport >= 131072 || read == total)
                    {
                        lastReport = read;
                        progress?.Report(new DownloadProgress(candidate.Name, read, total));
                    }
                }
            }

            // 有声明 sha1 就校验 —— 下到损坏的包比下不到更糟（启动时才崩）
            if (!string.IsNullOrWhiteSpace(candidate.Sha1))
            {
                var actual = await Sha1Async(partPath, ct);
                if (!actual.Equals(candidate.Sha1, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(partPath);
                    return false;
                }
            }

            File.Move(partPath, targetPath, overwrite: true);
            return true;
        }
        catch (OperationCanceledException)
        {
            TryDelete(partPath);
            throw;
        }
        catch
        {
            TryDelete(partPath);
            return false;
        }
    }

    private static void TryDelete(string path)
    {
        try { if (File.Exists(path)) File.Delete(path); } catch { }
    }

    private static async Task<string> Sha1Async(string path, CancellationToken ct)
    {
        using var sha = System.Security.Cryptography.SHA1.Create();
        await using var fs = File.OpenRead(path);
        var hash = await sha.ComputeHashAsync(fs, ct);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    public void Dispose()
    {
        _http.Dispose();
        if (_fallback.IsValueCreated) _fallback.Value.Dispose();
        _handler.Dispose();
    }
}

/// <summary>下载进度。</summary>
public sealed record DownloadProgress(string Name, long Downloaded, long Total)
{
    public double Fraction => Total > 0 ? Math.Clamp((double)Downloaded / Total, 0, 1) : 0;
    public string Text => Total > 0
        ? $"{Name}  {Downloaded / 1048576.0:F1}/{Total / 1048576.0:F1} MB ({Fraction * 100:F0}%)"
        : $"{Name}  {Downloaded / 1048576.0:F1} MB";
}

/// <summary>
/// 一条 Modrinth 搜索结果 —— 待用户挑选的项目摘要。
///
/// 注意这**不是**可下载候选：搜索接口只给项目级信息（名字、图标、下载数），
/// 不含具体版本和文件。用户点了某一项之后，才按该项目的 id 去
/// <see cref="ModDownloader.FindModrinthByIdAsync"/> 拿当前实例匹配的版本。
/// 这样搜索结果不会因为实例换了 MC 版本就失效。
/// </summary>
public sealed record ModSearchResult
{
    /// <summary>Modrinth project id（形如 AANobbMIt8HNwPtj）。</summary>
    public required string ProjectId { get; init; }

    /// <summary>URL 里用的 slug，比如 sodium。</summary>
    public required string Slug { get; init; }

    public required string Name { get; init; }

    /// <summary>一句话简介。</summary>
    public string Summary { get; init; } = "";

    public string? IconUrl { get; init; }

    public long Downloads { get; init; }

    public long Follows { get; init; }

    public string Author { get; init; } = "";

    /// <summary>从 categories 里挑出来的加载器名。</summary>
    public IReadOnlyList<string> Loaders { get; init; } = [];

    /// <summary>支持的 MC 版本。</summary>
    public IReadOnlyList<string> GameVersions { get; init; } = [];

    public string ProjectType { get; init; } = "mod";

    /// <summary>下载数显示成 1.2M / 34.5K 这种，比 1234567 好读。</summary>
    public string DownloadsText => Downloads switch
    {
        >= 100_000_000 => $"{Downloads / 100_000_000.0:F0}亿",
        >= 10_000 => $"{Downloads / 10_000.0:F1}万",
        _ => Downloads.ToString(),
    };

    /// <summary>加载器标签，给 UI 直接绑。</summary>
    public string LoadersText => Loaders.Count > 0 ? string.Join(" / ", Loaders) : "—";
}