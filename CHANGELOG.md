# 更新日志 / Changelog

格式参考 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本号遵循 [语义化版本](https://semver.org/lang/zh-CN/)。
版本号唯一来源为 `Community.PowerToys.Run.Plugin.Snip/plugin.json` 的 `Version`；发布时推送 `v<Version>` 标签，CI 会把安装程序与 zip 附加到 GitHub Release。

Format follows [Keep a Changelog](https://keepachangelog.com/en/1.1.0/); versions follow [SemVer](https://semver.org/).
The single source of the version is `Version` in `plugin.json`; pushing a `v<Version>` tag attaches the installers and zips to a GitHub Release.

## [1.4.0] - 未发布 / Unreleased

> 打 `v1.4.0` 标签发布时填写日期。 / Fill in the date when tagging `v1.4.0`.

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

[1.4.0]: https://github.com/Jotaro2025/PowerToys-Run-Snip/releases/tag/v1.4.0
