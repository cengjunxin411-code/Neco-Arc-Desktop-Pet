# 第三方来源与许可

这是源码候选，包含下述 YoneDesktopPet 继承及改编源码；未导入第三方 DLL、NuGet 包、Inno Setup、Pillow 或其他额外 vendored 库源码。当前所有项目无显式 PackageReference。编译仍依赖 SDK/framework packs；生成 apphost、WPF 编译产物和调试文件位于忽略目录，不能当作已获准公开的二进制包。

| 组件 | 使用与版本 | 官方来源和许可 | 当前分发边界 |
| --- | --- | --- | --- |
| .NET / runtime | SDK 10.0.401，现场 runtime/host pack 10.0.12 | [runtime LICENSE](https://github.com/dotnet/runtime/blob/main/LICENSE.TXT)，MIT | 构建工具与默认 apphost；无自包含安装器 |
| WPF | .NET 10 WindowsDesktop，现场 10.0.12 | [WPF LICENSE](https://github.com/dotnet/wpf/blob/main/LICENSE.TXT)，MIT | framework API / BAML 编译；未复制库源码 |
| actions/checkout | v4 ref resolved 2026-10-27 to 11d5960a326750d5838078e36cf38b85af677262 | [固定版本 LICENSE](https://github.com/actions/checkout/blob/11d5960a326750d5838078e36cf38b85af677262/LICENSE)，MIT | workflow 引用，不 vendoring Action 代码 |
| actions/setup-dotnet | v4 ref resolved 2026-10-27 to 67a3573c9a986a3f9c594539f4ab511d57bb3ce9 | [固定版本 LICENSE](https://github.com/actions/setup-dotnet/blob/67a3573c9a986a3f9c594539f4ab511d57bb3ce9/LICENSE)，MIT | workflow 引用，云端未运行 |

本项目由 ZINC 维护，基于 YoneDesktopPet 修改开发。维护者确认没有另行直接复制/改编其他项目或教程源码、没有其他开发者贡献，并使用 Codex、DeepSeek 等 AI 工具辅助后续开发。根 LICENSE 的 ZINC 许可仅覆盖其有权授权的修改，不是全部独立原创或完整权属的保证。P5 已从公开集合移除 P4 识别的原角色几何校准表、测量与人工标注；保留通用计算、策略及 Yone 派生算法，并保留完整上游 MIT/NOTICE 与 ZINC 修改署名，详见 [ASSETS](docs/ASSETS.md)。角色相关命名、动作槽位、资源键及其他品牌/文本边界仍存在；最终 GitHub 发布须经维护者审核并取得明确授权。中性占位图由 WPF 几何代码绘制，没有外部图片输入。

若未来分发任何自包含二进制，必须按实际 SDK/runtime pack 的 LICENSE 与 THIRD-PARTY-NOTICES 核对全体组件。以下上游通知不代表完整运行时传递组件清单或项目原创性证明，也不授予游戏资源权利。

## YoneDesktopPet — MIT

- 上游仓库：[DreamingBetter/YoneDesktopPet](https://github.com/DreamingBetter/YoneDesktopPet)。
- 上游维护者：**DreamingBetter**；账号署名不等于已证明其法律姓名或所有内容的版权主体。
- P3 固定审计提交：[`3c410d002e4c6b8ed2cbcb7689dc2b531a48653c`](https://github.com/DreamingBetter/YoneDesktopPet/commit/3c410d002e4c6b8ed2cbcb7689dc2b531a48653c)，不是已证明的唯一初始导入提交。
- 继承范围：WPF 窗口交互、鼠标拖拽、气泡窗口、窗口枚举/过滤及 DIP 转换、图片黑底抠图与透明裁边，以及经抽取/扩展的吸附候选、评分、跟随、布局和缓动下落生命周期。`MainWindow.xaml.cs` 含仍实质相同的实现；`AttachmentGeometry.cs` 与 `AttachmentContactProfile.cs` 保留吸附规则的抽取与改写。基础 App/窗口布局和项目结构亦有继承关系。
- 后续 GIF 动作、工作台、PMv2、跨屏、物理和语音等扩展不能抹去上述来源；对 ZINC 有权授权的后续修改，见根 [LICENSE](LICENSE) 的单独段落。

### 上游完整 LICENSE 原文

以下原文取自固定提交的 [LICENSE](https://github.com/DreamingBetter/YoneDesktopPet/blob/3c410d002e4c6b8ed2cbcb7689dc2b531a48653c/LICENSE)，保留原始无姓名版权行，未补写或替换作者。

```text
MIT License

Copyright (c) 2026

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

### 上游完整 NOTICE.md 原文

以下是 **YoneDesktopPet 原项目**的 [NOTICE.md](https://github.com/DreamingBetter/YoneDesktopPet/blob/3c410d002e4c6b8ed2cbcb7689dc2b531a48653c/NOTICE.md) 原文：

```text
# Notice

The MIT license in this repository applies to the source code only.

Character artwork, champion names, skin names, game-related text, and other third-party assets are not licensed by this repository unless explicitly stated by their respective owners.

This project is an unofficial personal desktop pet sample. It is not affiliated with, endorsed by, sponsored by, or approved by Riot Games or League of Legends.
```

上述 Riot Games / League of Legends 内容是上游原项目的素材版权和非官方关系说明，**不等于 Neco-Arc Desktop Pet 获得这些素材的授权，也不证明本项目的角色素材来源于该游戏**。本项目针对 Neco-Arc、月姬及相关权利人的非官方说明独立适用；任何角色、美术、语音、游戏文本及商标均不因代码 MIT 获得使用或再分发许可。未来第三方素材或自有配置的来源与许可须另行核验；这些内容不通过上述通知或 ZINC 版权行自动获得授权。移除旧校准表不表示所有潜在权利问题已消失。

## .NET runtime / WPF 主许可证通知

Copyright (c) .NET Foundation and Contributors

All rights reserved.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.

## GitHub Actions 主许可证通知

Copyright (c) 2018 GitHub, Inc. and contributors

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
