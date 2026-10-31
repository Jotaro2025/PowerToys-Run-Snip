# 本地翻译 API（浏览器扩展）

插件内置一个只监听 `127.0.0.1` 的翻译服务，把进程内的 **HyMT2Sharp** 模型暴露给浏览器扩展。PowerToys Run 常驻时，浏览器和图片翻译共用同一份模型、同一份翻译缓存。

## 启用

1. 设置 → 图片翻译 → 翻译引擎选 **HyMT2Sharp**，并选好 GGUF。
2. 同一页底部 **本地翻译 API**：
   - 勾选启用；
   - 端口默认 `17853`；
   - 复制 **Bearer Token**（可重新生成）；
   - 允许来源可留空（任意来源，仍需 Token），或填扩展的 `chrome-extension://<id>`。
3. 保存后状态应显示 `Listening on http://127.0.0.1:17853/v1/translate`。

PowerToys Run 重启后服务会随插件自动恢复；插件卸载/禁用时端口关闭。

## 安全模型

- 只绑定 `127.0.0.1`，不监听局域网；校验 `Host`，防 DNS rebinding。
- 所有请求必须带 `Authorization: Bearer <token>`（常量时间比较）。
- `Allowed origins` 非空时，带 `Origin` 的请求必须匹配（大小写不敏感）；留空则回显 `Access-Control-Allow-Origin: *`，但 Token 仍然是必需的。
- 限流：120 请求/分钟、最多 4 个并发连接；请求体 ≤ 1 MB，≤ 200 条文本，单条 ≤ 4000 字符，总计 ≤ 20000 字符。
- 不记录请求原文；日志只记录错误消息。
- 只暴露本地 HyMT 引擎，不会把 HTTP/腾讯等远程提供商的 Key 转发给浏览器。

## API

### `GET /v1/health`

```bash
curl -H "Authorization: Bearer <token>" http://127.0.0.1:17853/v1/health
```

```json
{ "status": "ok", "engine": "hymt", "port": 17853 }
```

### `POST /v1/translate`

```bash
curl -X POST http://127.0.0.1:17853/v1/translate \
  -H "Authorization: Bearer <token>" \
  -H "Content-Type: application/json" \
  -d '{"texts":["Hello world","Open settings"],"target":"简体中文"}'
```

```json
{ "translations": ["你好，世界！", "打开设置"], "count": 2 }
```

- `target` 可省略，默认使用设置中的目标语言。
- 相同原文只翻译一次；翻译记忆按「引擎 + 模型 + 目标语言」命中，重复页面通常 1–3 ms。
- 首次请求可能需要加载 GGUF（本机约 2 s），之后保持常驻；插件侧的闲置卸载设置同样生效。

### 错误

| 状态码 | 含义 |
|---|---|
| 400 | JSON/字段错误、Host 不合法 |
| 401 | Token 缺失/错误 |
| 403 | Origin 不在允许列表 |
| 404 | 路径不存在 |
| 411 | 既没有 `Content-Length` 也不是 `Transfer-Encoding: chunked` |
| 413 | 请求体/文本数量/长度超限 |
| 429 | 限流或并发已满 |
| 503 | 未选择 HyMT2Sharp，或 GGUF 不存在/加载失败 |

## 浏览器扩展示例

见 [`samples/browser-extension`](../samples/browser-extension)：

1. Chrome/Edge → 扩展 → 打开开发者模式 → 加载已解压的扩展。
2. 扩展选项里填 API 地址（默认 `http://127.0.0.1:17853/v1/translate`）和 Token。
3. 选中网页文字 → 右键 → **用 Snip 翻译所选内容**。

示例只依赖 `host_permissions: ["http://127.0.0.1/*"]`、右键菜单、通知；不需要额外服务或云端 Key。

## 为什么不单独做服务

PowerToys Run 默认常驻，插件进程就是现成的模型宿主。把服务放插件内可以：

- 复用已加载的 HyMT 实例，避免浏览器和插件各占 ~600 MB；
- 复用翻译记忆/去重；
- 不新增安装包、托盘程序或开机项。

如果以后需要浏览器在 PowerToys 未运行时也能翻译，再把同样的 API 抽成独立 companion 服务即可，协议不变。
