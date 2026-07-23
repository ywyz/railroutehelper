# 游戏版本—字段 schema mapper

`RailRouteHelper.SaveSchema` 位于无损存档读取与领域快照之间。它只解释显式登记并由
真实存档语料验证过的游戏版本。

## 已登记 schema

| Schema ID | 游戏版本 | 语料结果 |
| --- | --- | --- |
| `rail-route-save/2.3-observed/v1` | `2.3.17`, `2.3.18`, `2.3.22`, `2.3.23`, `2.3.24` | 59/59 份存档映射成功 |

没有语料的 `2.3.19`、`2.3.20`、`2.3.21` 和更新版本不会因为版本号接近而被自动
接受。未知版本会抛出 `UnsupportedGameVersionException`。

## 已映射字段

| 领域数据 | 存档字段 |
| --- | --- |
| 游戏版本 | `gameVersion` |
| 游戏时间 | `savedTimeController.currentTimeOfDay` |
| 车站 | `savedStationRepository.savedStations[].stationData` |
| 车站站台与轨道 | `stationData.platformsData[].platformNum/trackRef` |
| 活动轨道 | `savedNodeRepository.nodes[]` 中以 `Node:Track:` 开头且 `InternalState.active=true` 的节点 |
| 轨道端点与坐标 | `modelObjectData[1].endPoints/endPointGridPoints` |
| 当前列车 | `savedTrainRepository.savedTrains[]` 中 `disposed=false` 且 `initialized=true` 的项目 |
| 列车位置与朝向 | `occupiedNodes`、`headsTowards` |
| 列车停站计划 | `currentStopIndex`、`scheduledVisits` |
| 进路开通证据 | 活动节点的 `InternalState[1].allocationState` |
| 信号目标和道岔当前连接 | `InternalState[1].Connected` |

`initialized=false` 的列车是尚未生成的计划项目，不会混入当前运行列车快照。后续如
需显示待开行列车，应使用单独的生命周期模型。

## 进路状态的保守解释

mapper 会为非零分配码生成 `RouteClearanceObservation`，并始终保留
`RawAllocationCode`：

| 原始码 | 当前解释 | 证据边界 |
| --- | --- | --- |
| `1` | `Allocated` | 主要出现在未被列车占用的已分配节点 |
| `2` | `TrainOccupied` | 抽样存档中 82/82 个此状态节点都在列车占用集合中 |
| 其他非零值 | `UnknownAllocated` | 保留原值并产生诊断，不猜测含义 |

所有记录的 `Origin` 目前都是 `Unknown`。存档中尚未找到可证明 `Manual` 或
`Automatic` 的字段，因此 UI 不能把全部 `Allocated` 显示为“玩家手动开通”。

## 南通站受控对照

玩家提供的 `南通站1`（开通前）与 `南通站2`（开通后）均为 `2.3.24`。已知操作
是从南通动车所方向向南通站2道开放通路。只读差分得到：

- 南通站2道轨道为 `Node:Track:44,37-47,37:0`；
- 进路观察从 3 条变为 21 条，共有 24 个节点分配码发生变化；
- 开通后新增 20 个 `allocationState=1` 节点，形成连续的信号、道岔和轨道链；
- 列车 C3804 从动车所出口的 3 个占用节点移动到
  `Node:Track:35,40-35,43:2`，该节点变为 `allocationState=2`；
- `Node:Semaphore:35:38.Connected` 从 `nil` 变为南通站2道轨道引用；
- `savedInterlocking.defaultPaths` 前后均为空。

这组证据确认了 `1` 表示已分配的进路区段、`2` 表示列车当前占用，并证明
`Connected` 可记录本次进路的目标及道岔选通分支。但单份存档仍没有字段记录操作
来源，因此只有带已知操作标签的前后快照差分可以把本次变化归因为手动开通。

## 合法的地图差异

创意工坊地图和既有存档中已验证两种不能按常规值强制转换的情况：

- 尚未放置的车站可使用 `gridPoint=nil`；车站及站台仍保留，位置为 `null`；
- `notMovingSince` 可为有符号负数；mapper 按有符号 64 位原值保留。

## 调用示例

```csharp
ISaveFileAdapter adapter = new MessagePackLz4SaveFileAdapter();
var document = await adapter.ReadAsync(path, cancellationToken);

var registry = SaveSchemaMapperRegistry.CreateDefault();
var result = registry.Map(document);
var snapshot = result.Snapshot;
```

`result.Diagnostics` 会报告未识别分配码、无坐标车站，以及当前无法区分手动/自动
进路来源等证据边界。
