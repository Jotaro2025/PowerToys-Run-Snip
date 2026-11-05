# 性能基准（BenchmarkDotNet）

`Community.PowerToys.Run.Plugin.Snip.Benchmarks` 覆盖 perf 改动触及的图像处理热路径：

| 基准 | 覆盖 |
|---|---|
| `ScrollStitcherBenchmarks.AlignTiles_WithHint` | 捕获期逐帧对齐（hint 窗口 + SAD 精修，1280×720） |
| `ScrollStitcherBenchmarks.AlignTiles_FftColdStart` | tile#1 冷启动（FFT + 全 dh 扫描） |
| `ScrollStitcherBenchmarks.EstimateContributedRows` | 采集期的投影高度估算 |
| `ScrollPhaseCorrelatorBenchmarks.EstimateVerticalShift` | radix-2 FFT 相位相关（512×512 封顶后） |
| `ComposeStitchBenchmarks.StitchTiles` | 终态拼接合成（8 tile × 1280×720） |
| `LightInpaintBenchmarks.TryInpaintRect` | OCR 擦除 inpaint（32/96/160 方框） |
| `PixelMagnifierBenchmarks.FillZoomedGrid` | 放大镜网格填充（1920/2560/3840 源宽） |

## 运行

```powershell
# 全部（时间长，本地跑；不进 CI）
dotnet run -c Release --project Community.PowerToys.Run.Plugin.Snip.Benchmarks

# 按过滤器
dotnet run -c Release --project Community.PowerToys.Run.Plugin.Snip.Benchmarks -- -f "*StitchTiles*"
dotnet run -c Release --project Community.PowerToys.Run.Plugin.Snip.Benchmarks -- -f "*Inpaint*"

# 列出所有用例
dotnet run -c Release --project Community.PowerToys.Run.Plugin.Snip.Benchmarks -- --list flat

# 快速冒烟（放弃精度换速度）
dotnet run -c Release --project Community.PowerToys.Run.Plugin.Snip.Benchmarks -- --job short
```

结果写入 `BenchmarkDotNet.Artifacts/`（默认 Markdown + CSV）。对比两次提交时，分别在两个 worktree 里跑同一过滤器，用生成的表格对齐 Mean / Allocated。

注意：

- 必须 `-c Release`（Debug 结果无意义）。
- 与 PowerToys 安装相关的引用沿用 `PowerToysInstallDir` 属性，和单测项目一致。
- 单测项目（`dotnet test`）仍是 CI 门禁；基准只做本地回归参照。

## 2026-11-05 本机短测基线

基于 `23c38b3` 加本地翻译 API、模型卸载和 HTTP 响应限制改动。Release 使用 .NET SDK 10.0.402 编译，运行时为 .NET 10.0.13 x64，Windows 11 10.0.29698.1000。
BenchmarkDotNet 0.15.2 使用 `--job short --inProcess`：1 次启动、3 次预热、3 次测量；过滤器为 `*AlignTiles_WithHint*`、`*TryInpaintRect*`、`*FillZoomedGrid*`。

| 方法 | 参数 | Mean | StdDev | 每次托管分配 |
|---|---|---:|---:|---:|
| `AlignTiles_WithHint` | 1280×720 | 10.723 ms | 0.025 ms | 17.17 KiB |
| `TryInpaintRect` | 32×32 | 602.7 μs | 22.36 μs | 13.69 KiB |
| `TryInpaintRect` | 96×96 | 6.037 ms | 1.437 ms | 78.71 KiB |
| `TryInpaintRect` | 160×160 | 14.081 ms | 0.347 ms | 159.73 KiB |
| `FillZoomedGrid` | 源宽 1920 | 497.2 ns | 16.69 ns | 未检出 |
| `FillZoomedGrid` | 源宽 2560 | 486.2 ns | 3.58 ns | 未检出 |
| `FillZoomedGrid` | 源宽 3840 | 484.6 ns | 4.68 ns | 未检出 |

这些数据用于后续同机比较，不代表相对旧版本的提升。短测样本少，96×96 擦除的波动较大；正式性能结论需要重复独立进程测量。擦除基准复用并修改同一像素数组，反映重复调用热路径，不代表每次处理新截图的端到端耗时。分配列只统计托管内存，无法代替进程峰值内存测量。

本轮未完成真实 PowerToys 截图、混合 DPI、HDR、安装升级和 ARM64 运行验收；这些仍以 `windows-e2e-checklist.md` 为准。原始 Markdown、CSV 和日志保存在本机忽略目录 `artifacts/validation/benchmarks-20261105/`。
