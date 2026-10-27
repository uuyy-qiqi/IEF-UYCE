# IEF-UYCE

一个从零写的 Minecraft 启动器（.NET 8 + WPF），界面照着 PCL2 的手感做的。
版本安装、整合包导入、实例管理、联机大厅、百宝箱全都有，没有依赖任何启动器框架。

> 本项目与 PCL2、HMCL、Modrinth、CurseForge 均无隶属关系，只是尽量做得好用一点。

---

## 只想玩，不想编译？三步

1. 打开 **[Releases](https://github.com/uuyy-qiqi/IEF-UYCE/releases/latest)**，下载 `IEF-UYCE-v1-win-x64.zip`（约 70 MB）
2. 解压到**路径里没有中文、没有空格**的地方（例如 `D:\IEF-UYCE`），双击 `PclLike.exe`
3. 到「设置 → Java」确认 Java 路径 —— Minecraft 1.18 及以上要 **Java 17**，1.12.2 及更早要 **Java 8**

然后：

- 想装游戏：点「下载」→ 选版本 → 安装
- 有整合包：把 zip / mrpack / rar / 7z **直接拖进启动器窗口**，它自己认
- 配置和数据都在 `%APPDATA%\.pcllike`，删掉这个目录就恢复出厂

这个包是**自包含**的，机器上不需要装 .NET。第一次运行 SmartScreen 会拦一下（未签名），点「更多信息 → 仍要运行」。

---

## 它能做什么

### 版本安装

| 加载器 | 支持情况 |
|---|---|
| 原版 | 全版本，走官方版本清单 |
| Fabric | ✅ 全版本 |
| Forge | ✅ 全版本（补丁链自己跑，含 `install_profile.json` 里的库） |
| NeoForge | ✅ 1.20.2+ |
| Quilt | ✅ |
| Legacy Fabric | ✅ 1.6–1.13 |
| OptiFine | ✅（先装同版本 Forge，再把 jar 放进 mods —— OptiFine 的补丁产出是 Mod 形态，不是完整客户端） |
| LiteLoader | ✅ 1.7.10–1.12.2 |
| Cleanroom | ✅ 1.12.2 |

### 整合包导入

拖进窗口就行，认这些格式：

- **Modrinth** `.mrpack`
- **CurseForge** 清单包（`manifest.json`）—— 只给 projectID/fileID，文件名靠 cfwidget 反查
- **`.rar` / `.7z` / `.tar.gz` / `.tgz` / `.bz2` / `.xz`** —— 自动解包再按内容识别（SharpCompress）
- **解压即玩懒人包**（包里带整个 `.minecraft`，或者带 `versions/<id>/<id>.json`）
- **Prism / MultiMC** 导出（`mmc-pack.json` / `instance.cfg`）
- **HMCL / MCBBS** 格式（`mcbbs.packmeta`）
- **PCL** 导出（`PCL/Setup.ini`）
- **Technic**（`bin/modpack.jar`）
- **手工包**：有 `mods/` 但没清单 —— 整包铺进实例，版本和加载器在界面上选
- 外层套了一层作者文件夹的包（`包名/mods/...`）也能认

### 其它

- **实例管理**：每个实例独立目录 / 独立 `mods`，可复制、删除、改目录；分组折叠 + 关键词搜索
- **联机大厅**：局域网世界扫描 + 服务器列表（只做局域网，不开端口）
- **百宝箱**：游戏目录体检、垃圾清理
- **社区资源**：Modrinth 搜索下载（mod / 整合包 / 资源包 / 光影 / 存档）
- **正版登录**：微软账号，WebView2 内嵌登录窗
- **分层透明窗口**：逐像素 alpha，不是亚克力那种假透明

---

## 怎么构建

需要 **.NET SDK 8**（`dotnet --version` 应该是 8.x）。

```powershell
# 调试运行
dotnet run --project src/PclLike.Gui

# 发布（框架依赖，体积小，目标机要装 .NET 8 Desktop Runtime）
dotnet publish src/PclLike.Gui -c Release -o dist

# 发布（自包含，目标机什么都不用装，165 MB 左右）
dotnet publish src/PclLike.Gui -c Release -r win-x64 --self-contained true -o dist-portable
```

> `PublishSingleFile` 目前在 .NET 8.0.404 + WPF 上会报 `MSB4018 / Bundler.IsAssembly`，
> 所以只提供"文件夹 + zip"的发布形态。

### 跑测试

项目里一堆 `probe-*` 控制台项目，用来**真的连网/真的装一遍**验收某个子功能，不是 mock：

```powershell
dotnet run --project probe-classify --  某个整合包.zip     # 看拖入文件被认成什么
dotnet run --project probe-loader -- forge 1.20.1 --list-only  # 加载器版本列表
dotnet run --project probe-pack -- 整合包.zip             # 真装一遍整合包
```

启动器自己还有自检：`PclLike.exe --selftest`（110 项，全过才退出码 0）。

---

## 运行环境

- Windows 10 / 11（64 位）
- 玩游戏需要 Java：1.18+ 用 JDK 17，1.12.2 及更早用 JDK 8。启动器会自动找，也可以在设置里手动指定
- 正版登录需要 WebView2 运行时（Win11 自带；Win10 可能要装一次）
- 配置目录：`%APPDATA%\.pcllike`（版本、实例、账号、日志都在里面）

---

## 代码结构

```
src/PclLike.Core/         纯逻辑，不依赖 WPF
├── Minecraft/            版本清单、库、资源、启动参数、进程
│   ├── Launch/           ArgumentBuilder / ClasspathBuilder / GameLauncher
│   └── Loaders/          各加载器安装器（Fabric/Forge/NeoForge/Quilt/...）
├── Content/              整合包与资源安装
│   ├── DroppedContent.cs 拖入文件分类器（格式判定的唯一入口）
│   ├── ArchiveNormalizer.cs  rar/7z → zip 适配层
│   ├── BundleImporter.cs 懒人包导入
│   ├── ModPackInstaller.cs  .mrpack
│   └── CursePack.cs      CurseForge 清单包
├── Storage/              实例、账号、设置持久化
├── Net/                  代理解析、DNS 候选、CDN 光标轮换
├── Login/                微软登录
├── Online/               联机大厅
└── Diagnostics/          体检、清理

src/PclLike.Gui/          WPF 界面
src/PclLike.Cli/          命令行
probe-*/                  一次性验收探针（真的连网跑）
```

---

## 已知限制

- 未签名 exe，SmartScreen 会提示"未知发布者"
- 加密的 rar/7z 解不开（会明确报错，不会静默失败）
- CurseForge 文件偶发 403 / 超时：有 3 轮重试 + 失败清单写到实例目录下的 `下载失败.txt`，再拖一次同一个包会跳过已装好的、只补缺的
- 没有自动更新
- 只支持 Windows

---

## License

MIT，见 [LICENSE](LICENSE)。