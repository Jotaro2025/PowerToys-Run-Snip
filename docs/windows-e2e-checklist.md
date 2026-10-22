# Windows 手工验收清单（S1 lite）

在真实 Windows + 已安装 PowerToys 上，按本清单点验 Snip 插件。不替代自动化单测。

## 准备

- [ ] 已用 `scripts/pack-release.ps1`（或 `pack-release.sh`）打出 zip，并解压到  
      `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Snip\`
- [ ] 已知所用包类型：**slim**（默认 `Snip-<ver>.zip` / `*-slim.zip`）或 **full**（`*-full.zip`）
- [ ] 已重启 PowerToys；PowerToys Run 中输入 `snip` 能看到区域截屏一条命令
- [ ] 插件设置里按需配置保存目录、翻译引擎

## S1 — 区域截屏基础

- [ ] 区域截屏：拖拽框选、八手柄调整、尺寸提示正常
- [ ] 标注：矩形 / 椭圆 / 箭头 / 画笔 / 文字 / 马赛克
- [ ] 复制（Ctrl+C）与保存（Ctrl+S）可用
- [ ] Esc 取消、Enter 完成

## OCR（文字提取）

- [ ] **slim**：首次「文字提取」弹出下载确认 → 进度框能完成 → 之后识别正常；取消下载时有明确「需安装 OCR」提示
- [ ] **full**：插件目录存在 `wco_data\WeChatOCR.exe`，无需下载即可识别
- [ ] 工具栏「文字提取」能识别选区内文字
- [ ] 结果窗口可编辑/复制；无文字时有提示
- [ ] 仅 x64 PowerToys；非 x64 有架构提示

## 图片翻译 — HTTP

- [ ] 设置引擎为 HTTP，填写可用的 OpenAI 兼容 Base URL / 模型
- [ ] 「图片翻译」完成 OCR → 翻译 → 处理动画 → 仅展示成品图（译文列表默认折叠）；忙态可取消

## 图片翻译 — HyMT2Sharp

- [ ] 设置引擎为 HyMT2Sharp；GGUF 已下载或手动指定路径
- [ ] **CPU** 后端：能完成一次翻译（首次加载可能较慢）
- [ ] **Vulkan** 后端（若机器有合适 GPU / 驱动）：能完成一次翻译
- [ ] 连续第二次翻译：行为符合预期（有进程缓存时不应再次长时间加载；无缓存时会再加载）

## 多显示器 / DPI

- [ ] 主屏与副屏分别触发区域截屏，遮罩与选区对齐该屏
- [ ] 100% / 125% / 150%（或你常用的缩放）下框选与标注坐标无明显偏移
- [ ] 高 DPI 下保存/复制的图片尺寸为物理像素（不糊、不裁切错位）

## 部署回归

- [ ] `plugin.json` 中 Author / Website / Version 为本仓库信息（非 Microsoft / aka.ms）
- [ ] 中文界面下按钮与提示为简体中文（`zh-CN` 卫星程序集已部署）

## 安装程序（Inno Setup）

使用 `build/build-installer.ps1` 或 Release 中的 `Snip-Setup-<ver>-<arch>-<slim|full>.exe`。

- [ ] **未装 PowerToys**：运行安装程序 → 中英双语提示（含 Releases 链接与 `winget install Microsoft.PowerToys`）后退出，不写任何文件
- [ ] **PowerToys < 0.100**：提示版本过旧并退出；**≥ 0.100**（用户版 `%LOCALAPPDATA%\PowerToys` 与机器版 `%ProgramFiles%\PowerToys` 各测一次）：正常进入安装
- [ ] 全程无 UAC 弹窗；安装目录为 `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Snip\`
- [ ] **PowerToys 正在运行**：弹出关闭确认；选「确定」→ `PowerToys.exe` / `PowerToys.PowerLauncher` 被关闭 → 安装完成后从原路径自动重启，托盘图标与 Run 热键恢复
- [ ] 关闭确认选「取消」：安装中止，PowerToys 保持运行
- [ ] **PowerToys 未运行**：安装完成后不会自动启动 PowerToys
- [ ] 安装后 PowerToys Run 输入 `snip` 可见命令；**full** 首次 OCR 不触发下载，**slim** 按需下载
- [ ] **升级**：在插件目录放一个多余文件（如 `Stale.dll`）后安装新版本 → 该文件被清除；先装 full 再装 slim → `wco_data\` 被移除
- [ ] 升级 / 重装后 `%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\`（OCR 缓存、HyMT 模型）与 PowerToys Run 设置保留
- [ ] **卸载**（设置 → 应用 → *Snip for PowerToys Run*）：PowerToys 运行时同样询问关闭并在结束后重启；插件目录被删除
- [ ] 卸载时询问是否删除数据：默认「否」→ 数据目录保留；选「是」→ 数据目录被删除
- [ ] 静默卸载 `unins000.exe /VERYSILENT` 保留数据；加 `/DELETEDATA` 删除数据
- [ ] **架构**：x64 机器运行 arm64 安装包被拒绝；ARM64 机器运行 x64 安装包被拒绝；ARM64 安装包显示 ARM64 提示
- [ ] 中文系统下安装向导为简体中文，英文系统为英文
- [ ] 资源管理器中插件 DLL「详细信息 → 文件版本」与 `plugin.json` 的 Version 一致
