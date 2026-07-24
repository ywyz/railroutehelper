# 本机运行态投影、告警与 Web 仪表盘

`RailRouteHelper.LiveOperations` 位于版本化协议和展示层之间。实时监听与 JSONL
回放都把同一种 `RealtimeEnvelope` 交给单一公共接口：

```csharp
var projector = new LiveOperationsProjector();
projector.Apply(envelope);
var state = projector.Current;
```

`Current` 是可并发读取的不可变快照。投影器不读取存档、不运行拓扑推理，也不依赖
Web；这些职责分别保留在 Monitoring、Operations 和展示 Adapter 中。

## 投影内容

每个 `networkId` 独立维护：

- 最新信封序号、捕获时间、源存档文件名、schema、游戏版本和游戏时间；
- 最新列车运行判断；
- 最新一帧的进路变化；
- 最近 100 条带信封序号和捕获时间的进路变化时间线。

多个地图不会互相覆盖。默认保留上限可通过
`LiveOperationsProjector` 构造参数收紧；投影器重启后可以用已有 JSONL 回放重建
状态，不把运行态历史写入项目或游戏目录。

## 告警生命周期

第一版只从 `TrainOperationalStatus.PossibleBlocked` 生成
`PossibleBlockedTrain` Warning。它沿用 Operations 的保守证据边界，不表示列车
已经被证明“卡死”。

生命周期规则：

1. 某地图中的某列车首次进入 `PossibleBlocked` 时打开一个告警；
2. 后续报告仍为该状态时更新同一活动告警的最后观察时间、序号和观察次数；
3. 同一地图的后续报告中列车恢复其他状态或不再出现时，将告警标记为
   `Resolved`，并保留解决序号和时间；
4. 已解决后再次出现同一条件时，使用相同 fingerprint 打开新的 alert ID，避免
   把两次独立事件合并。

活动告警不受历史上限影响；默认最多保留最近 200 条已解决告警。Web API 同时返回
活动和已解决实例，调用方以 `status` 区分。

## localhost Web

启动：

```shell
dotnet run --project src/RailRouteHelper.Web -- \
  "/path/to/saves" --listen "http://127.0.0.1:5080"
```

入口：

- `/`：自包含 HTML 仪表盘，每 1.5 秒轮询本机状态；
- `/api/live`：camelCase JSON，枚举使用 camelCase 字符串。

默认和自定义监听地址都必须是普通 HTTP loopback origin。`0.0.0.0`、局域网地址、
公网域名、带路径/query/fragment 或用户信息的 URL 会被拒绝。响应使用
`Cache-Control: no-store`、CSP、`X-Content-Type-Options` 和
`Referrer-Policy`；页面没有第三方脚本、字体或网络依赖。

仪表盘显示各地图最新列车状态、当前位置、下一站、可达性、进路变化时间线，以及
活动/已恢复告警。它仍是独立、只读、非注入式工具，不修改、移动或删除存档。

## 回放验收

自动测试使用代码构造的脱敏场景：

- 南通两帧经过真实协议 JSONL 编码与 `ProtocolReplayReader` 解码后，投影得到
  C3804 对应脱敏列车的 `ApproachingStation`、2 道可达和 `Established`；
- 太原三帧回放后得到最终到达 2 道，并在时间线中保留入口信号的
  `Retargeted`、`Released`；
- 告警测试覆盖打开、连续观察和恢复；
- Web 测试在随机 loopback 端口启动真实 Kestrel，通过 HTTP 验证页面和 JSON。

测试不包含玩家原始存档、地图资源或游戏文件。
