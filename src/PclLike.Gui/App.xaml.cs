using System.IO;
using System.Windows;

namespace PclLike.Gui;

public partial class App : Application
{
    /// <summary>
    /// 游戏根目录。优先级：命令行第一个参数 &gt; 环境变量 PCLLIKE_HOME &gt; %APPDATA%\.pcllike。
    /// 传参是为了能开多个独立实例目录做测试。
    /// </summary>
    public static string GameRoot { get; private set; } = DefaultRoot();

    private static string DefaultRoot()
    {
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && !string.IsNullOrWhiteSpace(args[1])
            && !args[1].StartsWith('-') && Directory.Exists(args[1]))
            return Path.GetFullPath(args[1]);

        var env = Environment.GetEnvironmentVariable("PCLLIKE_HOME");
        if (!string.IsNullOrWhiteSpace(env)) return Path.GetFullPath(env);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), ".pcllike");
    }
}