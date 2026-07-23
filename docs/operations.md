# 运行态推理与 CLI

`RailRouteHelper.Operations` 接收一个当前 `OperationalSnapshot`，以及可选的前一
快照，通过单一接口返回列车运行判断和进路变化：

```csharp
var report = new OperationsAnalyzer().Analyze(current, previous);
```

模块不读取文件、不认识 MessagePack，也不依赖 CLI、协议或实时数据源。存档、
回放和未来获准的实时 Adapter 都可以复用同一个分析接口。

## 列车运行判断

每个 `TrainOperationsAssessment` 包含：

- 当前占用节点，以及能由站台轨道直接确认的当前车站和股道；
- 由 `currentStopIndex` 选出的下一计划停站；
- 下一站是否位于当前连续已分配路径中；
- 已开通到达的目标节点和第一个未开通节点；
- 运行状态以及区分事实与推断的证据。

`scheduledVisits[].departed/exited` 在受控语料中即使列车已经离站仍可能全部为
`false`，因此不能用“第一个 `exited=false`”寻找下一站。已验证的选择规则是直接
使用 `currentStopIndex`。列车到达后该索引会前移，所以当前所在站台独立地由
`occupiedNodes` 与站台轨道映射确定。

当前状态：

| 状态 | 含义 |
| --- | --- |
| `Unknown` | 缺少下一站轨道、方向，或前方分支无法唯一确定 |
| `AtScheduledPlatform` | 当前占用本次或刚完成的计划站台，且尚未明确离开 |
| `DepartingStation` | 已向下一站推进，但车尾仍占用刚完成的计划站台 |
| `ApproachingStation` | 下一站台位于当前连续已分配的前向路径中 |
| `RunningTowardRouteLimit` | 列车仍在运行，但已分配路径不能到达下一站 |
| `WaitingForRoute` | 列车静止且下一站不可达，但没有足够持续时间证据 |
| `PossibleBlocked` | 列车不在计划站台、已持续静止，且前方已分配路径存在缺口；仍只是推断 |

`PossibleBlocked` 不等于确定“卡车”。零速本身不触发该状态；必须同时存在有效的
`notMovingSince`、当前游戏时间和前方进路缺口。停在计划站台等待发车时不会被标为
疑似受阻。

## 拓扑与证据边界

分析器把每条轨道和它的两个端点组成二分图，从列车的 `headsTowards` 开始向前
遍历，并禁止退回当前占用节点。只有具有非零 `allocationState` 的节点可以继续
通过。

如果同一前向节点出现多个已分配分支，分析器只在 `Connected` 唯一选择其中一支
时继续。没有唯一选择时输出 `Unknown`，不会因为某条可能路径能到达站台就宣称
“可达”。

证据分两级：

- `Observed`：存档直接给出的占用、方向、站台或时间字段；
- `Inferred`：由拓扑、连续分配节点和时间关系得到的保守结论。

第一版不输出分钟级 ETA。轨道端点只能支持拓扑距离，尚不足以证明列车在轨道内的
精确位置和运行时分。

## 进路变化

提供前一快照时，分析器比较各控制节点的 `Connected`：

- 空 → 有目标：`Established`
- 有目标 → 不同目标：`Retargeted`
- 有目标 → 空：`Released`

一次完整进路会同时改变入口信号和沿途多个道岔，因此报告保留全部节点级事件。
当目标引用是已知站台轨道时，事件同时给出车站和股道。

## CLI

CLI 是薄 Adapter，只负责只读载入、选择 schema、调用 Operations 并打印报告：

```shell
dotnet run --project src/RailRouteHelper.Cli -- \
  analyze-save "/path/to/save.mp.lz4"

dotnet run --project src/RailRouteHelper.Cli -- \
  compare-saves "/path/to/before.mp.lz4" "/path/to/after.mp.lz4"
```

退出码 `0` 表示成功，`1` 表示命令行参数错误，`2` 表示文件、权限、版本或 schema
错误。Linux 和 Windows 使用相同命令结构；包含空格或中文的路径必须加引号。

测试只提交由代码构造的脱敏快照，不包含玩家原始存档或创意工坊内容。南通和太原
受控存档仅用于本机只读验收。

Operations 报告的连续协议、目录监听和 JSONL 记录见
[monitoring.md](monitoring.md)。
