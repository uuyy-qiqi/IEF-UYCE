using System.Windows;

namespace PclLike.Gui;

/// <summary>
/// 「下载 Java」的小对话框：只负责问用户要哪个大版本，下载和解压交给
/// <see cref="PclLike.Core.Minecraft.Launch.JavaRuntimeManager"/>。
/// </summary>
public partial class JavaPickDialog : Window
{
    /// <summary>用户选中的 Java 大版本。</summary>
    public int MajorVersion { get; private set; } = 17;

    public JavaPickDialog(int suggestedMajor)
    {
        InitializeComponent();

        // 预选：按当前选中版本推出来的那个
        var target = suggestedMajor switch
        {
            <= 8 => Pick8,
            >= 21 => Pick21,
            _ => Pick17,
        };
        target.IsChecked = true;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        MajorVersion = Pick8.IsChecked == true ? 8
                     : Pick21.IsChecked == true ? 21
                     : 17;

        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}