using System.Windows;
using Microsoft.Win32;
using PclLike.Core.Minecraft.Launch;
using PclLike.Core.Storage;

namespace PclLike.Gui;

public partial class SettingsWindow : Window
{
    private readonly MainViewModel _vm;

    public SettingsWindow(MainViewModel vm)
    {
        InitializeComponent();
        _vm = vm;

        // 把当前配置铺进控件
        UsernameBox.Text = vm.Config.Username;
        JavaPathBox.Text = vm.Config.JavaPath ?? "";
        MemorySlider.Value = Math.Clamp(vm.Config.MaxMemoryMb, 1024, 32768);
        ServerBox.Text = vm.Config.Server ?? "";
        JvmArgsBox.Text = vm.Config.JvmArgs ?? "";
        AutoInstallCheck.IsChecked = vm.Config.AutoInstall;
        KillOnExitCheck.IsChecked = vm.Config.KillGameOnExit;

        JavaListBox.ItemsSource = vm.JavaList;
        JavaListBox.Visibility = vm.JavaList.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        MemorySlider.ValueChanged += (_, _) => UpdateMemoryText();
        UpdateMemoryText();
    }

    private void UpdateMemoryText()
        => MemoryText.Text = $"{MemorySlider.Value / 1024.0:0.#} GB";

    private void BrowseJava_Click(object sender, RoutedEventArgs e)
    {
        var dlg = new OpenFileDialog
        {
            Title = "选择 java.exe",
            Filter = "Java 可执行文件|java.exe;java|所有文件|*.*",
            CheckFileExists = true,
        };

        if (dlg.ShowDialog() == true) JavaPathBox.Text = dlg.FileName;
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        var cfg = _vm.Config;

        cfg.Username = ConfigStore.SanitizeUsername(UsernameBox.Text);
        cfg.JavaPath = string.IsNullOrWhiteSpace(JavaPathBox.Text) ? null : JavaPathBox.Text.Trim();
        cfg.MaxMemoryMb = (int)MemorySlider.Value;
        cfg.Server = string.IsNullOrWhiteSpace(ServerBox.Text) ? null : ServerBox.Text.Trim();
        cfg.JvmArgs = string.IsNullOrWhiteSpace(JvmArgsBox.Text) ? null : JvmArgsBox.Text.Trim();
        cfg.AutoInstall = AutoInstallCheck.IsChecked == true;
        cfg.KillGameOnExit = KillOnExitCheck.IsChecked == true;

        ConfigStore.Save(_vm.GameRoot, cfg);

        // 同步回主界面绑定的属性
        _vm.Username = cfg.Username;
        _vm.MaxMemoryMb = cfg.MaxMemoryMb;
        _vm.RefreshJava();

        _vm.AppendLog(LogKind.Info, $"设置已保存（玩家名 {cfg.Username}，内存 {cfg.MaxMemoryMb} MB）");

        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}