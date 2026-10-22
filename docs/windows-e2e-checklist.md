# Windows 手工验收清单（S1 lite）

在真实 Windows + 已安装 PowerToys 上，按本清单点验 Snip 插件。不替代自动化单测。

## 准备

- [ ] 已用 `scripts/pack-release.ps1`（或 `pack-release.sh`）打出 zip，并解压到  
      `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Snip\`
- [ ] 已重启 PowerToys；PowerToys Run 中输入 `snip` 能看到区域截屏一条命令
- [ ] 插件设置里按需配置保存目录、翻译引擎

## S1 — 区域截屏基础

- [ ] 区域截屏：拖拽框选、八手柄调整、尺寸提示正常
- [ ] 标注：矩形 / 椭圆 / 箭头 / 画笔 / 文字 / 马赛克
- [ ] 复制（Ctrl+C）与保存（Ctrl+S）可用
- [ ] Esc 取消、Enter 完成

## OCR（文字提取）

- [ ] 工具栏「文字提取」能识别选区内文字
- [ ] 结果窗口可编辑/复制；无文字时有提示
- [ ] 确认插件目录存在 `wco_data\WeChatOCR.exe`（打包自检应已覆盖）

## 图片翻译 — HTTP

- [ ] 设置引擎为 HTTP，填写可用的 OpenAI 兼容 Base URL / 模型
- [ ] 「图片翻译」完成 OCR → 翻译 → 左侧贴图、右侧译文列表

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
