# WeChat OCR 优化笔记

上游：<https://github.com/ZGGSONG/WeChatOcr>（MIT，NuGet `WeChatOcr 1.0.5`）。
`WeChatOCR.exe`、`mmmojo_*.dll` 与 `Model/*.xnet` 是腾讯的闭源二进制；**能优化的是托管包装层和插件集成层，不能改原生推理**。

## 实测拆解（Ryzen 9 9900X + RX 9070，3440×1440、60 行文字）

| 项目 | 耗时 |
|---|---:|
| `new ImageOcr()`（预加载子进程） | 15 ms（系统缓存热）/ 69–86 ms |
| 原生 OCR（BMP 输入，预热后） | ~237–247 ms |
| 原生 OCR（PNG 输入，预热后） | ~266–269 ms |
| `OcrResponse.ToString()` + JSON 解析（60 行） | **0.44 ms/次** |
| 直接 `OcrResponse.Parser.ParseFrom` | 0.018 ms/次 |
| 插件服务冷路径（PNG 预编码） | 715 ms |
| 插件服务冷路径（BMP 直出，跳过预编码） | **562 ms** |

结论：

- 原生推理占绝对大头（~250 ms），没有源码无法优化。
- 包装层的 protobuf→文本→JSON 二次解析只有 0.44 ms，**不值得为此 fork**。
- 真正的收益在集成层：PNG 预编码（WPF `PngBitmapEncoder`，全屏约 50–200 ms）可以完全跳过，改为直接写 BMP。

## 已落地（插件侧）

1. `WeChatOcrService.RecognizeLinesAsync(BitmapSource)`：用 `BmpBitmapEncoder` 直接写临时 BMP，交给 `ImageOcr.Run(path, callback)`；原生支持 BMP。
2. `SnipWindow` 只在 **Windows OCR** 时才预编码 PNG；微信 OCR 和 PaddleOCR 都直接传位图。
3. 截图覆盖层打开时预热 `ImageOcr`（`PrewarmOcrOnCapture`，默认开），子进程启动成本被选区操作覆盖。

## 上游可改但不改的点

| 候选 | 收益 | 结论 |
|---|---|---|
| `Run(byte[])` 固定临时文件名 `generate4wechat.*` 且不删除 | 跨进程可能互相覆盖 | 我们不用该 API，自己用 GUID 文件名 |
| `CallUserCallback` 走 `ToString()` + Newtonsoft JSON | 0.44 ms/60 行 | 收益太小，不值得维护 fork |
| `Run` 吞掉异常、只靠调用方超时 | 可观测性 | 插件已有 3×4 s 重试与超时提示 |
| 无 `CancellationToken` / `Task` API | 结构 | 插件用 TCS + 轮询封装 |

如果未来要向 ZGGSONG/WeChatOcr 提 PR，优先做“唯一临时文件 + 清理”和“错误传播”，而不是性能。

## 更快的替代

微信 OCR 预热后 ~250–300 ms；PaddleOCR + Vulkan 在同机全屏 60 行约 **60–100 ms**，且无外部子进程。要极致速度优先选 PaddleOCR（内置离线、x64/ARM64）。
