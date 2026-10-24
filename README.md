# PclLike

一个自己写的 Minecraft 启动器（.NET 8 + WPF）。功能上对齐 PCL2：版本下载与安装、
加载器安装（Forge / NeoForge / Fabric）、实例隔离、Mod 管理、正版与离线登录，
界面照 PCL2 CE 的布局做。

## 怎么跑

需要 .NET 8 SDK。本机用的是用户级安装，所以路径得显式指一下：

```powershell
$env:DOTNET_ROOT = 'C:\Users\yuwei\.dotnet'
$env:PATH = "C:\Users\yuwei\.dotnet;$env:PATH"

# 编译
dotnet build src\PclLike.Gui\PclLike.Gui.csproj

# 跑图形界面
dotnet run --project src\PclLike.Gui

# 命令行（无界面，装了就能下）
dotnet run --project src\PclLike.Cli -- --help
```

## 工程结构

```
src\PclLike.Core    纯逻辑，不依赖 WPF（能单独单元测试）
src\PclLike.Gui     WPF 界面
src\PclLike.Cli     命令行
```

Core 里几块主要的东西：

| 目录 | 管什么 |
|---|---|
| `Minecraft\` | 版本清单、版本 JSON、libraries/assets 下载、参数拼装、启动 |
| `Minecraft\Loaders\` | Forge / NeoForge / Fabric 的安装，含 installer 的 processor 链 |
| `Storage\` | 实例（GameInstance）、配置、持久化 |
| `Mods\` | Mod 扫描、元数据、依赖分析、搜索与下载 |
| `Login\` | 微软正版登录（设备代码流）、离线账号 |

## 自检与出图

界面改动光编译过不算数 —— XAML 的资源引用、布局塌陷、绑定写错编译期都不报。
所以有两个探针：

```powershell
$exe = "src\PclLike.Gui\bin\Debug\net8.0-windows\PclLike.exe"

# 自检：把窗口真建起来，逐项检查控件和绑定
& $exe --selftest

# 出图：把窗口渲染成 PNG 看长什么样
& $exe --shoot tools\shots\launch.png 1120 720 launch
& $exe --shoot tools\shots\set_java.png 1120 720 settings --section set-java
```

`--section` 可以切设置页 / 工具页里的分区（`set-java` / `set-account` /
`set-appearance` / `set-misc` / `lobby` / `toolbox` / `help`）。

## 踩过的坑

- **别用 PowerShell 5.1 的 `Get-Content` / `Set-Content` 读写这些源文件。**
  它默认按 ANSI（中文机器上是 GBK）解码，UTF-8 源码里的中文注释会被双重编码变成
  乱码，而且这个过程有损（不可逆）。用编辑器工具，或者显式
  `[IO.File]::ReadAllText($p, [Text.Encoding]::UTF8)`。
- **截图探针必须复用 `App` 的资源字典**，不能自己 `new ResourceDictionary`，
  否则 `BoolVis` 之类的转换器找不到，折叠面板的绑定会静默失效。
- **Core 不依赖 WPF**：皮肤头像的解析放 Core，画到界面上的是 Gui 里的
  `SkinHeadView`。放错层直接编译不过。
- NeoForge 的 `install_profile.json` 里的 `libraries` 只用于 processor，
  **不能**混进游戏 classpath；而且 `processors[].outputs` 是 JSON 对象
  （`{"path": ...}`），不是字符串；`data` 表里的 `[maven{...}]` 占位符要还原。