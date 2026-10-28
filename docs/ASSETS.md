# 本地合法素材接口

本候选不含任何图片、GIF、ICO 或音频。不能自动下载游戏素材。你应先核实资源的使用权和适用条件；合法持有游戏不等于拥有公开再分发权。导入自己的素材后，源码候选的零媒体检查会拒绝这些本地输入，这是公开源码门禁，不应删除或降低。

## PNG、GIF 和编译时嵌入

可选 `assets/default_pet.png` 只在存在时作为 WPF Resource 嵌入；没有 PNG 或无法解码时使用代码绘制的中性占位。PNG 的透明画布、纵横比应符合预期显示，不能据此保证所有附着/动画几何兼容。Product 没有角色 ApplicationIcon；使用 SDK 默认图标。首次公开源文件不包含中性图标二进制。

GIF 使用下表规范路径，WPF 资源键按 Link 归一化。原始规范路径存在时优先；仅规范路径不存在时才使用列出的 Action 别名。没有别名的条目必须按规范准备。清单为 34 GIF + 1 可选 PNG，来自实际项目 Resource 声明：

| 规范路径/资源键 | 次选 Action 输入 |
| --- | --- |
| `assets/default_pet.png` | 无 |
| `NecoPetMaterials/idle/sway.gif` | `NecoPetMaterials/Action/idle/sway.gif` |
| `NecoPetMaterials/idle/base_idle.gif` | `NecoPetMaterials/Action/idle/base_idle.gif` |
| `NecoPetMaterials/idle/loitering.gif` | 无 |
| `NecoPetMaterials/idle/stand.gif` | `NecoPetMaterials/Action/idle/stand.gif` |
| `NecoPetMaterials/idle/hang_on.gif` | `NecoPetMaterials/Action/idle/hang_on.gif` |
| `NecoPetMaterials/idle/watch_phone.gif` | `NecoPetMaterials/Action/idle/watch_phone.gif` |
| `NecoPetMaterials/idle/goblet.gif` | `NecoPetMaterials/Action/idle/goblet.gif` |
| `NecoPetMaterials/idle/laugh.gif` | `NecoPetMaterials/Action/idle/laugh.gif` |
| `NecoPetMaterials/rest/prone_prop_dangle.gif` | `NecoPetMaterials/Action/rest/prone_prop_dangle.gif` |
| `NecoPetMaterials/rest/lie_down.gif` | `NecoPetMaterials/Action/rest/lie_down.gif` |
| `NecoPetMaterials/sleep/slumber.gif` | `NecoPetMaterials/Action/sleep/slumber.gif` |
| `NecoPetMaterials/sleep/stretch.gif` | `NecoPetMaterials/Action/sleep/stretch.gif` |
| `NecoPetMaterials/sleep/startle_awake.gif` | `NecoPetMaterials/Action/sleep/startle_awake.gif` |
| `NecoPetMaterials/sleep/grouchy.gif` | `NecoPetMaterials/Action/sleep/grouchy.gif` |
| `NecoPetMaterials/drag/dragged.gif` | `NecoPetMaterials/Action/drag/dragged.gif` |
| `NecoPetMaterials/drag/scruff.gif` | 无 |
| `NecoPetMaterials/drag/throw_away.gif` | 无 |
| `NecoPetMaterials/drag/throw_fly_left.gif` | 无 |
| `NecoPetMaterials/click/flick.gif` | `NecoPetMaterials/Action/click/flick.gif` |
| `NecoPetMaterials/click/pound.gif` | `NecoPetMaterials/Action/click/pound.gif` |
| `NecoPetMaterials/click/quick_punch.gif` | `NecoPetMaterials/Action/click/quick_punch.gif` |
| `NecoPetMaterials/click/rocket.gif` | `NecoPetMaterials/Action/click/rocket.gif` |
| `NecoPetMaterials/click/rocket2.gif` | `NecoPetMaterials/Action/click/rocket2.gif` |
| `NecoPetMaterials/click/rotation.gif` | `NecoPetMaterials/Action/click/rotation.gif` |
| `NecoPetMaterials/angry/caper.gif` | `NecoPetMaterials/Action/angry/caper.gif` |
| `NecoPetMaterials/angry/angry1.gif` | `NecoPetMaterials/Action/angry/angry1.gif` |
| `NecoPetMaterials/fall/falling_down2.gif` | `NecoPetMaterials/Action/fall/falling_down2.gif` |
| `NecoPetMaterials/fall/falling_down.gif` | `NecoPetMaterials/Action/fall/falling_down.gif` |
| `NecoPetMaterials/fall/landing.gif` | `NecoPetMaterials/Action/fall/landing.gif` |
| `NecoPetMaterials/special/fall_down.gif` | 无 |
| `NecoPetMaterials/move/lope.gif` | `NecoPetMaterials/Action/move/lope.gif` |
| `NecoPetMaterials/move/trot_relaxed.gif` | `NecoPetMaterials/Action/move/trot_relaxed.gif` |
| `NecoPetMaterials/move/run_excited.gif` | `NecoPetMaterials/Action/move/run_excited.gif` |
| `NecoPetMaterials/move/sprint_panic.gif` | `NecoPetMaterials/Action/move/sprint_panic.gif` |

GIF 经 assembly-qualified `pack://application:,,,/<AssemblyName>;component/<key>` 加载。必须在构建前把有权使用的输入放到对应目录并重新构建；放到已编译 EXE 旁不会自动增加嵌入资源。普通/Pilot/Product 的 AssemblyName 不同，由代码选择当前程序集。

缺少已校准的 base_idle 时尝试已校准的 sway，均缺失、未配置或不支持时普通版回退 PNG/原生占位；Product/Pilot 启动仍要求真实 idle GIF，缺失即 fail-closed 停止。动作素材缺失时已有可用性选择会禁用或拒绝相应表现，不代表该动作已验收。

优先使用同尺寸、透明、每帧独立完整画布的 GIF；各帧应能无残影循环。局部帧只有在解码器能证明透明基底且偏移/边界安全时才接受，不能假设所有 disposal/优化 GIF 都能自动合成。保留每帧 delay；解码实现会规范化异常/缺失时长。完整帧不等于所有动作可互换：循环、一次性、有限次数循环、held frame、loop/tail 范围分别由 GifPlaybackCursor、GifFramePlayer 与动作 session 控制。

几何校准现在仅从显式本地 Character Profile 读取；原逐资源缩放/偏移、规范尺寸、头部/身体注册、鞋/袖/侧点、预量轮廓、间接接地鞋点和侧覆盖校正已从公开通用源码移除，不提供原角色配置。详见 [配置说明](CHARACTER-PROFILE.md) 与 [逐参数迁移表](CHARACTER-PROFILE-MIGRATION.md)。中性例子是独立构造的矩形块数据，不代表实际游戏动作的测量值。

GIF 缺少配置条目或画布与配置不匹配时不可加载。没有所需接触点时依附/覆盖功能拒绝，不把占位图、全画布或旧角色点冒充有效接触。PMv2 原实时全帧 alpha union、DPI、RECT、覆盖/扫掠、所有权和安全停止规则继续保留；Profile 不能覆盖安全阈值。原 Yone 派生吸附算法及设计策略保留原位置和 MIT 来源通知。SideAttachmentDetectionProfile 与 DragVisualPlacement 不含本次需迁出的测量数据，保持原字节。

使用自己的合法素材须重新标定源画布、visual、支撑点、全动作轮廓并验证帧范围和完成事件。自有素材的测量和公开配置也应记录来源与许可；删除原测量不消除角色名称、资源键、语音池文件名或游戏文本的其他权利边界。项目不提供素材获取、镜像或下载工具。原 LICENSE/上游 NOTICE 的完整文本及 ZINC 修改署名保持不变，不把配置系统的存在当作发布授权。

## WAV 和运行时部署

WAV 通过现有一个共享 PilotVoicePlayer / WPF MediaPlayer 播放，不嵌入程序集。建议本地可解码的 PCM WAV；当前代码检查本地绝对路径和 `.wav` 扩展名，实际编码支持由 Windows 解码器决定，并没有保证任意采样率/声道组合。失败必须按播放失败/静音处理，不能标作听音通过。

动作根为 `AppContext.BaseDirectory/Voice`；开发时下列确切文件若存在由项目 Content 复制到输出，CopyToPublishDirectory=Never。没有发行器帮你补齐音频，`dotnet publish` 也不能被理解为打包了 Voice。动作池与相对路径如下（27 池，63 个条目）：

| 动作目录 | 文件名 |
| --- | --- |
| `Voice/flick/` | `nec00_2104_a.wav`, `nec00_2104_b.wav`, `nec00_2124.wav` |
| `Voice/pound/` | `nec00_2005.wav` |
| `Voice/quick_punch/` | `nec00_2121.wav`, `nec00_3071.wav`, `nec00_3072.wav` |
| `Voice/rotation/` | `nec00_3031.wav`, `nec00_3032.wav` |
| `Voice/caper/` | `nec00_2610.wav`, `nec00_2611.wav` |
| `Voice/angry1/` | `nec00_2021.wav`, `nec00_2123.wav`, `nec00_2200.wav`, `nec00_2207.wav`, `nec00_2706.wav` |
| `Voice/rocket/` | `nec00_3111.wav`, `nec00_3112.wav`, `nec00_3115.wav`, `nec00_3116.wav`, `nec00_3120.wav`, `nec00_3121.wav` |
| `Voice/watch_phone/` | `nec00_3057.wav`, `nec00_3058.wav`, `nec00_3060.wav`, `nec00_3064.wav`, `nec00_3065.wav`, `nec00_3066.wav` |
| `Voice/goblet/` | `nec00_2036.wav`, `nec00_3083.wav` |
| `Voice/laugh/` | `nec00_3031.wav` |
| `Voice/falling_down/` | `nec00_2109.wav`, `nec00_2115.wav` |
| `Voice/landing/` | `nec00_2031.wav`, `nec00_2033.wav` |
| `Voice/dragged/` | `nec00_2301.wav` |
| `Voice/scruff/` | `nec00_2204.wav` |
| `Voice/throw_away/` | `nec00_2610.wav`, `nec00_2611.wav` |
| `Voice/lope/` | `nec00_2002.wav`, `nec00_2505.wav`, `nec00_2506.wav` |
| `Voice/trot_relaxed/` | `nec00_2001.wav`, `nec00_2700.wav` |
| `Voice/run_excited/` | `nec00_2307.wav` |
| `Voice/sprint_panic/` | `nec00_2004.wav`, `nec00_2110.wav` |
| `Voice/stand/` | `nec00_2301.wav` |
| `Voice/loitering/` | `nec00_2003.wav`, `nec00_2034.wav`, `nec00_3056.wav` |
| `Voice/fall_down/` | `nec00_2600.wav`, `nec00_2601.wav`, `nec00_2604.wav` |
| `Voice/throw_fly_left/` | `nec00_2206.wav`, `nec00_2602.wav`, `nec00_2603.wav` |
| `Voice/rest/` | `chr019_00_4200.wav` |
| `Voice/stretch/` | `nec00_2301.wav`, `nec00_2407.wav` |
| `Voice/startle_awake/` | `nec00_2204.wav` |
| `Voice/grouchy/` | `nec00_2200.wav`, `nec00_2207.wav` |

启动池另需 `Voice/nec00_2001.wav`、`Voice/nec00_2125.wav`、`Voice/nec00_2301.wav`、`Voice/nec00_3056.wav` 四个平铺文件，齐全时按等权池选择。公共候选将启动查找限制为调用方显式本地绝对目录或 EXE 目录下的 Voice；不再读取旧 NECO_PILOT_VOICE_DIR/NECO_VOICE_ROOT，不扫描工作目录或父目录，避免暗中找到私有素材。动作池仍只使用 EXE 的 Voice，显式启动根不改变动作根。

Catalog 的 Approved/池名只是内部选择元数据，不是版权授权。语音设置仍走编辑→校验→应用→保存；0% 是有效静音会话。声音启动必须遵守已有视觉准入与 owner/session 生命周期，不能为替换素材绕过门禁。无 WAV 时相关表现静音/不可用，未进行人工听音及同步验收。
