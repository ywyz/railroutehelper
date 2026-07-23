# 第一阶段架构

第一阶段以 .NET 8 类库实现，目标运行环境为 Windows x64 与 Linux x64。业务核心
不使用平台专属 API；路径由调用方传入，不在核心代码中硬编码 Steam 或 Unity
目录。

```text
                      Realtime source (许可门禁后)
                               |
                               v
Save file -> ISaveSnapshotReader -> Domain snapshot -> Protocol -> Recording
                                                        |
                                                        v
                                                  ReplayReader
```

模块职责：

- `RailRouteHelper.Core`：稳定的领域快照和数据源接口；不认识存档格式或传输方式。
- `RailRouteHelper.Protocol`：v1 JSON Lines 信封的编解码；不负责网络连接。
- `RailRouteHelper.SaveFiles`：只读打开 `.mp.lz4`，解压并映射为领域快照。
- `RailRouteHelper.Replay`：从协议记录中按顺序产出消息。

实时插件未来只能作为新的数据源 Adapter 接入 `Core`。它不能成为领域模型、存档
读取或回放模块的依赖，因此即使插件未获许可，独立存档分析器仍能工作。

