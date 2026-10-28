# Character Profile 参数依赖与迁移边界

本表在改动生产源码前根据 P3/P4 审计和当前消费者整理。为避免再次传播未经确认的角色校准，本表只记录原符号与资源键，不重录测量数值。A=通用算法；B=Yone 派生算法/策略；C=角色测量、注册或人工视觉校准；D=通用策略和安全阈值。分类与旧 export manifest 的 A/B 筛选标签不同。

| 原参数或实现 | 类别 | 消费者 | 新字段或保留位置 | 缺失行为 |
| --- | --- | --- | --- | --- |
| AnimationVisualProfiles.CanonicalCanvasHeight；原头部/画布说明 | C | MainWindow.ApplyPetLayout；PilotVisualArrange；接触单位 | canonicalCanvas；headAnchor/bodyAnchor 为可选描述点 | 未验证配置不能加载 GIF；普通占位使用自身尺寸 |
| MainWindow.GifLayoutAspect | C | GIF 逻辑宽度、锚点、依附规划 | logicalAspect | GIF 不准入，不套用原角色比例 |
| CanonicalWidth/Height、Top/Left/RightContact | C | AttachmentGeometry.Contacts → 普通 AlignAttachedPet、PilotAttachmentContact | 每动画 contacts，按该动画归一化，不再隐式继承规范角色接触点 | 所需接触点缺失即拒绝依附或覆盖动作 |
| CanonicalRegistration 的 sway/普通注册、虚拟鞋点 | C | 旧 Contacts.Map | 显式 contacts；不保留虚拟角色注册；坐标必须在画布内 | 不自动由头框推导鞋点；需开发者自行重标定 |
| stand/hang_on/loitering 的鞋/袖/侧点 | C | TryPoseContacts；PMv2 姿态开始与跟随 | 三个资源各自 canvas、visual、contacts | 姿态缺点则不绑定依附、不改变原所有者 |
| flick/pound/quick_punch/rotation/caper/angry1 的支撑点 | C | 依附点击覆盖与对齐 | 各动画 contacts.top | 顶部覆盖缺点则拒绝；普通播放仍须 canvas/visual |
| sway/dragged/base_idle 的全帧 DetectionSourceBounds | C | GetAttachmentDetectionBounds → 普通释放检测 | 各动画 detectionBounds（源像素） | 返回无检测能力；不伪造透明轮廓或画布代替轮廓 |
| PilotSidePoseWallInset/BottomToContact | C | 侧依附点击覆盖、视觉规划、跟随 | 覆盖动画 sideOverlay（规范源单位，在变换后轮廓基础上应用） | 缺失时拒绝该覆盖，保留已准入的依附姿态 |
| PilotMotionSourceContact 的两个下落/三个点击分支及画布底边默认值 | C | PilotMotionContactDip → 旧布局接地；紧致全帧布局原有分支保持 | 各动画 contacts.top；不再使用画布底边隐式鞋点 | 无点返回无接触，不提交基于伪造点的校正 |
| Detect/CandidateScore/Align 中上游吸附范围、评分与 inset | B/D | 普通与 PMv2 依附 | AttachmentContactProfile 原策略常量保留；MIT 通知不变 | 非角色配置，不允许外部覆盖 |
| TopContactOffsetFactor、吸附距离等后续策略调参 | D（非已证实素材测量） | Detect/Align | 原位置保留；不是素材坐标 | 非配置项 |
| SideAttachmentDetectionProfile 全部阈值与舍入容差 | D | Detect/阈值显示 | 原文件逐字节保留 | 非配置项 |
| TransformPoint、归一化、有限值检查、contact 映射 | A/B | 布局与接触消费者 | 通用计算保留，使用配置单位；TryContact 显式失败 | 不输出非有限窗口目标 |
| Pilot/DragVisualPlacement 的覆盖/可达/扫掠/最近点与有限搜索限制 | A/D | PMv2 拖拽准入 | 原文件逐字节保留 | 原拒绝逻辑不变 |
| GIF 解码、全帧实时 alpha union | A | PMv2 视觉全足迹/工作区安全 | 原算法保留；canvas 与实际 GIF 必须匹配 | 配置不能覆盖实际透明轮廓与安全门禁 |
| 核心 RECT、DPI、2px 容差、所有权/代际、工作区并集、安全停止 | A/D | 所有 PMv2 模块 | 原核心与三个冻结文件保留 | 配置不能关闭、放宽或替换 |

以下为原 31 项视觉注册逐项迁移。每行只迁移角色校准，保留动作资源键作为兼容槽位。

| 原注册键 | 类别 | 消费者 | 新字段 | 缺失行为 |
| --- | --- | --- | --- | --- |
| `NecoPetMaterials/idle/base_idle.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/idle/base_idle.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/idle/sway.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/idle/sway.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/idle/watch_phone.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/idle/watch_phone.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/idle/goblet.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/idle/goblet.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/idle/laugh.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/idle/laugh.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/rest/prone_prop_dangle.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/rest/prone_prop_dangle.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/rest/lie_down.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/rest/lie_down.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/sleep/slumber.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/sleep/slumber.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/sleep/stretch.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/sleep/stretch.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/sleep/startle_awake.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/sleep/startle_awake.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/sleep/grouchy.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/sleep/grouchy.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/move/lope.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/move/lope.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/move/trot_relaxed.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/move/trot_relaxed.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/move/run_excited.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/move/run_excited.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/move/sprint_panic.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/move/sprint_panic.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/drag/dragged.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/drag/dragged.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/drag/scruff.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/drag/scruff.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/drag/throw_away.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/drag/throw_away.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/drag/throw_fly_left.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/drag/throw_fly_left.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/special/fall_down.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/special/fall_down.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/click/flick.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/click/flick.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/click/pound.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/click/pound.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/click/quick_punch.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/click/quick_punch.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/click/rocket.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/click/rocket.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/click/rocket2.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/click/rocket2.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/click/rotation.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/click/rotation.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/angry/caper.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/angry/caper.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/angry/angry1.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/angry/angry1.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/fall/falling_down.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/fall/falling_down.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/fall/falling_down2.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/fall/falling_down2.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |
| `NecoPetMaterials/fall/landing.gif` | C | AnimationVisualProfiles.For → ApplyAnimationVisualProfile → 普通/PMv2 布局 | `animations["NecoPetMaterials/fall/landing.gif"].canvas/visual` | 该 GIF 不可缓存/切换；不默用中性校准 |

31 项不是全部动作槽位。原 stand、loitering、hang_on 借用过 Neutral 视觉，现在也必须显式提供 canvas/visual。中性例子覆盖全部 34 槽位；这不等于任何合法素材可直接互换。

依赖方向：显式本地 JSON → 严格验证 → 不可变进程快照 → GIF 资源/画布准入 → 视觉单位和显式接触能力 → 既有布局/依附事务 → 既有 DPI/所有权/覆盖/原生写入/后校验。配置不持有 HWND、计时器、物理会话或安全阈值。

残留角色/动作假设：程序集与资源目录名称、固定动作槽位、方向/镜像约定、循环/尾帧/范围和完成事件、语音文件名及文案继续沿用，属于后续动作目录/播放协议边界。本阶段不声称任意角色插件化。原素材测量表、原注册注释和隐式角色接触回退不进入新公开配置或 fixture。
