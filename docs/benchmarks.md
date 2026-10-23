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
