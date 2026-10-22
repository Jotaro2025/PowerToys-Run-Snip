# Snip — PowerToys Run 截屏插件

**[中文](README.md) | [English](README.en.md)**

PowerToys Run（PowerToys 启动器）的**区域截屏**插件：框选标注、OCR 文字提取、图片翻译、钉住浮窗。纯 C#/WPF 实现，不修改 PowerToys，也不依赖 TDS 二进制。

当前插件元数据版本见 [`plugin.json`](Community.PowerToys.Run.Plugin.Snip/plugin.json)（Author: Jotaro2025）。

## 致谢与来源

本插件面向 / 依赖官方宿主，并引用了以下项目的思路或包；**与 Microsoft 无隶属关系**，为社区插件。

| 项目 | 作用 | 许可 / 说明 |
|------|------|-------------|
| **[microsoft/PowerToys](https://github.com/microsoft/PowerToys)** | PowerToys Run 插件宿主；引用其自带的 `Wox.Plugin` 等程序集 | MIT |
| **[LdotJdot/TDS](https://github.com/LdotJdot/TDS)**（[`TDSAot/ScreenShot/`](https://github.com/LdotJdot/TDS/tree/main/TDSAot/ScreenShot)） | 区域截屏的捕获、遮罩、标注、**滚动截屏拼接**等实现思路；本仓库为面向 PowerToys Run 的纯 C#/WPF 重写 | 不依赖 TDS 二进制；不修改 PowerToys |
| **[WeChatOcr](https://github.com/ZGGSONG/WeChatOcr)**（[NuGet](https://www.nuget.org/packages/WeChatOcr/)） | 文字提取 / 图片翻译的 OCR | 仅包引用；包说明为 “Study only, do not use for commercial purposes”；OCR 组件来自微信生态 |
| **[Sdcb.HyMT2Sharp](https://github.com/sdcb/HyMT2Sharp)**（[NuGet](https://www.nuget.org/packages/Sdcb.HyMT2Sharp)，Apache-2.0） | 可选进程内本地翻译；另可选用 HTTP OpenAI 兼容服务 | 推荐权重 **腾讯混元 Hy-MT2** GGUF（[ModelScope](https://www.modelscope.cn/models/Tencent-Hunyuan/Hy-MT2-1.8B-2Bit-GGUF) / [Hugging Face](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF)，Apache-2.0）；**权重不随插件捆绑** |
| 本仓库新增代码 | 插件本体 | [MIT](LICENSE) |

> **学习向说明：** WeChatOcr 按包说明面向学习/研究；商业产品请自行评估合规与替代方案。

## 功能

在 PowerToys Run 中输入关键词 `snip`，可挂载：

- **区域截屏**：遮罩框选 + 标注 / OCR / 译图 / 钉住（需要整屏时框选整块显示器即可）
- **设置**：打开 Snip 专用设置窗口

也可在设置中配置**全局快捷键**直接开始区域截屏。

### 区域截屏编辑器

- 拖拽框选、八手柄调整选区、实时尺寸提示
- 标注：矩形、椭圆、箭头、画笔、文字、马赛克
- 7 色颜色板 + 4 档笔画粗细；撤销 / 重做
- 快捷键：Enter 完成、Esc 取消、右键取消、Ctrl+C 复制、Ctrl+S 保存、Ctrl+Z 撤销、Ctrl+Y 重做、Delete 删除选中标注
- **滚动截屏**（思路来自 TDS）：框选后点工具栏「滚动截屏」，选区挖洞露出桌面并自动滚轮逐帧捕获，停止后点完成/保存一次性拼接长图（依赖 MathNet.Numerics；部分应用不响应 `WM_MOUSEWHEEL` 时效果有限）
- **文字提取（OCR）**：识别选区文字，结果窗可编辑 / 复制；关闭后回到遮罩可继续标注
- **钉住图片**：置顶轻量浮窗（拖动移动；Esc / 双击 / 右键关闭）；不结束截屏会话
- **图片翻译**：OCR 取框后翻译，擦除原文并重绘译文；可「重新贴图」/「保存图片」
- **会话保持**：OCR / 翻译时先隐藏遮罩，关闭结果窗后恢复同一选区与标注；钉住不关遮罩
- 多显示器 + 高 DPI 适配（取光标所在屏幕，按物理像素捕获）

界面语言默认跟随 PowerToys / 系统（内置英文 + 简体中文）；可在专用设置窗口「常规」顶部或 AdditionalOptions `UiLanguage`（`system` / `en` / `zh-CN`）手动切换，立即刷新设置页文案。

## 系统要求

- [PowerToys](https://github.com/microsoft/PowerToys) **≥ 0.100**（.NET 10 宿主）
- **x64** 或 **ARM64** PowerToys：微信 OCR 组件仅有 x64 版本；ARM64（或手动选择时）使用系统自带的 **Windows OCR**，需要已安装 OCR 语言（见「文字提取」）
- Windows（插件为 WPF）

## 安装

### 方式一：安装程序（推荐）

从 [Releases](https://github.com/Jotaro2025/PowerToys-Run-Snip/releases) 下载与 **Windows 架构** 对应的安装程序：

| 文件 | 架构 | 内容 | 大小（约） |
|---|---|---|---|
| `Snip-Setup-<ver>-x64-slim.exe` | x64 | 与 slim zip 相同；首次 OCR 时按需下载 `wco_data` | 7 MB |
| `Snip-Setup-<ver>-x64-full.exe` | x64 | slim + 内置 `wco_data`（约 61 MB），离线即可 OCR / 译图 | 35 MB |
| `Snip-Setup-<ver>-arm64-slim.exe` | ARM64 | 同 x64-slim | 7 MB |
| `Snip-Setup-<ver>-arm64-full.exe` | ARM64 | 同 x64-full；ARM64 上 OCR 使用 Windows OCR，内置的 `wco_data` 不会被使用，一般选 slim 即可 | 35 MB |

- **前置条件：需先安装 PowerToys ≥ 0.100**（首个 .NET 10 宿主版本，本插件目标为 `net10.0-windows`）。安装程序会检测 `%LOCALAPPDATA%\PowerToys\`、`%ProgramFiles%\PowerToys\` 与卸载注册表；未安装或版本过旧会提示 [PowerToys Releases](https://github.com/microsoft/PowerToys/releases) / `winget install Microsoft.PowerToys` 并退出。
- **按用户安装、无需管理员（无 UAC）**，安装到 `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Snip\`。
- 若 PowerToys 正在运行，会征得同意后关闭 `PowerToys.exe` / `PowerToys.PowerLauncher`，安装完成后从原路径重新启动（原本未运行则不启动）。卸载同理。
- x64 与 ARM64 安装程序互不通用，架构不匹配时拒绝安装。
- **升级**：直接运行新版本安装程序；会先清空插件目录中的旧文件再复制（slim/full 可互相覆盖）。设置与已下载的 OCR / HyMT 数据（`%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\`）不会被删除。
- **卸载**：在「设置 → 应用」中卸载 *Snip for PowerToys Run*；默认保留数据，卸载时会询问是否一并删除（默认「否」）。静默卸载可加 `/DELETEDATA` 删除数据。
- **HyMT 翻译模型不打包进任何安装程序**：GGUF 模型数 GB，体积过大；首次使用图片翻译时按需下载到上述数据目录。

### 方式二：手动解压 zip

1. 从 [Releases](https://github.com/Jotaro2025/PowerToys-Run-Snip/releases) 下载 `Snip-<ver>.zip`（精简包，推荐）或 `Snip-<ver>-full.zip`
2. 解压得到的 `Snip\` 目录整体放到：

```
%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Snip\
```

（PowerToys 0.9x 及更早为 `%LOCALAPPDATA%\Microsoft\PowerToys\RunPlugins\`。）

3. 重启 PowerToys

典型目录内容：

```
Snip\
├── Community.PowerToys.Run.Plugin.Snip.dll
├── plugin.json
├── Images\…
├── WeChatOcr.dll、Google.Protobuf.dll、Newtonsoft.Json.dll   # OCR 托管依赖（两种包都有）
├── Sdcb.HyMT2Sharp.dll 等                                      # 进程内翻译（不含 GGUF）
├── wco_data\…                                                  # 仅 full 包；slim 首次 OCR 下载
└── zh-CN\….resources.dll
```

插件以 `DynamicLoading` 加载；`Wox.Plugin` 等由 PowerToys 提供，不会复制到输出目录。

## 用法

1. 打开 PowerToys Run，输入 `snip`
2. 选择 **区域截屏**，框选后标注 / OCR / 译图 / 钉住 / 复制 / 保存
3. 或选择 **设置**，打开专用设置窗口
4. 若已配置全局快捷键，可直接触发区域截屏

## 设置

### 专用设置窗口（推荐）

PowerToys Run → `snip` → **设置**。含常规 / 快捷键 / 翻译 / 关于。翻译页可按引擎折叠字段；可配置全局快捷键（默认空 = 禁用）。保存时写回 PowerToys Run `settings.json`，与系统设置页同步。

### PowerToys 系统设置页

PowerToys 设置 → PowerToys Run → Snip 的 **AdditionalOptions** 仍可用。常见项：

| 项 | 说明 |
|----|------|
| 保存目录 | 留空则用 `图片\Snip` |
| 截屏后复制到剪贴板 | 默认开 |
| 区域截屏后自动保存 | |
| 截屏延迟（毫秒） | 默认 150，给 Run 隐藏窗口的时间 |
| 全局快捷键 | 如 `Ctrl+Shift+S`；留空禁用 |
| 图片翻译引擎 | `关闭` / `HTTP（OpenAI 兼容）` / `HyMT2Sharp（本地 GGUF）` |
| 目标语言 | 标签前缀 `翻译 ·` |
| HTTP · Base URL / API Key / 模型名 | OpenAI 兼容服务 |
| HyMT · GGUF 路径 / 下载推荐模型 / 自定义 URL / 后端 | 自动 / CPU / Vulkan |

> **折叠限制：** `AdditionalOptions` **不能**按引擎动态隐藏字段；请用专用设置窗口按引擎折叠。系统设置页用前缀分组降低噪音。

## 文字提取（OCR）

两种引擎，均在本地识别，结果统一供「文字提取」与「图片翻译」使用。设置项 **OCR 引擎**（专用设置窗口「常规」页 / PowerToys 设置页）：

| 选项 | 行为 |
|---|---|
| **自动**（默认） | x64 上已有微信 OCR 组件（full 包或已下载）→ 微信 OCR；否则 → Windows OCR（不再弹下载提示） |
| **微信 OCR** | 始终用微信 OCR；slim 包首次使用提示下载组件 |
| **Windows OCR** | 始终用系统自带 `Windows.Media.Ocr` |

ARM64 版 PowerToys 中始终使用 Windows OCR（微信 OCR 只有 x64 原生组件）。

### 微信 OCR

使用 NuGet 包 [WeChatOcr](https://www.nuget.org/packages/WeChatOcr/)（见上文致谢）。**不需要安装微信**。

- **精简包（slim / 默认 `Snip-<ver>.zip`）**：不含 `wco_data`；首次「文字提取」或「图片翻译」时提示下载 WeChatOcr NuGet，解压到  
  `%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\wco_data\`
- **完整包（full）**：插件目录自带 `wco_data\`，可离线 OCR（约多 61 MB）
- 托管 DLL（`WeChatOcr.dll` 等约 1.3 MB）随两种包一起分发
- 仅 **x64**；OCR 缺失 / 超时 / 失败时结果窗会提示
- 关闭结果窗会取消进行中的识别；引擎忙碌时会显示等待提示

### Windows OCR

- 系统自带，无需下载，x64 与 ARM64 均原生运行；中文识别效果一般弱于微信 OCR，且不提供倾斜文字的四边形（译图按水平框贴字）
- 需要已安装 OCR 语言：「设置 → 时间和语言 → 语言和区域」添加 **中文(简体，中国)**（或所需语言），并确认已安装其「光学字符识别」功能；或在管理员 PowerShell 运行  
  `Add-WindowsCapability -Online -Name "Language.OCR~~~zh-CN~0.0.1.0"`，然后重启 PowerToys
- 识别语言按 Windows 用户语言列表顺序选择（首个已安装 OCR 的语言），没有时再找简体中文
- 超长滚动截图会自动分段识别（单段受 `OcrEngine.MaxImageDimension` 限制）

## 图片翻译

复用 WeChat OCR 的逐行框，翻译后擦除原文并重绘译文。多行结果会批量发给引擎。结果窗以**成品图为主**（处理中有动画；译文列表默认折叠在「调整译文」），可「重新贴图」/「保存图片」；忙态可点「取消」或 Esc 中止。

翻译引擎默认**关闭**，需在设置中任选其一：

1. **HTTP（OpenAI 兼容）**  
   指向任意 `POST /v1/chat/completions` 服务，例如 [HyMT2Sharp.Server](https://github.com/sdcb/HyMT2Sharp)：`http://127.0.0.1:8080/v1`。可选 API Key 与模型名。
   API Key 以 Windows DPAPI（当前用户）加密后写入 PowerToys Run 的 `settings.json`（形如 `dpapi:v1:…`；旧版明文会在首次读取时自动改为加密存储）。因此**在另一台电脑或另一个 Windows 账户上恢复 / 同步该设置后，API Key 无法解密，需要重新填写**。PowerToys 设置页里该文本框显示的是密文，直接输入新的明文 Key 即可（下次读取时自动加密）。

2. **HyMT2Sharp（进程内）**  
   引用 [`Sdcb.HyMT2Sharp`](https://www.nuget.org/packages/Sdcb.HyMT2Sharp)，**不**捆绑 GGUF。推荐官方 [Hy-MT2-1.8B-2Bit.gguf](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF)（约 573 MB）：
   - 设置中勾选「下载推荐的 2Bit 模型」并保存，或首次点击「图片翻译」时按提示下载
   - 缓存：`%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\models\Hy-MT2-1.8B-2Bit.gguf`
   - 下载源（按序，国内优先）：[魔搭 ModelScope](https://www.modelscope.cn/models/Tencent-Hunyuan/Hy-MT2-1.8B-2Bit-GGUF) → hf-mirror → [Hugging Face](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF)
   - 支持断点续传与 SHA-256 校验；可选自定义 HTTPS 下载地址或手动填写 GGUF 路径
   - 后端：自动 / CPU / Vulkan（Vulkan 约需 4GB 显存）；模型按路径+后端缓存，推理串行

未配置或配置错误时，点击「图片翻译」会提示，不会关闭截屏窗口。

## 构建与测试

需要 .NET 10 SDK 和已安装的 PowerToys（引用其 `Wox.Plugin` 等）。CI / 无完整安装时可用仓库内 `build/PowerToysRefs` 作为 `-p:PowerToysInstallDir=...`（见 `.github/workflows/ci.yml`）。

```powershell
dotnet build Community.PowerToys.Run.Plugin.Snip
# PowerToys 装在其他位置时：
dotnet build Community.PowerToys.Run.Plugin.Snip -p:PowerToysInstallDir="D:\PowerToys"

dotnet test Community.PowerToys.Run.Plugin.Snip.UnitTests
```

代码按 PowerToys 同款规则集构建（StyleCop + .NET 分析器，警告即错误）。

## 发布打包

版本号以 `plugin.json` 的 `Version` 为准，变更记录见 [CHANGELOG.md](CHANGELOG.md)；推送 `v<Version>` 标签即由 CI 附加到 GitHub Release。

```powershell
# Windows（需本机 PowerToys，或 -PowerToysInstallDir）
.\scripts\pack-release.ps1
.\scripts\pack-release.ps1 -PowerToysInstallDir "D:\PowerToys"
```

```bash
# Linux / CI 交叉编译（EnableWindowsTargeting）
./scripts/pack-release.sh --powertoys-dir /path/to/PowerToysOrStubs
```

脚本会 Release 构建、暂存到 `artifacts/staging/Snip/`（跳过 `.pdb`）、自检关键文件，并生成：

- **`Snip-<Version>-slim.zip`** / **`Snip-<Version>.zip`（默认，推荐）**：无 `wco_data`，首次 OCR 按需下载
- **`Snip-<Version>-full.zip`**：含 `wco_data`，可离线 OCR / 译图

### 安装程序（Inno Setup）

需要 [Inno Setup](https://jrsoftware.org/isinfo.php) **6.5+**（`choco install innosetup` / `winget install JRSoftware.InnoSetup`）。

```powershell
.\build\build-installer.ps1 -PowerToysInstallDir "D:\PowerToys"
# 仅某架构 / 某变体：-Architectures x64 -Variants slim；指定 ISCC：-IsccPath "C:\...\ISCC.exe"
```

脚本分别以 `win-x64` / `win-arm64` 构建，暂存 slim（不含 `wco_data` 与 `.pdb`，与 slim zip 一致）与 full 布局，再用 `installer/Snip.iss` 编译 4 个安装程序到 `artifacts/installer/`。版本号取自 `plugin.json`，经 `ISCC /DAppVersion=…` 传入。Linux 上可通过 wine 运行 ISCC（`-IsccPath` 填 wine 内的 Windows 路径）。

CI（`.github/workflows/installer.yml`）在 Windows 上构建 4 个安装程序与 zip 并上传为 artifact；推送 `v*` 标签时一并附加到 GitHub Release。

Windows 手工验收见 [docs/windows-e2e-checklist.md](docs/windows-e2e-checklist.md)。

## 在 PowerToys 源码树内开发

若要把插件提交进 [microsoft/PowerToys](https://github.com/microsoft/PowerToys)，将 `Community.PowerToys.Run.Plugin.Snip` 复制到 `src/modules/launcher/Plugins/`，并把工程文件里的四个 `<Reference>` 换成对源码工程的引用：

```xml
<ItemGroup>
  <ProjectReference Include="..\..\Wox.Plugin\Wox.Plugin.csproj">
    <Private>false</Private>
  </ProjectReference>
</ItemGroup>
```

（`Wox.Plugin` 会传递引用 `ManagedCommon`、`Common.UI`、`Settings.UI.Library`。）

## 许可

本仓库新增代码以 MIT 许可发布，见 [LICENSE](LICENSE)。第三方组件的许可与使用限制见上文「致谢与来源」。
