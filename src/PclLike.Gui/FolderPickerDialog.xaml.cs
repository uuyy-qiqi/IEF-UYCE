using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace PclLike.Gui;

/// <summary>
/// 「选择文件夹」弹窗 —— 照 PCL 那个做的。
///
/// 为什么不用系统自带的 <c>OpenFolderDialog</c>：PCL 的选目录框能一眼看见
/// 「名称 / 修改日 / 类型」，选之前就知道这个文件夹是什么、多新，而不是在一堆
/// 只有名字的目录里盲点。装整合包、加已有文件夹这些地方都要用它。
/// </summary>
public partial class FolderPickerDialog : Window
{
    public sealed record Entry(string Name, string FullPath, string ModifiedText);

    private string _current;
    private List<Entry> _all = [];

    public string? ChosenPath { get; private set; }

    public FolderPickerDialog(string? startPath = null)
    {
        InitializeComponent();

        var start = startPath;
        if (string.IsNullOrWhiteSpace(start) || !Directory.Exists(start))
            start = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
        if (!Directory.Exists(start))
            start = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        _current = Path.GetFullPath(start);

        // 不在构造函数里读盘：桌面可能被 OneDrive 接管，枚举一个云端目录会卡住
        // 好几分钟（--xamlcheck 就是这么卡死的）。等到窗口真显示出来再读，
        // 窗口已经画好了，用户看到的是「正在读……」，不是白屏假死。
        Loaded += (_, _) => { if (_all.Count == 0) Load(); };
    }

    /// <summary>用一行代码开一个选择框；用户取消返回 null。</summary>
    /// <summary>探针用：当前停在哪一层。</summary>
    public string CurrentDirectory => _current;

    public static string? Pick(Window? owner, string? startPath = null)
    {
        var dlg = new FolderPickerDialog(startPath);
        if (owner is not null && owner.IsVisible) dlg.Owner = owner;
        return dlg.ShowDialog() == true ? dlg.ChosenPath : null;
    }

    // ══════════════════ 列表 ══════════════════

    private void Load()
    {
        BreadcrumbText.Text = _current;
        ChosenPathText.Text = _current;

        _all = [];
        try
        {
            // 上限 2000 条：正常文件夹用不到这么多，而个别目录（打包解压出来的
            // 海量小目录）能耗光界面线程。宁可少列，也不能卡住。
            foreach (var dir in Directory.EnumerateDirectories(_current).Take(2000))
            {
                var name = Path.GetFileName(dir);
                if (string.IsNullOrWhiteSpace(name)) continue;

                // 系统隐藏目录（$RECYCLE.BIN、System Volume Information 之类）不列
                try
                {
                    var attr = File.GetAttributes(dir);
                    if (attr.HasFlag(FileAttributes.System)) continue;
                }
                catch { continue; }

                string modified = "";
                try { modified = Directory.GetLastWriteTime(dir).ToString("yyyy/MM/dd HH:mm"); }
                catch { }

                _all.Add(new Entry(name, dir, modified));
            }
        }
        catch (Exception ex)
        {
            ChosenPathText.Text = $"{_current}（读不出来：{ex.Message}）";
        }

        _all.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        ApplyFilter();
        UpButton.IsEnabled = Path.GetDirectoryName(_current) is not null;
    }

    private void ApplyFilter()
    {
        var key = SearchBox.Text.Trim();
        var shown = string.IsNullOrEmpty(key)
            ? _all
            : _all.Where(e => e.Name.Contains(key, StringComparison.OrdinalIgnoreCase)).ToList();

        DirList.ItemsSource = shown;
        EmptyHint.Visibility = shown.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SearchHint.Visibility = string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
    }

    private void Enter(Entry e)
    {
        if (!Directory.Exists(e.FullPath)) return;
        _current = e.FullPath;
        SearchBox.Text = "";
        Load();
    }

    // ══════════════════ 事件 ══════════════════

    private void DirList_DoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        if (DirList.SelectedItem is Entry entry) Enter(entry);
    }

    private void DirList_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        // 选中一个子文件夹时，底部的「文件夹：」跟着它 —— 这样直接按「选择文件夹」
        // 就是进到那个子文件夹里去，跟资源管理器一致。
        if (DirList.SelectedItem is Entry entry) ChosenPathText.Text = entry.FullPath;
        else ChosenPathText.Text = _current;
    }

    private void Up_Click(object sender, RoutedEventArgs e)
    {
        var parent = Path.GetDirectoryName(_current);
        if (parent is null) return;
        _current = parent;
        SearchBox.Text = "";
        Load();
    }

    private void Search_TextChanged(object sender, TextChangedEventArgs e) => ApplyFilter();

    private void NewFolder_Click(object sender, RoutedEventArgs e)
    {
        // 新建文件夹也用自家的输入框，样式统一、并且回车就能确认
        var name = TextInputDialog.Ask(this, "新建文件夹", "输入文件夹名称", "新建文件夹");
        if (string.IsNullOrWhiteSpace(name)) return;

        var target = Path.Combine(_current, name.Trim());
        try
        {
            Directory.CreateDirectory(target);
            Load();
            var created = _all.FirstOrDefault(x => string.Equals(x.Name, name.Trim(), StringComparison.OrdinalIgnoreCase));
            if (created is not null) { DirList.SelectedItem = created; ChosenPathText.Text = created.FullPath; }
        }
        catch (Exception ex)
        {
            ChosenPathText.Text = $"新建失败：{ex.Message}";
        }
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        ChosenPath = DirList.SelectedItem is Entry entry ? entry.FullPath : _current;
        if (!Directory.Exists(ChosenPath))
        {
            ChosenPathText.Text = "这个路径不存在了，重新选一个";
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}