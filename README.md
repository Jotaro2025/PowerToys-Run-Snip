# Snip — PowerToys Run 截屏插件

PowerToys Run（PowerToys 启动器/搜索）的**区域截屏**插件：框选标注、OCR 文字提取、图片翻译、钉住浮窗。功能移植自 [TDS](https://github.com/) 的区域截屏工具。纯 C#/WPF 实现，不依赖 TDS 本体，也不修改 PowerToys。

> **学习向说明：** 文字提取依赖的 [WeChatOcr](https://www.nuget.org/packages/WeChatOcr) NuGet 包（及其随包 OCR 组件）按包说明面向学习/研究场景；商业产品请自行评估合规与替代方案。详见文末「代码来源与许可」。

当前插件元数据版本见 [`plugin.json`](Community.PowerToys.Run.Plugin.Snip/plugin.json)（Author: Jotaro2025）。

## 功能

在 PowerToys Run 中输入关键词 `snip`，挂载**区域截屏**一条命令（遮罩框选 + 标注 / OCR / 译图 / 钉住；需要整屏时框选整块显示器即可，不再单独提供全屏到剪贴板 / 全屏保存入口）。仓库与插件描述以区域为主。

区域截屏编辑器能力：

- 拖拽框选、八手柄调整选区、实时尺寸提示
- 标注工具：矩形、椭圆、箭头、画笔、文字、马赛克（拖拽时用廉价中心 2×2 采样预览；松手/导出用全块平均，观感与原先导出一致）
- 7 色颜色板 + 4 档笔画粗细、撤销（Ctrl+Z）
- 快捷键：Enter 完成、Esc 取消、Ctrl+C 复制、Ctrl+S 保存、Ctrl+Z 撤销、Ctrl+Y 重做、Delete 删除选中标注
- 导出路径：已有 PNG 字节时剪贴板不再二次编码；无标注时裁剪结果跳过 RenderTargetBitmap；PNG 编码优先 TryGetBuffer / 直写文件，减少多余数组拷贝
- **文字提取（OCR）**：工具栏上的「文字提取」按钮，仿微信截图：识别选区（含标注）中的文字，
  在结果窗口中显示，可编辑/选中，支持「复制」（有选中时复制选中部分）和「全部复制」；关闭结果窗后回到遮罩可继续标注
- **钉住图片**：工具栏图钉按钮，以置顶轻量窗口贴在屏幕上（拖动移动；Esc / 双击 / 右键关闭）；**不结束**截屏会话，可继续标注或再次钉住
- **图片翻译**：工具栏上的「图片翻译」按钮；OCR 取出带框文字后翻译，擦除原文并重绘译文，
  结果窗口左侧为翻译图、右侧为可编辑译文列表（需在设置中配置翻译引擎）；
  可「重新贴图」把编辑后的译文再画回左侧，也可「保存图片」/复制图片
- **会话保持（OCR / 翻译 / 钉住）**：点文字提取或图片翻译时先隐藏全屏遮罩，结果窗关闭后自动恢复同一选区与标注（仿微信截图可再试）；钉住不关遮罩。Enter / 复制 / 保存 / Esc 取消仍结束本次截屏；Esc 取消会顺带关掉未关的 OCR/翻译结果窗，避免残留
- 多显示器 + 高 DPI 正确适配（取光标所在屏幕，按物理像素捕获；GDI 用 CreateDIBSection 直写 top-down BGRA，再单次拷入 `CapturedFrame`，无 AllocHGlobal 中间缓冲；BitBlt/拷贝仍在线程池，UI 再组 WriteableBitmap）

插件设置（PowerToys 设置 → PowerToys Run → Snip）：

- 保存目录（留空则用 `图片\Snip`）
- 截屏后复制到剪贴板（默认开）
- 区域截屏后自动保存
- 截屏延迟（毫秒，默认 150，给 PowerToys Run 隐藏窗口的时间）
- **图片翻译引擎**：`关闭` / `HTTP（OpenAI 兼容）` / `HyMT2Sharp（本地 GGUF）`（默认关闭）
- 目标语言（标签前缀 `翻译 ·`）
- HTTP 相关项标签前缀 `HTTP ·`：Base URL、可选 API Key、模型名
- HyMT 相关项标签前缀 `HyMT ·`：GGUF 路径、**下载推荐的 2Bit 模型**（勾选后保存触发）、自定义下载 URL、后端（自动 / CPU / Vulkan）
- **设置折叠限制**：PowerToys Run 的 `AdditionalOptions` **不能**按引擎动态隐藏字段（无 `IsVisible` / 条件折叠 API；改引擎后设置页也不会即时重刷选项列表）。因此采用前缀分组降低噪音；引擎说明里也有同一提示

界面语言跟随 PowerToys（内置英文 + 简体中文）。

### 文字提取说明

文字提取使用 NuGet 包 [WeChatOcr](https://www.nuget.org/packages/WeChatOcr)，该包自带完整的 OCR 组件
（`WeChatOCR.exe`、mmmojo DLL 和模型文件）。**默认推荐精简包（slim）**，不强制每人下载约 61 MB 的 `wco_data`：

- **不需要安装微信**；识别在本地进行（下载组件后可不联网）
- **仅支持 x64** 版 PowerToys（OCR 组件只有 x64 版本）
- **精简包（slim / 默认 `Snip-<ver>.zip`）**：不含 `wco_data`；首次「文字提取」或「图片翻译」时提示下载 WeChatOcr NuGet 包（nuget.org → Azure 中国 CDN），解压到  
  `%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\wco_data\`
- **完整包（full）**：插件目录自带 `wco_data\`，可离线 OCR，体积大约多 61 MB
- 托管 DLL（`WeChatOcr.dll` 等约 1.3 MB）仍随两种包一起分发
- OCR 缺失、超时、识别失败或没有文字时，结果窗口会提示；可改下完整包或允许下载
- 关闭 OCR / 图片翻译结果窗口会取消进行中的识别与翻译；若引擎正被占用会显示忙碌等待提示

### 图片翻译说明

图片翻译复用 WeChat OCR 的逐行框（`Left/Top/Right/Bottom`），翻译后按框擦除原文并重绘译文：框外多环中位色采样（用于对比色文字）；擦除优先用纯 C# **Telea 风格轻量 inpaint**（区域局部 mask + 边界队列洋葱皮推进，距离加权 + 廉价梯度项，无 OpenCV 原生依赖；不再为每个 OCR 框分配全图 `bool[W*H]`），过小或失败时回退中位色实心填充；框内换行（英文按词 / 中文按字）并自适应字号（有下限），边缘留出少量 padding。
有 `SinglePos` 四角点时按四边形仿射贴字（纯 WPF，无 OpenCV），擦除对该四边形外接矩形做 inpaint；否则回退轴对齐框；HyMT 首次加载 GGUF 时结果窗显示「正在加载模型…」。
多行 OCR 结果会**批量**发给翻译引擎（编号对齐；解析失败则回退逐行），HTTP 与 HyMT2Sharp 均减少往返次数。
结果窗口支持编辑右侧译文后点「重新贴图」仅重绘左侧（不重新 OCR/翻译），以及「保存图片」（默认目录与截屏保存目录一致，可改路径）。

翻译引擎默认**关闭**，需在插件设置中任选其一：

1. **HTTP（OpenAI 兼容）**  
   指向任意实现 `POST /v1/chat/completions` 的服务，例如 [HyMT2Sharp.Server](https://github.com/sdcb/HyMT2Sharp)：
   `http://127.0.0.1:8080/v1`。可选 API Key 与模型名。

2. **HyMT2Sharp（进程内）**  
   引用 NuGet 包 [`Sdcb.HyMT2Sharp`](https://www.nuget.org/packages/Sdcb.HyMT2Sharp)（Apache-2.0，约 460KB），
   **不**捆绑 `Sdcb.GgufWeights.*`（2Bit 约 573MB）。推荐使用官方 [Hy-MT2-1.8B-2Bit.gguf](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF)
   （Apache-2.0，与 `Sdcb.GgufWeights.Hy-MT2-1.8B-2Bit` 同源，约 573MB / 600,534,880 字节）：
   - **设置**：勾选「下载推荐的 2Bit 模型」并保存，或首次点击「图片翻译」时按提示下载
   - **缓存目录**：`%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\models\Hy-MT2-1.8B-2Bit.gguf`
   - **下载源（按顺序尝试，国内优先）**：
     1. **魔搭 ModelScope**（默认）：`https://www.modelscope.cn/api/v1/models/Tencent-Hunyuan/Hy-MT2-1.8B-2Bit-GGUF/repo?Revision=master&FilePath=Hy-MT2-1.8B-2Bit.gguf`
     2. **hf-mirror**：`https://hf-mirror.com/tencent/Hy-MT2-1.8B-2Bit-GGUF/resolve/main/Hy-MT2-1.8B-2Bit.gguf`
     3. **Hugging Face**（兜底）：`https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF/resolve/main/Hy-MT2-1.8B-2Bit.gguf`
   - 某一源中途失败会自动换下一个（保留 `.downloading` 断点；支持 HTTP Range 续传，源不支持则该源整文件重下）；全部失败时提示检查网络 / 魔搭
   - 下载完成后校验官方 SHA-256，不匹配则**删除并拒绝**该文件，改试下一源
   - 下载成功后会自动填入 `HyMtGgufPath`，并写回 PowerToys Run 的 `settings.json`（同时落盘旁路 `.path` sidecar）；缓存已存在且体积看起来完整时会跳过重复下载
   - 可选「自定义 GGUF 下载地址」：填写 HTTPS URL 时优先于魔搭 / 镜像链；留空仍为国内优先默认顺序
   - 也可手动下载后在设置里填写任意 GGUF 路径
   - 后端可选自动 / CPU / Vulkan（Vulkan 约需 4GB 显存）。模型首次使用时懒加载，并在进程内按「GGUF 路径 + 后端」缓存，避免每次翻译后释放再重载数 GB 权重；推理串行排队（模型非线程安全）。插件卸载时会显式释放缓存，否则随进程退出清理

未配置或配置错误时，点击「图片翻译」会弹出中文提示，不会关闭截屏窗口。

## 构建

需要 .NET 10 SDK 和一份已安装的 PowerToys（插件引用其自带的 `Wox.Plugin` 等程序集）：

CI / 无完整安装时可用仓库内 `build/PowerToysRefs` 作为 `-p:PowerToysInstallDir=...`（见 `.github/workflows/ci.yml`）。

```powershell
dotnet build Community.PowerToys.Run.Plugin.Snip
# PowerToys 装在其他位置时：
dotnet build Community.PowerToys.Run.Plugin.Snip -p:PowerToysInstallDir="D:\PowerToys"
```

运行测试：

```powershell
dotnet test Community.PowerToys.Run.Plugin.Snip.UnitTests
```

代码按 PowerToys 仓库同款规则集构建（StyleCop + .NET 分析器，警告即错误）。

## 发布打包

推荐用仓库脚本打出可分发的 zip（根目录为 `Snip\`，解压到 PowerToys Run 的 `Plugins\` 下即可）：

```powershell
# Windows（需本机已装 PowerToys，或用 -PowerToysInstallDir 指向含 Wox.Plugin.dll 的目录）
.\scripts\pack-release.ps1
# 若 PowerToys 不在默认路径：
.\scripts\pack-release.ps1 -PowerToysInstallDir "D:\PowerToys"
```

```bash
# Linux / CI 交叉编译（EnableWindowsTargeting），需提供 Wox.Plugin 等引用目录
./scripts/pack-release.sh --powertoys-dir /path/to/PowerToysOrStubs
```

脚本会：

1. `dotnet build -c Release -p:EnableWindowsTargeting=true`
2. 将输出暂存为 `artifacts/staging/Snip/`（跳过 `.pdb`）
3. 自检关键文件：`plugin.json`、主 DLL、`Images\`、`WeChatOcr.dll`、`Sdcb.HyMT2Sharp.dll`、`wco_data\WeChatOCR.exe`（构建输出仍含 OCR，供 full）、`zh-CN\*.resources.dll`，并拒绝仍带 Microsoft / aka.ms 元数据的 `plugin.json`
4. 生成两套包（版本号读取自 `plugin.json`）：
   - **`Snip-<Version>-slim.zip`** / **`Snip-<Version>.zip`（默认，推荐）**：去掉 `wco_data`，首次 OCR 按需下载
   - **`Snip-<Version>-full.zip`**：保留 `wco_data`，可离线文字提取 / 图片翻译

Windows 手工验收可参考 [docs/windows-e2e-checklist.md](docs/windows-e2e-checklist.md)。

## 部署

把构建输出目录（或发布 zip 解压出的 `Snip\`）整体放到 PowerToys 的插件目录，然后重启 PowerToys：

```
%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Snip\
├── Community.PowerToys.Run.Plugin.Snip.dll
├── Community.PowerToys.Run.Plugin.Snip.pdb        (可选)
├── plugin.json
├── Images\Snip.dark.png
├── Images\Snip.light.png
├── WeChatOcr.dll、Google.Protobuf.dll、Newtonsoft.Json.dll         (文字提取托管依赖；两种包都有)
├── Sdcb.HyMT2Sharp.dll 等                                           (图片翻译：进程内引擎，不含 GGUF)
├── wco_data\…                                                       (仅 full 包；slim 首次 OCR 下载到 LocalAppData)
├── Community.PowerToys.Run.Plugin.Snip.deps.json 等其余构建输出
└── zh-CN\Community.PowerToys.Run.Plugin.Snip.resources.dll   (中文资源)
```

即 `Community.PowerToys.Run.Plugin.Snip\bin\Debug\` 下的全部内容。插件以 `DynamicLoading` 方式加载，
依赖从插件自己的目录解析；PowerToys 自带的 `Wox.Plugin` 等程序集不会复制到输出目录。
PowerToys 0.9x 及更早版本的插件目录是 `%LOCALAPPDATA%\Microsoft\PowerToys\RunPlugins\`。

## 在 PowerToys 源码树内开发

如果要把插件提交进 [microsoft/PowerToys](https://github.com/microsoft/PowerToys) 仓库，把 `Community.PowerToys.Run.Plugin.Snip` 文件夹复制到
`src/modules/launcher/Plugins/` 下，并把工程文件里的四个 `<Reference>` 换成对源码工程的引用：

```xml
<ItemGroup>
  <ProjectReference Include="..\..\Wox.Plugin\Wox.Plugin.csproj">
    <Private>false</Private>
  </ProjectReference>
</ItemGroup>
```

（`Wox.Plugin` 会传递引用 `ManagedCommon`、`Common.UI`、`Settings.UI.Library`。）

## 代码来源与许可

- 截屏捕获、标注模型与编辑器逻辑移植自 TDS 的区域截屏模块（MIT）。
- 插件框架（`Wox.Plugin`）来自 PowerToys（MIT）。
- 文字提取使用 NuGet 包 [WeChatOcr](https://www.nuget.org/packages/WeChatOcr)（NuGet 标注 MIT 许可，仅以包引用，
  未复制其源码）。该包的描述注明“Study only, do not use for commercial purposes”（仅供学习，勿用于商业用途）；
  其自带的 OCR 组件（`WeChatOCR.exe`、mmmojo DLL、模型文件）来自腾讯微信；完整包构建时复制到输出目录，
  精简包则在首次使用时从同一 NuGet 包下载到 LocalAppData。
- 图片翻译可选引用 [Sdcb.HyMT2Sharp](https://www.nuget.org/packages/Sdcb.HyMT2Sharp)（Apache-2.0）；
  **不**引用 `Sdcb.GgufWeights.*`。推荐权重与官方 2Bit GGUF 同源（Apache-2.0）；
  插件默认从 [魔搭 ModelScope](https://www.modelscope.cn/models/Tencent-Hunyuan/Hy-MT2-1.8B-2Bit-GGUF)
  下载到 LocalAppData（便于国内访问），失败再回退 hf-mirror / [Hugging Face](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF)，
  也可由用户手动配置路径。
- 本仓库新增代码以 MIT 许可发布，见 [LICENSE](LICENSE)。
