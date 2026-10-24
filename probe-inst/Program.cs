using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using PclLike.Core.Minecraft;
using PclLike.Core.Minecraft.Loaders;
using PclLike.Core.Storage;

class FullTest {
    static async Task Main() {
        var root = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".pcllike");
        var li = new LoaderInstaller(root);
        var store = new InstanceStore(root);
        var ct = CancellationToken.None;

        Console.WriteLine("=== A. 启动 Fabric 实例（真跑游戏）===");
        var inst = store.EnsureDefault("1.20.1");
        var fab = store.Create("Fabric测试", "fabric-loader-0.20.1-1.20.1", true);
        using (var svc = new LaunchService(root)) {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(300));
            var reached = false;
            try {
                await svc.LaunchAsync(new LaunchService.LaunchOptions {
                    VersionId = fab.VersionId, Instance = fab,
                    Username = "FabTest", Uuid = ConfigStore.OfflineUuid("FabTest"),
                    AccessToken = "0", MaxMemoryMb = 2048,
                }, null, line => {
                    if (line.Contains("Setting user") || line.Contains("Loading Minecraft") ||
                        line.Contains("实例") || line.Contains("ERROR") || line.Contains("Exception") ||
                        line.Contains("fabricmc"))
                        Console.WriteLine("   | " + line.Trim());
                    if (line.Contains("Setting user")) { reached = true; svc.KillGame(); }
                }, cts.Token);
            } catch (TaskCanceledException) { Console.WriteLine("   (取消)"); }
            catch (Exception ex) { Console.WriteLine($"   异常: {ex.GetType().Name}: {ex.Message}"); }
            Console.WriteLine($"  >>> Fabric 进入主循环: {reached}");
        }

        Console.WriteLine();
        Console.WriteLine("=== B. 安装 Forge 1.20.1 ===");
        try {
            var prog = new Progress<LoaderInstaller.Progress>(p => Console.WriteLine($"    [{p.Stage}]"));
            var r = await li.InstallAsync(LoaderKind.Forge, "1.20.1", null, prog, ct);
            Console.WriteLine($"  版本 id = {r.VersionId}");
            Console.WriteLine($"  JSON = {new FileInfo(r.JsonPath).Length} 字节");
            var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(r.JsonPath));
            var e = doc.RootElement;
            Console.WriteLine($"  id={e.GetProperty("id").GetString()}");
            Console.WriteLine($"  inheritsFrom={(e.TryGetProperty("inheritsFrom", out var i) ? i.GetString() : "(无)")}");
            Console.WriteLine($"  mainClass={(e.TryGetProperty("mainClass", out var m) ? m.GetString() : "(无)")}");
            Console.WriteLine($"  libraries={e.GetProperty("libraries").GetArrayLength()} 个");
        } catch (Exception ex) {
            Console.WriteLine($"  失败: {ex.GetType().Name}: {ex.Message}");
            if (ex.InnerException is not null) Console.WriteLine($"  内层: {ex.InnerException.Message}");
        }

        Console.WriteLine();
        Console.WriteLine("=== C. Mod 实例隔离 ===");
        var modInst = store.Create("Mod服", "1.20.1", true);
        var modsDir = store.ModsDirFor(modInst);
        Directory.CreateDirectory(modsDir);
        File.WriteAllText(Path.Combine(modsDir, "test-mod.jar"), "fake");
        Console.WriteLine($"  mods 目录 = {modsDir}");
        Console.WriteLine($"  在实例内: {modsDir.StartsWith(modInst.Directory, StringComparison.OrdinalIgnoreCase)}");
    }
}
