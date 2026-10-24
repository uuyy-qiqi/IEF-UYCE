using System;
using System.IO;
using PclLike.Core.Storage;

class Probe {
    static void Main() {
        var root = Path.Combine(Path.GetTempPath(), "pcl_inst_probe");
        if (Directory.Exists(root)) Directory.Delete(root, true);

        var store = new InstanceStore(root);
        Console.WriteLine($"launcherRoot = {store.LauncherRoot}");

        var iso = store.Save(new GameInstance {
            Id = "Test Pack 1.20",  // 故意带空格，测 SanitizeId
            Name = "1.20.1 整合包",
            VersionId = "1.20.1",
            Isolated = true,
            MaxMemoryMb = 8192,
            JvmArgs = "-XX:+UseG1GC\n-XX:MaxGCPauseMillis=50",
        });
        Console.WriteLine($"iso.Id       = {iso.Id}");
        Console.WriteLine($"iso.Directory= {iso.Directory}");
        Console.WriteLine($"iso.GameDir  = {iso.GameDir}");
        Console.WriteLine($"iso.Isolated = {iso.Isolated} / {iso.IsolationLabel}");
        Console.WriteLine($"iso.mem      = {iso.MaxMemoryMb}");
        Console.WriteLine($"iso.jvmArgs  = [{string.Join(" | ", iso.ExtraJvmArgs)}]");

        var shared = store.Save(new GameInstance {
            Id = "shared", Name = "共享", VersionId = "1.21.4", Isolated = false,
        });
        Console.WriteLine($"shared.GameDir = {shared.GameDir}");
        Console.WriteLine($"  期望 == launcherRoot: {shared.GameDir == root}");

        var all = store.LoadAll();
        Console.WriteLine($"LoadAll -> {all.Count} 个实例");
        foreach (var i in all)
            Console.WriteLine($"   {i.Id,-16} {i.Name,-14} {i.VersionId,-10} {i.IsolationLabel}");

        // 持久化回读
        var reread = store.Load("Test_Pack_1_20");
        Console.WriteLine($"回读 name={reread?.Name} mem={reread?.MaxMemoryMb} jvm={reread?.ExtraJvmArgs.Count}条");
        Console.WriteLine($"回读 GameDir 一致: {reread?.GameDir == iso.GameDir}");

        // 目录真的建了吗
        Console.WriteLine($"隔离目录存在: {Directory.Exists(iso.GameDir)}");
        Console.WriteLine($"元数据存在:   {File.Exists(store.FileFor(iso.Id))}");
    }
}
