# 更新日志 / Changelog

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。
版本号唯一来源为 `Community.PowerToys.Run.Plugin.Snip/plugin.json` 的 `Version`；发布时推送 `v<Version>` 标签，CI 会把安装程序与 zip 附加到 GitHub Release。

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).
The single source of the version is `Version` in `plugin.json`; pushing a `v<Version>` tag attaches the installers and zips to a GitHub Release.

## [Unreleased]

### 修复 / Fixed

- 模型下载窗口可最小化后台运行：OCR 资源与 Hy-MT 翻译模型下载窗此前强制 `Topmost` 置顶且不在任务栏显示——永远悬浮在所有窗口之上，最小化后也无从找回。现在下载窗不再置顶，最小化到后台下载继续进行，任务栏留有入口可随时恢复查看进度 — Model download windows can now be minimized to run in the background: they were forced topmost (floating above every window) and hidden from the taskbar, so minimizing stranded them. They no longer stay on top and leave a taskbar entry to restore (@Jotaro2025)
- 图片翻译中途“卡住”（扫描线/闪光不动、进度条不动）：HyMT2Sharp 在调用线程上同步加载 GGUF 并逐 token 推理，结果窗现在跑在宿主 UI 线程上，整个模型加载 + 生成期间把 Dispatcher 堵死；模型闲置卸载（默认 10 分钟）后的下一次翻译最明显。翻译调用改为 `Task.Run` 后台执行，进度回调仍经 `Progress<T>` 回到 UI 线程 — The busy animation and progress bar froze mid-translate because HyMT2Sharp loads the GGUF and runs inference synchronously on the calling thread, which is the host UI dispatcher now that the result window runs there (worst after the 10-minute idle model unload). The translate call now runs on a pool thread; progress still marshals back (@Jotaro2025)
- 图片翻译留下的零散“黑点”和灰色涂抹：根因在擦除而不是贴字 —— OCR 框贴得比字形紧，字符的上/下伸部露在擦除框外，既原样留成黑点，又被 inpaint 从框边采样后拖进框内变成灰涂抹（用「只擦不画」实验在真实截图上复现）。现在擦除前按实际墨迹逐行自适应扩框（每边最多 6 px 或 40% 框高），并对行内代码的浅色底纹额外覆盖 2 行；顺带修掉扩框循环因 `top` 逐轮变化而跳行的 bug（4 个新单测） — Stray "black dots" and grey smears after image translation came from the erase step, not the glyph drawing: OCR boxes hug the x-height, so ascender/descender strokes stayed outside the erase rect (as dots) and were sampled by the inpaint and dragged inward (as smears). The paint box now grows row by row over real ink (≤ 6 px or 40% of the box height per side) plus two rows for inline-code tints (@Jotaro2025)
- 图片翻译结果里的零散“黑点”：OCR 会把同一视觉行内的行内代码/标点/单字切成独立的小框，渲染器逐框翻译贴字后，每个碎片都会在对应位置留下 1–2 个字的小字块。现在翻译前先按视觉行合并（同行合并、纯标点噪声丢弃、重复框去重），整行原文擦除更完整，译文也按整句送出（`TranslateLineMerger`，8 个单测） — Stray "black dots" in translated images: OCR reports inline-code / punctuation / single-character fragments as separate boxes on the same visual line, and painting each fragment's translation left 1–2 character blobs. Fragments are now merged into one box per visual line (punctuation-only noise dropped, duplicates collapsed) before translation and erase (@Jotaro2025)
- 图片翻译/文字提取结果窗跨线程崩溃：结果窗原本建在插件自建的截图 STA 上，运行期动态添加的 TextBox 会去应用宿主（PowerToys Run）线程创建的样式，样式 setter 里未冻结的 Freezable（画刷）一 seal 就抛 `VerifyAccess`（「调用线程无法访问此对象，因为另一个线程拥有该对象」），整个翻译流程在“构建译文编辑框”时失败。现在结果窗和设置窗口一样在宿主的 WPF Dispatcher 上创建（覆盖层隐藏/恢复与关闭改为跨线程调度），译文编辑框另加本地样式兜底；截图位图在交给宿主线程前先在截图线程上 `MaterializeFrozen` 成冻结副本（未冻结的位图连 `IsFrozen` 都会抛同样的异常） — Result windows (image translate / OCR) now open on PowerToys Run's WPF dispatcher instead of the snipping STA: dynamically added TextBoxes there applied host-thread styles whose unfrozen Freezables threw VerifyAccess and aborted the translate pipeline. The capture bitmap is materialized into a frozen copy on the snip thread first, since an unfrozen bitmap throws on plain `IsFrozen` access from the host thread (@Jotaro2025)
- 图片翻译结果整片空白：当拟合出的字号恰好落在 OCR 框高度边界上时，`FormattedText` 的 `MaxTextHeight` 会把整行裁掉（宽高变 0），于是原文被擦除、译文一个字都没画。现在绘制不再按框高裁剪，字号/换行在渲染前只计算一次；译文比原文长时擦除框按需向下（贴边时向上）扩展，最多 +40 px，避免长译文的尾部被静默丢弃 — Image translate no longer comes out blank: a fitted font size landing on the box-height boundary made FormattedText clip the whole line to 0×0, erasing the source and painting nothing. Drawing no longer clips to the box height, layout is computed once, and the erase box grows (down, or up at the image edge, max +40 px) so long translations keep their tail (@Jotaro2025)
- HDR 显示器截图发白：8 位 BGRA 捕获会把按 SDR 白电平放大的合成值直接剪辑，导致颜色被冲淡。现在 HDR 显示器改用 FP16 scRGB 捕获，并按 `DISPLAYCONFIG_SDR_WHITE_LEVEL` 做 HDR→SDR 色调映射；矩形/滚动分片/GIF/多屏合并同样走该路径；WGC 改为原生 COM 实现，修复宿主内 CsWinRT 重复运行时导致的 `Attempt to update previously set global instance` 抓屏失败 — Washed-out screenshots on HDR displays are fixed by FP16 scRGB capture + SDR white-level tone mapping; rect/scroll/GIF/multi-monitor paths share it, and a raw-COM WGC implementation no longer fails inside the PowerToys host (@Jotaro2025)
- 自动保存目录校验：设置中的保存目录含非法字符（如 `C:\Sho<ts`）、相对路径或孤立盘符冒号时回退默认目录——此前每次自动保存都以原始异常消息失败 — Invalid / relative save directories now fall back to the default folder instead of failing every auto-save (@Jotaro2025)
- GIF 录制中直接取消/关闭截屏窗（Esc 两连、Alt+F4、确认路径）后台捕获循环不再残留：此前循环会继续抓屏直到 300 帧上限，且 CTS 永不释放 — GIF recording loop now stops when the overlay closes or cancels mid-recording (@Jotaro2025)
- GIF 会话 `Dispose` 语义修正：先 Cancel 再 Dispose（原实现只 Dispose 不 Cancel，挂起的 `Task.Delay` 会以 ObjectDisposedException 风暴空转到底），循环改用缓存 CancellationToken 避免 Dispose 后访问 `.Token` 崩溃 — GIF session Dispose now cancels first and uses a cached token (@Jotaro2025)

### 新增 / Added

- 腾讯翻译（TMT）实现已内置但**默认从界面隐藏**（实验性，暂不提供设置项）：`Services/TencentTranslateTranslator.cs` 走 `TextTranslate` + TC3-HMAC-SHA256，Key/地域设置与语言映射齐备；启用只需在 `Main.AdditionalOptions` 与设置窗口恢复该提供商项 — Tencent TMT translator is implemented but hidden from the UI for now (experimental): `TencentTranslateTranslator` (TC3-HMAC-SHA256 `TextTranslate`), settings and language mapping are in place; re-enabling means restoring the provider item in `Main.AdditionalOptions` and the settings window (@Jotaro2025)
- 小图 OCR 增强：短边 < 128 px 的小选区在识别前 2× 放大（上限 1024 px），两个引擎统一生效；识别框/角点按比例映射回原图坐标，文本提取、图片翻译渲染与导出几何全部对齐原图 — Small-image OCR: 2× upscale before recognition with boxes mapped back to source space (@Jotaro2025)
- OCR 结果窗快捷键：Ctrl+C 复制全文（无选区时）、Ctrl+E 导出；复制/导出状态 2 秒后自动清除 — OCR result window: Ctrl+C / Ctrl+E shortcuts, transient status (@Jotaro2025)
- 「复制后通知」开关（默认关闭）：截屏复制到剪贴板后不再弹 PowerToys Run 通知，需要时在插件设置勾选 — Optional notify-after-copy toggle, off by default (@Jotaro2025)
- QR 多码识别：整图解码失败时按左/右半（含重叠）递归分半解码，并排/堆叠多码可全部解出（数量与深度有上限、结果去重） — Multi-QR collection via recursive half splits (@Jotaro2025)
- OCR 结果窗新增「导出…」：行级数据导出 Markdown 表格或 JSON（含 box/角点几何），`OcrExport` 纯函数渲染 — OCR export to Markdown / JSON with geometry (@Jotaro2025)
- 区域 GIF 录屏：工具栏录制按钮 + Esc/再点停止，选区逐帧捕获经自研 GIF89a 编码器（median-cut 调色板 + LZW，零依赖）保存 — Region GIF recording with a zero-dependency GIF89a encoder (@Jotaro2025)

### 变更 / Changed

- 设置窗口改版：按功能分组卡片（界面 / 截图与保存 / 滚动截屏 / 文字识别 / 图片翻译 + 引擎卡片）、统一字段间距与控件高度、圆角主次按钮与页脚分隔线、Tab 头留白、窗口默认 640×680；引擎卡片内不再重复「HTTP · / HyMT · / 翻译 ·」标签前缀 — Redesigned settings window: grouped cards, uniform field spacing and control heights, rounded primary/secondary buttons with a footer separator, inset tabs, 640×680 default size, and engine-prefix labels dropped inside their cards (@Jotaro2025)
- 钉图窗复制/保存后显示自消失状态徽标（成功绿色/失败红色，2 秒或点击后消失，移动/缩放跟随）——此前操作完全静默，失败无感知 — Pinned image window now shows a transient status pill after copy/save (@Jotaro2025)
- 工具栏马赛克工具 tooltip 随点击循环显示当前效果（像素化/高斯模糊/纯色涂抹）与下一步切换目标 — Mosaic toolbar tooltip now shows the active effect while cycling (@Jotaro2025)

### 性能 / Performance

- 马赛克块平均向量化（byte→ushort→uint 两次 Widen + BGRA 通道掩码，兼容 AVX-512），块填充改 `Span<uint>.Fill` — Vectorized mosaic block averaging (@Jotaro2025)
- 滚动重复帧检测增加失配早退：明显不同的帧不再扫全像素 — Early-exit in duplicate-tile matching (@Jotaro2025)
- QR 二值化阈值趟向量化（`Vector.LessThan` 掩码） — Vectorized QR threshold pass (@Jotaro2025)

### 代码质量 / Code Quality

- 滚轮投递（SendInput→UIA 回退 + 指针驻留）从 ScrollCaptureController 提取为 `ScrollWheelDriver` — Extracted ScrollWheelDriver from ScrollCaptureController (@Jotaro2025)
- 静默 catch 全量审计：9 处 best-effort 清理补意图注释；取消/关闭竞态的捕获顺序验证无吞没 — Silent-catch audit with intent comments (@Jotaro2025)
- 翻译渲染器字体回退审计：复合字体族 + 缺字回退路径验证一致，补防回归单测 — Font fallback audit with regression test (@Jotaro2025)

## [1.6.0] - 2026-10-25

### 新增 / Added

- 首次截屏预热：插件初始化后闲时预建截屏专用 STA 线程（Dispatcher + DPI 提升 + WPF Application），首次 Alt+A 不再付线程冷启动成本 — Prewarm the snip STA after plugin init so the first capture skips thread bootstrap (@Jotaro2025)
- 首次截屏预热（续）：同链预热 WinRT 捕获的 D3D 设备（适配器枚举 + 设备创建），首次整屏/窗口捕获再省一段 — Also prewarm the WinRT capture D3D device in the same prewarm chain (@Jotaro2025)
- WeChatOCR.exe 闲置自动回收：与 HyMT 模型共用同一「闲置 N 分钟卸载」设置（默认 10，0 关闭），OCR 子进程不再常驻 — Idle auto-recycle for the WeChatOCR.exe child process, sharing the HyMT idle-unload setting (@Jotaro2025)

### 变更 / Changed

- 安装程序改用 `dotnet publish` + ReadyToRun：插件 DLL 预编译为本机码，PowerToys 加载时省去 JIT（体积约 +120%，仅安装包路径；zip 路径不变）— Installer builds are now R2R-precompiled via publish (@Jotaro2025)
- 选区边框光晕由 DropShadowEffect 改为双层半透明边框：拖拽选区不再跑每帧 shader — Selection glow via stacked translucent borders instead of a per-frame DropShadow shader (@Jotaro2025)
- Win32 P/Invoke 迁移至 source-generated `LibraryImport`（消除运行时 marshal stub）；StringBuilder / 委托回调 / ByValTStr 结构三处保留经典 DllImport — Win32 P/Invokes migrated to source-generated LibraryImport (@Jotaro2025)
- SnipWindow 拆分：滚动实时预览提取为 `ScrollLivePreview`、放大镜 HUD 提取为 `MagnifierHud`（2227 → 2007 行，行为不变）— SnipWindow split: extract ScrollLivePreview and MagnifierHud (@Jotaro2025)
- 导出合成提取为 `SnipExportComposer`（选区裁剪 + 标注渲染纯函数化），新增 3 个 STA 单测覆盖物理像素映射与边界钳制 — Extract SnipExportComposer with STA unit tests (@Jotaro2025)
- `SnipVerbose.Scroll` 日志发射拆入 NoInlining 方法：JIT 不再强制加载 Wox.Plugin，无宿主环境（基准/子进程）调用不再 FileNotFoundException — Lazy Wox.Plugin load in SnipVerbose so benchmarks run hostless (@Jotaro2025)
- winget 1.5.0 清单就绪：真实 SHA256 与 Inno ProductCode，`winget validate` 通过，可 PR 到 winget-pkgs — winget 1.5.0 manifests with real hashes, validate-clean (@Jotaro2025)

## [1.5.0] - 2026-10-25

### 新增 / Added

- Run 入口支持活动窗口与全屏截图 — Window / full-screen capture entries in PowerToys Run (#77, @Jotaro2025)
- 截屏延迟可见倒计时浮层 — Visible countdown overlay for delayed capture (#73, @Jotaro2025)
- 标注：直线 / 半透明高亮 / 编号步骤 — Annotations: line, translucent highlight, numbered steps (#74, @Jotaro2025)
- 导出支持 JPEG / BMP（PNG 默认） — JPEG / BMP export with PNG default (#75, @Jotaro2025)
- 钉图滚轮缩放与透明度调节 — Pinned image wheel-zoom and opacity (#76, @Jotaro2025)
- Windows OCR 语言 Auto / 已安装列表 — Windows OCR language picker (Auto / installed) (#72, @Jotaro2025)
- 设置 HTTP API Key 改用 PasswordBox — PasswordBox for the HTTP API key (#70, @Jotaro2025)
- 可选 GitHub Releases 更新检查（默认关闭） — Optional update check against GitHub Releases, off by default (#79, @Jotaro2025)
- 捕获优先 WinRT Graphics Capture（GDI 回退） — Prefer WinRT Graphics Capture with GDI fallback (#80, @Jotaro2025)
- 滚动路径可选 Verbose 插件日志 — Optional verbose plugin logging for the scroll path (#82, @Jotaro2025)
- 滚动停止条帧数 / 暂停 / DPI 与 Esc 冲突提示 — Stop strip with frame count / pause; DPI & Esc conflict hints (#94, @Jotaro2025)
- SendInput 失败时回退 UIA ScrollPattern 滚动 — UIA ScrollPattern fallback when SendInput fails (#99, @Jotaro2025)
- 滚动截屏优先窗口捕获（CreateForWindow/PrintWindow），失败回退 hide+BitBlt — Prefer window capture for scrolling (#100, @Jotaro2025)
- 滚动截屏缓存帧间对齐秒出图、窗口捕获实时预览与固定侧边栏/底栏抗干扰 — Cached frame alignment for instant scroll preview; live window-capture preview; fixed side/bottom-bar immunity (#103, @Jotaro2025)
- 贴图置顶窗保留原图清晰度并支持复制 / 保存 / 1:1 重置 / 鼠标穿透 — Pin window keeps native resolution; copy / save / 1:1 reset / click-through (#104, @Jotaro2025)
- 标注体验打磨：画笔颜色 / 线宽持久化、序号防重、Shift 正交约束、高斯模糊 / 实心遮挡 — Annotation polish: persisted pen color / width, unique numbering, Shift ortho constraint, Gaussian blur / solid redaction (#105, @Jotaro2025)
- 图片翻译结果窗增强：原文 / 双语对照复制、一键贴图置顶、分片翻译局部容错 — Translate result window: source / bilingual copy, pin-to-top, per-chunk fault tolerance (#106, @Jotaro2025)
- 框选放大镜与零分配像素取色器（C / Shift+C 复制 HEX/RGB） — Selection magnifier with zero-alloc pixel color picker (#107, @Jotaro2025)
- 窗口与 UI Automation 子元素悬停自动吸附框选 — Hover snap to windows and UIA children (#108, @Jotaro2025)
- 二维码与条形码识别（一键复制及 HTTP/HTTPS 链接打开） — QR / barcode recognition with copy and link opening (#109, @Jotaro2025)
- 翻译进度增量上屏：忙碌态实时显示 n/m — Incremental translate progress (n/m) in the busy state (@Jotaro2025)

### 变更 / Changed

- 滚动相位相关改用仓库内 radix-2 FFT，移除 MathNet.Numerics 依赖（~1.6 MB） — Replace MathNet with in-repo radix-2 FFT for scroll phase correlation (#85, @Jotaro2025)
- winget 清单草稿（待 Release 资源与 SHA256） — winget manifest draft (#78, @Jotaro2025)
- 剔除 GlobalSuppressions 中 PowerToys 宿主专用项 — Drop PowerToys-host-only suppressions (#81, @Jotaro2025)

### 性能 / Performance

- AlphaBlendInto 使用 ArrayPool 临时缓冲 — ArrayPool scratch buffer for AlphaBlendInto (#67, @Jotaro2025)
- WinRT 捕获复用 FramePool，像素走 ArrayPool — FramePool reuse; ArrayPool pixels (#97, @Jotaro2025)
- ScrollWindowCapture 与 ScrollPhaseCorrelator 零分配 / ArrayPool 池化 — Zero-alloc / pooled scroll capture and phase correlation (#102, @Jotaro2025)
- 图像处理热路径算法加速（模糊 / 拼接 / QR / inpaint / 剪贴板） — Hot-path algorithm speedups (blur / stitch / QR / inpaint / clipboard) (@Jotaro2025)
- 注解画布渲染缓存与滚动采集吞吐优化 — Annotation canvas render cache; scroll capture throughput (@Jotaro2025)
- WeChat OCR 进程单例复用；Windows OCR 缩放合并进解码 transform — WeChatOCR process reuse; scaling merged into the decode transform (@Jotaro2025)
- UIA 滚动回退迁至常驻 MTA 工程线程，不再阻塞 UI STA — UIA scroll fallback on a resident MTA worker thread (@Jotaro2025)
- 捕获 item / session 跨 tile 复用 — Item / session reuse across tiles (@Jotaro2025)

### 修复 / Fixed

- 滚动截屏：#100 窗口捕获成功时遮罩保持可见，但 StartScrollCapture 仍 Opacity=0 打孔 → 选区持续白屏（拼接仍正常）；探测到窗口捕获后保留冻结预览、不打白洞，SW_HIDE 回退路径仍走 #87 隐藏+打孔 — Keep frozen selection preview on window-capture path (no white hole); SW_HIDE fallback still hides+punches (@Jotaro2025)

- 滚动截屏：会话内一次 SW_HIDE（消除每轮闪烁）；浮动「停止」条 + 全局 Esc/空格 可中途停止并保留已截帧（#89 后遮罩隐藏导致 Esc/工具条不可用）— Session-long overlay hide to cut flicker; floating Stop strip + global Esc/Space mid-stop keeping tiles (@Jotaro2025)

- 滚动截屏：每轮滚轮+截帧前 SW_HIDE 整个截屏窗（#88 日志 overlayCovers=False 仍 1 tile，洞/透明命中理论不足）；SW_SHOWNOACTIVATE 恢复以保持 Edge 前台；PostMessage 辅投 parent/root — Hide snip window for each wheel+capture cycle; restore without activating; PostMessage parent/root nudge (@Jotaro2025)

- 滚动截屏：SendInput 期间临时 WS_EX_TRANSPARENT 让滚轮命中 Edge；AttachThreadInput/SetForegroundWindow 激活目标；PostMessage 辅投；延长 settle；打孔前 Opacity+Render flush 并在 Begin 后复打孔 — Make SendInput hit Edge via temporary WS_EX_TRANSPARENT; activate underlay; PostMessage nudge; longer settle; anti-flash Opacity/Render + re-hole (@Jotaro2025)

- 滚动截屏：首轮左键聚焦 + 会话内光标保持选区中心（避免立刻还原导致 Edge 不滚）；放宽 duplicate auto-stop（阈值 4、至少 3 次成功滚轮）；StartScrollCapture 隐藏冻结截图再打孔，消除白闪 — Edge focus click + hold cursor; soften duplicate stop; hide frozen screenshot before hole to avoid white flash (@Jotaro2025)

- 滚动截屏对 Chromium/Edge：`SendMessage(WM_MOUSEWHEEL)` 虽返回 0 但页面不滚 → 相同 tile 立刻 auto-stop；改为优先 `SendInput(MOUSEEVENTF_WHEEL)`（选区 hole 内短暂移入光标后还原），失败再回退 SendMessage；保留 #84 下窗解析 — Prefer SendInput wheel so Edge/Chromium scroll; SendMessage fallback; keep underlay resolve from #84 (@Jotaro2025)

- 滚动截屏：白屏与 overlay 下滚轮命中失败导致立刻 auto-stop — Scroll-capture white screen; wheel hit-testing under the overlay (#84, @Jotaro2025)
- 滚动截屏：检测 UIPI / 提升目标并提示 — Detect UIPI / elevated targets and inform (#92, @Jotaro2025)
- 滚动会话结束统一先关停止条再恢复遮罩 — Close the stop strip before restoring the overlay at session end (#93, @Jotaro2025)
- 超高拼图 OCR 限高 / 分片并提示改用 Windows OCR — Cap OCR height for very tall stitches, chunk and hint Windows OCR (#95, @Jotaro2025)
- 串行化 WinRT Graphics Capture，失败时重建 D3D 设备 — Serialize WinRT capture; rebuild the D3D device on failure (#96, @Jotaro2025)
- 混合 DPI 下选区洞 / captureRect / 停止条比例换算 — Mixed-DPI scaling of the selection hole / captureRect / stop strip (#98, @Jotaro2025)
- 按窗捕获时保留选区冻结预览，避免白洞 — Keep the frozen preview on window capture (#101, @Jotaro2025)
- 附加选项 API Key 空框占位且留空保留 — AdditionalOptions API key placeholder; empty keeps the stored key (#71, @Jotaro2025)
- MouseMove 双注册去重、QR 扫描移出 UI 线程、HRGN 泄漏 — Deduplicate MouseMove registration; QR scan off the UI thread; HRGN leak (@Jotaro2025)
- WinRT 捕获 FrameArrived 泄漏退订 — Unsubscribe leaked FrameArrived handlers (@Jotaro2025)
- 放大镜十字线通道序、翻译字号不收缩、inpaint 环状伪影 — Magnifier crosshair channel order; translate font size; inpaint ringing artifacts (@Jotaro2025)
- 冻结级联导致放大镜崩溃与 D3D 预览灰屏 — Freeze() cascade crash in the magnifier; gray D3D preview (@Jotaro2025)
- DPI-unaware 宿主（PowerLauncher）下坐标虚拟化错位与 overlay 重复叠加 — Coordinate virtualization misalignment under a DPI-unaware host; duplicate overlay stacking (@Jotaro2025)
- 截屏专用 STA 未创建 WPF Application 时，系统主题切换经 ThemeManager → Application.Windows 跨线程 VerifyAccess，导致 PowerToys.PowerLauncher FATAL；STA 在 Dispatcher.Run 前创建 OnExplicitShutdown Application（若宿主尚无），否则安装 Dispatcher 异常过滤，插件 Dispose 时显式关闭自有 Application 而不关闭宿主 — Theme-change FATAL on snip STA (#83, @Jotaro2025)

### 测试与工程 / Tests & Engineering

- 修复 10 个从未绿过的存量测试（对齐方向 / 设置数 / 资源断言） — Fix 10 never-green legacy tests (@Jotaro2025)
- BenchmarkDotNet 基准项目覆盖图像处理热路径 — BenchmarkDotNet project for image hot paths (@Jotaro2025)
- 删除未使用资源 — Remove unused resources (#66, @Jotaro2025)

### 文档 / Docs

- e2e 清单：Edge 滚动 + 中途停止验收 — e2e checklist: Edge scroll & mid-stop (#91, @Jotaro2025)
- e2e 清单：DPAPI 与 OCR SHA 验收 — e2e checklist: DPAPI & OCR SHA (#68, @Jotaro2025)
- README 说明 OCR azure.cn 为哈希校验后的回退源 — README: azure.cn as hash-verified fallback (#69, @Jotaro2025)

## [1.4.0] - 2026-10-23


### 新增 / Added

- 安装程序：Inno Setup，x64 / ARM64 × slim / full 四个包；按用户安装无 UAC，检测 PowerToys ≥ 0.100，征得同意后关闭并重启 PowerToys，升级清理旧文件、保留数据，卸载可选删除数据 — Installer (x64/ARM64 × slim/full) (#58, @Jotaro2025)
- 滚动截屏：TDS 风格自动滚轮捕获与相位相关拼接 — Scroll capture with automatic wheel scrolling and stitching (#41, @Jotaro2025)
- 滚动截屏到底自动停止，停止后 Esc / 提示 UX — Auto-stop at the end of the page (#43, @Jotaro2025)
- 滚动间隔 / 最大帧数设置；可手动卸载 HyMT 模型 — Scroll interval / max-frame settings; unload HyMT model (#46, @Jotaro2025)
- 滚动拼接最大高度上限，达到即自动停止并提示 — Max stitched height with auto-stop (#50, @Jotaro2025)
- 向上滚动截屏入口；鼠标离开选区自动暂停 — Scroll-up capture; pause while the cursor leaves the selection (#56, @Jotaro2025)
- HyMT 模型闲置 N 分钟自动卸载（默认 10，0 关闭） — Idle auto-unload of the HyMT model (#52, @Jotaro2025)
- 设置页界面语言切换（跟随系统 / English / 简体中文） — UI language switch in settings (#38, @Jotaro2025)
- 截屏覆盖层右键取消（等同 Esc） — Right-click cancels the overlay (#40, @Jotaro2025)
- 译图忙态取消按钮 — Cancel button while translating an image (#47, @Jotaro2025)
- Windows.Media.Ocr 备用 OCR 引擎与引擎设置（自动 / 微信 / Windows），ARM64 可用文字提取与译图 — Windows OCR fallback engine and OCR engine setting, enabling OCR on ARM64 (#63, @Jotaro2025)
- 多显示器跨屏框选：按虚拟桌面捕获，混合 DPI 统一换算 — Cross-monitor selection with mixed-DPI support (#64, @Jotaro2025)

### 变更 / Changed

- 图片翻译 UX：隐藏 OCR 列表、处理动画、仅展示成品图 — Image-translate UX shows only the finished image (#42, @Jotaro2025)
- HTTP API Key 改用 DPAPI（当前用户）加密存储，旧明文自动迁移 — HTTP API key stored with DPAPI, plaintext migrated automatically (#62, @Jotaro2025)

### 性能 / Performance

- 滚动 tile 改用 BGRA `byte[]`，UI 线程再物化 WriteableBitmap — Scroll tiles as BGRA buffers (#44, @Jotaro2025)
- 翻译渲染避免整图二次 RenderTargetBitmap — Fewer full-image re-renders in translate (#48, @Jotaro2025)
- 翻译复制大图 CopyPixels 离开 UI 线程 — Off-UI copy for translated images (#49, @Jotaro2025)
- 主截图复制改用 CopyBitmapAsync — Off-UI copy for the main capture (#51, @Jotaro2025)

### 修复 / Fixed

- 滚动截屏：选区 hole 失败/全客户区打孔导致白屏，且 `ResolveScrollTargetAt` 命中 overlay 返回 Zero → 滚轮全失败 → 重复 tile 立刻 auto-stop — Keep frozen preview; refuse full-client hole; find window under overlay for wheel; do not count failed-wheel dups toward stop; Log.Warn/Info on auto-stop (@Jotaro2025)

- 设置窗口跨 Dispatcher 导致的 FATAL 崩溃 — Settings window cross-dispatcher crash (#36, @Jotaro2025)
- slim 包 OCR / 翻译提示框被 Topmost 遮挡导致「无响应」 — Prompts hidden behind the topmost overlay (#39, @Jotaro2025)
- HyMT Dispose 先取消推理再限时等待，避免卸载时卡死 — HyMT dispose no longer hangs (#55, @Jotaro2025)
- 原先被吞掉的异常改为写入插件日志 — Swallowed exceptions are now logged (#54, @Jotaro2025)
- OCR 组件包（wechatocr.1.0.5.nupkg）下载校验 SHA-256，不匹配即拒绝 — SHA-256 check for the OCR package download (#61, @Jotaro2025)

### 测试 / Tests

- ScrollStitcher 合成 tile 单测 — Synthetic-tile tests for ScrollStitcher (#45, @Jotaro2025)
- 翻译贴字预乘混合与边缘裁剪像素单测 — Premultiplied blend / edge-clip pixel tests (#53, @Jotaro2025)

### CI / 发布 / Release

- CI 警告即错误（含代码风格），上传 Release zip 与测试结果 — Warnings-as-errors CI, release zip and test-result artifacts (#57, @Jotaro2025)
- 版本号升至 1.4.0 并新增 CHANGELOG — Bump to 1.4.0 and add this changelog (#60, @Jotaro2025)
- DLL 版本取自 plugin.json；修正 .gitignore；验收清单补充安装程序 — Assembly version from plugin.json, .gitignore fix, installer checklist (#59, @Jotaro2025)

### 文档 / Docs

- 双语 README，突出 PowerToys / TDS / WeChatOcr / HyMT 致谢 — Bilingual README with acknowledgments (#37, @Jotaro2025)

## [1.3.0] 及更早 / and earlier

`plugin.json` 自 #6 起为 1.3.0，#1–#35 均在此版本号下合入且未单独打标签，主要包括：区域截屏与标注编辑器、文字提取（WeChatOcr，slim 包按需下载）、图片翻译（HTTP OpenAI 兼容 / 进程内 HyMT2Sharp，GGUF 下载、断点续传与 SHA-256 校验）、钉住图片、专用设置窗口与发布打包脚本，以及捕获 / 剪贴板 / 译图渲染的一系列性能优化（@Jotaro2025）。

`plugin.json` has been 1.3.0 since #6; #1–#35 landed under that version without separate tags: region capture and annotation editor, text extraction (WeChatOcr, on-demand download for slim), image translation (HTTP OpenAI-compatible / in-process HyMT2Sharp with GGUF download, resume and SHA-256 check), pin image, the dedicated settings window and pack scripts, plus capture / clipboard / translate-render performance work (@Jotaro2025).

[1.6.0]: https://github.com/Jotaro2025/PowerToys-Run-Snip/releases/tag/v1.6.0
[1.5.0]: https://github.com/Jotaro2025/PowerToys-Run-Snip/releases/tag/v1.5.0
[1.4.0]: https://github.com/Jotaro2025/PowerToys-Run-Snip/releases/tag/v1.4.0
