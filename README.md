# Rail Route Helper

Rail Route Helper 是一系列社区工具，用于从 Rail Route 游戏中获取运行数据并提供调度辅助。

## 实时调度助手（BepInEx 插件 + 桌面程序）

通过 BepInEx 插件在游戏运行时实时采集列车数据，经 HTTP 提供给桌面程序显示告警和列车状态。

```
Rail Route 游戏 (Unity)
├── BepInEx 插件 (RailRouteAssistant.dll)
│   ├── Harmony 补丁 + 后台轮询线程 → 采集列车数据
│   └── HTTP 服务器 (localhost:8787) → 提供 JSON API
└── 桌面程序 (RailRouteAssistantDesktop.exe)
    └── 轮询 HTTP → 显示告警列表 + 列车列表
```

**功能：**
- 实时列车状态：车号、速度、延误、进路、信号
- 告警引擎：越过信号后提前监视紧邻下一信号、停车超时、发车预告、入图预告、站台冲突（时间重叠判定）、进路相交
- 语音播报：等待入图接近、到站（含早点/正点/晚点与调向提示）、通过、中间站发车前预告和发车自动播报；分钟按中文十/百位正常朗读
- 车次始发终到查询：按车号精确查询 12306；失败时依次降级到随桌面程序发布的授权路路通表和冻结的 12306 `train_list.js` 快照，自动拆分换向合并车号；查询时自动移除地图专用“通”前缀
- 车次分类着色：G/D/Z/T/K 不同背景色
- 智能排序：运行中 > 停车 > 等待入图 > 已完成
- 车次搜索：列表上方提供搜索框，按完整或部分车号即时筛选
- 车次详情：双击车次、选中后按回车或使用右键菜单，在游戏地图内时刻表与 12306 当日全程时刻表之间切换，并显示 12306 提供的全部车型
- 版本辨识：窗口标题栏显示桌面程序版本号，便于确认没有误启动旧版 EXE
- Windows 一体包：随包提供 BepInEx，首次运行自动发现或询问游戏目录，安装后自动启动 Steam 游戏

详见 [实时调度助手文档](docs/railroute-assistant.md)。

## 存档分析工具（CLI + Web）

从玩家存档中生成运行快照，通过版本化协议记录和回放列车状态。

- Windows 与 Linux 共用的 .NET 核心；
- 版本化实时协议；
- 能保留任意 MessagePack 键类型的只读 `.mp.lz4` 存档 Adapter；
- 按已验证游戏版本识别列车、轨道、车站和进路开通证据的 schema mapper；
- 从快照推断列车当前/下一站、前向进路可达性、进路缺口和可能受阻状态；
- 比较前后快照中的进路建立、改向和释放；
- 以版本化 JSONL 连续记录并回放 Operations 报告；
- 将实时或回放报告投影为按地图隔离的最新运行图、进路事件时间线和告警生命周期；
- 持续监听存档目录，自动稳定、去重、归组和排序新存档；
- 提供只绑定 loopback 的 localhost Web 运行仪表盘；
- 使用合成测试数据的协议回放和受控场景回放；
- `analyze-save`、`compare-saves` 与 `watch-saves` 命令行工具。

schema mapper 当前覆盖有本地真实语料的 `2.3.17`-`2.3.24` 版本子集，详细
版本表见 [schema-mapping.md](docs/schema-mapping.md)，完成度和证据边界见
[项目进度](docs/progress.md)。

本仓库不会包含或分发游戏 DLL、游戏资源、创意工坊内容或玩家原始存档。

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

持续监听并把 JSONL 同时写到标准输出和一个新记录文件：

```shell
dotnet run --project src/RailRouteHelper.Cli -- \
  watch-saves "/path/to/saves" --record "run.jsonl"
```

使用 `--once` 只处理启动时已有的存档并退出。CLI 不会移动、改名或写回存档；
只有显式 `--record` 指定的新文件会被创建，已有记录不会覆盖。运行态状态见
[operations.md](docs/operations.md)，监听规则见
[monitoring.md](docs/monitoring.md)。

启动本机 Web 仪表盘并持续监听存档目录：

```shell
dotnet run --project src/RailRouteHelper.Web -- \
  "/path/to/saves" --listen "http://127.0.0.1:5080"
```

`--listen` 只接受 `localhost`、`127.0.0.0/8` 或 `::1` 的 HTTP origin，不允许
绑定局域网或公网地址。仪表盘、投影状态和告警生命周期见
[live-operations.md](docs/live-operations.md)。

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
[save-files.md](docs/save-files.md)，监听行为见
[monitoring.md](docs/monitoring.md)，回放行为见 [replay.md](docs/replay.md)，
本机投影和仪表盘见 [live-operations.md](docs/live-operations.md)，领域术语见
[CONTEXT.md](CONTEXT.md)。
