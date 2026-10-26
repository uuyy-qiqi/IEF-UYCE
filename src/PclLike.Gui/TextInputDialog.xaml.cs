using System;
using System.Windows;
using System.Windows.Input;

namespace PclLike.Gui
{
    /// <summary>
    /// PCL 那个「输入实例名称」弹窗（蓝顶栏 + 说明 + 单行输入 + 确定/取消）。
    ///
    /// 新建实例 / 重命名 / 复制 / 加已有文件夹都走这一个 —— 之前是各写各的
    /// 内联输入框，样式不统一，而且光标位置和回车行为也各不一样。
    /// </summary>
    public partial class TextInputDialog : Window
    {
        internal TextInputDialog()
        {
            InitializeComponent();
        }

        /// <summary>输入的结果；取消时为 null。</summary>
        public string? Result { get; private set; }

        /// <summary>
        /// 弹一个输入框问一句话。用户点确定 → 返回去掉首尾空白的文本（空文本返回 null）；
        /// 点取消 / 关窗 / 按 Esc → 返回 null。
        /// </summary>
        public static string? Ask(
            Window? owner,
            string title,
            string hint,
            string initial = "",
            string okText = "确定")
        {
            var dlg = new TextInputDialog
            {
                Title = title,
            };

            dlg.TitleText.Text = title;
            dlg.HintText.Text = hint;
            dlg.Input.Text = initial;
            dlg.OkButton.Content = okText;

            if (owner is not null && !ReferenceEquals(owner, dlg))
            {
                dlg.Owner = owner;
                dlg.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            }

            var ok = dlg.ShowDialog() == true;
            if (!ok) return null;

            var text = dlg.Result?.Trim();
            return string.IsNullOrWhiteSpace(text) ? null : text;
        }

        protected override void OnContentRendered(EventArgs e)
        {
            base.OnContentRendered(e);

            Input.Focus();
            Input.SelectAll();
        }

        private void TopBar_MouseDown(object sender, MouseButtonEventArgs e)
        {
            if (e.ChangedButton != MouseButton.Left) return;

            try
            {
                DragMove();
            }
            catch (InvalidOperationException)
            {
                // 鼠标已松开，忽略
            }
        }

        private void Input_KeyDown(object sender, KeyEventArgs e)
        {
            switch (e.Key)
            {
                case Key.Enter:
                    Accept();
                    e.Handled = true;
                    break;

                case Key.Escape:
                    Cancel_Click(sender, e);
                    e.Handled = true;
                    break;
            }
        }

        private void Ok_Click(object sender, RoutedEventArgs e) => Accept();

        private void Accept()
        {
            var text = Input.Text.Trim();
            if (string.IsNullOrWhiteSpace(text)) return;   // 空的就留在弹窗里，不当作确定

            Result = text;
            DialogResult = true;
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            Result = null;
            DialogResult = false;
        }
    }
}