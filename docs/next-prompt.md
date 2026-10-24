【上下文】
项目 C:\Users\yuwei\Documents\pcl2-like 是自写的 Minecraft 启动器（.NET 8，Core/Gui/Cli）。
这轮在查「正版登录 403 Invalid app registration」，已确认：xbox live 全链走通，
卡在 login_with_xbox —— 微软给启动器的 access_token 是 JWE 加密（alg=dir / A256GCM），不是 JWT。
加速方式：C:\Users\yuwei\Documents\pcl2-like\docs\jwe-experiment.md 是我这轮实验的原始记录。

【要做的事】
把 JWE 解密链做完：从 accounts.json 里把注入的 AES 密钥抠出来，
用它拆开 JWE，确认里面是可用的 Minecraft access_token，
然后把这条链引进启动器，让正版登录真的能跑完。

【约束】
- .NET 8，Core 不依赖 WPF（Gui 才有 WPF）。
- 构建：$env:DOTNET_ROOT='C:\Users\yuwei\.dotnet'，再调 dotnet.exe build，必须 0 警告 0 错误。
- 改代码只用 read/write/edit 工具 —— 不要用 pwsh 的 Get-Content/Set-Content 读写源文件
  （这台机器是 PowerShell 5.1，默认 ANSI/GBK，会把无 BOM 的 UTF-8 中文注释写坏，已经踩过一次）。
- 用户要的是「能用」，不要写 demo；别把复杂度甩给用户。