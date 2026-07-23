# 实时协议 v1

协议使用 UTF-8 JSON Lines（JSONL）：每条消息是一个 JSON 对象，并以单个 LF
字节结束。记录文件和未来的本地实时传输使用相同信封。

```json
{"protocolVersion":1,"sequence":42,"capturedAtUtc":"2026-01-02T03:04:05+00:00","messageType":"snapshot","payload":{"source":"synthetic"}}
```

字段：

| 字段 | 类型 | 说明 |
| --- | --- | --- |
| `protocolVersion` | integer | 当前固定为 `1`；未知版本必须拒绝，不能猜测解析。 |
| `sequence` | integer | 数据源内的单调递增序号；回放层负责检查顺序。 |
| `capturedAtUtc` | RFC 3339 timestamp | 捕获时间，使用 UTC 偏移。 |
| `messageType` | string | 消息类型；第一阶段定义 `snapshot`。 |
| `payload` | JSON value | 由消息类型定义的负载。 |

兼容策略：

- 同一主版本可以增加 `payload` 字段，读取方应忽略不认识的负载字段。
- 删除或改变已有字段语义、改变信封字段类型时必须提升协议版本。
- 当前实现仅接受 v1，避免把未来消息静默解释为错误状态。
- 协议层不绑定 TCP、WebSocket、命名管道或 Unix socket；传输由外层 Adapter
  决定。

