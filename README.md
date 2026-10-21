# Snip — PowerToys Run 截屏插件

PowerToys Run（PowerToys 启动器/搜索）的区域截屏插件，功能移植自 [TDS](https://github.com/) 的区域截屏工具。纯 C#/WPF 实现，不依赖 TDS 本体，也不修改 PowerToys。

## 功能

在 PowerToys Run 中输入关键词 `snip`，提供三条命令：

| 命令 | 行为 |
|---|---|
| **区域截屏** | 全屏遮罩拖拽框选，支持标注后复制或保存 |
| **全屏截屏到剪贴板** | 直接截取光标所在屏幕并复制（DIB + PNG，可粘进画图/Word/QQ） |
| **全屏截屏并保存** | 截取光标所在屏幕并保存为 PNG |

区域截屏编辑器能力：

- 拖拽框选、八手柄调整选区、实时尺寸提示
- 标注工具：矩形、椭圆、箭头、画笔、文字、马赛克
- 7 色颜色板 + 4 档笔画粗细、撤销（Ctrl+Z）
- 快捷键：Enter 完成、Esc 取消、Ctrl+C 复制、Ctrl+S 保存、Delete 删除选中标注
- **文字提取（OCR）**：工具栏上的「文字提取」按钮，仿微信截图：识别选区（含标注）中的文字，
  在结果窗口中显示，可编辑/选中，支持「复制」（有选中时复制选中部分）和「全部复制」
- 多显示器 + 高 DPI 正确适配（取光标所在屏幕，按物理像素捕获）

插件设置（PowerToys 设置 → PowerToys Run → Snip）：

- 保存目录（留空则用 `图片\Snip`）
- 截屏后复制到剪贴板（默认开）
- 区域截屏后自动保存
- 截屏延迟（毫秒，默认 150，给 PowerToys Run 隐藏窗口的时间）

界面语言跟随 PowerToys（内置英文 + 简体中文）。

### 文字提取说明

文字提取使用 NuGet 包 [WeChatOcr](https://www.nuget.org/packages/WeChatOcr)，该包自带完整的 OCR 组件
（`WeChatOCR.exe`、mmmojo DLL 和模型文件），随插件一起部署在插件目录的 `wco_data\` 下：

- **不需要安装微信**，离线识别，不联网
- 插件体积因此增加约 61 MB（`wco_data\` 约 60 MB + 依赖 DLL 约 1.3 MB）
- 仅支持 x64 版 PowerToys（OCR 组件只有 x64 版本）
- OCR 组件缺失、超时、识别失败或没有识别到文字时，结果窗口会给出对应提示

## 构建

需要 .NET 10 SDK 和一份已安装的 PowerToys（插件引用其自带的 `Wox.Plugin` 等程序集）：

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

## 部署

把构建输出目录整体复制到 PowerToys 的插件目录，然后重启 PowerToys：

```
%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Snip\
├── Community.PowerToys.Run.Plugin.Snip.dll
├── Community.PowerToys.Run.Plugin.Snip.pdb        (可选)
├── plugin.json
├── Images\Snip.dark.png
├── Images\Snip.light.png
├── WeChatOcr.dll、Google.Protobuf.dll、Newtonsoft.Json.dll         (文字提取依赖)
├── wco_data\WeChatOCR.exe、mmmojo*.dll、x64.config、Model\*          (文字提取 OCR 组件)
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
  其自带的 OCR 组件（`WeChatOCR.exe`、mmmojo DLL、模型文件）来自腾讯微信，构建时由该包复制到输出目录。
- 本仓库新增代码以 MIT 许可发布，见 [LICENSE](LICENSE)。
