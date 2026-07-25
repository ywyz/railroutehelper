# 跨平台只读架构

第一阶段以 .NET 8 类库实现，目标运行环境为 Windows x64 与 Linux x64。业务核心
不使用平台专属 API；路径由调用方传入，不在核心代码中硬编码 Steam 或 Unity
目录。

```text
Runtime object reader -> runtime/v1 loopback TCP -> Runtime pipeline
                                                   |
Save directory -> Monitoring -> Save Adapter -> schema mapper
                       |                           |
                       +-------- Domain snapshot --+
                                                   |
                                                   v
                                           OperationsAnalyzer
                                                   |
                                                   v
                                           OperationsReport
                                                              |
                                                              v
                                                       Protocol envelope
                                                              |
                                  +------------+-------------+-------------+
                                  |                          |             |
                                  v                          v             v
                             JSONL record                  CLI     LiveOperationsProjector
                                  |                                      |
                                  v                                      v
                             ReplayReader                       localhost Web
```

模块职责：

- `RailRouteHelper.Core`：稳定的领域快照和数据源接口；不认识存档格式或传输方式。
- `RailRouteHelper.Protocol`：v1 JSON Lines 信封、强类型 Operations payload 和
  payload 版本的编解码；不负责网络连接。
- `RailRouteHelper.Runtime`：loopback TCP 接收/发送、帧上限和会话序号校验、
  断线重连采集循环，以及 Runtime 快照到 Operations 投影的管线；不依赖游戏
  程序集。
- `RailRouteHelper.SaveFiles`：只读打开 `.mp.lz4`，解压为无损 `SaveValue` 树。
- `RailRouteHelper.SaveSchema`：按存档内嵌游戏版本选择字段 schema，并将
  `SaveValue` 映射为 `Core` 中的领域快照。
- `RailRouteHelper.Operations`：从一个或两个快照推断列车前向可达性、当前/下一
  站、进路缺口、可能受阻状态，以及进路建立、改向和释放事件。
- `RailRouteHelper.Monitoring`：持续观察目录，等待文件稳定，对文件修订去重，以
  拓扑身份隔离地图，按游戏时间排序，并维护各地图的前一快照。
- `RailRouteHelper.LiveOperations`：消费协议信封，按 `networkId` 投影最新运行图，
  保留有界进路事件时间线，并维护疑似受阻告警的打开、持续、恢复和复发实例。
- `RailRouteHelper.Web`：只绑定 loopback 的 ASP.NET Core Adapter；默认把
  Runtime 消息送入投影器，也保留 Monitoring 离线模式，并通过 HTML、
  `/api/live` 和 `/api/runtime` 暴露只读状态。
- `RailRouteHelper.Cli`：只读存档分析、前后比较和连续监听的命令行 Adapter；
  不包含拓扑、排序或状态判断。
- `RailRouteHelper.Replay`：从协议记录中按顺序产出信封或强类型 Operations
  报告。

`SaveFiles` 不猜测某个游戏版本的字段语义。后续 schema mapper 将经过验证的字段
映射为领域快照；游戏更新只需替换 mapper，不应改变压缩读取器或协议。

游戏进程内的极薄采集桥只能调用读取接口、构造领域 DTO 并发送到 localhost。
游戏程序集、对象类型和加载器均不是 `Core`、分析器或 Web 的依赖；因此接收端
可以完全用合成快照测试，加载器也不会进入普通发布包。

领域术语及尚未解决的语义边界见仓库根目录的 [CONTEXT.md](../CONTEXT.md)。
运行态算法、状态和 CLI 用法见 [operations.md](operations.md)。
连续监听、记录和故障行为见 [monitoring.md](monitoring.md)。
本机投影、告警生命周期和 Web 行为见
[live-operations.md](live-operations.md)。
