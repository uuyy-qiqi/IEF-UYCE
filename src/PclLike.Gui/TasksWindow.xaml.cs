using System;
using System.Collections.Specialized;
using System.Windows;
using System.Windows.Input;
using PclLike.Core.Diagnostics;

namespace PclLike.Gui
{
    /// <summary>
    /// 「任务管理」窗（照 PCL CE 那个做的）。
    ///
    /// 它不碰安装代码：订阅启动器那份 <see cref="MainViewModel.LogLines"/>，
    /// 把每一行新日志喂给 <see cref="TaskProgressViewModel.OnLog"/>，
    /// 由后者认出「12/345 个文件」「装 Forge」这类句式并推进清单。
    /// 这样安装流程一行都不用改，以后新步骤只要打日志就会自动出现。
    /// </summary>
    public partial class TasksWindow : Window
    {
        private readonly MainViewModel _vm;
        private bool _hooked;

        public TasksWindow(MainViewModel vm)
        {
            InitializeComponent();

            _vm = vm;
            DataContext = vm.Tasks;

            Loaded += OnLoaded;
            Closed += OnClosed;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            // 先把已经攒下的日志过一遍（窗口晚开时不会漏掉前面的步骤）
            foreach (var line in _vm.LogLines) _vm.Tasks.OnLog(line.Text);

            if (!_hooked)
            {
                _vm.LogLines.CollectionChanged += OnLogLinesChanged;
                _hooked = true;
            }
        }

        private void OnClosed(object? sender, EventArgs e)
        {
            if (_hooked)
            {
                _vm.LogLines.CollectionChanged -= OnLogLinesChanged;
                _hooked = false;
            }
        }

        private void OnLogLinesChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            if (e.NewItems is null) return;

            foreach (var item in e.NewItems)
            {
                if (item is LogLine line) _vm.Tasks.OnLog(line.Text);
            }
        }

        // ── 窗口外壳（和 LogWindow 一致的那套）────────────────────────────

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

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标已经松开，忽略
            }
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
            => WindowState = WindowState.Minimized;

        private void Close_Click(object sender, RoutedEventArgs e) => Close();

        /// <summary>刷新：把整份日志重放一遍（等于重新数一次进度）。</summary>
        private void Refresh_Click(object sender, RoutedEventArgs e)
        {
            _vm.Tasks.Begin(_vm.Tasks.Title);

            foreach (var line in _vm.LogLines) _vm.Tasks.OnLog(line.Text);
        }

        /// <summary>把任务卡收起来 —— 只隐清单，窗口还在（对应卡右上那个 ✕）。</summary>
        private void CardClose_Click(object sender, RoutedEventArgs e)
            => _vm.Tasks.Begin("");
    }
}