# Character Profile v1

本阶段建立角色几何配置边界，尚不是任意角色即插即用系统。示例仅描述独立构造的矩形块，不包含图片、动画或声音，也不适用于原游戏素材。原角色视觉注册、接触点、透明轮廓和间接鞋点已从公开通用源码移除。逐项来源/消费者映射见 [迁移表](CHARACTER-PROFILE-MIGRATION.md)。

## 指定与生命周期

```powershell
$env:NECO_CHARACTER_PROFILE = (Resolve-Path './examples/character-profile.neutral.json').Path
& ./bin/Release/net10.0-windows/NecoArcDesktopPet.exe
# 清除显式选择；再次启动使用中性占位：
Remove-Item Env:NECO_CHARACTER_PROFILE
```

选择值必须是明确的本地绝对 JSON 文件路径。没有默认配置发现，不遍历当前目录、父目录或网络；不接受 UNC、网络驱动器、符号链接/目录联接路径。文件从指定位置读取，不加载插件或执行脚本。与设置目录分别配置，不读完整私有版的配置。异常返回 Missing、InvalidPath、ReadFailed、InvalidJson、UnsupportedVersion 或 InvalidProfile，以及明确诊断；成功为 Valid。

Windows 文件准入以实际打开对象为依据：先将盘符根解析为本地卷 GUID，再从卷根逐级打开目录和最终文件。每一级使用 `FILE_FLAG_OPEN_REPARSE_POINT`，检查句柄属性和 `GetFinalPathNameByHandle(VOLUME_NAME_GUID)` 的最终路径；全部目录句柄以不共享写入/删除的方式保留到读取结束。最终 JSON 从同一个已验证文件句柄读取，并在读取过程中再次限制 1 MiB；不会验证一个文件后按路径重开另一个文件。任何打开、属性、最终路径或共享冲突均安全拒绝。依据为 Windows [CreateFile](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-createfilew)、[本地卷标识](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getvolumenameforvolumemountpointw) 和 [句柄最终路径](https://learn.microsoft.com/en-us/windows/win32/api/fileapi/nf-fileapi-getfinalpathnamebyhandlew) 规则。

这是本地 Windows 文件系统的路径准入，不是文件来源认证或权限提升。它拒绝 Junction、Symbolic Link、其他重解析点及子目录卷挂载；无法解析为本地卷或无法核验最终路径的盘符别名、短名称等也可能拒绝。读取仍使用进程原有访问权限，不启用额外特权。它不认证文件作者、硬链接的其他名称，也不承诺阻止获取保护句柄之前已完成的普通文件替换；管理员/内核、恶意文件系统驱动及预先存在的可写内存映射不在此命名空间竞态保证内。通过 schema 不证明内容或素材许可可信；不支持的文件系统行为应以拒绝或独立未验收记录处理。

第一次资源/配置访问固定整个进程的不可变快照，包括失败结果。修改文件或环境变量只在重新启动后生效；没有热更新。整个文件验证通过后才创建强类型记录和 FrozenDictionary；某个动作数据损坏会拒绝整个文件，不会混合新旧参数。直接加载 API 可用于离线检查多个版本，但不会替换运行中快照。

## 格式与单位

根字段只有 `schemaVersion`（目前必须是整数 1）、`id`（非空，最多 80 字符）、`canonicalCanvas`、`logicalAspect`、`animations`。属性名称大小写严格；未知字段、重复字段及大小写冲突资源键全部拒绝。不能加入 safetyStop、DPI、RECT 容差、工作区或物理所有权选项。

| 字段 | 含义与约束 | 缺失时 |
| --- | --- | --- |
| canonicalCanvas.width/height | 角色的统一源像素单位；整数，1–16384 | 整个配置无效 |
| logicalAspect | 逻辑宽度/用户逻辑高度；有限值，0.01–100 | 整个配置无效 |
| animations | 1–256 项，完整资源键到动画校准映射 | 整个配置无效 |
| 每动画 canvas.width/height | 实际 GIF 逻辑画布整数尺寸，1–16384；加载时必须与文件头匹配 | 整个配置无效；不匹配的 GIF 不准入 |
| visual.scale | 正有限比例，0.001–100 | 整个配置无效 |
| visual.offsetXFactor/offsetYFactor | 以用户逻辑高度为单位的视觉平移；有限值，-100–100 | 整个配置无效 |
| contacts.top/left/right | 可选归一化源点 `{x,y}`，两坐标都在 [0,1]；top 是支撑点，左右是侧接触点 | 相关接触能力不可用；不生成原角色默认点 |
| detectionBounds | 可选全动作透明轮廓包围框，源像素 `{x,y,width,height}`；非空且在本动画画布内 | 普通模式不进行依附释放检测；不把画布冒充轮廓 |
| sideOverlay | 可选 `{wallInset,bottomToContact}`；有限非负，分别不超过画布半宽/高度 | 侧边依附覆盖动作不可用 |
| headAnchor/bodyAnchor | 可选归一化源点，必须在画布内 | 不影响准入；目前仅为描述/开发者校准资料，运行代码不据此推导接触 |

所有数字拒绝 NaN、Infinity、溢出、字符串伪装的数字、负尺寸和越界坐标。JSON 最大 1 MiB、深度 24。正向字段白名单不允许配置修改核心安全策略。表示范围内的配置仍可能不适合某个动作或工作区，原布局/完整足迹/原生后校验继续具有最终拒绝权；通过解析不等于运行适配成功。

资源键必须形如 `NecoPetMaterials/idle/base_idle.gif`，使用 `/`，不允许绝对路径、URI、查询参数、`.`/`..` 或其他目录逃逸。现有代码以 34 个兼容动作槽位引用这些键：原 31 项视觉注册，加 stand、loitering、hang_on。允许配置额外合规键不代表现有动作调度器会调用它；新行为仍需维护动作目录和状态机。名字不授予对应素材的使用权。

源单位为 `用户高度 / canonicalCanvas.height`。先按该单位安排本动画画布，再应用 visual 缩放/偏移和既有 RenderTransformOrigin/镜像。归一化接触点乘以本动画画布得到源像素，然后通过同一变换映射到当前图像局部坐标。左侧依附选变换后更靠屏幕右的侧点，右侧依附选更靠屏幕左的侧点；两侧能力都需要明确左右点。坐标在未镜像画布内编写，运行时只变换一次，不自动插入角色特定的二次反射。顶部不推导鞋点；缩小画布后原来的虚拟画布外点必须重新校准为合法接触。

sideOverlay 的校正以**规范源单位**计：在变换后的实际全帧 alpha union 上，从左右相应边内收 wallInset、从底边向上 bottomToContact，再乘 `用户高度 / canonicalCanvas.height`。校正不会再乘 visual.scale。结果必须在变换后的包围框内且有限。每个覆盖动画必须自行声明该数据，不从侧待机动画复制。没有合法校正时保留原依附姿态，拒绝覆盖动作；顶部覆盖必须有该覆盖动画的 contacts.top。

## 接入开发者自己的合法素材

1. 创建自己的配置；测量实际 GIF 逻辑画布及所有播放帧。头部/身体视觉锚点供校准参考，鞋、袖和侧边支撑点应人工验证，不能只取 alpha 最低点。
2. 给待机、移动、拖拽、休息、睡眠、点击、特殊/下落等每个使用槽位填写 canvas/visual。示例所有槽位使用同一矩形块参数，只用于理解结构和合成检查，绝不是这些动作的实际标定。
3. 需要依附时配置对应待机、stand、loitering 或覆盖动画的接触点；普通模式还需释放动画 detectionBounds。PMv2 的 HWND 足迹继续来自实际解码帧的全帧 alpha union，JSON 轮廓不能替代它。
4. 按 [ASSETS](ASSETS.md) 的确切资源键在**构建前**提供自己的 GIF，重新构建；仅把 GIF 放到 EXE 旁不会被发现。现有 pack URI 从当前程序集加载，Profile 不改变嵌入规则、不引用远程素材。
5. WAV 仍为 EXE 的 Voice 目录下的外部文件，通过共享 WPF MediaPlayer 播放，不嵌入 GIF 程序集；启动语音的显式本地根规则和现有池文件名保持。Profile 不重写音频池，也不下载音频。
6. 分别验证 ordinary、Product、Pilot。真实待机、动作帧序列、尾循环/帧范围、镜像方向、完成回调、支撑点、透明轮廓、鼠标、声音以及多屏/混合 DPI 需要独立验收。配置只覆盖几何边界，不自动改写所有动作语义。

没有选择合法配置、配置损坏、缺条目或画布不匹配的 GIF 不能创建/缓存/切换。普通版仍可显示中性占位、打开/关闭工作台并保存隔离设置。Product/Pilot 保持真实 idle GIF 与合法布局准入；没有可加载且校准匹配的 idle，在原生布局之前安全停止。只有视觉标定但缺接触的 GIF 可用于普通显示/既有安全视觉流程，但不能进入所缺接触的依附或覆盖动作。

测试入口为 `scripts/verify-source-only.ps1 -DesktopSmoke`。其中合成配置检查使用 WPF 原生绘制的矩形 GIF，在 `.local` 的独立源码副本中嵌入和解码；该副本、媒体、日志、设置和含素材程序集不在公开白名单内。它验证能力拒绝与原门禁，不能替代合法真实角色的完整运行验收。
