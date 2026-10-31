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
- **Scroll capture** (UX from TDS): after selecting a region, use the toolbar **Scroll capture** button — hole-punch the overlay, auto-wheel, then Done/Save stitches once. Wheel prefers `SendInput(MOUSEEVENTF_WHEEL)` (works with Chromium/Edge), then `PostMessage`/`SendMessage(WM_MOUSEWHEEL)` fallback; overlay is session-hidden once to reduce flicker; mid-stop via floating **Stop** strip / Esc / Space keeps frames already captured
- **Text extraction (OCR)**: recognize text in the selection; editable result window; closing returns to the overlay
- **Pin image**: lightweight always-on-top window (drag to move; Esc / double-click / right-click to close); does not end the capture session
- **Image translation**: OCR boxes → translate → erase original and redraw translated text; “re-render” / “save image”
- **Session keep-alive**: OCR / translate hide the overlay first, then restore the same selection and annotations; pin leaves the overlay open
- Multi-monitor + high-DPI: with one monitor only that monitor is captured (unchanged); with several, the overlay covers the whole virtual desktop so **a selection can span monitors**, captured in physical pixels and converted with the overlay window's actual DPI under mixed scaling (e.g. 100% + 150%); the toolbar and scroll hint stay on the monitor holding the selection. Areas of the virtual desktop not covered by any monitor (different monitor sizes) capture as black
- **WinRT Graphics Capture** (preferred, GDI BitBlt fallback): full-screen / active-window captures keep the cursor and composited content more reliably; multi-monitor virtual desktop, scroll tiles, and arbitrary rects stay on GDI. Export is SDR BGRA32; HDR displays are captured as FP16 scRGB and tone-mapped to SDR with the system SDR white level, avoiding the washed-out look of the clipped 8-bit composition. Needs Windows 10 1903+. Protected content may be black and automatically falls back to GDI. See “Capture backends” below.

UI language follows PowerToys (built-in English + Simplified Chinese).

## Requirements

- [PowerToys](https://github.com/microsoft/PowerToys) **≥ 0.100** (.NET 10 host)
- **x64** or **ARM64** PowerToys: the WeChat OCR component is x64-only; ARM64 (or when chosen) uses the built-in **Windows OCR**, which needs an installed OCR language (see Text extraction)
- Windows (WPF plugin)


## Capture backends

| Path | Used for | Limits |
|------|----------|--------|
| **WinRT Graphics Capture** | Single-monitor full screen, active window (`CreateForMonitor` / `CreateForWindow`) | Win10 1903+; optional cursor; HDR→FP16 scRGB tone-mapped to SDR BGRA32; DRM/protected content may be black |
| **GDI BitBlt** (fallback) | Virtual-desktop spanning, scroll tiles, rect crops, any WinRT failure | No hardware cursor composition; rect/scroll captures on HDR displays route through the WGC FP16 path |

On Windows, `CaptureFactory` returns `PreferWinRtCaptureService`: WinRT when possible, otherwise silent GDI fallback.

## Install

### Option 1: installer (recommended)

Download the installer matching your **Windows architecture** from [Releases](https://github.com/Jotaro2025/PowerToys-Run-Snip/releases):

| File | Arch | Contents | Size (approx.) |
|---|---|---|---|
| `Snip-Setup-<ver>-x64-slim.exe` | x64 | Same as the slim zip; `wco_data` downloads on first OCR | 7 MB |
| `Snip-Setup-<ver>-x64-full.exe` | x64 | slim + bundled `wco_data` (~61 MB) for offline OCR / translate | 35 MB |
| `Snip-Setup-<ver>-arm64-slim.exe` | ARM64 | Same as x64-slim | 7 MB |
| `Snip-Setup-<ver>-arm64-full.exe` | ARM64 | Same as x64-full; on ARM64 OCR uses Windows OCR and the bundled `wco_data` is unused, so slim is usually enough | 35 MB |

- **Prerequisite: PowerToys ≥ 0.100 must be installed** (the first .NET 10 host; this plugin targets `net10.0-windows`). Setup checks `%LOCALAPPDATA%\PowerToys\`, `%ProgramFiles%\PowerToys\` and the Uninstall registry; if PowerToys is missing or too old it points to [PowerToys Releases](https://github.com/microsoft/PowerToys/releases) / `winget install Microsoft.PowerToys` and exits.
- **Per-user, no admin rights (no UAC)**; installs to `%LOCALAPPDATA%\Microsoft\PowerToys\PowerToys Run\Plugins\Snip\`.
- If PowerToys is running, setup asks before closing `PowerToys.exe` / `PowerToys.PowerLauncher` and restarts it from the same path afterwards (only if it was running). Uninstall does the same.
- The x64 and ARM64 installers are separate; setup refuses to run on the wrong architecture.
- **Upgrade**: just run the newer installer. The old plugin files are removed before copying (slim and full can replace each other). Settings and downloaded OCR / HyMT data (`%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\`) are never deleted.
- **Uninstall**: remove *Snip for PowerToys Run* from Settings → Apps. Data is kept by default; uninstall asks whether to delete it too (default *No*). For a silent uninstall pass `/DELETEDATA` to delete it.
- **The HyMT translation model is not bundled in any installer**: the GGUF model is several GB. It downloads on demand into the data folder above the first time you translate an image.

### Option 2: extract the zip manually

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

Three engines, all local; their results feed both Text extract and Image translate. Setting **OCR engine** (dedicated settings window → General / PowerToys settings page):

| Option | Behavior |
|---|---|
| **Auto** (default) | x64 with the WeChat OCR component present (full pack or downloaded) → WeChat OCR; otherwise → Windows OCR (no download prompt) |
| **WeChat OCR** | Always WeChat OCR; the slim pack prompts to download the component on first use |
| **Windows OCR** | Always the built-in `Windows.Media.Ocr` |
| **PaddleOCR (built-in offline)** | Pure C# PP-OCRv6 with embedded models, no download, x64 and ARM64 |

An ARM64 PowerToys always uses Windows OCR or PaddleOCR (WeChat OCR ships only x64 native code).

### PaddleOCR compute backend (AMD / NVIDIA / Intel GPU acceleration)

When the PaddleOCR engine is selected, **PaddleOCR compute backend** is available:

| Option | Behavior |
|---|---|
| **CPU** (default) | Managed SIMD kernels, most stable, loads no graphics driver |
| **Auto** | Library heuristic: uses a GPU only when it benchmarks faster than the CPU |
| **Vulkan GPU** | Forces the system `vulkan-1.dll` (AMD / NVIDIA / Intel) |

Measured on a Ryzen 9 9900X + Radeon RX 9070, 3440×1440 screen with 60 text lines: CPU ~550 ms, Vulkan / Auto ~60 ms — about **8–9×**, identical output. Note that Vulkan loads the graphics driver and third-party Vulkan layers (Steam / Epic, etc.) inside the PowerToys Run host process, so a driver fault would take the host down with it; CPU stays the default and Vulkan is opt-in. Changing the backend rebuilds the pipeline on the next recognition, and the pipeline is prewarmed while the snip overlay opens.

### WeChat OCR

Uses the [WeChatOcr](https://www.nuget.org/packages/WeChatOcr/) NuGet package (see Acknowledgments). **WeChat app install is not required**.

- **Slim pack (default `Snip-<ver>.zip`)**: no `wco_data`; first “Text extract” or “Image translate” prompts to download the WeChatOcr NuGet package into  
  `%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\wco_data\`
- **Full pack**: ships `wco_data\` for offline OCR (~61 MB larger)

- **Download sources**: prefer the [nuget.org](https://www.nuget.org/) flat container; on failure fall back to the [Azure CN mirror](https://nuget.cdn.azure.cn/) (`nuget.cdn.azure.cn`). **Every** source must pass the pinned SHA-256 (`OcrAssetsDownloader.ExpectedSha256`); a mirror whose bytes differ from that nuget.org package is rejected. Do not reorder to prefer the CN mirror unless nuget.org is unreachable long-term and you have confirmed the hash still matches.
> **PowerToys Additional Options · API key**: the box stays empty with a “leave empty to keep the saved key” placeholder and **never** shows `dpapi:v1:` ciphertext. Empty on save keeps the key; clear it only in the **Snip settings window**.- Managed DLLs (`WeChatOcr.dll`, etc., ~1.3 MB) ship in both packs
- **x64 only**; missing / timeout / failed OCR shows a message in the result window
- Closing the result window cancels in-flight recognition; a busy engine shows a wait hint

### Windows OCR

- Built into Windows, no download, native on x64 and ARM64; Chinese accuracy is usually below WeChat OCR, and it gives no rotated quads (translated text is drawn into axis-aligned boxes)
- Needs an installed OCR language: Settings → Time & language → Language & region → add **Chinese (Simplified, China)** (or the language you need) and make sure its "Optical character recognition" feature is installed; or run in an elevated PowerShell  
  `Add-WindowsCapability -Online -Name "Language.OCR~~~zh-CN~0.0.1.0"`, then restart PowerToys
- The recognizer language follows your Windows language list (first language with OCR installed), falling back to Simplified Chinese
- Very tall scroll captures are recognized in strips (each limited by `OcrEngine.MaxImageDimension`)

## Image translation

Reuses OCR line boxes; after translation, erases the original and redraws the translated text. Multi-line results are batched to the engine (HyMT 12 lines per batch; HTTP/Tencent 20 lines with up to 3 requests in flight). The result window is **image-first** (processing animation; translation list collapsed under “Edit translations”), with Re-render / Save image; Cancel or Esc aborts while busy.

**Translation memory**: exact source→translation pairs are cached per engine + model + target language in
`%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\translation-memory.json`
(LRU, up to 5000 entries). Re-capturing the same screen hits the cache and skips the model; repeated strings within one capture are translated once. Disable or clear it on the Image translation page of the settings window.

Translation is **off** by default; pick one engine in settings:

1. **HTTP (OpenAI-compatible)**  
   Any `POST /v1/chat/completions` service, e.g. [HyMT2Sharp.Server](https://github.com/sdcb/HyMT2Sharp): `http://127.0.0.1:8080/v1`. Optional API key and model name.
   The API key is stored encrypted with Windows DPAPI (current user) in PowerToys Run `settings.json` (as `dpapi:v1:…`; a legacy plaintext key is re-saved encrypted the first time it is read). **After restoring or syncing those settings on another PC or Windows account the key cannot be decrypted and must be entered again.** The PowerToys settings page shows the ciphertext in that textbox; just type a new plaintext key there and it is encrypted on the next read.

2. **HyMT2Sharp (in-process)**  
   References [`Sdcb.HyMT2Sharp`](https://www.nuget.org/packages/Sdcb.HyMT2Sharp); **does not** bundle GGUF. Recommended official [Hy-MT2-1.8B-2Bit.gguf](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF) (~573 MB):
   - Check “Download recommended 2Bit model” in settings and save, or follow the prompt on first “Image translate”
   - Cache: `%LOCALAPPDATA%\PowerToys\RunPlugins\Community.PowerToys.Run.Plugin.Snip\models\Hy-MT2-1.8B-2Bit.gguf`
   - Download sources (in order, China-first): [ModelScope](https://www.modelscope.cn/models/Tencent-Hunyuan/Hy-MT2-1.8B-2Bit-GGUF) → hf-mirror → [Hugging Face](https://huggingface.co/tencent/Hy-MT2-1.8B-2Bit-GGUF)
   - Resume + SHA-256 verify; optional custom HTTPS URL or manual GGUF path
   - Backend: Auto / CPU / Vulkan (~4 GB VRAM for Vulkan); model cached by path+backend; inference is serialized

If misconfigured, “Image translate” shows a prompt and does not close the capture window.

## winget (draft)

[`winget/`](winget/) holds draft manifests for `Jotaro2025.PowerToysRunSnip` pointing at Release `Snip-Setup-<ver>-{x64,arm64}-slim.exe`. **Submit to [winget-pkgs](https://github.com/microsoft/winget-pkgs) only after assets exist and SHA256 values are filled**; see [`winget/README.md`](winget/README.md).

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

The version comes from `Version` in `plugin.json`; see [CHANGELOG.md](CHANGELOG.md) for changes. Pushing a `v<Version>` tag lets CI attach the artifacts to a GitHub Release.

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

### Installers (Inno Setup)

Requires [Inno Setup](https://jrsoftware.org/isinfo.php) **6.5+** (`choco install innosetup` / `winget install JRSoftware.InnoSetup`).

```powershell
.\build\build-installer.ps1 -PowerToysInstallDir "D:\PowerToys"
# One arch / variant only: -Architectures x64 -Variants slim; explicit ISCC: -IsccPath "C:\...\ISCC.exe"
```

The script builds `win-x64` and `win-arm64`, stages the slim layout (no `wco_data`, no `.pdb`, same as the slim zip) and the full layout, then compiles the four installers from `installer/Snip.iss` into `artifacts/installer/`. The version comes from `plugin.json` and is passed as `ISCC /DAppVersion=…`. On Linux ISCC can run under wine (pass the Windows-side path as `-IsccPath`).

CI (`.github/workflows/installer.yml`) builds the four installers and the zips on Windows and uploads them as artifacts; pushing a `v*` tag also attaches them to the GitHub Release.

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

- **Check for plugin updates** (Additional Options, default off): when enabled, one quiet GitHub Releases lookup compares the latest tag to `plugin.json`; private/offline failures stay silent.
