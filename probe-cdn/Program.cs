// 专门验证「系统解析器给出死节点时，能不能靠别的解析器兜住」。
//
// 背景：cdn.modrinth.com 的默认 DNS 答案在本机长期只回一个 146.75.113.242，
// 而那个 Fastly 边缘的 TCP 是死的。修好之前，这个下载必定失败。

using System.Diagnostics;
using PclLike.Core.Mods;

var file = new ModDownloadCandidate
{
    Platform = "modrinth",
    ProjectId = "P7dR8mSH",
    VersionId = "rvI2dfzR",
    Name = "Fabric API",
    Version = "0.92.12+1.20.1",
    FileName = "fabric-api-0.92.12+1.20.1.jar",
    DownloadUrl = "https://cdn.modrinth.com/data/P7dR8mSH/versions/rvI2dfzR/fabric-api-0.92.12%2B1.20.1.jar",
};

using var downloader = new ModDownloader();
var target = Path.Combine(Path.GetTempPath(), "probe-cdn-fabapi.jar");
if (File.Exists(target)) File.Delete(target);

Console.WriteLine("=== 系统解析器当前给出的地址 ===");
try
{
    foreach (var ip in await System.Net.Dns.GetHostAddressesAsync("cdn.modrinth.com"))
        Console.WriteLine($"    {ip}");
}
catch (Exception ex) { Console.WriteLine($"    DNS 失败：{ex.Message}"); }

Console.WriteLine();
Console.WriteLine("=== 下载（走 ModDownloader 的连接层）===");
var sw = Stopwatch.StartNew();
var ok = await downloader.DownloadAsync(file, target);
sw.Stop();

Console.WriteLine($"    结果 = {ok}");
Console.WriteLine($"    耗时 = {sw.ElapsedMilliseconds} ms");

if (File.Exists(target))
{
    var info = new FileInfo(target);
    Console.WriteLine($"    落盘 = {info.Length} 字节");

    // 真的下全了吗：jar 是 zip，头必须是 PK\x03\x04，而且体积得是 MB 级
    var head = new byte[4];
    using (var fs = File.OpenRead(target)) fs.ReadExactly(head);
    var isZip = head[0] == 0x50 && head[1] == 0x4B && head[2] == 0x03 && head[3] == 0x04;
    Console.WriteLine($"    是完整 jar（PK 头）= {isZip}");
    Console.WriteLine($"    体积合理（>1MB）  = {info.Length > 1024 * 1024}");
    File.Delete(target);
}

Console.WriteLine();
Console.WriteLine("=== 再走一次（这次应命中 IP 缓存，明显更快）===");
sw.Restart();
var ok2 = await downloader.DownloadAsync(file, target);
sw.Stop();
Console.WriteLine($"    结果 = {ok2}  耗时 = {sw.ElapsedMilliseconds} ms");
if (File.Exists(target)) File.Delete(target);

return ok && ok2 ? 0 : 1;