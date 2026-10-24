using System.Text.Json;
using PclLike.Core.Login;
using PclLike.Core.Storage;

Console.OutputEncoding = System.Text.Encoding.UTF8;
var tmp = Path.Combine(Path.GetTempPath(), "pcllike-auth-probe-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(tmp);

Console.WriteLine("══════════ 账号模块验证 ══════════");
Console.WriteLine($"临时根目录: {tmp}");
Console.WriteLine();

// ── 1. 离线 uuid 规则与 ConfigStore 一致 ──
Console.WriteLine("[1] 离线 uuid 稳定性与格式");
var n1 = "Steve";
var a = AccountManager.PlayerProfile.Offline(n1);
var b = AccountManager.PlayerProfile.Offline(n1);
var c = AccountManager.PlayerProfile.Offline("Alex");
Console.WriteLine($"    Steve  → {a.Uuid}");
Console.WriteLine($"    两次一致: {a.Uuid == b.Uuid}");
Console.WriteLine($"    不同名不同 uuid: {a.Uuid != c.Uuid}");
Console.WriteLine($"    与 ConfigStore 一致: {a.Uuid == ConfigStore.OfflineUuid(n1)}");
Console.WriteLine($"    格式合法(8-4-4-4-12): {System.Text.RegularExpressions.Regex.IsMatch(a.Uuid, "^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$")}");
Console.WriteLine($"    accessToken: '{a.AccessToken}'  IsOnline={a.IsOnline}");
Console.WriteLine();

// ── 2. 持久化往返 ──
Console.WriteLine("[2] 持久化往返");
{
    var m = new AccountManager(tmp);
    Console.WriteLine($"    初始 Profile: {(m.Profile is null ? "null（未登录）" : m.Profile.Name)}");
    Console.WriteLine($"    初始 OfflineName: '{m.OfflineName}'");

    m.SetOffline("Notch");
    var f = Path.Combine(tmp, "accounts.json");
    Console.WriteLine($"    落盘文件存在: {File.Exists(f)}");

    var m2 = new AccountManager(tmp);
    Console.WriteLine($"    重载后 Name: '{m2.Profile?.Name}'  IsOnline={m2.Profile?.IsOnline}");
    Console.WriteLine($"    重载后 OfflineName: '{m2.OfflineName}'");
    Console.WriteLine($"    uuid 保持: {m2.Profile?.Uuid == m.Profile?.Uuid}");
}
Console.WriteLine();

// ── 3. 正版档保存/读取 ──
Console.WriteLine("[3] 正版档保存与读取");
{
    var m = new AccountManager(tmp);
    m.SetOnlineProfile("069a79f4-44e9-4726-a5be-fca90e38aaf5", "Notch", "FAKE_AT", "FAKE_RT");
    var m2 = new AccountManager(tmp);
    var p = m2.Profile!;
    Console.WriteLine($"    Name={p.Name} IsOnline={p.IsOnline}");
    Console.WriteLine($"    Uuid={p.Uuid}");
    Console.WriteLine($"    AccessToken={p.AccessToken}");
    Console.WriteLine($"    RefreshToken={p.RefreshToken}");
    Console.WriteLine($"    文件含敏感提示: {File.ReadAllText(Path.Combine(tmp, "accounts.json")).Contains("请勿分享")}");

    m2.Clear();
    var m3 = new AccountManager(tmp);
    Console.WriteLine($"    退出登录后 Profile: {(m3.Profile is null ? "null ✓" : m3.Profile.Name)}");
}
Console.WriteLine();

// ── 4. client_id 未配置时的行为 ──
Console.WriteLine("[4] client_id 未配置 → 应给出注册指引而非 AADSTS");
{
    var auth = new MicrosoftAuth(null);
    Console.WriteLine($"    IsConfigured: {auth.IsConfigured}  （期望 False）");
    try
    {
        await auth.StartDeviceCodeAsync();
        Console.WriteLine("    ✗ 不该成功");
    }
    catch (InvalidOperationException ex)
    {
        var first = ex.Message.Split('\n')[0];
        Console.WriteLine($"    ✓ 抛出可操作错误: {first}");
        Console.WriteLine($"      含注册步骤: {ex.Message.Contains("应用注册")}");
        Console.WriteLine($"      含门户链接: {ex.Message.Contains("portal.azure.com")}");
        Console.WriteLine($"      提到离线可用: {ex.Message.Contains("离线模式")}");
    }
}
Console.WriteLine();

// ── 5. 假 client_id → 应被微软拒绝（证明链路真的打到服务端）──
Console.WriteLine("[5] 假 client_id 的真实服务端应答");
{
    var auth = new MicrosoftAuth("00000000-0000-0000-0000-000000000000");
    Console.WriteLine($"    IsConfigured: {auth.IsConfigured}  （期望 True）");
    try
    {
        await auth.StartDeviceCodeAsync();
        Console.WriteLine("    ✗ 意外成功（不该发生）");
    }
    catch (InvalidOperationException ex)
    {
        var m = System.Text.RegularExpressions.Regex.Match(ex.Message, "AADSTS\\d+");
        Console.WriteLine($"    ✓ 服务端拒绝，错误码 {(m.Success ? m.Value : "(无)")}（期望 AADSTS700038/700016）");
    }
    catch (Exception ex)
    {
        Console.WriteLine($"    异常: {ex.GetType().Name}: {ex.Message[..Math.Min(100, ex.Message.Length)]}");
    }
}
Console.WriteLine();

// ── 6. 离线名边界（ASCII 规则，与对话框一致）──
Console.WriteLine("[6] 离线名边界（ASCII 字母数字下划线，3~16）");
static bool Valid(string n) =>
    n.Length is >= 3 and <= 16 &&
    n.All(ch => (ch >= 'a' && ch <= 'z') || (ch >= 'A' && ch <= 'Z') || (ch >= '0' && ch <= '9') || ch == '_');

foreach (var (name, expect) in new (string, bool)[]
{
    ("abc", true), ("ab", false), (new string('a', 17), false),
    ("Steve_123", true), ("steve-1", false), ("玩家名", false), ("", false),
    ("Тест", false), ("プレイヤー", false), ("User Name", false),
})
    Console.WriteLine($"    '{name}' → {Valid(name)}  期望 {expect}  {(Valid(name) == expect ? "✓" : "✗")}");

Console.WriteLine();
Console.WriteLine("══════════ 验证结束 ══════════");
try { Directory.Delete(tmp, true); } catch { }