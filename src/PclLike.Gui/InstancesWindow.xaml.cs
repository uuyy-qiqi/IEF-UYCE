using System;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;

namespace PclLike.Gui;

/// <summary>
/// 实例管理窗口。
///
/// 这一屏的立场：用户在这里管的是自己几十小时的存档，所以「删除」必须问清楚，
/// 而「复制 / 改名 / 打开目录」这类安全动作就别啰嗦。
/// </summary>
public partial class InstancesWindow : Window
{
    private readonly MainViewModel _vm;
    private readonly InstancesListViewModel _list = new();

    /// <summary>右侧详情正在展示的那个实例。列表里没选中时是 null。</summary>
    private PclLike.Core.Storage.GameInstance? _picked;

    public InstancesWindow(MainViewModel vm)
    {
        _vm = vm;
        InitializeComponent();

        DataContext = _list;

        // 版本下拉直接用主窗口已经加载好的列表，不重新联网
        foreach (var v in vm.Versions)
            NewVersionBox.Items.Add(v.Id);

        if (vm.SelectedVersion is not null)
            NewVersionBox.SelectedItem = vm.SelectedVersion.Id;
        else if (NewVersionBox.Items.Count > 0)
            NewVersionBox.SelectedIndex = 0;

        _vm.RefreshInstances();
        Reload();
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);

        // 无边框窗口的圆角得自己跟 DWM 要（Win11 才有；Win10 会失败，忽略即可）
        WindowBackdrop.TryRoundCorners(this);
    }

    // ─────────────────── 顶栏 ───────────────────
    //
    // 顶栏是自己画的（CaptionHeight=0，整窗都是客户区），所以拖动和三个按钮
    // 得自己接。不接的话窗口就拖不动。

    private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left) return;

        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        // 鼠标松开得比这次调用还快时 DragMove 会抛，这种情况没什么可做的
        try { DragMove(); }
        catch (InvalidOperationException) { }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    /// <summary>
    /// 「添加已有文件夹」：把已经下好的 .minecraft 文件夹（或整合包文件夹）加成实例。
    ///
    /// 判定走的是拖放那套识别逻辑（认得 .minecraft、懒人包、整合包文件夹），
    /// 这里不另写一份 —— 两条路识别结果必须一样，否则用户会遇到
    /// "拖进去能认、从按钮进去不认"这种事。
    /// </summary>
    private async void AddFolder_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog
        {
            Title = "选一个已经下好的 .minecraft 文件夹（或整合包文件夹）",
            Multiselect = false,
        };

        if (dlg.ShowDialog() != true) return;
        if (string.IsNullOrWhiteSpace(dlg.FolderName)) return;

        var picked = dlg.FolderName.TrimEnd('\\', '/');

        _vm.Tasks.Begin("导入文件夹：" + System.IO.Path.GetFileName(picked));
        new TasksWindow(_vm) { Owner = this }.Show();

        var summary = await _vm.DropFilesAsync(new[] { picked });
        CurrentLabel.Text = summary;
        _list.Rebuild(_vm.Instances);
    }

    /// <summary>「导入整合包」：选一个压缩包，走和拖进来一样的那套安装流程。</summary>
    private async void ImportPack_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new Microsoft.Win32.OpenFileDialog
        {
            Title = "选一个整合包压缩包",
            Filter = "整合包压缩包|*.zip;*.mrpack|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog() != true) return;

        _vm.Tasks.Begin("导入：" + System.IO.Path.GetFileNameWithoutExtension(dlg.FileName));
        new TasksWindow(_vm) { Owner = this }.Show();

        var summary = await _vm.DropFilesAsync(new[] { dlg.FileName });
        CurrentLabel.Text = summary;
        _list.Rebuild(_vm.Instances);
    }

    // ─────────────────── 列表 ───────────────────

    /// <summary>重建左栏。传 selectId 就选中那一条（新建/复制完走这条）。</summary>
    private void Reload(string? selectId = null)
    {
        _list.Rebuild(_vm.Instances);

        if (selectId is not null)
            _list.SelectById(selectId);

        _picked = _list.SelectedRow?.Instance;

        UpdateCurrentLabel();
        UpdateDetail();
    }

    private void UpdateCurrentLabel()
        => CurrentLabel.Text = _vm.SelectedInstance is null
            ? "当前实例：未选择"
            : $"当前实例：{_vm.SelectedInstance.Name}";

    private void UpdateDetail()
    {
        var row = _list.SelectedRow;
        var inst = row?.Instance;

        // 一个实例都没有（或搜索把列表过滤空了）就把右侧整块禁掉 ——
        // 否则用户会对着上一轮的残留字段点按钮，改的是已经不显示的那个实例。
        DetailPanel.IsEnabled = inst is not null;

        if (row is null || inst is null)
        {
            DetailName.Text = "没有选中实例";
            DetailMc.Text = "—";
            DetailLoader.Text = "—";
            DetailIsoChip.Text = "—";

            VerLabel.Text = "—";
            IsoLabel.Text = "—";
            LoaderLabel.Text = "—";
            CreatedLabel.Text = "—";
            PlayedLabel.Text = "—";
            SizeLabel.Text = "—";

            NameBox.Text = "";
            PathBox.Text = "";
            NoteBox.Text = "";
            return;
        }

        // 图标跟列表里那一行同色，右侧这块和左栏是一个实例的两面
        DetailGlyph.TopBrush = row.Top;
        DetailGlyph.LeftBrush = row.Left;
        DetailGlyph.RightBrush = row.Right;

        DetailName.Text = inst.Name;
        DetailMc.Text = row.McVersion;
        DetailLoader.Text = row.LoaderText;
        DetailIsoChip.Text = inst.Isolated ? "独立目录" : "共享目录";

        NameBox.Text = inst.Name;
        VerLabel.Text = inst.VersionId;
        IsoLabel.Text = inst.Isolated ? "独立（只属于这个实例）" : "共享（与其它版本共用启动器根目录）";
        LoaderLabel.Text = row.LoaderText;

        CreatedLabel.Text = inst.CreatedAt.LocalDateTime.ToString("yyyy/MM/dd HH:mm");
        PlayedLabel.Text = inst.LastPlayedAt is { } played
            ? played.LocalDateTime.ToString("yyyy/MM/dd HH:mm")
            : "还没有";

        // 共享实例的 gameDir 就是启动器根目录，把整个根目录算成"这个实例占用"
        // 是误导，所以宁可写清楚不统计。
        SizeLabel.Text = inst.Isolated
            ? $"{PclLike.Core.Storage.InstanceStore.SizeMb(inst):0.#} MB"
            : "共享目录，不单独统计";

        PathBox.Text = inst.GameDir;
        NoteBox.Text = inst.Note ?? "";
    }

    /// <summary>
    /// 两个 ListBox 一组一个，各自独立选中就会出现"两组同时高亮"，
    /// 所以选中状态统一交给 _list 裁决：谁被点了，另一组的选中就被清掉。
    /// </summary>
    private void Group_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (sender is not ListBox box) return;
        if (box.DataContext is not InstanceGroup group) return;
        if (box.SelectedItem is not InstanceRow row) return;

        // 已经是当前选中行的话不必再走一遍回调（Select 会绕回来）
        if (!ReferenceEquals(_list.SelectedRow, row))
            group.Selected = row;

        _picked = row.Instance;
        UpdateCurrentLabel();
        UpdateDetail();
    }

    /// <summary>双击 = 设为当前实例。</summary>
    private void Group_MouseDoubleClick(object sender, MouseButtonEventArgs e)
        => Use_Click(sender, e);

    // ─────────────────── 动作 ───────────────────

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = TextInputDialog.Ask(this, "输入实例名称", "新实例的名字（中文可以）", "") ?? "";
            if (name.Length == 0) return;
        if (name.Length == 0)
        {
            Warn("先给新实例起个名字。");
            return;
        }

        if (NewVersionBox.SelectedItem is not string versionId || versionId.Length == 0)
        {
            Warn("先选一个游戏版本。");
            return;
        }

        var inst = _vm.CreateInstance(name, versionId, NewIsolated.IsChecked == true);
        if (inst is null)
        {
            Warn("没创建成功，具体原因记在下面的日志里。");
            return;
        }

        
        Reload(inst.Id);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (_picked is null) return;

        var name = NameBox.Text.Trim();
        if (name.Length == 0)
        {
            Warn("名字不能是空的。");
            return;
        }

        _picked.Name = name;
        _picked.Note = NoteBox.Text.Trim();
        _vm.Store.Save(_picked);
        _vm.RefreshInstances();

        Reload(_picked.Id);
    }

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        if (_picked is null) return;

        _vm.SelectedInstance = _picked;
        UpdateCurrentLabel();
        Reload(_picked.Id);
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        if (_picked is null) return;

        // 新名字：下面输入框里写了就用那个，没写就让 Core 自己加后缀
        var wanted = TextInputDialog.Ask(this, "复制实例", "复制出来的新实例叫什么名字？（直接确定就用原名 + 副本）", "") ?? "";
        var copy = _vm.CopyInstance(_picked, wanted.Length > 0 ? wanted : null);
        if (copy is null)
        {
            Warn("复制失败，具体原因记在下面的日志里。");
            return;
        }

        
        Reload(copy.Id);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        if (_picked is null) return;

        var inst = _picked;
        var size = inst.Isolated
            ? $"{PclLike.Core.Storage.InstanceStore.SizeMb(inst):0.#} MB"
            : "共享目录";

        // 三选一而不是"确定/取消"：删文件这件事不可撤销，得给用户留一条
        // "只是从列表里拿掉"的路。
        var answer = MessageBox.Show(
            this,
            $"要删除实例「{inst.Name}」吗？\n\n" +
            $"版本：{inst.VersionId}\n" +
            $"游戏目录：{inst.GameDir}\n" +
            $"占用：{size}\n\n" +
            $"【是】连游戏目录一起删掉 —— 存档、Mod、配置都跟着没\n" +
            $"【否】只从列表里移除，文件原样留着\n" +
            $"【取消】什么也不做",
            "删除实例", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (answer == MessageBoxResult.Cancel) return;

        if (_vm.DeleteInstance(inst, answer == MessageBoxResult.Yes))
        {
            _picked = null;
            Reload();
        }
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        if (_picked is null) return;

        MainViewModel.OpenInExplorer(_picked.GameDir);
    }

    private void Warn(string message)
        => MessageBox.Show(this, message, "实例管理", MessageBoxButton.OK, MessageBoxImage.Information);
}