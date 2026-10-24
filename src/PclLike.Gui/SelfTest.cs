using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Media;
using System.Diagnostics;

namespace PclLike.Gui;

/// <summary>
/// 无头自检：把 MainWindow 真的建出来、跑完数据加载、然后检查控件树。
///
/// 存在的理由：WPF 的绑定失败是**静默**的——绑错了属性名只会在输出里
/// 留一条 Trace，界面看起来照常，值是空的。光看"进程还活着"证明不了什么。
/// 这个自检把关键控件的实际值读出来，绑错就当场暴露。
///
/// 用法：PclLike.exe --selftest
/// </summary>
public static class SelfTest
{
    public static int Run()
    {
        var failures = new List<string>();
        var notes = new List<string>();

        void Check(bool ok, string what, string? detail = null)
        {
            if (ok) notes.Add($"  [OK]   {what}" + (detail is null ? "" : $"  →  {detail}"));
            else failures.Add($"  [FAIL] {what}" + (detail is null ? "" : $"  →  {detail}"));
        }

        Window? window = null;
        MainViewModel? vm = null;

        try
        {
            // 必须走和真实启动完全相同的初始化路径：
            //   new App() → InitializeComponent()（装载 App.xaml 资源字典）
            //   → 建窗口
            // 少任何一步，MainWindow.xaml 里的 StaticResource 都找不到，
            // 自检会报出一堆假的"资源缺失"。
            var app = new App { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            app.InitializeComponent();

            window = new MainWindow();
            window.Show();

            vm = window.DataContext as MainViewModel;
            Check(vm is not null, "DataContext 是 MainViewModel");

            if (vm is null) return Report(failures, notes);

            // ---- 等异步的版本清单加载完成 ----
            notes.Add("  等待版本清单加载...");
            var sw = Stopwatch.StartNew();
            while (sw.Elapsed < TimeSpan.FromSeconds(60))
            {
                if (vm.Versions.Count > 0 || vm.StatusText.Contains("失败")) break;
                DoEvents();
                Thread.Sleep(120);
            }

            Check(vm.Versions.Count > 0, "版本列表已加载", $"{vm.Versions.Count} 个版本");
            Check(vm.VersionsView is not null, "VersionsView 存在");
            Check(vm.JavaList.Count > 0, "Java 已探测",
                string.Join("、", vm.JavaList.Select(j => $"Java {j.MajorVersion}")));
            Check(vm.SelectedVersion is not null, "自动选中了版本", vm.SelectedVersion?.Id ?? "null");
            Check(vm.SelectedVersion?.Type == "release",
                "默认选中的是正式版而非快照", $"{vm.SelectedVersion?.Id} ({vm.SelectedVersion?.Type})");
            Check(vm.CanLaunch, "启动按钮可用（CanLaunch）");

            // ---- 控件树：确认 XAML 绑定真的栓到了值 ----
            var versionList = FindByName<ListBox>(window, "VersionListBox");
            Check(versionList is not null, "找到版本列表控件");
            Check(versionList?.Items.Count > 0, "版本列表控件有内容", $"{versionList?.Items.Count} 项");

            var runButton = FindByName<Button>(window, "RunButton");
            Check(runButton is not null, "找到启动按钮");
            // 新 UI 的按钮内容是图标 + 文字的 StackPanel，所以查 VM 的文案。
            Check(vm.RunButtonText == "启动游戏", "启动按钮文案正确", vm.RunButtonText);
            Check(runButton?.IsEnabled == true, "启动按钮已启用");
            Check(runButton?.Background is SolidColorBrush, "启动按钮有背景色");

            var logList = FindByName<ListBox>(window, "LogList");
            Check(logList is not null, "找到日志列表");
            Check(logList?.Items.Count > 0, "日志列表有内容", $"{logList?.Items.Count} 行");

            var usernameBox = FindByName<TextBox>(window, "UsernameBox");
            Check(usernameBox?.Text is { Length: > 0 }, "用户名框有值", usernameBox?.Text);

            var memorySlider = FindByName<Slider>(window, "MemorySlider");
            Check(memorySlider is not null, "找到内存滑块");
            Check(memorySlider?.Value > 0, "内存滑块有值", $"{memorySlider?.Value:0} MB");

            var rootText = FindByName<TextBlock>(window, "GameRootText");
            Check(rootText?.Text is { Length: > 0 }, "设置页显示游戏目录", rootText?.Text);

            // ---- 筛选功能 ----
            notes.Add("  测试版本筛选...");
            var total = vm.Versions.Count;
            vm.VersionFilter = "1.21";
            DoEvents();

            var filtered = vm.VersionsView is null ? 0 : vm.VersionsView.Cast<object>().Count();
            Check(filtered > 0 && filtered < total, "筛选能缩小列表", $"1.21 → {filtered}/{total}");
            Check(vm.VersionCountText.Contains("筛选出"), "筛选计数文案切换", vm.VersionCountText);

            vm.VersionFilter = "";
            DoEvents();
            Check(vm.VersionsView is null || vm.VersionsView.Cast<object>().Count() == total, "清空筛选后恢复全部");

            // ---- 离线 UUID 正确性（这是多人服务器认人的关键）----
            var uuid = Core.Storage.ConfigStore.OfflineUuid("Notch");
            Check(uuid == "b50ad385-829d-3141-a216-7e7d7539ba7f",
                "离线 UUID 与 Mojang 算法一致", uuid);

            Check(Core.Storage.ConfigStore.SanitizeUsername("ab") == "Player",
                "用户名过短被替换");
            Check(Core.Storage.ConfigStore.SanitizeUsername("坏名字!!") == "Player",
                "非法字符被过滤后过短则替换");
            Check(Core.Storage.ConfigStore.SanitizeUsername("Good_Name123") == "Good_Name123",
                "合法用户名原样保留");

            // ---- 实例 ----
            // 实例决定 --gameDir，也就是存档落在哪儿。走错这个，用户老存档
            // 就等于"消失"了 —— 所以这一组值得单独测。
            notes.Add("  测试实例...");

            var store = new Core.Storage.InstanceStore(App.GameRoot);
            vm.RefreshInstances();
            Check(vm.Instances.Count == 0 || vm.SelectedInstance is not null,
                "能选出一个当前实例", vm.InstanceLabel);

            var probeName = "自检实例";
            var stale = vm.Instances.FirstOrDefault(i => i.Name == probeName);
            if (stale is not null) vm.DeleteInstance(stale, deleteFiles: true);

            var made = vm.CreateInstance(probeName, vm.SelectedVersion?.Id ?? "1.20.1", isolated: true);
            Check(made is not null && made.Isolated, "能新建隔离实例", made?.Id ?? "(null)");
            Check(made is not null
                  && made.GameDir.StartsWith(store.InstancesDir, StringComparison.OrdinalIgnoreCase)
                  && made.GameDir.EndsWith(".minecraft", StringComparison.OrdinalIgnoreCase),
                "隔离实例的 gameDir 落在 instances/<id>/.minecraft 下", made?.GameDir ?? "");
            Check(vm.SelectedInstance?.Id == made?.Id, "新建后自动设为当前实例", vm.InstanceLabel);
            Check(vm.InstanceLabel == probeName, "启动页显示的实例名跟着变", vm.InstanceLabel);
            Check(vm.InstanceIsolationLabel == "独立", "隔离实例标成「独立」", vm.InstanceIsolationLabel);

            var copied = made is null ? null : vm.CopyInstance(made, $"{probeName}-副本");
            Check(copied is not null && copied.Id != made!.Id, "能复制实例", copied?.Id ?? "(null)");

            // 收尾：两个探查实例都要清掉，别在用户机器上留垃圾
            if (copied is not null) vm.DeleteInstance(copied, deleteFiles: true);
            if (made is not null)
            {
                Check(vm.DeleteInstance(made, deleteFiles: true), "能删除实例（连文件）");
                Check(!System.IO.Directory.Exists(made.Directory), "删除后实例目录真的没了", made.Directory);
            }

            Check(MainViewModel.GuessLoader("1.20.1-forge-47.2.0") == "forge", "从版本 id 认出 forge");
            Check(MainViewModel.GuessLoader("fabric-loader-0.15.0-1.20.1") == "fabric", "从版本 id 认出 fabric");
            Check(MainViewModel.GuessLoader("neoforge-47.1.106") == "neoforge", "从版本 id 认出 neoforge");
            Check(MainViewModel.GuessLoader("1.20.1") == "vanilla", "原版 id 认成 vanilla");

            // ---- 百宝箱 ----
            notes.Add("  测试百宝箱工具...");

            var toolbox = FindByName<FrameworkElement>(window!, "ToolSecBox");
            Check(toolbox is not null, "找到百宝箱分区");

            // 结果条 + 目录体检 + 十张工具卡。加卡片忘了改这个数，这里就会红。
            var blocks = toolbox is Panel ok ? ok.Children.Count : 0;
            Check(blocks == 12, "百宝箱有 12 块（结果条 + 体检 + 10 张工具卡）", $"{blocks} 块");

            // 这几个是只读的，可以真跑 —— handler 里拼字符串拼错了这里就抓到。
            var diag = vm.BuildDiagnostics();
            Check(diag.Length > 100 && diag.Contains(vm.GameDirNow()),
                "诊断信息含当前实例目录", $"{diag.Length} 个字符");

            var cmd = vm.LastLaunchCommand();
            Check(cmd is null || cmd.Contains("java"),
                "启动命令取自日志首行", cmd is null ? "(还没启动过)" : $"{cmd.Length} 个字符");

            var junk = vm.ScanJunk();
            Check(junk.Count == junk.Paths.Count, "垃圾扫描结果自洽", $"{junk.Count} 个 / {junk.SizeText}");

            // 体检是丢到后台线程算的，等它回 UI 线程填好
            vm.RefreshSurvey();
            for (var i = 0; i < 80 && !vm.HasSurvey; i++)
            {
                DoEvents();
                System.Threading.Thread.Sleep(50);
            }

            Check(vm.HasSurvey, "体检能算出来", vm.ToolSummaryText);
            Check(vm.ToolUsages.Count == 7, "体检列出七个子目录占用", $"{vm.ToolUsages.Count} 项");

            // ---- 联机大厅 ----
            notes.Add("  测试联机大厅...");

            var lobbySec = FindByName<FrameworkElement>(window!, "ToolSecLobby");
            Check(lobbySec is not null, "找到大厅分区");

            var lobby = vm.Lobby;

            // 本机地址未必探得到（比如网卡全是虚拟的），探不到就只做编解码这半边
            lobby.RefreshLocalAddresses();
            notes.Add($"    本机地址 {lobby.LocalAddresses.Count} 个：{string.Join(", ", lobby.LocalAddresses)}");

            if (lobby.LocalAddresses.Count > 0)
            {
                lobby.PortText = "25565";
                lobby.CreateCode();
                Check(lobby.HasCreatedCode, "能生成大厅编号", lobby.CreatedCode);
                Check(lobby.CreatedCode.Contains('-'), "编号带分组连字符");

                // 自己生成的编号喂回【加入】，必须能解回同一个地址和端口 —— 这是整屏的核心承诺
                var ip = lobby.SelectedAddress;
                lobby.JoinCode = lobby.CreatedCode;
                Check(lobby.CanJoin, "生成的编号能被自己解析");
                Check(lobby.JoinPreview.Contains(ip) && lobby.JoinPreview.Contains("25565"),
                    "编号解回原地址和端口", lobby.JoinPreview);
            }
            else
            {
                notes.Add("    跳过编号往返（没探到本机地址）");
            }

            // 乱输入的编号必须被判为不可加入，而不是解析出半个地址
            lobby.JoinCode = "这不是编号";
            Check(!lobby.CanJoin, "乱编号判为不可加入", lobby.JoinPreview);

            // 加入会真写 servers.dat —— 写在当前实例目录里，测完要还原原样。
            // 存原始字节而不是"有没有这个文件"：AddOrUpdate 会把文件整个重写一遍，
            // 哪怕条目数没变，字节也可能不一样（NBT 顺序/压缩）。只有按字节还原，
            // 这个自检才是真的不留痕迹。
            var serversPath = lobby.ServersPath;
            byte[]? originalServers = null;
            try { if (System.IO.File.Exists(serversPath)) originalServers = System.IO.File.ReadAllBytes(serversPath); }
            catch { /* 读不出来就当原来没有，下面按"删掉"处理 */ }

            var before = lobby.Servers.Count;

            lobby.JoinCode = "60N0-0GMR-YXK";
            Check(lobby.CanJoin, "示例编号可解析");

            // 期望值从同一个 Core API 解出来，别在测试里抄一份常量 ——
            // 抄了的话哪天编码方案一变，测试和实现一起错，还测不出来。
            var sample = Core.Online.LobbyCode.Decode("60N0-0GMR-YXK");
            var wantEndpoint = sample is null ? "(解不出来)" : $"{sample.Value.Ip}:{sample.Value.Port}";

            lobby.Join();
            Check(lobby.Servers.Count == before + 1, "加入后服务器列表多一条",
                $"{before} → {lobby.Servers.Count}");
            Check(System.IO.File.Exists(serversPath), "servers.dat 真被写出来了", serversPath);

            // servers.dat 的 ip 字段按游戏的规矩是「地址:端口」一体
            var added = lobby.Servers.FirstOrDefault(s => s.Ip == wantEndpoint);
            Check(added is not null, "写进去的是编号里的地址:端口", added?.Ip ?? wantEndpoint);

            if (added is not null)
            {
                lobby.RemoveServer(added);
                Check(lobby.Servers.Count == before, "能删掉刚加的服务器", $"{lobby.Servers.Count} 条");
                Check(lobby.Servers.All(s => s.Ip != wantEndpoint), "删完列表里不再有它");
            }

            // 还原：原来有就写回原字节，原来没有就删掉
            try
            {
                if (originalServers is null)
                {
                    if (System.IO.File.Exists(serversPath)) System.IO.File.Delete(serversPath);
                    notes.Add("    清理：原本没有 servers.dat，测完删回原样");
                }
                else
                {
                    System.IO.File.WriteAllBytes(serversPath, originalServers);
                    notes.Add($"    清理：servers.dat 已按原字节还原（{originalServers.Length} 字节）");
                }
            }
            catch (Exception ex)
            {
                notes.Add($"    清理失败（无害）：{ex.Message}");
            }

            lobby.RefreshServers();

            // 扫描是 5 秒的网络动作，自检里只要求"跑得完、不抛"。
            // 不能 .GetResult() 等 —— 它是 await 回 UI 线程的，阻塞 UI 线程就是死锁。
            _ = lobby.ScanLanAsync();
            for (var i = 0; i < 150 && lobby.IsScanning; i++)
            {
                DoEvents();
                System.Threading.Thread.Sleep(100);
            }

            Check(!lobby.IsScanning, "局域网扫描能结束（不卡在扫描中）", lobby.ScanNote);

            // ---- 社区资源（联网搜索）----
            // 这一屏要真的打 Modrinth 的 API，所以自检里只断言"能跑完、
            // 有结果、状态行说人话"，不断言具体条数 —— 网络断了不该让自检变红。
            notes.Add("  测试社区资源搜索...");
            var content = vm.Content;
            content.Activate(ContentTab.Mod, autoSearch: false);

            Check(!content.IsBusy, "社区资源初始不忙");
            Check(content.CanSearch, "社区资源可以搜索");
            Check(content.ProjectType == "mod", "Mod 栏的 project_type 是 mod", content.ProjectType);

            var searchTask = content.SearchAsync();
            for (var i = 0; i < 400 && !searchTask.IsCompleted; i++)
            {
                DoEvents();
                System.Threading.Thread.Sleep(100);
            }

            Check(searchTask.IsCompleted, "Modrinth 搜索能返回（40 秒内）", content.StatusMessage);
            if (searchTask.IsFaulted)
                notes.Add($"    搜索抛异常：{searchTask.Exception?.GetBaseException().Message}");

            Check(content.Results.Count > 0, "搜到了结果", $"{content.Results.Count} 条");

            if (content.Results.Count > 0)
            {
                var first = content.Results[0];
                notes.Add($"    第一条：{first.Title} · {first.TypeText} · {first.LoadersText}");

                Check(first.Title.Length > 0, "结果有标题", first.Title);
                Check(first.ProjectId.Length > 0, "结果有 projectId", first.ProjectId);
                Check(first.FavoriteButtonText.Length > 0, "结果有收藏按钮文案", first.FavoriteButtonText);

                // 收藏：点一下要进收藏夹，再点一下要出来。自检自己造的自己删。
                var beforeFav = content.Favorites.Count;
                content.ToggleFavorite(first);
                content.ToggleFavorite(first);
                Check(content.Favorites.Count == beforeFav,
                    "收藏开关能进能出（不留残留）", $"{beforeFav} → {content.Favorites.Count}");
            }

            // 真装一个进实例 —— 这是"搜索→下载→落地"整条链里唯一没法靠单元
            // 断言替代的一步。装完把新增的文件删掉，不碰用户自己的 Mod。
            var modsDir = System.IO.Path.Combine(vm.GameDirNow(), "mods");
            var beforeJars = System.IO.Directory.Exists(modsDir)
                ? System.IO.Directory.GetFiles(modsDir, "*.jar").Select(System.IO.Path.GetFileName).ToHashSet()
                : new HashSet<string?>();

            if (content.Results.Count > 0)
            {
                var pickRow = content.Results[0];
                notes.Add($"    安装试验：{pickRow.Title} → {modsDir}");

                var installTask = content.InstallAsync(pickRow);
                for (var i = 0; i < 1800 && !installTask.IsCompleted; i++)
                {
                    DoEvents();
                    System.Threading.Thread.Sleep(100);
                }

                var afterJars = System.IO.Directory.Exists(modsDir)
                    ? System.IO.Directory.GetFiles(modsDir, "*.jar").Select(System.IO.Path.GetFileName).ToHashSet()
                    : new HashSet<string?>();

                var addedJars = afterJars.Where(f => f is not null && !beforeJars.Contains(f)).ToList();

                Check(installTask.IsCompleted, "安装能在 3 分钟内结束", content.StatusMessage);
                Check(addedJars.Count > 0, "点「安装」能把文件装进实例的 mods/",
                    $"{beforeJars.Count} → {afterJars.Count} 个 jar{(addedJars.Count > 0 ? "：" + string.Join(", ", addedJars) : "")}");

                // 清理：只删这次新增的
                foreach (var f in addedJars)
                {
                    try { System.IO.File.Delete(System.IO.Path.Combine(modsDir, f!)); }
                    catch { }
                }

                var finalJars = System.IO.Directory.Exists(modsDir)
                    ? System.IO.Directory.GetFiles(modsDir, "*.jar").Length
                    : 0;
                Check(finalJars == beforeJars.Count, "自检装的文件已清理（不留残留）", $"{finalJars} 个 jar");
            }

            // 收藏夹里安装走的是另一条路：收藏只记项目本身、不记版本，所以点安装时
            // 必须拿项目 id 回 Modrinth 重新挑一个适配当前实例的版本。
            // 这条"重新解析"的路径跟搜索结果行不同，得单独跑一遍。
            var favRow = content.Results[0];
            var favProjectId = favRow.ProjectId;

            try
            {
                content.ToggleFavorite(favRow);
                content.Activate(ContentTab.Favorite, autoSearch: false);

                Check(content.Favorites.Count > 0, "收藏夹里有刚收藏的项目", $"{content.Favorites.Count} 条");

                if (content.Favorites.Count > 0)
                {
                    var favItem = content.Favorites[0];
                    Check(favItem.ProjectId.Length > 0, "收藏项带 projectId", favItem.ProjectId);

                    var reinstall = content.InstallAsync(favItem);
                    for (var i = 0; i < 1800 && !reinstall.IsCompleted; i++)
                    {
                        DoEvents();
                        System.Threading.Thread.Sleep(100);
                    }

                    var jarsNow = System.IO.Directory.Exists(modsDir)
                        ? System.IO.Directory.GetFiles(modsDir, "*.jar").Select(System.IO.Path.GetFileName).ToList()
                        : [];

                    var added2 = jarsNow.Where(f => f is not null && !beforeJars.Contains(f)).ToList();

                    Check(reinstall.IsCompleted, "收藏夹安装能在 3 分钟内结束", content.StatusMessage);
                    Check(added2.Count > 0, "收藏夹里点安装能重新解析版本并落地",
                        added2.Count > 0 ? string.Join(", ", added2) : content.StatusMessage);

                    foreach (var f in added2)
                    {
                        try { System.IO.File.Delete(System.IO.Path.Combine(modsDir, f!)); }
                        catch { }
                    }
                }
            }
            finally
            {
                // 收藏夹是用户的持久数据 —— 自检自己造的那一条必须自己删掉
                content.Activate(ContentTab.Favorite, autoSearch: false);

                if (content.Favorites.Any(f => f.ProjectId == favProjectId))
                    content.ToggleFavorite(content.Favorites.First(f => f.ProjectId == favProjectId));
            }

            Check(!content.Favorites.Any(f => f.ProjectId == favProjectId), "自检的收藏已清掉（不留残留）");

            var jarsLeft = System.IO.Directory.Exists(modsDir)
                ? System.IO.Directory.GetFiles(modsDir, "*.jar").Length
                : 0;
            Check(jarsLeft == beforeJars.Count, "收藏夹安装的文件已清理（不留残留）", $"{jarsLeft} 个 jar");

            // 数据包栏：参数必须落成 mod + datapack 分类。
            // Modrinth 没有 datapack 这个 project_type，写成 datapack 会被服务端
            // 静默忽略（不报错，返回一堆 Mod）—— 所以这里盯死这两个值。
            content.Activate(ContentTab.Datapack, autoSearch: false);
            Check(content.ProjectType == "mod", "数据包栏发的是 project_type:mod", content.ProjectType);
            Check(content.Category == "datapack", "数据包栏带 categories:datapack", content.Category);
            Check(content.NeedsWorldPick, "数据包栏要选世界");

            // 数据包是装到某个世界下面的 —— 这条路径必须真跑一遍，
            // 因为它跟"装到实例目录"是两码事，写错了会装到实例根上。
            var inst = vm.CurrentInstance();
            var savesDir = System.IO.Path.Combine(inst.GameDir, "saves");
            var testWorld = "自检数据包世界";
            var worldDir = System.IO.Path.Combine(savesDir, testWorld);
            var tmpZip = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "自检数据包.zip");
            var packTarget = System.IO.Path.Combine(worldDir, "datapacks");

            try
            {
                System.IO.Directory.CreateDirectory(worldDir);
                System.IO.File.WriteAllBytes(System.IO.Path.Combine(worldDir, "level.dat"), [0x0A, 0x00, 0x00]);
                System.IO.File.WriteAllBytes(tmpZip, [0x50, 0x4B, 0x05, 0x06, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);

                content.RefreshWorlds();
                Check(content.Worlds.Any(w => w.Name == testWorld),
                    "能列出新建的存档", string.Join(",", content.Worlds.Select(w => w.Name)));

                var packTask = PclLike.Core.Content.ContentInstaller.InstallFileAsync(
                    inst, PclLike.Core.Content.ContentKind.Datapack, tmpZip,
                    preferredFileName: "自检数据包.zip", worldName: testWorld);

                for (var i = 0; i < 60 && !packTask.IsCompleted; i++)
                {
                    DoEvents();
                    Thread.Sleep(50);
                }

                var packResult = packTask.IsCompleted ? packTask.Result : null;
                Check(packResult?.Ok == true, "数据包装进存档能成功", packResult?.Message ?? "超时");

                if (packResult?.Ok == true)
                {
                    Check(System.IO.File.Exists(System.IO.Path.Combine(packTarget, "自检数据包.zip")),
                        "数据包落在 saves/<世界>/datapacks/ 下", packResult.InstalledPath ?? "");

                    // 反向必须失败：世界不存在时不能瞎猜装到哪儿
                    var badTask = PclLike.Core.Content.ContentInstaller.InstallFileAsync(
                        inst, PclLike.Core.Content.ContentKind.Datapack, tmpZip,
                        preferredFileName: "自检数据包.zip", worldName: "根本没有这个世界");

                    for (var i = 0; i < 60 && !badTask.IsCompleted; i++)
                    {
                        DoEvents();
                        Thread.Sleep(50);
                    }

                    var badResult = badTask.IsCompleted ? badTask.Result : null;
                    Check(badResult?.Ok == false, "世界不存在时数据包安装要报错而不是瞎装",
                        badResult?.Message ?? "超时");
                }
            }
            finally
            {
                try { if (System.IO.Directory.Exists(worldDir)) System.IO.Directory.Delete(worldDir, true); } catch { }
                try { if (System.IO.File.Exists(tmpZip)) System.IO.File.Delete(tmpZip); } catch { }
            }

            Check(!System.IO.Directory.Exists(worldDir), "自检的存档已清理（不留残留）");
            Check(!System.IO.Directory.Exists(packTarget), "自检的 datapacks 目录已随存档一起清掉");

            // 切到世界栏：这栏不联网，但要把存档列出来
            content.Activate(ContentTab.World, autoSearch: false);
            Check(!content.IsOnlineTab, "世界栏不是联网栏目");
            Check(!content.ShowSearchPanel, "世界栏不显示联网搜索条");
            Check(content.ShowWorldPanel, "世界栏显示存档工具条");
            Check(content.ContentDirForTab().EndsWith("saves", StringComparison.OrdinalIgnoreCase),
                "世界栏的\"打开目录\"指向 saves", content.ContentDirForTab());

            // 收藏夹栏也要能进
            content.Activate(ContentTab.Favorite, autoSearch: false);
            Check(content.ShowFavoritePanel, "收藏夹栏显示收藏列表");

            // ---- 配置往返 ----
            notes.Add("  测试配置持久化...");
            var cfgPath = Core.Storage.ConfigStore.PathFor(App.GameRoot);
            vm.Username = "SelfTest";
            vm.MaxMemoryMb = 6144;
            vm.Persist();

            var reloaded = Core.Storage.ConfigStore.Load(App.GameRoot);
            Check(reloaded.Username == "SelfTest", "配置写盘并能读回（用户名）", reloaded.Username);
            Check(reloaded.MaxMemoryMb == 6144, "配置写盘并能读回（内存）", $"{reloaded.MaxMemoryMb} MB");

            // 恢复原值，别污染用户配置
            vm.Username = "Player";
            vm.MaxMemoryMb = 4096;
            vm.Persist();
        }
        catch (Exception ex)
        {
            failures.Add($"  [FAIL] 自检抛异常：{ex}");
        }
        finally
        {
            try { window?.Close(); } catch { }
        }

        return Report(failures, notes);
    }

    private static void DoEvents()
    {
        var frame = new System.Windows.Threading.DispatcherFrame();
        System.Windows.Threading.Dispatcher.CurrentDispatcher.BeginInvoke(
            System.Windows.Threading.DispatcherPriority.Background,
            new Action(() => frame.Continue = false));
        System.Windows.Threading.Dispatcher.PushFrame(frame);
    }

    /// <summary>
    /// 按 Name 找控件。先走可视树；找不到再退回名字范围。
    /// 折叠的页面（Visibility=Collapsed）不会被测量，模板也就不会展开，
    /// 它的子控件压根不存在于可视树里，只能用名字范围取。
    /// </summary>
    private static T? FindByName<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        var visual = FindVisual<T>(root, name);
        if (visual is not null) return visual;

        if (root is FrameworkElement fe && fe.FindName(name) is T named) return named;

        return FindLogical<T>(root, name);
    }

    private static T? FindVisual<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T typed && typed.Name == name) return typed;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            var found = FindVisual<T>(VisualTreeHelper.GetChild(root, i), name);
            if (found is not null) return found;
        }

        return null;
    }

    private static T? FindLogical<T>(DependencyObject root, string name) where T : FrameworkElement
    {
        if (root is T typed && typed.Name == name) return typed;

        foreach (var child in LogicalTreeHelper.GetChildren(root))
        {
            if (child is not DependencyObject d) continue;
            var found = FindLogical<T>(d, name);
            if (found is not null) return found;
        }

        return null;
    }

    private static int Report(List<string> failures, List<string> notes)
    {
        Console.WriteLine();
        Console.WriteLine("========== GUI SELFTEST ==========");

        foreach (var n in notes) Console.WriteLine(n);
        if (failures.Count > 0)
        {
            Console.WriteLine();
            foreach (var f in failures) Console.WriteLine(f);
        }

        Console.WriteLine();
        Console.WriteLine(failures.Count == 0
            ? $"ALL PASSED ({notes.Count} checks)"
            : $"{failures.Count} FAILED");
        Console.WriteLine("==================================");

        return failures.Count == 0 ? 0 : 1;
    }
}