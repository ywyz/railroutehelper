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
| `sequence` | integer | 数据源内的连续递增序号；回放层检查重复、乱序和缺号。 |
| `capturedAtUtc` | RFC 3339 timestamp | 捕获时间，使用 UTC 偏移。 |
| `messageType` | string | 消息类型；当前定义 `runtime-snapshot`、`operations-report` 和 `save-monitor-diagnostic`。 |
| `payload` | JSON value | 由消息类型定义的负载。 |

兼容策略：

- 同一主版本可以增加 `payload` 字段，读取方应忽略不认识的负载字段。
- 删除或改变已有字段语义、改变信封字段类型时必须提升协议版本。
- 当前实现仅接受 v1，避免把未来消息静默解释为错误状态。
- 一份记录的首个序号可以是任意值；之后每条消息必须严格等于前一条加一。
- 协议层不绑定 TCP、WebSocket、命名管道或 Unix socket；传输由外层 Adapter
  决定。

## Operations 报告

`operations-report` 的 `payloadVersion` 当前为 `1`。负载包含：

- `sourceSaveName`：仅文件名，不记录绝对路径；
- `schemaId`、`gameVersion`、`gameTimeTicks`；
- `networkId`：由轨道端点和站台轨道生成的 SHA-256 拓扑身份；
- `report`：列车运行判断和进路变化。

外层 `protocolVersion` 与负载的 `payloadVersion` 独立演进。增加一种消息类型或
向负载添加可忽略字段不需要提升信封版本；改变已有字段语义时必须提升对应版本。

`save-monitor-diagnostic` 同样具有独立的 `payloadVersion=1`，只包含源文件名、
稳定错误码和脱敏说明。

## Runtime 快照

`runtime-snapshot` 的 `payloadVersion` 当前为 `1`，schema ID 为
`rail-route-runtime/v1`。负载只包含：

- `sessionId`：每次采集器启动生成的新身份；
- `networkId`：当前地图/网络身份；
- `snapshot`：与数据源无关的 `OperationalSnapshot`。

TCP Adapter 只监听 IPv4 loopback，默认端口为 `5081`。每个连接使用同一种
UTF-8 JSONL 编码，单帧上限 4 MiB。接收端允许断开后重新连接，但同一
`sessionId` 的 `sequence` 必须严格递增；重复或倒退帧只关闭当前连接，不清空
已有投影，也不停止监听后续连接。

运行时快照不携带游戏对象、程序集类型名或任意对象转储。采集侧必须先把列车、
轨道、车站、道岔/信号连接和占用状态归一化为领域 DTO。
