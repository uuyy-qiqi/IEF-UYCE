// 代理解析验收：这台机器上到底能不能找到代理，找到之后 NeoForge 通不通。
//
// 背景：用户开着 Clash（127.0.0.1:7897），但 Windows"局域网设置"里那个代理
// 开关是关的 —— .NET 默认只看那个开关，于是启动器直连、NeoForge 超时。
// 这个探针就是验：ProxyResolver 能不能通过"系统里留着的地址 + 本机端口探测"
// 把它找出来，以及找出来之后请求是不是真的通了。

using System.Diagnostics;
using System.Net.Sockets;
using PclLike.Core.Minecraft.Loaders;
using PclLike.Core.Net;

Console.OutputEncoding = System.Text.Encoding.UTF8;

// ── A. 本机那几个常见端口，谁在听 ──
Console.WriteLine("── A. 本机常见代理端口 ──");
foreach (var port in new[] { 7897, 7890, 10809, 10808, 1080, 8889, 8080, 1087 })
{
    var listening = false;
    try
    {
        using var c = new TcpClient();
        listening = c.ConnectAsync("127.0.0.1", port).Wait(300) && c.Connected;
    }
    catch { /* 没在听 */ }

    Console.WriteLine($"  127.0.0.1:{port,-6} {(listening ? "在听" : "—")}");
}

// ── B. 解析结果 ──
Console.WriteLine();
Console.WriteLine("── B. ProxyResolver 解析 ──");

var auto = ProxyResolver.Refresh("auto", null);
Console.WriteLine($"  auto           → {auto.DisplayText}");

using (var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
           @"Software\Microsoft\Windows\CurrentVersion\Internet Settings"))
{
    Console.WriteLine($"  注册表 ProxyEnable = {key?.GetValue("ProxyEnable") ?? "(空)"}");
    Console.WriteLine($"  注册表 ProxyServer = {key?.GetValue("ProxyServer") ?? "(空)"}");
}

Console.WriteLine($"  环境变量 HTTPS_PROXY = {Environment.GetEnvironmentVariable("HTTPS_PROXY") ?? "(空)"}");

var manual = ProxyResolver.Refresh("manual", "127.0.0.1:7897");
Console.WriteLine($"  manual(填7897) → {manual.DisplayText}");

var off = ProxyResolver.Refresh("off", null);
Console.WriteLine($"  off            → {off.DisplayText}");

// ── C. 代理生效时，NeoForge 的 maven 通不通 ──
Console.WriteLine();
Console.WriteLine("── C. 真去拉一次 NeoForge maven（这是原来超时的那一步）──");

const string url = "https://maven.neoforged.net/releases/net/neoforged/forge/maven-metadata.xml";

async Task ProbeAsync(string label)
{
    LoaderHttp.ResetClient();

    var sw = Stopwatch.StartNew();
    try
    {
        var text = await LoaderHttp.GetStringAsync(url, CancellationToken.None);
        sw.Stop();
        Console.WriteLine($"  {label,-22} ✓ {text.Length} 字节 / {sw.ElapsedMilliseconds} ms");
    }
    catch (Exception ex)
    {
        sw.Stop();
        Console.WriteLine($"  {label,-22} ✗ {sw.ElapsedMilliseconds} ms  {ex.GetType().Name}: {ex.Message}");
    }
}

// 让 LoaderHttp 也走同一个解析结果
LoaderHttp.ProxyUrl = null;
ProxyResolver.Refresh("auto", null);
await ProbeAsync("auto（探测到就用）");

ProxyResolver.Refresh("off", null);
LoaderHttp.ProxyUrl = "";
await ProbeAsync("off（强制直连）");

ProxyResolver.Refresh("manual", "127.0.0.1:7897");
LoaderHttp.ProxyUrl = "127.0.0.1:7897";
await ProbeAsync("manual(127.0.0.1:7897)");

// ── D. 起一个只有 30 行的迷你代理，验证"请求真的从代理走了" ──
//
// 上面那一段只能证明"设置被采纳了"（连 7897 被拒 = 它确实去连代理了）。
// 要证明"经过代理能拿到数据"，得有个真代理。这里自己起一个只做 CONNECT
// 转发的：它记下每次隧道请求的目标主机，再看请求是否真的返回了内容。
Console.WriteLine();
Console.WriteLine("── D. 迷你 CONNECT 代理 + 真过一遍 ──");

var proxy = new MiniProxy();
proxy.Start();
Console.WriteLine($"  迷你代理监听 127.0.0.1:{proxy.Port}");

const string reachable = "https://api.modrinth.com/v2/search?limit=1&query=fabric";

ProxyResolver.Refresh("manual", $"http://127.0.0.1:{proxy.Port}");
LoaderHttp.ProxyUrl = $"http://127.0.0.1:{proxy.Port}";

// 关键：LoaderHttp.Client 是缓存住的，改完代理必须让它重建 ——
// 第一次写这个探针时就漏了这一步，结果 D 段两次请求都还在往 C 段那个
// 已经死掉的 7897 发（错误信息里写得明明白白）。生产代码里
// MainViewModel.ApplyProxyAsync 是带 ResetClient 的，提醒：以后任何
// "改代理"的地方都必须跟着调一次。
LoaderHttp.ResetClient();

var sw2 = Stopwatch.StartNew();
try
{
    var body = await LoaderHttp.GetStringAsync(reachable, CancellationToken.None);
    sw2.Stop();
    Console.WriteLine($"  经代理请求 api.modrinth.com ✓ {body.Length} 字节 / {sw2.ElapsedMilliseconds} ms");
}
catch (Exception ex)
{
    sw2.Stop();
    Console.WriteLine($"  经代理请求 api.modrinth.com ✗ {sw2.ElapsedMilliseconds} ms  {ex.Message}");
}

await Task.Delay(300);
Console.WriteLine($"  代理看到的隧道请求：{(proxy.Tunnels.Count == 0 ? "（一条都没有！）" : string.Join("、", proxy.Tunnels))}");

// 反向对照：不走代理，同一个地址（应该也是通的，但代理那条记录就不该再增加）
ProxyResolver.Refresh("off", null);
LoaderHttp.ProxyUrl = "";
LoaderHttp.ResetClient();
var before = proxy.Tunnels.Count;
try
{
    var body = await LoaderHttp.GetStringAsync(reachable, CancellationToken.None);
    Console.WriteLine($"  不经代理请求同一个地址 ✓ {body.Length} 字节");
}
catch (Exception ex)
{
    Console.WriteLine($"  不经代理请求同一个地址 ✗ {ex.Message}");
}

await Task.Delay(300);
Console.WriteLine($"  代理记录条数 {before} → {proxy.Tunnels.Count}（应当不变，说明 off 真的是直连）");
proxy.Stop();

// 收尾：恢复成自动，别把探针的临时设置留给真正的启动器
ProxyResolver.Refresh("auto", null);
LoaderHttp.ProxyUrl = null;
Console.WriteLine();
Console.WriteLine($"结束，恢复为：{ProxyResolver.Current.DisplayText}");

/// <summary>只做 CONNECT 的迷你代理，够用来证明"请求确实从这儿过了"。</summary>
internal sealed class MiniProxy
{
    private readonly List<string> _tunnels = new();
    private TcpListener? _listener;

    /// <summary>被隧道过的 "主机:端口"，用来断言。</summary>
    public IReadOnlyList<string> Tunnels
    {
        get { lock (_tunnels) return _tunnels.ToArray(); }
    }

    public int Port { get; private set; }

    public void Start()
    {
        _listener = new TcpListener(System.Net.IPAddress.Loopback, 0);
        _listener.Start();
        Port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
        _ = AcceptLoopAsync();
    }

    public void Stop()
    {
        try { _listener?.Stop(); } catch { /* 收尾 */ }
    }

    private async Task AcceptLoopAsync()
    {
        while (true)
        {
            TcpClient client;
            try
            {
                client = await _listener!.AcceptTcpClientAsync();
            }
            catch
            {
                return;   // 停了
            }

            _ = HandleAsync(client);
        }
    }

    private async Task HandleAsync(TcpClient client)
    {
        try
        {
            using (client)
            {
                var downstream = client.GetStream();
                using var reader = new StreamReader(
                    downstream, System.Text.Encoding.ASCII, false, 1024, leaveOpen: true);

                var requestLine = await reader.ReadLineAsync();
                string? header;
                while (!string.IsNullOrEmpty(header = await reader.ReadLineAsync())) { }

                if (string.IsNullOrWhiteSpace(requestLine)) return;

                var parts = requestLine.Split(' ');
                if (parts.Length < 2 || !parts[0].Equals("CONNECT", StringComparison.OrdinalIgnoreCase))
                {
                    // 非 CONNECT（明文 HTTP）也简单拒绝掉 —— 这个探针只用 CONNECT
                    var deny = System.Text.Encoding.ASCII.GetBytes("HTTP/1.1 405 Method Not Allowed\r\n\r\n");
                    await downstream.WriteAsync(deny);
                    return;
                }

                var target = parts[1];
                lock (_tunnels) _tunnels.Add(target);

                var colon = target.LastIndexOf(':');
                var host = target[..colon];
                var port = int.Parse(target[(colon + 1)..]);

                using var upstream = new TcpClient();
                await upstream.ConnectAsync(host, port);

                await downstream.WriteAsync(System.Text.Encoding.ASCII.GetBytes(
                    "HTTP/1.1 200 Connection Established\r\n\r\n"));

                var up = upstream.GetStream();
                await Task.WhenAny(downstream.CopyToAsync(up), up.CopyToAsync(downstream));
            }
        }
        catch
        {
            // 代理隧道里的任何异常都不该影响探针结论
        }
    }
}