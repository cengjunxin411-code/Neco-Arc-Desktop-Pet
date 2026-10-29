# Windows 构建与验证

宿主：Windows x64，.NET SDK 10.0.401，WPF / WindowsDesktop runtime。global.json 固定该 SDK feature band，仅允许补丁滚动。没有显式 NuGet 包；NuGet.Config 清空继承的包源，依赖 SDK 自带 reference/framework packs。缺少 SDK packs 时请先安装完整 Windows .NET SDK，不要加入私有源或复制旧 DLL。

以下命令均在候选根目录运行，使用独立 bin/obj 树。工作台是同一 WPF 项目的 XAML 和 partial 类，随对应模式编译，无独立工作台项目。

```powershell
# 普通 WPF（可无素材打开中性占位窗口）
dotnet restore NecoArcDesktopPet.csproj --configfile NuGet.Config
dotnet build NecoArcDesktopPet.csproj -c Release --no-restore -t:Rebuild

# Product（编译入口及 PMv2 manifest；无 idle GIF 时保留 Safety Stop）
dotnet restore NecoArcDesktopPet.csproj --configfile NuGet.Config -p:DesktopPetProduct=true
dotnet build NecoArcDesktopPet.csproj -c Release --no-restore -t:Rebuild -p:DesktopPetProduct=true

# Pilot（独立实验入口；无 idle GIF 时保留 Safety Stop）
dotnet restore NecoArcDesktopPet.csproj --configfile NuGet.Config -p:DesktopPetPmv2Pilot=true
dotnet build NecoArcDesktopPet.csproj -c Release --no-restore -t:Rebuild -p:DesktopPetPmv2Pilot=true
```

产物分别位于 `bin/Release/net10.0-windows/`、`bin/product/Release/net10.0-windows/`、`bin/pmv2-pilot/Release/net10.0-windows/`。两个模式属性不可同时设置。Product/Pilot 无素材时能够编译，但缺少必需且校准匹配的 idle GIF 会在原生布局前明确安全停止，退出码为 4。Product 显示缺素材说明；Pilot 可用 `--log` 指定本地日志。普通 WPF 则可以无素材启动并显示中性占位图。无素材场景不具备完整动作、物理或跨屏运行能力；兼容合法素材下的运行仍须另行验收。

Product/Pilot 运行必须使用对应 EXE apphost；`dotnet <Product.dll>` 不能替代 apphost/PMv2 门禁。

统一验收入口：

```powershell
./scripts/verify-source-only.ps1
./scripts/verify-source-only.ps1 -DesktopSmoke
```

该脚本逐项记录真实退出码到 `.local/verification-*/commands.json`；首个失败即退出，完整 stdout 在同目录。它运行显式 restore、三种 Release Rebuild、原九个控制台套件及新增 CharacterProfileTests、源码白名单/签名/已知凭据规则、各模式 .g.resources 与原生图标检查。`SourceOnlyFallbackTests` 还分别针对 Product/Pilot 编译运行 NaN、正负 Infinity、零、负数、缩放溢出与正常尺寸回归。`-DesktopSmoke` 另打开普通版真实 WPF 窗口和工作台，以及两种 PMv2 构建的缺 idle GIF 安全停止与布局边界回归；使用隔离设置，随后关闭。缺素材回归程序退出 0 表示它正确观察到安全停止，不代表 Product/Pilot 本身运行成功。无交互式桌面时应将这一项记录为受阻。

控制台套件为 ClickReactionTests（播放游标）、CoordinateGeometryTests、MonitorTopologyTests（含 fake-native 验证）、PhysicsModelTests、GravityTests、PlanarInertiaTests、PhysicsGatingTests、DpiHwndVerificationTests、SourceOnlyIsolationTests。单独运行示例：

```powershell
dotnet restore tests/GravityTests --configfile NuGet.Config
dotnet run --project tests/GravityTests -c Release --no-restore
```

这些是自带断言的控制台程序，`dotnet test` 不能替代。受限素材集成测试没有导出，也没有计为通过。测试入口会生成隔离 fixture，建议使用统一脚本；单独运行 PhysicsGatingTests 时先把 `NECO_TEST_OUTPUT_ROOT` 指向 `.local` 下的绝对本地目录。

资源检查器不加载游戏素材：托管资源仅允许 WPF BAML；EXE 原生 icon/group payload 必须逐项匹配当前安装的 .NET 10 x64 SDK apphost。默认 SDK 图标是 .NET 输入，不是角色图标；松散源文件仍禁止任何图片。检查器仅为项目离线规则，不是独立秘密扫描工具或任意混淆载荷证明。

GitHub Actions 配置使用 Windows runner、精确 SDK 和固定 commit SHA 的 Actions，不上传 artifact、不调用本地素材发行器。云端真实执行仍待建立新仓库后验证。脚本成功不表示 Product 动画、混合 DPI、双屏、听音或安装验收通过。

## Character Profile 验证

`NECO_CHARACTER_PROFILE` 仅接受本地绝对 JSON 路径；不指定时没有隐式原角色配置。配置格式、允许范围与资源嵌入见 [Character Profile](CHARACTER-PROFILE.md)。JSON 是运行时外部配置，GIF 仍须按原项目资源键在构建前嵌入；WAV 的外部加载与 CopyToPublishDirectory=Never 规则不变。新增代码只使用 .NET 自带 System.Text.Json/FrozenDictionary，无新增包或第三方依赖。

统一脚本增加 CharacterProfileTests（结构、数值、原子快照、接触与镜像纯计算）。`-DesktopSmoke` 还调用 `scripts/verify-character-profile.ps1`：原生 WPF 绘制中性矩形 GIF，仅复制精确白名单到 `.local` 隔离源码副本，再对普通/Pilot/Product 测试无配置、无接触、错误画布和无效 JSON。每次进程固定独立快照，每条命令的退出码保存在忽略目录的 commands.json。合成媒体和含媒体的测试产物不属于公开候选文件，不能递归上传 `.local`。真实普通窗口/工作台及原 missing-idle/layout-input 回归均继续执行，不删除或降低旧断言。

这些检查不能证明动作帧协议、真实角色完整适配、用户鼠标、真实多屏/mixed-DPI、听音或安装验收。Product/Pilot 的合成 idle 准入只证明实际解码/布局和能力拒绝边界。

## v1.1.0 定向测试

轻量验证仍使用上面的 `scripts/verify-source-only.ps1` 默认入口，不运行私有版完整回归。

```powershell
dotnet restore tests/PoundDurationIntegration --configfile NuGet.Config
dotnet run --project tests/PoundDurationIntegration -c Release --no-restore -- --cursor-only

# 只编译 F1/F3 集成源码；不放宽公开版资源/Character Profile 准入。
dotnet restore tests/VisualAdmissionIntegration --configfile NuGet.Config
dotnet build tests/VisualAdmissionIntegration -c Release --no-restore
```

`--cursor-only` 使用真实 ClickDefinitions 和 GifPlaybackCursor，对合成的两帧、每帧 100ms 循环验证 pound 的 13 次完整循环、完成通知、重绕和打断，以及 flick/quick_punch 的原循环预算；它不读取或证明任何真实 GIF 的时序。没有该参数时，pound 集成程序要求按原资源键自备兼容的 pound/flick/quick_punch GIF 及显式匹配的 NECO_CHARACTER_PROFILE，并测试真实解码、原始帧时序、暂停恢复和抢占。缺素材会失败，不计为通过；项目中的资源项均有 Exists 条件。

`VisualAdmissionIntegration` 从 v1.1.0 的定向测试提取 F1/F3 用例，使用公开 Pilot 源码构建。运行需要交互桌面、自备兼容且匹配的 idle/base_idle、idle/watch_phone、click/rotation、angry/caper、angry/angry1 GIF 和显式 `NECO_CHARACTER_PROFILE`。例如使用 `.local/visual-r1` 作为全新证据目录，参数为 `--visual-admission R1`。R1/R2/R4 检查捕获与切换，R3-3/R3-30 检查真实外部窗口覆盖仍安全停止；C-once/C-repeat/C-native-diverged 等检查回滚、一次重试与后继所有权。测试不附带原角色校准数据，任意素材不保证满足用例前提，不能把编译成功当作运行通过。不要提交自备素材、测试输出或含素材程序集。
