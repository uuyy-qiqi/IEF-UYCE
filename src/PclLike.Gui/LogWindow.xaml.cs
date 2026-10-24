using System.Collections.Specialized;
using System.Text;
using System.Windows;
using System.Windows.Input;

namespace PclLike.Gui;

/// <summary>
/// 启动器日志窗口。
///
/// 日志原来挂在主窗口底部当抽屉。问题是那个抽屉一直占着窗口最下面那一条，
/// 正好压在左栏「启动游戏」下面，而且日志本身大部分时间是空的 —— 用一个
/// 常驻的横条去装一份偶尔才看一眼的信息，不划算。现在改成按需打开。
///
/// 这里直接用 MainViewModel 的 LogLines（同一个 ObservableCollection），
/// 所以窗口关着的时候日志照样在记，打开就能看到全部历史 —— 不做任何缓冲。
/// </summary>
public partial class LogWindow : Window
{
    private readonly MainViewModel _vm;

    public LogWindow(MainViewModel vm)
    {
        _vm = vm;

        InitializeComponent();
        DataContext = vm;

        ((INotifyCollectionChanged)vm.LogLines).CollectionChanged += OnLogChanged;

        Loaded += (_, _) =>
        {
            UpdateCount();
            ScrollToEnd();
            LogList.Focus();
        };
    }

    private void OnLogChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        UpdateCount();

        if (FollowBox.IsChecked == true)
            ScrollToEnd();
    }

    private void UpdateCount()
        => CountText.Text = _vm.LogLines.Count == 0 ? "（还没有日志）" : $"{_vm.LogLines.Count} 行";

    /// <summary>
    /// 滚到最新一行。
    ///
    /// 用 ScrollIntoView 而不是滚到 ScrollViewer 底部：日志行可能折行，
    /// 最后一行的底边只有交给 ListBox 自己算才准（虚拟化开着的时候更是这样）。
    /// </summary>
    private void ScrollToEnd()
    {
        if (_vm.LogLines.Count == 0) return;

        var last = _vm.LogLines[^1];
        try
        {
            LogList.ScrollIntoView(last);
        }
        catch (InvalidOperationException)
        {
            // 虚拟化 + 容器还没生成时偶尔会抛；下一行日志进来时会再滚一次，
            // 不值得为它中断 UI。
        }
    }

    private void CopyAll_Click(object sender, RoutedEventArgs e)
    {
        if (_vm.LogLines.Count == 0)
        {
            MessageBox.Show(this, "还没有日志。", "复制日志",
                MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        var sb = new StringBuilder();
        foreach (var line in _vm.LogLines)
            sb.Append(line.Timestamp).Append(' ').AppendLine(line.Text);

        try
        {
            Clipboard.SetText(sb.ToString());
        }
        catch (Exception ex)
        {
            MessageBox.Show(this, $"写剪贴板失败：{ex.Message}", "复制日志",
                MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }

    private void Clear_Click(object sender, RoutedEventArgs e)
    {
        // 清日志是不可撤销的，问一句 —— 用户常常是在排查问题，手一抖就没了
        var ok = MessageBox.Show(this,
            $"确定清空这 {_vm.LogLines.Count} 行日志吗？清掉就找不回来了。",
            "清空日志", MessageBoxButton.OKCancel, MessageBoxImage.Question);

        if (ok == MessageBoxResult.OK)
            _vm.ClearLog();
    }

    private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2)
        {
            WindowState = WindowState == WindowState.Maximized
                ? WindowState.Normal
                : WindowState.Maximized;
            return;
        }

        try
        {
            DragMove();
        }
        catch (InvalidOperationException)
        {
            // 鼠标已经松开时 DragMove 会抛，属于正常情况
        }
    }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();
}