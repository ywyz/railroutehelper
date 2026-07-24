# 连续存档监听与记录

`RailRouteHelper.Monitoring` 把存档目录转换为连续、版本化的协议消息流：

```csharp
await foreach (var envelope in new SaveDirectoryMonitor().WatchAsync(
                   saveDirectory,
                   cancellationToken: cancellationToken))
{
    // 写入 JSONL，或交给 LiveOperationsProjector
}
```

模块隐藏文件系统事件、周期补扫、存档读取、schema 选择、地图归组、前后快照
状态和 Operations 分析。调用方只需消费 `RealtimeEnvelope`。

## CLI

持续监听；每条 JSONL 同时写到标准输出和新记录文件：

```shell
railroutehelper watch-saves "/path/to/saves" \
  --record "run.jsonl"
```

只处理启动时已有的存档并退出：

```shell
railroutehelper watch-saves "/path/to/saves" --once
```

`--record` 使用“仅新建”模式。目标已经存在时命令返回错误，不会覆盖或追加。
没有 `--record` 时不会创建文件。按 Ctrl+C 会正常停止监听。

## 顺序与去重

- 启动时默认补扫已有的 `*.mp.lz4`。
- `FileSystemWatcher` 用于低延迟唤醒；每个扫描周期再次枚举目录，弥补平台事件
  合并或漏报。
- 文件大小和最后写入时间在稳定间隔内保持一致后才读取。
- 同一路径只有在长度或最后写入时间变化后才会作为新修订处理。
- `networkId` 由排序后的轨道、端点和站台轨道计算，不包含站名或绝对路径。
- 不同 `networkId` 分别维护前一快照，禁止跨地图生成进路变化。
- 同一轮补扫内，同一地图按 `gameTimeTicks`、捕获时间和路径稳定排序。
- 连续运行中晚到且游戏时间早于已处理快照的文件视为旧历史，不回退当前状态。

不同地图的游戏时钟彼此不可比较，因此全局 `sequence` 只表示协议输出顺序；地图
内的 Operations 比较使用该地图自己的前一快照。

## 消息

`operations-report` 使用外层协议 v1 和 Operations payload v1，包含源文件名、
schema、拓扑身份、游戏版本、游戏时间和完整 `OperationsReport`。

单个文件无法读取时不会停止整个目录：

| 诊断码 | 含义 |
| --- | --- |
| `save-container-invalid` | 不是有效的 MessagePack/LZ4 容器 |
| `save-version-unsupported` | 内嵌游戏版本未注册 |
| `save-schema-invalid` | 字段形状不符合该版本 schema |
| `save-access-denied` | 没有读取权限 |
| `save-read-failed` | 其他文件读取失败 |

诊断消息只包含文件名和固定说明，不记录绝对路径或底层异常文本。文件后续发生变化
时会作为新修订重新尝试。

## 能力边界

监听器仍是独立、只读、非注入式工具。它不会修改、移动、删除存档，也不会读取
游戏内存。刷新延迟由游戏生成存档的频率、文件稳定间隔和扫描周期共同决定，因此
是基于存档的准实时，而不是逐帧实时。

只绑定本机 loopback 的 Web Adapter、投影状态和告警规则见
[live-operations.md](live-operations.md)。
