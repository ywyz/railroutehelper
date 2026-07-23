# 跨平台只读架构

第一阶段以 .NET 8 类库实现，目标运行环境为 Windows x64 与 Linux x64。业务核心
不使用平台专属 API；路径由调用方传入，不在核心代码中硬编码 Steam 或 Unity
目录。

```text
                      Realtime source (许可门禁后)
                               |
                               v
Save file -> ISaveFileAdapter -> SaveValue -> schema mapper -> Domain snapshot
                                                            |
                         +-----------------------------------+------------------+
                         |                                                      |
                         v                                                      v
                 OperationsAnalyzer                                      Protocol
                         |                                                      |
                         v                                                      v
                    OperationsReport                                      Recording
                         |                                                      |
                         v                                                      v
                       CLI                                                ReplayReader
```

模块职责：

- `RailRouteHelper.Core`：稳定的领域快照和数据源接口；不认识存档格式或传输方式。
- `RailRouteHelper.Protocol`：v1 JSON Lines 信封的编解码；不负责网络连接。
- `RailRouteHelper.SaveFiles`：只读打开 `.mp.lz4`，解压为无损 `SaveValue` 树。
- `RailRouteHelper.SaveSchema`：按存档内嵌游戏版本选择字段 schema，并将
  `SaveValue` 映射为 `Core` 中的领域快照。
- `RailRouteHelper.Operations`：从一个或两个快照推断列车前向可达性、当前/下一
  站、进路缺口、可能受阻状态，以及进路建立、改向和释放事件。
- `RailRouteHelper.Cli`：只读存档分析和前后存档比较的命令行 Adapter；不包含
  拓扑或状态判断。
- `RailRouteHelper.Replay`：从协议记录中按顺序产出消息。

`SaveFiles` 不猜测某个游戏版本的字段语义。后续 schema mapper 将经过验证的字段
映射为领域快照；游戏更新只需替换 mapper，不应改变压缩读取器或协议。

实时插件未来只能作为新的数据源 Adapter 接入 `Core`。它不能成为领域模型、存档
读取或回放模块的依赖，因此即使插件未获许可，独立存档分析器仍能工作。

领域术语及尚未解决的语义边界见仓库根目录的 [CONTEXT.md](../CONTEXT.md)。
运行态算法、状态和 CLI 用法见 [operations.md](operations.md)。
