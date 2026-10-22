# Snip — PowerToys Run Screenshot Plugin

**[中文](README.md) | [English](README.en.md)**

A **region screenshot** plugin for PowerToys Run (the PowerToys launcher): selection overlay with annotation, OCR text extraction, image translation, and pin-to-screen. Pure C#/WPF. Does **not** modify PowerToys and does **not** depend on the TDS binary.

Plugin metadata / version: [`plugin.json`](Community.PowerToys.Run.Plugin.Snip/plugin.json) (Author: Jotaro2025).

## Acknowledgments & Sources

This plugin targets / depends on the official host and references the following projects’ ideas or packages. **Not affiliated with Microsoft** — community plugin.

| Project | Role | License / notes |
|---------|------|-----------------|
| **[microsoft/PowerToys](https://github.com/microsoft/PowerToys)** | PowerToys Run plugin host; references shipped assemblies such as `Wox.Plugin` | MIT |
| **[LdotJdot/TDS](https://github.com/LdotJdot/TDS)** ([`TDSAot/ScreenShot/`](https://github.com/LdotJdot/TDS/tree/main/TDSAot/ScreenShot)) | Region screenshot capture, overlay, annotation, and **scroll-capture stitching** ideas; this repo is a pure C#/WPF reimplementation for PowerToys Run | Does **not** depend on the TDS binary; does **not** modify PowerToys |
| **[WeChatOcr](https://github.com/ZGGSONG/WeChatOcr)** ([NuGet](https://www.nuget.org/packages/WeChatOcr/)) | OCR for text extraction / image translation | Package reference only; package states “Study only, do not use for commercial purposes”; OCR binaries from the WeChat ecosystem |
| **[Sdcb.HyMT2Sharp](https://github.com/sdcb/HyMT2Sharp)** ([NuGet](https://www.nuget.org/packages/Sdcb.HyMT2Sharp), Apache-2.0) | Optional in-process local translation; alternatively HTTP OpenAI-compatible | Recommended weights: **Tencent Hunyuan Hy-MT2** GGUF ([ModelScope](https://www.modelscope.cn/models/Tencent-Hunyuan/Hy-MT2-1.8B-2Bit-GGUF) / [Hugging Face](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF), Apache-2.0); **weights are not bundled** |
| New code in this repo | Plugin itself | [MIT](LICENSE) |

> **Study-only note:** WeChatOcr is documented for learning / research. Evaluate compliance and alternatives before commercial use.

## Features

In PowerToys Run, type the keyword `snip` to get:

- **Region screenshot**: overlay selection + annotate / OCR / translate / pin (select the whole display for a full-screen capture)
- **Settings**: opens the dedicated Snip settings window

You can also configure a **global hotkey** in settings to start a region capture directly.

### Region editor

- Drag to select, eight-handle resize, live size tip
- Annotations: rectangle, ellipse, arrow, pen, text, mosaic
- 7-color palette + 4 stroke widths; undo / redo
- Shortcuts: Enter finish, Esc cancel, right-click cancel, Ctrl+C copy, Ctrl+S save, Ctrl+Z undo, Ctrl+Y redo, Delete remove selected annotation
- **Scroll capture** (UX from TDS): after selecting a region, use the toolbar **Scroll capture** button — hole-punch the overlay, auto-wheel, then Done/Save stitches once (MathNet.Numerics; apps that ignore `WM_MOUSEWHEEL` may not scroll)
- **Text extraction (OCR)**: recognize text in the selection; editable result window; closing returns to the overlay
- **Pin image**: lightweight always-on-top window (drag to move; Esc / double-click / right-click to close); does not end the capture session
- **Image translation**: OCR boxes → translate → erase original and redraw translated text; “re-render” / “save image”
- **Session keep-alive**: OCR / translate hide the overlay first, then restore the same selection and annotations; pin leaves the overlay open
- Multi-monitor + high-DPI (capture the screen under the cursor in physical pixels)

UI language follows PowerToys (built-in English + Simplified Chinese).

## Requirements

- [PowerToys](https://github.com/microsoft/PowerToys) **≥ 0.100** (.NET 10 host)
- **x64** PowerToys (OCR components are x64-only)
- Windows (WPF plugin)

## Install

1. Download `Snip-<ver>.zip` (slim, recommended) or `Snip-<ver>-full.zip` from [Releases](https://github.com/Jotaro2025/PowerToys-Run-Snip/releases)
2. Place the extracted `Snip\` folder at:

```
%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Snip\
```

(On PowerToys 0.9x and earlier: `%LOCALAPPDATA%\Microsoft\PowerToys\RunPlugins\`.)

3. Restart PowerToys

Typical layout:

```
Snip\
├── Community.PowerToys.Run.Plugin.Snip.dll
├── plugin.json
├── Images\…
├── WeChatOcr.dll, Google.Protobuf.dll, Newtonsoft.Json.dll   # OCR managed deps (both packs)
├── Sdcb.HyMT2Sharp.dll, …                                    # in-process translate (no GGUF)
├── wco_data\…                                                # full pack only; slim downloads on first OCR
└── zh-CN\….resources.dll
```

Loaded with `DynamicLoading`; `Wox.Plugin` and related assemblies come from PowerToys and are not copied into the plugin output.

## Usage

1. Open PowerToys Run and type `snip`
2. Choose **Region screenshot**, then annotate / OCR / translate / pin / copy / save
3. Or choose **Settings** for the dedicated settings window
4. If a global hotkey is configured, it starts a region capture directly

## Settings

### Dedicated settings window (recommended)

PowerToys Run → `snip` → **Settings**. Tabs: General / Hotkey / Translation / About. Translation fields fold by engine; global hotkey defaults to empty (disabled). Saves back to PowerToys Run `settings.json`, kept in sync with the system settings page.

### PowerToys system settings page

PowerToys Settings → PowerToys Run → Snip **AdditionalOptions** still work. Common items:

| Option | Notes |
|--------|--------|
| Save folder | Empty → `Pictures\Snip` |
| Copy to clipboard after capture | On by default |
| Auto-save after region capture | |
| Capture delay (ms) | Default 150, time for Run to hide |
| Global hotkey | e.g. `Ctrl+Shift+S`; empty disables |
| Image translation engine | `Off` / `HTTP (OpenAI-compatible)` / `HyMT2Sharp (local GGUF)` |
| Target language | Label prefix `Translation ·` |
| HTTP · Base URL / API Key / model | OpenAI-compatible endpoint |
| HyMT · GGUF path / download recommended / custom URL / backend | Auto / CPU / Vulkan |

> **Folding limit:** `AdditionalOptions` **cannot** hide fields dynamically per engine; use the dedicated settings window for that. The system page uses label prefixes to reduce noise.

## Text extraction (OCR)

Uses the [WeChatOcr](https://www.nuget.org/packages/WeChatOcr/) NuGet package (see Acknowledgments). **WeChat app install is not required**; recognition runs locally.

- **Slim pack (default `Snip-<ver>.zip`)**: no `wco_data`; first “Text extract” or “Image translate” prompts to download the WeChatOcr NuGet package into  
  `%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\wco_data\`
- **Full pack**: ships `wco_data\` for offline OCR (~61 MB larger)
- Managed DLLs (`WeChatOcr.dll`, etc., ~1.3 MB) ship in both packs
- **x64 only**; missing / timeout / failed OCR shows a message in the result window
- Closing the result window cancels in-flight recognition; a busy engine shows a wait hint

## Image translation

Reuses WeChat OCR line boxes; after translation, erases the original and redraws the translated text. Multi-line results are batched to the engine. The result window supports editing the right-hand text then “Re-render”, plus “Save image”.

Translation is **off** by default; pick one engine in settings:

1. **HTTP (OpenAI-compatible)**  
   Any `POST /v1/chat/completions` service, e.g. [HyMT2Sharp.Server](https://github.com/sdcb/HyMT2Sharp): `http://127.0.0.1:8080/v1`. Optional API key and model name.

2. **HyMT2Sharp (in-process)**  
   References [`Sdcb.HyMT2Sharp`](https://www.nuget.org/packages/Sdcb.HyMT2Sharp); **does not** bundle GGUF. Recommended official [Hy-MT2-1.8B-2Bit.gguf](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF) (~573 MB):
   - Check “Download recommended 2Bit model” in settings and save, or follow the prompt on first “Image translate”
   - Cache: `%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\models\Hy-MT2-1.8B-2Bit.gguf`
   - Download sources (in order, China-first): [ModelScope](https://www.modelscope.cn/models/Tencent-Hunyuan/Hy-MT2-1.8B-2Bit-GGUF) → hf-mirror → [Hugging Face](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF)
   - Resume + SHA-256 verify; optional custom HTTPS URL or manual GGUF path
   - Backend: Auto / CPU / Vulkan (~4 GB VRAM for Vulkan); model cached by path+backend; inference is serialized

If misconfigured, “Image translate” shows a prompt and does not close the capture window.

## Build & test

Requires the .NET 10 SDK and an installed PowerToys (for `Wox.Plugin` etc.). For CI / no full install, use repo `build/PowerToysRefs` as `-p:PowerToysInstallDir=...` (see `.github/workflows/ci.yml`).

```powershell
dotnet build Community.PowerToys.Run.Plugin.Snip
# If PowerToys is elsewhere:
dotnet build Community.PowerToys.Run.Plugin.Snip -p:PowerToysInstallDir="D:\PowerToys"

dotnet test Community.PowerToys.Run.Plugin.Snip.UnitTests
```

Built with the same rule set as PowerToys (StyleCop + .NET analyzers, warnings as errors).

## Pack & release

```powershell
# Windows (local PowerToys, or -PowerToysInstallDir)
.\scripts\pack-release.ps1
.\scripts\pack-release.ps1 -PowerToysInstallDir "D:\PowerToys"
```

```bash
# Linux / CI cross-compile (EnableWindowsTargeting)
./scripts/pack-release.sh --powertoys-dir /path/to/PowerToysOrStubs
```

The script Release-builds, stages to `artifacts/staging/Snip/` (skips `.pdb`), self-checks key files, and produces:

- **`Snip-<Version>-slim.zip`** / **`Snip-<Version>.zip` (default, recommended)**: no `wco_data`; OCR downloads on demand
- **`Snip-<Version>-full.zip`**: includes `wco_data` for offline OCR / translate

Windows manual checklist: [docs/windows-e2e-checklist.md](docs/windows-e2e-checklist.md).

## Develop inside the PowerToys tree

To land the plugin in [microsoft/PowerToys](https://github.com/microsoft/PowerToys), copy `Community.PowerToys.Run.Plugin.Snip` under `src/modules/launcher/Plugins/` and replace the four `<Reference>` entries with a project reference:

```xml
<ItemGroup>
  <ProjectReference Include="..\..\Wox.Plugin\Wox.Plugin.csproj">
    <Private>false</Private>
  </ProjectReference>
</ItemGroup>
```

(`Wox.Plugin` transitively references `ManagedCommon`, `Common.UI`, `Settings.UI.Library`.)

## License

New code in this repository is released under the MIT License — see [LICENSE](LICENSE). Third-party licenses and usage limits are listed under Acknowledgments & Sources above.
