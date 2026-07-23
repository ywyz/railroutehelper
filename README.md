# Rail Route Helper

Rail Route Helper 是一个规划中的跨平台、只读调度辅助工具，用于从玩家自己的
Rail Route 存档中生成运行快照，并通过版本化协议记录和回放列车状态。

当前阶段只实现独立进程中的基础能力：

- Windows 与 Linux 共用的 .NET 核心；
- 版本化实时协议；
- 只读存档 Adapter；
- 使用合成测试数据的协议回放。

本仓库不会包含或分发游戏 DLL、游戏资源、创意工坊内容或玩家原始存档。向游戏
进程注入代码的实时插件不属于当前获准范围；其实现需要先满足
[合规门禁](docs/compliance.md)。

本项目是非官方社区工具，与 Bitrich.info 或 Valve 无隶属、背书或合作关系。
