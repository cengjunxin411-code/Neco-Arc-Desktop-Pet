# Neco-Arc Desktop Pet

一个基于 [YoneDesktopPet](https://github.com/DreamingBetter/YoneDesktopPet) 扩展开发的 Windows 桌面宠物项目。

使用 **C#、WPF、Win32 和 .NET 10** 构建，围绕 GIF 动画、桌面交互、窗口吸附、物理运动与多显示器适配等功能进行开发。

项目不仅关注桌宠的视觉表现，也尝试让角色与桌面环境之间产生更加自然的交互。

**当前仓库为 Source-Only 版本**，提供项目源码、配置示例和测试工具，不包含游戏角色的图片、动画及语音素材。

## 主要功能

### 动画与行为系统

- **GIF 动画播放**：支持逐帧播放、循环控制、动画切换和播放完成事件。
- **待机行为**：支持随机待机动作与自主移动。
- **休息与睡眠**：包含休息、入睡、唤醒及相关状态切换。
- **交互反馈**：提供点击、连续点击、拖拽与特定动作的响应机制。
- **动作状态管理**：协调不同动作之间的转换、打断与恢复。

### 桌面交互与物理运动

- **自由拖拽**：通过鼠标拖动桌宠，并根据交互状态处理后续行为。
- **窗口吸附**：支持检测桌面窗口，并计算角色与窗口边缘之间的接触位置。
- **重力与惯性**：包含下落、抛掷、水平惯性及相关运动模型。
- **多显示器支持**：处理不同显示器之间的坐标转换、屏幕边界和 DPI 差异。
- **窗口跟随**：在满足接触及安全条件时，使桌宠跟随目标窗口移动。

### 工作台与设置

提供可视化工作台，用于调整桌宠的交互和行为参数，包括：

- 普通行为与自主移动
- 休息和睡眠
- 鼠标交互
- 窗口吸附
- 语音及相关设置
- 诊断选项

相关配置支持本地持久化保存。

### 角色配置与音频

- **Character Profile**：通过外部 JSON 描述动画画布、视觉缩放、接触点及轮廓等角色相关参数。
- **动作语音**：包含动作语音映射和播放控制接口。
- **素材与逻辑分离**：角色素材、视觉校准数据和桌宠行为逻辑分别管理。

上述功能是项目源码中实现的能力，不代表无素材状态下可以直接使用全部功能。完整角色动作需要提供兼容素材及对应配置。

## 当前版本说明

为方便公开源码与后续开发，仓库没有包含原项目使用的游戏 GIF、PNG、ICO、WAV 等媒体资源。

目前提供三种运行模式：

| 模式 | 说明 |
|---|---|
| 普通 WPF 模式 | 无素材时仍能启动，显示由 WPF 绘制的中性占位图，并可打开工作台 |
| Product 模式 | 面向产品化运行的宿主，具备相关运行与设置管理逻辑 |
| Pilot 模式 | 包含窗口交互、物理运动及相关原生窗口控制能力的运行宿主 |

普通模式可以直接用于体验基础界面、查看设置及研究代码。

**Product 和 Pilot 模式需要提供符合要求的 GIF 动画及 Character Profile。** 如果缺少必要的待机动画或校准配置，程序会执行安全停止，而不是以不完整的角色状态继续运行。

项目目前保留既有的动画槽位、动作协议与素材命名约定。因此，Character Profile 提供的是角色适配接口，并不意味着可以直接加载任意 GIF 自动生成完整桌宠。

真实角色素材下的完整动作表现，以及多显示器、混合 DPI 和物理交互等场景，仍需要根据实际运行环境进行验证。

## 快速开始

### 环境要求

- Windows x64
- .NET 10 SDK
- PowerShell
- Git

### 获取源码

```powershell
git clone https://github.com/cengjunxin411-code/Neco-Arc-Desktop-Pet.git
cd Neco-Arc-Desktop-Pet
```

### 构建项目

在项目根目录执行：

```powershell
dotnet restore NecoArcDesktopPet.csproj --configfile NuGet.Config
dotnet build NecoArcDesktopPet.csproj -c Release --no-restore -t:Rebuild
```

### 启动程序

```powershell
& ./bin/Release/net10.0-windows/NecoArcDesktopPet.exe
```

未配置角色素材时，普通模式将显示中性占位图。

右键点击占位图，可以打开工作台、查看相关功能及退出程序。

更完整的构建说明见 [BUILD.md](docs/BUILD.md)。

## 角色素材与 Character Profile

项目将角色相关的视觉校准信息放入独立的 Character Profile，而不是直接写入通用物理与窗口交互代码。

Profile 使用版本化 JSON，主要描述：

- 角色的规范画布尺寸与逻辑比例
- 各动画的画布尺寸和视觉校准
- 用于窗口吸附的接触点
- 动画轮廓与部分动作的侧边覆盖校正

可以通过环境变量 `NECO_CHARACTER_PROFILE` 指定本地 Profile 文件。

当前实现要求使用绝对本地路径，配置在进程首次读取后固定，修改配置需要重新启动程序。

GIF 素材需要按照项目已有的资源键和构建方式进行嵌入，并与对应 Profile 匹配。

相关文档：

- [Character Profile 配置说明](docs/CHARACTER-PROFILE.md)
- [Character Profile 示例](examples/character-profile.neutral.json)
- [素材与资源说明](docs/ASSETS.md)
- [角色校准迁移说明](docs/CHARACTER-PROFILE-MIGRATION.md)

仓库提供的示例使用中性矩形数据，不包含实际游戏角色素材。

## 开发与测试

项目包含针对动画、几何计算、物理模型、显示器拓扑、DPI、窗口附着及角色配置的测试程序。

在 Windows PowerShell 环境下，可执行：

```powershell
./scripts/verify-source-only.ps1
```

如果运行环境具备交互式 Windows 桌面，还可以执行：

```powershell
./scripts/verify-source-only.ps1 -DesktopSmoke
```

后者包含普通 WPF 窗口和工作台等桌面验证。

Character Profile 还提供独立的配置和合成素材测试，用于检查资源准入、参数有效性及相关安全边界。

自动测试主要验证源码行为、模型逻辑和受控场景，并不能代替所有实际桌面环境下的人工测试。

## 项目来源与致谢

本项目基于 **[DreamingBetter / YoneDesktopPet](https://github.com/DreamingBetter/YoneDesktopPet)** 开发。

感谢原项目提供的 WPF 桌宠实现基础。本项目在其基础上进行了功能扩展和结构调整，包括动画系统、行为状态、桌面物理、多显示器处理、工作台及角色配置等。

**当前维护者：ZINC**

项目开发过程中使用了 Codex、DeepSeek 等 AI 工具辅助代码实现、测试及审查。

## 开源许可

项目源码依照 [LICENSE](LICENSE) 中的相应 MIT 许可条款提供，保留 YoneDesktopPet 的原始版权与许可通知，以及 ZINC 对后续修改的署名。

本仓库不提供游戏素材，代码许可不包含第三方角色形象、音频及商标的使用授权。项目为非官方作品。

详细信息见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

---

*持续完善中的 Windows 桌面宠物项目。*
