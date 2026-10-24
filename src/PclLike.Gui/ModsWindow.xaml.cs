using System.Windows;
using System.Windows.Controls;
using Microsoft.Win32;
using PclLike.Core.Storage;

namespace PclLike.Gui;

/// <summary>
/// Mod 管理窗口。
///
/// 代码后置只做「事件 → ViewModel 方法」的转发和二次确认，
/// 一行业务逻辑都不放这里 —— 扫描、下载、删除全在 <see cref="ModsViewModel"/>。
/// </summary>
public partial class ModsWindow : Window
{
    private readonly ModsViewModel _vm;

    public ModsWindow(GameInstance instance)
    {
        InitializeComponent();

        _vm = new ModsViewModel(instance);
        DataContext = _vm;

        InstanceLabel.Text = $"Mod 管理 — {_vm.InstanceName}";
        ContextLabel.Text = _vm.ContextLabel;

        // 切页时联动：本地安装行只在「已安装」页显示，切到搜索页把焦点给搜索框
        MainTabs.SelectionChanged += MainTabs_SelectionChanged;

        Loaded += async (_, _) => await _vm.RefreshAsync();
    }

    // ────────────────────────────────────────────────
    // 顶栏
    // ────────────────────────────────────────────────

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        try { _vm.OpenFolder(); }
        catch { /* 打不开资源管理器不影响使用 */ }
    }

    private async void Refresh_Click(object sender, RoutedEventArgs e)
        => await _vm.RefreshAsync();

    // ────────────────────────────────────────────────
    // 筛选与多选
    // ────────────────────────────────────────────────

    private void Filter_Changed(object sender, TextChangedEventArgs e)
        => _vm.Filter = FilterBox.Text;

    private void SelectAll_Click(object sender, RoutedEventArgs e)
    {
        ModList.SelectAll();
        UpdateSelectionUi();
    }

    private void ModList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => UpdateSelectionUi();

    /// <summary>选中项变化时更新按钮文案，让用户知道"删除选中"会删几个。</summary>
    private void UpdateSelectionUi()
    {
        var n = ModList.SelectedItems.Count;
        _vm.SelectedCount = n;

        DeleteButton.Content = n > 0 ? $"删除选中 ({n})" : "删除选中";
        DeleteButton.IsEnabled = n > 0;

        // 「全部禁用 / 全部启用」按当前列表里多数项的状态来定 —— 少点一次按钮
        var rows = _vm.FilteredMods;
        var enabledCount = rows.Count(r => r.Enabled);
        ToggleAllButton.Content = enabledCount > 0 ? "全部禁用" : "全部启用";
        ToggleAllButton.IsEnabled = rows.Count > 0;
    }

    // ────────────────────────────────────────────────
    // 启停与删除
    // ────────────────────────────────────────────────

    private async void ModToggle_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not CheckBox { Tag: ModRow row }) return;

        await _vm.ToggleAsync(row);
        UpdateSelectionUi();
    }

    private async void ToggleAll_Click(object sender, RoutedEventArgs e)
    {
        var rows = _vm.FilteredMods.ToList();
        if (rows.Count == 0) return;

        // 有启用的就把全部禁用，全禁用了就全部启用
        var enable = rows.All(r => !r.Enabled);
        var verb = enable ? "启用" : "禁用";

        var answer = MessageBox.Show(
            $"将{verb}当前列表中的 {rows.Count} 个 Mod，继续？",
            $"全部{verb}", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        await _vm.ToggleAllAsync(rows, enable);
        UpdateSelectionUi();
    }

    private async void Delete_Click(object sender, RoutedEventArgs e)
    {
        var rows = ModList.SelectedItems.OfType<ModRow>().ToList();
        if (rows.Count == 0) return;

        var names = string.Join("\n", rows.Take(8).Select(r => "· " + r.Title));
        if (rows.Count > 8) names += $"\n… 还有 {rows.Count - 8} 个";

        var answer = MessageBox.Show(
            $"将从磁盘删除以下 {rows.Count} 个 Mod，无法撤销：\n\n{names}",
            "确认删除", MessageBoxButton.OKCancel, MessageBoxImage.Warning);

        if (answer != MessageBoxResult.OK) return;

        await _vm.DeleteAsync(rows);
        UpdateSelectionUi();
    }

    // ────────────────────────────────────────────────
    // 依赖
    // ────────────────────────────────────────────────

    private async void Check_Click(object sender, RoutedEventArgs e)
        => await _vm.CheckAsync();

    private async void AutoFix_Click(object sender, RoutedEventArgs e)
    {
        var answer = MessageBox.Show(
            "将联网从 Modrinth 查找并下载缺失的依赖，继续？\n\n" +
            "只下载标记为「必需」的前置 Mod，可选依赖不会装。",
            "自动补齐依赖", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (answer != MessageBoxResult.OK) return;

        await _vm.AutoFixAsync();
        UpdateSelectionUi();
    }

    // ────────────────────────────────────────────────
    // 安装本地文件
    // ────────────────────────────────────────────────

    private async void Browse_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择要安装的 Mod",
            Filter = "Minecraft Mod (*.jar)|*.jar|所有文件 (*.*)|*.*",
            Multiselect = false,
        };

        if (dlg.ShowDialog(this) != true) return;

        InstallPathBox.Text = dlg.FileName;
        await _vm.InstallLocalAsync(dlg.FileName);
        UpdateSelectionUi();
    }

    private async void Install_Click(object sender, RoutedEventArgs e)
    {
        var path = InstallPathBox.Text.Trim();
        if (path.Length == 0)
        {
            MessageBox.Show("先选择一个 .jar 文件，或粘贴文件 / 文件夹路径。",
                "没有选择文件", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 用户从资源管理器复制的路径常带引号，去掉
        path = path.Trim('"', '\'', ' ');

        await _vm.InstallLocalAsync(path);
        UpdateSelectionUi();
    }

    // ────────────────────────────────────────────────
    // 拖放：直接把 jar 拖进窗口任何位置
    // ────────────────────────────────────────────────

    protected override void OnDragOver(DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop)
            ? DragDropEffects.Copy
            : DragDropEffects.None;

        e.Handled = true;
        base.OnDragOver(e);
    }

    protected override async void OnDrop(DragEventArgs e)
    {
        base.OnDrop(e);

        if (e.Data.GetData(DataFormats.FileDrop) is not string[] paths || paths.Length == 0) return;

        if (paths.Length == 1)
        {
            InstallPathBox.Text = paths[0];
            await _vm.InstallLocalAsync(paths[0]);
        }
        else
        {
            // 拖了一堆文件：逐个装，最后统一报结果
            foreach (var p in paths)
                await _vm.InstallLocalAsync(p);
        }

        UpdateSelectionUi();
    }

    // ────────────────────────────────────────────────
    // 搜索下载
    // ────────────────────────────────────────────────

    /// <summary>回车即搜索 —— 搜索框里按回车是最自然的操作。</summary>
    private async void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != System.Windows.Input.Key.Enter) return;

        e.Handled = true;
        await _vm.SearchAsync();
    }

    private async void Search_Click(object sender, RoutedEventArgs e)
        => await _vm.SearchAsync();

    /// <summary>点「安装」按钮。</summary>
    private async void SearchInstall_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: ModSearchRow row }) return;

        await _vm.InstallFromSearchAsync(row);

        // 装完刷新「已安装」页，让用户切过去就能看到
        UpdateSelectionUi();
    }

    /// <summary>双击整行也是安装 —— 比瞄准那个小按钮快。</summary>
    private async void SearchList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (SearchList.SelectedItem is not ModSearchRow row) return;

        await _vm.InstallFromSearchAsync(row);
        UpdateSelectionUi();
    }

    /// <summary>切到「搜索下载」页时把焦点给搜索框，省一次点击。</summary>
    private void MainTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!ReferenceEquals(e.OriginalSource, MainTabs)) return;

        // 本地安装那一行在搜索页没意义，切走免得占地方
        LocalInstallPanel.Visibility = MainTabs.SelectedIndex == 0
            ? Visibility.Visible
            : Visibility.Collapsed;

        if (MainTabs.SelectedIndex == 1)
            SearchBox.Focus();
    }
}