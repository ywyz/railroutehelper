# Rail Route Assistant - 实时调度助手插件

Rail Route Assistant 是一个 BepInEx 插件 + 桌面程序，用于在 Rail Route 游戏运行时实时采集列车数据，并通过 HTTP 提供给桌面端显示告警和列车状态。

## 架构

```
Rail Route 游戏 (Unity 进程)
├── BepInEx 插件 (RailRouteAssistant.dll, .NET Framework 4.7.2)
│   ├── Harmony 补丁 Train.Move  ← 列车移动时触发数据采集
│   ├── 后台轮询线程  ← 每 1 秒自动采集一次
│   ├── 告警引擎 AlertEngine  ← 评估告警规则
│   └── HTTP 服务器 (localhost:8787)  ← 后台线程提供 JSON 数据
│
└── 桌面程序 (RailRouteAssistantDesktop.exe, .NET 8)
    └── 每 1 秒轮询 http://localhost:8787/data → 置顶显示
```

## 功能

### 数据采集
通过 Harmony 补丁和反射读取游戏内部数据：

- 列车基本信息：车号、速度、目标速度、延误、最大速度
- 运行状态：是否在线、是否可发车、是否已完成、是否故障
- 进路信息：前方铁轨段数（LookaheadCount）、信号区间数（RouteTotalSteps/RouteCurrentStep）
- 信号状态：前方信号灯状态（IsActing、PendingRoute 等）
- 停车信息：停车原因、停车时长
- 下一站信息

### 告警规则

| 类型 | 级别 | 触发条件 |
|------|------|----------|
| 前方信号区间需配置进路 | 紧急 | `NeedsRouteAhead=true` 且速度≤10km/h |
| 前方信号区间需配置进路 | 警告 | `NeedsRouteAhead=true` 且速度>10km/h |
| 前方进路未配置 | 紧急 | `LookaheadCount=0` 且在运行中 |
| 已停车 - 需配置进路 | 紧急 | 停车且 `NeedsRouteAhead=true` |
| 可发车但无进路 | 紧急 | `CanDepart=true` 且 `LookaheadCount=0` |
| 线路停车超时 | 警告 | 非到站停车超过 10 秒 |
| 即将发车 | 警告 | `CanDepart=true` 且在停车 |
| 即将进入地图 | 警告/信息 | 等待入图且 5 分钟内 |
| 进路冲突 | 警告 | 两列车均需进路且下一站相同 |
| 列车故障 | 紧急 | `IsBrokenDown=true` |

### 桌面程序 UI

- **上半部分（告警区）**：按紧急 > 警告 > 信息排序
- **下半部分（列车列表）**：按状态排序，不同车次类型用不同背景色区分

列车排序优先级：故障 > 运行中 > 在线停车 > 在线其他 > 等待入图 > 其他 > 已完成

车次背景色：

| 车次前缀 | 背景色 |
|----------|--------|
| G（高铁）| 暗红 |
| D（动车）| 暗蓝 |
| Z（直达）| 暗绿 |
| T（特快）| 暗橙 |
| K（快速）| 暗黄 |
| 数字 | 默认深灰 |

## 安装

### 前提条件
- Rail Route 游戏（Steam 版）
- [BepInEx 5.x](https://github.com/BepInEx/BepInEx) (x64)
- .NET 8 运行时（桌面程序）

### 步骤

1. 安装 BepInEx 5.x 到 Rail Route 游戏目录：
   ```
   C:\Program Files (x86)\Steam\steamapps\common\Rail Route\BepInEx\
   ```

2. 编译插件：
   ```shell
   dotnet build RailRouteAssistant/RailRouteAssistant.csproj -c Release
   ```
   编译后会自动复制到 BepInEx plugins 目录。

3. 编译桌面程序：
   ```shell
   dotnet build RailRouteAssistantDesktop/RailRouteAssistantDesktop.csproj -c Release
   ```

4. 启动游戏，进入有列车的地图

5. 运行桌面程序：
   ```
   RailRouteAssistantDesktop\bin\Release\net8.0-windows\RailRouteAssistantDesktop.exe
   ```

## API

### `GET /data`

返回 JSON 格式的列车数据和告警：

```json
{
  "gameReady": true,
  "serverTime": "14:30:25",
  "trains": [
    {
      "name": "1228",
      "speed": 120,
      "maxSpeed": 120,
      "targetSpeed": 120.0,
      "delay": -2,
      "canDepart": false,
      "finished": false,
      "brokenDown": false,
      "onBoard": true,
      "waiting": false,
      "lookahead": 7,
      "hasRoute": false,
      "needsRoute": true,
      "hasSignal": true,
      "signalState": "IsActing=True",
      "routeTotal": 5,
      "routeCur": 2,
      "routeRemain": 2,
      "nextStation": "南京站",
      "stopReasons": ""
    }
  ],
  "alerts": [
    {
      "level": "warning",
      "train": "1228",
      "message": "前方信号区间需配置进路 进路3/5 剩余2个区间 速度120km/h -> 南京站（IsActing=True）"
    }
  ]
}
```

## 日志

BepInEx 日志位于：
```
<Rail Route 游戏目录>\BepInEx\LogOutput.log
```

## 技术细节

### 数据采集方式

插件使用三种方式采集数据：

1. **Harmony Postfix 补丁** - 在 `Train.Move` 方法后触发，记录列车移动
2. **后台轮询线程** - 每 1 秒通过反射读取 `TrainRepository.Trains`
3. **HTTP 服务器** - 在后台线程运行，响应桌面程序请求

### 关键游戏类型

| 类型 | 用途 |
|------|------|
| `Game.Train.Train` | 列车主类，包含速度、进路、信号等 |
| `Game.Train.TrainRepository` | 列车仓库，`Trains` 属性返回所有列车 |
| `Game.Maintenance.Routes.ServiceRouteRun` | 进路运行状态，包含 Steps 和 CurrentStepIndex |
| `Game.Railroad.SavedSemaphoreState` | 信号灯状态，包含 IsActing 和 PendingRoute |

### 概念说明

- **铁轨段（Segment）** = 游戏中的一段铁轨，`LookaheadCount` 是前方铁轨段数
- **信号区间（Route Step）** = 从一个信号到下一个信号之间的路，`RouteTotalSteps` 是总区间数
- **`NeedsRouteAhead`** = 列车前方信号区间需要配置进路
- **`IsActing`** = 信号灯是否在起作用（Nullable<bool>）

## 项目结构

```
RailRouteAssistant/           # BepInEx 插件 (.NET Framework 4.7.2)
├── Plugin.cs                  # 插件入口，初始化 HTTP 服务器和轮询线程
├── TrainPatch.cs              # Harmony 补丁，数据采集
├── AlertEngine.cs             # 告警引擎，评估告警规则
├── HttpServer.cs              # HTTP 服务器，提供 JSON API
├── DataStore.cs               # 数据模型和线程安全存储
└── RailRouteAssistant.csproj

RailRouteAssistantDesktop/     # 桌面程序 (.NET 8, WinForms)
├── Program.cs                 # 入口
├── MainForm.cs                # 主窗口，告警列表 + 列车列表
└── RailRouteAssistantDesktop.csproj
```

## 免责声明

本项目是非官方社区工具，与 Bitrich.info 或 Valve 无隶属、 endorsements 或合作关系。使用风险自负。
