using System;
using System.Diagnostics;
using System.Linq;
using System.Windows;
using PclLike.Core.Storage;

namespace PclLike.Gui;

/// <summary>
/// 实例管理窗口。
///
/// 实例决定启动时 --gameDir 指到哪儿，所以「设为当前」比「保存」重要得多 ——
/// 建完不设为当前，启动还是走原来那个目录，用户会以为没生效。
/// </summary>
public partial class InstancesWindow : Window
{
    private readonly MainViewModel _vm;

    public InstancesWindow(MainViewModel vm)
    {
        InitializeComponent();

        _vm = vm;

        foreach (var v in vm.Versions) NewVersionBox.Items.Add(v.Id);
        if (vm.SelectedVersion is { } sv) NewVersionBox.SelectedItem = sv.Id;
        else if (NewVersionBox.Items.Count > 0) NewVersionBox.SelectedIndex = 0;

        _vm.RefreshInstances();
        Reload();
    }

    private GameInstance? Picked => List.SelectedItem as GameInstance;

    private void Reload()
    {
        var keep = Picked?.Id ?? _vm.SelectedInstance?.Id;

        // ItemsSource 直接挂 VM 的集合：VM 里的增删会直接反映到列表上
        List.ItemsSource = _vm.Instances;

        if (keep is not null) List.SelectedItem = _vm.Instances.FirstOrDefault(i => i.Id == keep);
        if (List.SelectedItem is null && _vm.Instances.Count > 0) List.SelectedIndex = 0;

        UpdateCurrentLabel();
        UpdateDetail();
    }

    private void UpdateCurrentLabel()
        => CurrentLabel.Text = $"当前实例：{_vm.SelectedInstance?.Name ?? "未选择"}";

    private void UpdateDetail()
    {
        var inst = Picked;

        if (inst is null)
        {
            DetailPanel.IsEnabled = false;
            NameBox.Text = NoteBox.Text = PathBox.Text = "";
            VerLabel.Text = IsoLabel.Text = LoaderLabel.Text = "";
            CreatedLabel.Text = PlayedLabel.Text = SizeLabel.Text = "";
            return;
        }

        DetailPanel.IsEnabled = true;

        NameBox.Text = inst.Name;
        VerLabel.Text = $"游戏版本：{inst.VersionId}";
        IsoLabel.Text = $"游戏目录：{inst.IsolationLabel}（{inst.IsolationLabel switch { "独立" => "只属于这个实例", _ => "与其它版本共用" } }）";
        LoaderLabel.Text = $"加载器：{inst.Loader}";
        CreatedLabel.Text = $"创建时间：{inst.CreatedAt:yyyy/MM/dd HH:mm}";
        PlayedLabel.Text = inst.LastPlayedAt is { } t ? $"最近游玩：{t:yyyy/MM/dd HH:mm}" : "最近游玩：还没有";
        PathBox.Text = inst.GameDir;
        NoteBox.Text = inst.Note ?? "";

        // 共享实例的 gameDir 就是启动器根，里面装着所有版本的公共文件（assets/libraries
        // 动辄上万个小文件）。统计它等于把整个启动器扫一遍，界面会卡死 —— 所以不统计。
        SizeLabel.Text = inst.Isolated
            ? $"占用空间：{InstanceStore.SizeMb(inst):0.#} MB"
            : "占用空间：共享目录，不单独统计";
    }

    private void List_SelectionChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        => UpdateDetail();

    private void List_MouseDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (Picked is not null) Use_Click(sender, e);
    }

    // ─────────── 新建 / 保存 ───────────

    private void Create_Click(object sender, RoutedEventArgs e)
    {
        var name = NewNameBox.Text.Trim();
        if (name.Length == 0) { Warn("先给实例起个名字。"); return; }

        if (NewVersionBox.SelectedItem is not string ver || ver.Length == 0)
        {
            Warn("先在下拉里选一个游戏版本 —— 实例必须挂在具体版本上。");
            return;
        }

        var inst = _vm.CreateInstance(name, ver, NewIsolated.IsChecked == true);
        if (inst is null) return;

        NewNameBox.Clear();
        Reload();
        List.SelectedItem = _vm.Instances.FirstOrDefault(i => i.Id == inst.Id);
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var inst = Picked;
        if (inst is null) return;

        var name = NameBox.Text.Trim();
        if (name.Length == 0) { Warn("名字不能为空。"); return; }

        inst.Name = name;
        inst.Note = NoteBox.Text.Trim() is { Length: > 0 } n ? n : null;

        _vm.Store.Save(inst);
        _vm.RefreshInstances();

        Reload();
        List.SelectedItem = _vm.Instances.FirstOrDefault(i => i.Id == inst.Id);
    }

    // ─────────── 操作 ───────────

    private void Use_Click(object sender, RoutedEventArgs e)
    {
        var inst = Picked;
        if (inst is null) return;

        _vm.SelectedInstance = inst;
        UpdateCurrentLabel();
    }

    private void Copy_Click(object sender, RoutedEventArgs e)
    {
        var inst = Picked;
        if (inst is null) return;

        if (MessageBox.Show(this,
                $"复制实例「{inst.Name}」？\n\n连同 mods / config / saves 一起复制一份。\n" +
                (inst.Isolated ? "" : "源实例是共享目录，只复制它私有的那几个子目录。\n") +
                "\n文件多的时候会卡一下。",
                "复制实例", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK)
            return;

        var typed = NewNameBox.Text.Trim();
        var copy = _vm.CopyInstance(inst, typed.Length > 0 ? typed : null);
        if (copy is null)
        {
            Warn("复制失败，详情见主窗口日志。");
            return;
        }

        NewNameBox.Clear();
        Reload();
        List.SelectedItem = _vm.Instances.FirstOrDefault(i => i.Id == copy.Id);
    }

    private void Delete_Click(object sender, RoutedEventArgs e)
    {
        var inst = Picked;
        if (inst is null) return;

        var r = MessageBox.Show(this,
            $"删除实例「{inst.Name}」？\n\n" +
            "是 = 连游戏文件一起删除（mods、config、saves 也会没）\n" +
            "否 = 只从列表移除，文件留在磁盘上，以后还能手动找回来\n" +
            "取消 = 什么都不做",
            "删除实例", MessageBoxButton.YesNoCancel, MessageBoxImage.Warning);

        if (r == MessageBoxResult.Cancel) return;

        if (_vm.DeleteInstance(inst, deleteFiles: r == MessageBoxResult.Yes)) Reload();
    }

    private void Open_Click(object sender, RoutedEventArgs e)
    {
        var inst = Picked;
        if (inst is null) return;

        try
        {
            System.IO.Directory.CreateDirectory(inst.GameDir);
            Process.Start(new ProcessStartInfo("explorer.exe", $"\"{inst.GameDir}\"") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Warn($"打不开目录：{ex.Message}");
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    private void Warn(string message)
        => MessageBox.Show(this, message, "实例管理", MessageBoxButton.OK, MessageBoxImage.Information);
}