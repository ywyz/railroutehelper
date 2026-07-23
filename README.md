# Rail Route Helper

Rail Route Helper 是一个规划中的跨平台、只读调度辅助工具，用于从玩家自己的
Rail Route 存档中生成运行快照，并通过版本化协议记录和回放列车状态。

当前实现提供独立进程中的跨平台只读能力：

- Windows 与 Linux 共用的 .NET 核心；
- 版本化实时协议；
- 能保留任意 MessagePack 键类型的只读 `.mp.lz4` 存档 Adapter；
- 按已验证游戏版本识别列车、轨道、车站和进路开通证据的 schema mapper；
- 从快照推断列车当前/下一站、前向进路可达性、进路缺口和可能受阻状态；
- 比较前后快照中的进路建立、改向和释放；
- 使用合成测试数据的协议回放和受控场景回放；
- `analyze-save` 与 `compare-saves` 命令行工具。

schema mapper 当前覆盖有本地真实语料的 `2.3.17`—`2.3.24` 版本子集，详细
版本表见 [schema-mapping.md](docs/schema-mapping.md)，完成度和证据边界见
[项目进度](docs/progress.md)。

本仓库不会包含或分发游戏 DLL、游戏资源、创意工坊内容或玩家原始存档。向游戏
进程注入代码的实时插件不属于当前获准范围；其实现需要先满足
[合规门禁](docs/compliance.md)。

本项目是非官方社区工具，与 Bitrich.info 或 Valve 无隶属、背书或合作关系。

## 开发

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

```shell
dotnet restore --locked-mode
dotnet test RailRouteHelper.sln --no-restore
```

分析单份存档：

```shell
dotnet run --project src/RailRouteHelper.Cli -- \
  analyze-save "/path/to/save.mp.lz4"
```

比较两份按时间先后保存的存档：

```shell
dotnet run --project src/RailRouteHelper.Cli -- \
  compare-saves "/path/to/before.mp.lz4" "/path/to/after.mp.lz4"
```

CLI 只读取文件；不会移动、改名或写回存档。运行态状态、证据等级和限制见
[operations.md](docs/operations.md)。

## 本地代码图谱

`graphify-out/` 是已忽略的本地产物，不提交到 Git。需要刷新带 LLM 语义关系的完整
图谱时，使用系统环境中已有的 OpenAI-compatible 配置：

```shell
graphify extract . --backend openai --mode deep --force --max-concurrency 2
graphify label . --backend=openai --max-concurrency=2
```

命令读取 `OPENAI_API_KEY`、`OPENAI_BASE_URL` 和 `OPENAI_MODEL`，仓库内不得保存
这些值。需要显式改用 DeepSeek 时，将 `--backend openai` 改为
`--backend deepseek`；不要为同一次刷新同时调用两个后端。

协议说明见 [protocol-v1.md](docs/protocol-v1.md)，模块边界见
[architecture.md](docs/architecture.md)，存档格式与读取示例见
[save-files.md](docs/save-files.md)，回放行为见 [replay.md](docs/replay.md)，
领域术语见 [CONTEXT.md](CONTEXT.md)。
