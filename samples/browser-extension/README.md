# Snip Local Translate（示例扩展）

最小 MV3 扩展示例：选中网页文字 → 右键 → **用 Snip 翻译所选内容**，结果通过系统通知显示。

## 安装

1. 在 Snip 设置 → 图片翻译 → 本地翻译 API：
   - 勾选启用；
   - 翻译引擎必须是 **HyMT2Sharp**；
   - 复制 **Bearer Token**。
2. Chrome/Edge → `chrome://extensions` → 打开「开发者模式」→「加载已解压的扩展」→ 选择本目录。
3. 扩展 → 选项：确认 API 地址（默认 `http://127.0.0.1:17853/v1/translate`），粘贴 Token，保存。
4. 选中任意网页文字 → 右键 →「用 Snip 翻译所选内容」。

## 说明

- `host_permissions` 使用 `http://127.0.0.1/*`，不限定端口；改端口不用改 manifest。
- 扩展只调用本机 loopback，不经过任何云端服务。
- 如果 Snip 的「允许来源」填了具体值，需要把扩展的 `chrome-extension://<id>` 加进去；留空则任意来源可用（仍需 Token）。
- 通知需要 `notifications` 权限；如果不想用通知，可以改成打开一个结果页。

完整 API 文档见 [`docs/local-translation-api.md`](../../docs/local-translation-api.md)。
