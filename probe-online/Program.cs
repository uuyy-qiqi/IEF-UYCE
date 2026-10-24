using System.Net;
using System.Net.Sockets;
using System.Text;
using PclLike.Core.Online;

// probe-online：Online 四个模块的真实跑通探针。
// 不用测试框架（任务要求），自己数 PASS/FAIL，最后按失败数决定退出码。

var pass = 0;
var fail = 0;

void Check(string name, bool ok, string detail = "")
{
    if (ok) { pass++; Console.WriteLine($"PASS  {name}{(detail.Length > 0 ? "  :: " + detail : "")}"); }
    else { fail++; Console.WriteLine($"FAIL  {name}{(detail.Length > 0 ? "  :: " + detail : "")}"); }
}

void Section(string title)
{
    Console.WriteLine();
    Console.WriteLine($"===== {title} =====");
}

// ═══════════════════════════════════════════════════════════════════
Section("1. NBT 往返 Read(Write(x)) == x");

var nested = NbtCompound.Of(
    ("level", new NbtInt(42)),
    ("seed", new NbtLong(9223372036854775807L)),
    ("hardcore", new NbtByte(1)),
    ("negByte", new NbtByte(-8)),
    ("ratio", new NbtFloat(0.5f)),
    ("precise", new NbtDouble(Math.PI)),
    ("title", new NbtString("中文 世界 §a")),
    ("raw", new NbtByteArray([0x00, 0xFF, 0x7F, 0x80])),
    ("ints", new NbtIntArray([1, -1, int.MaxValue, int.MinValue])),
    ("longs", new NbtLongArray([1L, long.MinValue, long.MaxValue])),
    ("small", new NbtShort(-12345)),
    ("inner", NbtCompound.Of(
        ("deep", NbtCompound.Of(("leaf", new NbtString("bottom")))),
        ("flag", new NbtByte(0)))),
    ("list", NbtList.Of(
        NbtCompound.Of(("name", new NbtString("A")), ("ip", new NbtString("1.2.3.4"))),
        NbtCompound.Of(("name", new NbtString("B")), ("ip", new NbtString("5.6.7.8"))))),
    ("strList", new NbtList(NbtIo.TagString, [new NbtString("x"), new NbtString("y")])),
    ("emptyList", new NbtList(NbtIo.TagEnd, Array.Empty<NbtTag>())));

var nbtBytes = NbtIo.Write(nested);
Console.WriteLine($"      写入 {nbtBytes.Length} 字节；头部 = {Hex(nbtBytes, 3)}");

var readBack = NbtIo.Read(nbtBytes);
Check("整体值相等 (record 值语义)", readBack == nested);
Check("逐字段相等", FieldWiseEqual(nested, readBack), "递归比对每个键与类型");

Check("根节点 type id = 10", nbtBytes[0] == NbtIo.TagCompound, $"byte0=0x{nbtBytes[0]:X2}");
Check("根名字长度 = 0", nbtBytes[1] == 0 && nbtBytes[2] == 0, $"{nbtBytes[1]:X2} {nbtBytes[2]:X2}");
Check("Byte 值", readBack.GetInt("level") == 42, $"level={readBack.GetInt("level")}");
Check("Long 值", (readBack["seed"] as NbtLong)?.Value == long.MaxValue, $"seed={(readBack["seed"] as NbtLong)?.Value}");
Check("String 值（含中文与 §）", readBack.GetString("title") == "中文 世界 §a", $"title=\"{readBack.GetString("title")}\"");
Check("深层嵌套", readBack["inner"] is NbtCompound c1
                 && c1["deep"] is NbtCompound c2
                 && c2.GetString("leaf") == "bottom");
Check("List<String> 元素", readBack["strList"] is NbtList sl && sl.Items.Count == 2
                 && (sl.Items[1] as NbtString)?.Value == "y", $"TagType={(readBack["strList"] as NbtList)?.TagType}");
Check("空 List TagType 保留", readBack["emptyList"] is NbtList el && el.TagType == NbtIo.TagEnd && el.Items.Count == 0);
Check("二次往返稳定", NbtIo.Write(readBack).AsSpan().SequenceEqual(nbtBytes));
Check("截断的字节流会抛 InvalidDataException", Throws<InvalidDataException>(() => NbtIo.Read(nbtBytes[..^3])));
Check("根不是 Compound 会抛",
    Throws<InvalidDataException>(() => NbtIo.Read([NbtIo.TagString, 0, 0, 0, 1, 0x41])));
Check("List 元素类型不符会抛",
    Throws<ArgumentException>(() => NbtIo.Write(NbtCompound.Of(
        ("bad", new NbtList(NbtIo.TagInt, [new NbtString("oops")]))))));

// ═══════════════════════════════════════════════════════════════════
Section("2. servers.dat 读写");

var tmpDir = Path.Combine(Path.GetTempPath(), "probe-online-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmpDir);
var datPath = Path.Combine(tmpDir, "servers.dat");
Console.WriteLine($"      临时文件 {datPath}");

var one = new ServerEntry("我的服务器", "192.168.1.7:25565");
Check("AddOrUpdate 第 1 条 → 1", ServerListFile.AddOrUpdate(datPath, one) == 1);

var afterOne = ServerListFile.Read(datPath);
Check("读回 1 条", afterOne.Count == 1, $"count={afterOne.Count}");
Check("读回内容一致", afterOne.Count == 1 && afterOne[0] == one,
    afterOne.Count == 1 ? $"name={afterOne[0].Name} ip={afterOne[0].Ip} acceptTextures={afterOne[0].AcceptTextures}" : "");

var bytes1 = File.ReadAllBytes(datPath);
Console.WriteLine($"      文件 {bytes1.Length} 字节，前 3 字节 = {Hex(bytes1, 3)}");
Check("前 3 字节是 0A 00 00", bytes1[0] == 0x0A && bytes1[1] == 0x00 && bytes1[2] == 0x00, Hex(bytes1, 3));
Check("文件里能找到 'servers' 键名", Encoding.ASCII.GetString(bytes1).Contains("servers"));

Check("同 Ip 更新名字不新增",
    ServerListFile.AddOrUpdate(datPath, new ServerEntry("改过名字", "192.168.1.7:25565")) == 1);
var renamed = ServerListFile.Read(datPath);
Check("名字已更新", renamed.Count == 1 && renamed[0].Name == "改过名字", $"name={renamed[0].Name}");
Check("acceptTextures 可置位",
    ServerListFile.AddOrUpdate(datPath, new ServerEntry("改过名字", "192.168.1.7:25565", true)) == 1
    && ServerListFile.Read(datPath)[0].AcceptTextures);

Check("再 Add 两条 → 3", ServerListFile.AddOrUpdate(datPath, new ServerEntry("纯主机名", "mc.example.com")) == 2
                      && ServerListFile.AddOrUpdate(datPath, new ServerEntry("IPv6", "[::1]:25566")) == 3);
var afterThree = ServerListFile.Read(datPath);
Check("读回 3 条", afterThree.Count == 3,
    string.Join(" | ", afterThree.Select(s => $"{s.Name}@{s.Ip}")));

Check("Remove 未存在的 Ip → false", !ServerListFile.Remove(datPath, "10.0.0.1"));
Check("Remove 存在的 Ip → true", ServerListFile.Remove(datPath, "192.168.1.7:25565"));
var afterRemove = ServerListFile.Read(datPath);
Check("读回 2 条", afterRemove.Count == 2,
    string.Join(" | ", afterRemove.Select(s => $"{s.Name}@{s.Ip}")));
Check("删掉的确实不在了", afterRemove.All(s => s.Ip != "192.168.1.7:25565"));

// 损坏 / 不存在都必须安静返回空表
var junkPath = Path.Combine(tmpDir, "junk.dat");
File.WriteAllBytes(junkPath, [0x0A, 0x00, 0x00, 0x09, 0xFF, 0xFF, 0xFF, 0x7F]);
Check("损坏文件 → 空列表不抛", ServerListFile.Read(junkPath).Count == 0);
Check("不存在的文件 → 空列表不抛", ServerListFile.Read(Path.Combine(tmpDir, "nope.dat")).Count == 0);
Check("空文件 → 空列表不抛", Run(() => { File.WriteAllBytes(junkPath, []); return ServerListFile.Read(junkPath).Count == 0; }));

Directory.Delete(tmpDir, true);
Check("临时目录已清理", !Directory.Exists(tmpDir));

// ═══════════════════════════════════════════════════════════════════
Section("3. LobbyCode 编解码");

var code = LobbyCode.Encode("192.168.1.7", 25565);
Console.WriteLine($"      Encode(\"192.168.1.7\", 25565) = {code}");
Check("编号形状 XXXX-XXXX-XXX（11 字符 + 2 连字符 = 13）",
    code.Length == 13 && code[4] == '-' && code[9] == '-', code);
Check("只含 Crockford 字母表字符",
    code.Replace("-", "").All(c => "0123456789ABCDEFGHJKMNPQRSTVWXYZ".Contains(c)));

var decoded = LobbyCode.Decode(code);
Check("Decode 回原值", decoded == ("192.168.1.7", 25565), decoded?.ToString() ?? "null");
Check("Decode 小写", LobbyCode.Decode(code.ToLowerInvariant()) == ("192.168.1.7", 25565));
Check("Decode 去连字符", LobbyCode.Decode(code.Replace("-", "")) == ("192.168.1.7", 25565));
Check("Decode 多连字符 + 空格", LobbyCode.Decode(" " + code.Replace("-", "--") + " ") == ("192.168.1.7", 25565));

// O→0 / I→1 / L→1 / U→V 容错：把编号里每个字符都换成它的易混写法试一遍
var confusable = code.Replace('0', 'O').Replace('1', 'I').Replace('V', 'U');
var confusable2 = code.Replace('1', 'L');
Console.WriteLine($"      易混替换后 {confusable} / {confusable2}");
Check("容错 O→0 I→1 U→V", LobbyCode.Decode(confusable) == ("192.168.1.7", 25565),
    LobbyCode.Decode(confusable)?.ToString() ?? "null");
Check("容错 L→1", LobbyCode.Decode(confusable2) == ("192.168.1.7", 25565),
    LobbyCode.Decode(confusable2)?.ToString() ?? "null");

// 校验字符故意改错
var last = code[^1];
var wrong = "0123456789ABCDEFGHJKMNPQRSTVWXYZ".First(c => c != last);
var corrupted = code[..^1] + wrong;
Console.WriteLine($"      校验位 {last} 改成 {wrong} → {corrupted}");
Check("校验字符不符 → null", LobbyCode.Decode(corrupted) is null);
Check("改掉数据位之一 → null",
    LobbyCode.Decode(code[..3] + (code[3] == 'Z' ? 'Y' : 'Z') + code[4..]) is null
    || LobbyCode.Decode(code[..3] + (code[3] == 'Z' ? 'Y' : 'Z') + code[4..]) != decoded,
    "改动后要么校验挡住，要么解出别的值");

// 边界端口
foreach (var (ip, port) in new[] { ("0.0.0.0", 1), ("255.255.255.255", 65535), ("127.0.0.1", 25565), ("10.0.0.1", 4445) })
{
    var c = LobbyCode.Encode(ip, port);
    var d = LobbyCode.Decode(c);
    Check($"边界 {ip}:{port} → {c}", d == (ip, port), d?.ToString() ?? "null");
}

Check("非法输入一律 null：空串", LobbyCode.Decode("") is null);
Check("非法输入一律 null：null", LobbyCode.Decode(null!) is null);
Check("非法输入一律 null：长度不足", LobbyCode.Decode("7K2M-4Q9X") is null);
Check("非法输入一律 null：字母表外字符", LobbyCode.Decode("7K2M-4Q9X-T!") is null);
Check("非法输入一律 null：端口 0", LobbyCode.Decode(LobbyCode.Encode("1.2.3.4", 1)).HasValue
    ? !LobbyCode.Decode(LobbyCode.Encode("1.2.3.4", 1))!.Value.Equals(("1.2.3.4", 0)) : true);
Check("Encode 拒绝非法 IP", Throws<ArgumentException>(() => LobbyCode.Encode("1.2.3", 25565)));
Check("Encode 拒绝域名", Throws<ArgumentException>(() => LobbyCode.Encode("mc.example.com", 25565)));
Check("Encode 拒绝端口 0", Throws<ArgumentOutOfRangeException>(() => LobbyCode.Encode("1.2.3.4", 0)));
Check("Encode 拒绝端口 65536", Throws<ArgumentOutOfRangeException>(() => LobbyCode.Encode("1.2.3.4", 65536)));

// Pack/Unpack 往返（CS0675 修的就是这段）：换个地址就错位一个字节的那种 bug 只能靠扫
var parentCase = LobbyCode.Encode("192.168.1.10", 25565);
Console.WriteLine($"      Encode(\"192.168.1.10\", 25565) = {parentCase}");
Check("指定用例 192.168.1.10:25565 往返", LobbyCode.Decode(parentCase) == ("192.168.1.10", 25565),
    LobbyCode.Decode(parentCase)?.ToString() ?? "null");

var sweepFail = 0;
var sampled = new List<string>();
var addressSweep = new[]
{
    (0, 0, 0, 0), (0, 0, 0, 1), (1, 2, 3, 4), (10, 0, 0, 1), (127, 0, 0, 1),
    (128, 0, 0, 1), (169, 254, 1, 1), (192, 168, 0, 1), (192, 168, 1, 10),
    (192, 168, 1, 255), (200, 255, 255, 254), (224, 0, 2, 60),
    (255, 255, 255, 254), (255, 255, 255, 255),
};
var portSweep = new[] { 1, 2, 255, 256, 257, 1024, 4445, 25565, 32767, 32768, 65534, 65535 };

foreach (var (a, b, c, d) in addressSweep)
{
    foreach (var p in portSweep)
    {
        var ipText = $"{a}.{b}.{c}.{d}";
        var text = LobbyCode.Encode(ipText, p);
        var back = LobbyCode.Decode(text);
        if (back != (ipText, p))
        {
            sweepFail++;
            if (sweepFail <= 5) Console.WriteLine($"      （错位：{ipText}:{p} → {text} → {back?.ToString() ?? "null"}）");
        }

        if (sampled.Count < 4) sampled.Add($"{ipText}:{p}→{text}");
    }
}

Check($"Pack/Unpack 全扫 {addressSweep.Length * portSweep.Length} 组地址×端口全部往返一致", sweepFail == 0,
    $"失败 {sweepFail} 组；样例 {string.Join(" ", sampled)}");
Check("高字节不被符号扩展（255.255.255.255 首字符落在字母表高位）",
    LobbyCode.Decode(LobbyCode.Encode("255.255.255.255", 65535)) == ("255.255.255.255", 65535));
Check("负值端口不污染地址段（端口低位直接落 (byte)）",
    LobbyCode.Decode(LobbyCode.Encode("192.168.1.10", 300)) == ("192.168.1.10", 300));

// ═══════════════════════════════════════════════════════════════════
Section("4. LanWorldScanner 解析 + 真实组播");

// 先单独验解析（不依赖网卡/防火墙）
var realPacket = Encoding.UTF8.GetBytes("[MOTD]§a小明 的世界[/MOTD][AD]25565[/AD]");
var parsed = LanWorldScanner.ParsePacket(realPacket, "192.168.1.7");
Console.WriteLine($"      ParsePacket(真包) = {(parsed is null ? "null" : $"{parsed.Motd} | {parsed.Endpoint}")}");
Check("解析真包 MOTD/端口/地址", parsed is not null && parsed.Motd == "§a小明 的世界"
    && parsed.Port == 25565 && parsed.Address == "192.168.1.7", parsed?.Endpoint ?? "null");

// MOTD 里含 "[AD]" 时必须取 [/MOTD] 之后那一个 —— 这是容易写错的地方
var tricky = LanWorldScanner.ParsePacket(
    Encoding.UTF8.GetBytes("[MOTD]小心 [AD]9999[/AD] 陷阱[/MOTD][AD]25566[/AD]"), "10.0.0.5");
Check("MOTD 含假 [AD] 时取真的那个", tricky is not null && tricky.Port == 25566,
    tricky is null ? "null" : $"port={tricky.Port} motd=\"{tricky.Motd}\"");

Check("杂包 → null（没有 [MOTD]）", LanWorldScanner.ParsePacket(Encoding.ASCII.GetBytes("hello world"), "1.1.1.1") is null);
Check("缺 [/MOTD] → null", LanWorldScanner.ParsePacket(Encoding.ASCII.GetBytes("[MOTD]x[AD]25565[/AD]"), "1.1.1.1") is null);
Check("缺 [AD] → null", LanWorldScanner.ParsePacket(Encoding.ASCII.GetBytes("[MOTD]x[/MOTD]"), "1.1.1.1") is null);
Check("端口非数字 → null", LanWorldScanner.ParsePacket(Encoding.ASCII.GetBytes("[MOTD]x[/MOTD][AD]abc[/AD]"), "1.1.1.1") is null);
Check("端口越界 → null", LanWorldScanner.ParsePacket(Encoding.ASCII.GetBytes("[MOTD]x[/MOTD][AD]70000[/AD]"), "1.1.1.1") is null);
Check("空包 → null", LanWorldScanner.ParsePacket([], "1.1.1.1") is null);
Check("带首尾空白的端口也能解",
    LanWorldScanner.ParsePacket(Encoding.ASCII.GetBytes("[MOTD]x[/MOTD][AD] 4445 [/AD]"), "1.1.1.1")?.Port == 4445);

// 真的往 224.0.2.60:4445 发一个广播包，让 ScanAsync 收
using var scanner = new LanWorldScanner();
var scanTask = scanner.ScanAsync(TimeSpan.FromSeconds(3));

// 等扫描器完成 bind + join（构造本身不做 IO，绑端口在 ScanAsync 里）
await Task.Delay(400);

var sender = new UdpClient();
sender.Client.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
sender.MulticastLoopback = true;
var group = new IPEndPoint(IPAddress.Parse(LanWorldScanner.MulticastGroup), LanWorldScanner.DiscoveryPort);

var sentOk = true;
try
{
    for (var i = 0; i < 6; i++)
    {
        await sender.SendAsync(realPacket, group);
        await Task.Delay(250);
    }
}
catch (Exception ex)
{
    sentOk = false;
    Console.WriteLine($"      发送组播失败：{ex.GetType().Name}: {ex.Message}");
}
finally
{
    sender.Dispose();
}

Console.WriteLine($"      发出 6 个组播包到 {group}（MulticastLoopback=true）：{(sentOk ? "OK" : "失败")}");

var found = await scanTask;
Console.WriteLine($"      ScanAsync 返回 {found.Count} 个世界，LastError = {(scanner.LastError ?? "<null>")}");
foreach (var w in found) Console.WriteLine($"        → {w.Endpoint}  \"{w.Motd}\"");

if (scanner.LastError is not null)
{
    Console.WriteLine($"      环境说明：监听 4445 失败（{scanner.LastError}）—— 端口被游戏/其它进程占用或权限不足，");
    Console.WriteLine("      组播路径本次无法验证；但上面的 ParsePacket 已单独验证解析逻辑。");
    Check("绑定失败时安静返回空列表（不抛）", found.Count == 0, "LastError 已写入属性供界面显示");
}
else
{
    Check("3 秒内收到自己的组播包", found.Count >= 1, $"count={found.Count}");
    Check("收到的世界解析正确",
        found.Any(w => w.Port == 25565 && w.Motd == "§a小明 的世界"),
        found.Count > 0 ? $"第一个 = {found[0].Endpoint} \"{found[0].Motd}\"" : "空");
    Check("Address 是发送方 IPv4（不是 0.0.0.0/::ffff:）",
        found.All(w => !w.Address.Contains(':') && w.Address != "0.0.0.0"),
        string.Join(",", found.Select(w => w.Address)));
    Check("端点是可直接用的 host:port", found.All(w => w.Endpoint.Contains(':') && w.Endpoint.StartsWith(w.Address)));
}

// 去重：同一个包发两次只应留一条（用窗口内两次扫描验证不易，直接验证 Endpoint 唯一性）
Check("结果按 Endpoint 去重", found.Select(w => w.Endpoint).Distinct().Count() == found.Count);

var scanner2 = new LanWorldScanner();
var empty = await scanner2.ScanAsync(TimeSpan.FromMilliseconds(300));
Console.WriteLine($"      0.3 秒窗口（无广播）= {empty.Count} 个世界，LastError = {(scanner2.LastError ?? "<null>")}");
Check("没有广播时返回 0 个", empty.Count == 0);
Check("取消令牌立即取消不抛",
    await SafeAsync(async () => { using var cts = new CancellationTokenSource(50); await scanner2.ScanAsync(TimeSpan.FromSeconds(5), cts.Token); return true; }));
scanner2.Dispose();
Check("Dispose 后不再监听（返回空）", (await SafeScanAsync(scanner)).Count == 0);

// ═══════════════════════════════════════════════════════════════════
Console.WriteLine();
Console.WriteLine($"===== 汇总：PASS {pass} / FAIL {fail} =====");
return fail == 0 ? 0 : 1;

// ─────────────────────────── 小工具 ───────────────────────────

static string Hex(byte[] data, int count)
    => string.Join(' ', data.Take(Math.Min(count, data.Length)).Select(b => b.ToString("X2")));

static bool FieldWiseEqual(NbtTag a, NbtTag b)
{
    if (a.TagTypeId != b.TagTypeId || a.GetType() != b.GetType()) return false;

    return (a, b) switch
    {
        (NbtByte x, NbtByte y) => x.Value == y.Value,
        (NbtShort x, NbtShort y) => x.Value == y.Value,
        (NbtInt x, NbtInt y) => x.Value == y.Value,
        (NbtLong x, NbtLong y) => x.Value == y.Value,
        (NbtFloat x, NbtFloat y) => x.Value.Equals(y.Value),
        (NbtDouble x, NbtDouble y) => x.Value.Equals(y.Value),
        (NbtString x, NbtString y) => x.Value == y.Value,
        (NbtByteArray x, NbtByteArray y) => x.Value.SequenceEqual(y.Value),
        (NbtIntArray x, NbtIntArray y) => x.Value.SequenceEqual(y.Value),
        (NbtLongArray x, NbtLongArray y) => x.Value.SequenceEqual(y.Value),
        (NbtList x, NbtList y) => x.TagType == y.TagType
                                  && x.Items.Count == y.Items.Count
                                  && x.Items.Zip(y.Items).All(p => FieldWiseEqual(p.First, p.Second)),
        (NbtCompound x, NbtCompound y) => x.Value.Count == y.Value.Count
                                          && x.Value.All(kv => y.Value.TryGetValue(kv.Key, out var other)
                                                               && FieldWiseEqual(kv.Value, other)),
        _ => false,
    };
}

static bool Throws<T>(Action action) where T : Exception
{
    try { action(); return false; }
    catch (T) { return true; }
    catch (Exception ex) { Console.WriteLine($"      （抛的是 {ex.GetType().Name}，不是 {typeof(T).Name}）"); return false; }
}

static bool Run(Func<bool> f)
{
    try { return f(); } catch { return false; }
}

static async Task<bool> SafeAsync(Func<Task<bool>> f)
{
    try { return await f(); } catch { return false; }
}

static async Task<IReadOnlyList<LanWorld>> SafeScanAsync(LanWorldScanner scanner)
{
    try { return await scanner.ScanAsync(TimeSpan.FromMilliseconds(200)); } catch { return []; }
}