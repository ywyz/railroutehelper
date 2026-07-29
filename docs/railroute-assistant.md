# Rail Route Assistant - 实时调度助手插件

Rail Route Assistant 是一个 BepInEx 插件 + 桌面程序，用于在 Rail Route 游戏运行时实时采集列车数据，并通过 HTTP 提供给桌面端显示告警和列车状态。

## 架构

```
Rail Route 游戏 (Unity 进程)
├── BepInEx 插件 (RailRouteAssistant.dll, .NET Framework 4.7.2)
│   ├── Harmony 补丁 Train.Move  ← 列车移动时触发数据采集（每 1 秒节流）
│   ├── ReflectCache  ← 反射缓存，所有 Type/Property/Field/Method 只查找一次
│   ├── 告警引擎 AlertEngine  ← 评估告警规则
│   └── HTTP 服务器 (localhost:8787)  ← 后台线程提供 JSON 数据
│
└── 桌面程序 (RailRouteAssistantDesktop.exe, .NET 8)
    └── 每 1 秒轮询 http://localhost:8787/data -> 置顶显示
```

## 功能

### 数据采集
通过 Harmony 补丁和反射读取游戏内部数据：

- 列车基本信息：车号、速度、目标速度、延误、最大速度
- 运行状态：是否在线、是否可发车、是否已完成、是否故障
- 信号状态：前方信号机状态（开放/关闭/等待/无信号）
- 信号机详情：AllocationState、Type（Manual/Auto/Shunting）、IsPendingRoute
- 前方轨道：Front Connection 的 AllocationState
- 停车信息：停车原因、停车时长
- 下一站信息：站名 + 站台号

### 信号状态

插件从多个维度判断信号状态：

1. **信号机 AllocationState**：`Free(0)` = 信号未开放，`Allocated(1)` = 信号已开放
2. **前方轨道 AllocationState**（信号机 Front Connection）：`Free(0)` = 前方轨道未配进路
3. **列车 TargetSpeed**：`≈0` = 列车已进入制动距离开始减速

> 注意：`FrontAllocationState == Occupied(2)` 不作为信号关闭的依据，因为列车自己可能就在这段轨道上。

### 告警规则（当前实现）

告警核心逻辑：**只在信号确实关闭时告警**，信号开放时不告警，正常进站减速不告警。

#### 运行中列车

| 场景 | 级别 | 触发条件 |
|------|------|----------|
| 信号关闭但列车尚未减速 | 警告 | 信号机 `AllocationState=Free` 或前方轨道 `Free`，且 `TargetSpeed` 仍正常 |
| 前方进路未配置 | 紧急 | 信号关闭 且 `LookaheadCount=0`（前方完全无铁轨段） |
| 前方信号关闭，减速中 | 紧急 | 信号关闭 且 `TargetSpeed≈0` 且 速度>5km/h |
| 因信号关闭即将停车 | 警告 | 信号关闭 且 `TargetSpeed≈0` 且 速度≤5km/h |
| 即将进站停车 | 信息 | 减速中 且 `StopReasons` 含 `Station` |

#### 已停车列车

> 信号关闭判断统一使用 `TargetSpeed <= 0.5`，与桌面程序显示逻辑一致；到站停车（`StopReasons` 含 `Station`）不在此告警。

| 场景 | 级别 | 触发条件 |
|------|------|----------|
| 可发车但无进路 | 紧急 | `CanDepart=true` 且 `LookaheadCount=0` |
| 可发车但信号关闭 | 紧急 | `CanDepart=true` 且 `TargetSpeed<=0.5` 且 非到站停车 |
| 信号关闭导致停车 | 紧急 | `CanDepart=false` 且 非到站停车 且 `LookaheadCount>0` |
| 前方进路未配置 | 紧急 | `CanDepart=false` 且 非到站停车 且 `LookaheadCount=0` |
| 线路停车超时 | 警告 | 非到站停车超过 10 秒 |

#### 其他告警

| 场景 | 级别 | 触发条件 |
|------|------|----------|
| 即将发车 | 警告 | `CanDepart=true` 且在停车 |
| 即将进入地图 | 警告/信息 | 等待入图且 5 分钟内 |
| 站台冲突 | 紧急 | 两列车前往同一站同一站台，一列在站一列接近 |
| 站台可能冲突 | 警告 | 两列车均在运行中且前往同一站同一站台 |
| 进路相交 | 紧急 | 两列车前方进路经过同一段轨道 |
| 列车故障 | 紧急 | `IsBrokenDown=true` |

### 桌面程序 UI

- **上半部分（告警区）**：按紧急 > 警告 > 信息排序
- **下半部分（列车列表）**：按状态排序，不同车次类型用不同背景色区分

列车列表列：`车号 | km/h | 延误 | 信号 | 状态 | 前方停站 | 站台`

- **信号列**：显示开放/关闭/无信号
- **前方停站列**：仅显示站名
- **站台列**：显示站台号（如 `3台`）

列车排序优先级：故障 > 运行中 > 在线停车 > 在线其他 > 等待入图 > 其他 > 已完成

### 右键菜单

在列车列表或告警列表上右键，可：
- **复制选中行**：复制当前选中的行（Tab 分隔，可粘贴到 Excel）。右键点击时会自动选中点击的行，并通过 `_lastRightClickedList` 记录操作的列表，避免 `Focused` 判断不准导致无反应。
- **复制全部列车数据**：复制全部列车数据（含表头）

车次背景色：

| 车次前缀 | 背景色 | 说明 |
|----------|--------|------|
| G（高铁）| 暗红 | |
| D（动车）| 暗蓝 | |
| C 三字（Cxxx）| 暗绿 | 城际三字车次 |
| C 四字（Cxxxx）| 暗青 | 城际四字车次 |
| X（行包）| 暗紫 | |
| Z（直达）| 暗绿 | |
| T（特快）| 暗橙 | |
| K（快速）| 暗黄 | |
| L（临客）| 暗灰蓝 | |
| S（市郊）| 暗紫 | |
| 数字 | 默认深灰 | |

列车行颜色基于信号状态：

| 信号状态 | 速度 | 颜色 |
|----------|------|------|
| 关闭/等待 | ≤10km/h 或停车 | 红色（紧急） |
| 关闭/等待 | >10km/h | 橙色（警告） |
| 开放 | 任意 | 白色（正常） |

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
      "lookahead": 1,
      "hasRoute": false,
      "needsRoute": true,
      "hasSignal": true,
      "signalState": "Node:Semaphore:320:287",
      "signalAllocationState": 0,
      "frontAllocationState": 0,
      "platform": 3,
      "nextStation": "南京站",
      "stopReasons": ""
    }
  ],
  "alerts": [
    {
      "level": "warning",
      "train": "1228",
      "message": "信号未开放 速度120km/h -> 南京站 3台"
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

插件使用 **Harmony Postfix 补丁** 在 `Train.Move` 方法后触发数据采集，每 1 秒节流一次。所有反射结果（Type/PropertyInfo/FieldInfo/MethodInfo）通过 `ReflectCache` 静态类缓存，只在首次使用时查找一次，后续直接使用缓存值，避免重复反射开销。

> 兜底机制：另起一个后台线程 `PollLoop`，每 3 秒调用一次 `CollectAllTrains`。当地图上没有列车移动时（`Train.Move` 不触发），仍能采集数据并刷新 `gameReady` 状态，避免桌面程序误报"游戏未就绪"。

### 关键游戏类型

| 类型 | 用途 |
|------|------|
| `Game.Train.Train` | 列车主类，包含速度、信号、下一站等 |
| `Game.Train.TrainRepository` | 列车仓库，`Trains` 属性返回所有列车 |
| `Game.Schedule.StationVisit` | 站台访问信息，包含 Station 和 PlatformNumber |
| `Game.Railroad.Semaphore` | 信号机，继承自 Node，有 `AllocationState`、`Type`、`IsPendingRoute`、`Front` |
| `Game.Railroad.Connection` | 轨道段，继承自 Node，有 `AllocationState`(State 枚举) |
| `Game.Maintenance.Routes.ServiceRouteRun` | 进路运行，包含 `Steps`(ResolvedStep 列表) |
| `Game.Maintenance.Routes.ResolvedStep` | 进路步骤，包含 `Destination`(Connection) |

### AllocationState 枚举

| 值 | 名称 | 含义 |
|----|------|------|
| 0 | Free | 空闲——未配置进路 |
| 1 | Allocated | 已分配——已配置进路 |
| 2 | Occupied | 已占用——列车在该轨道段上 |
| 3 | Shunting | 调车——调车模式下的特殊占用 |

### SemaphoreType 枚举

| 值 | 名称 | 含义 |
|----|------|------|
| 0 | Manual | 手动信号机（玩家直接控制） |
| 1 | Auto | 自动信号机（需要配进路） |
| 2 | Shunting | 调车信号机 |

### 概念说明

- **信号区间** = 两个信号灯（传感器）之间的路
- **信号机 AllocationState=Free** = 信号未开放（无进路通过）
- **前方轨道 AllocationState=Free** = 前方轨道未配进路
- **TargetSpeed ≈ 0** = 列车已进入制动距离，开始减速
- **NeedsRouteAhead** = 列车前方有 Auto 类型信号机但没有 pending route
- **LookaheadCount** = 前方铁轨段数（仅用于判断完全无进路的情况）
- **PlatformNumber** = 站台号（如 3台）

### 进路说明

本项目中的进路全部为**手动配置**，不涉及自动进路。告警系统帮助玩家判断：
- 信号是否开放（列车能否继续通行）
- 前方轨道段是否已配置进路（通过 `AllocationState` 提前预警）
- 何时需要提前配置下一个信号区间
- 哪些列车因信号关闭而停车

> **关于进路冲突**：两列列车前往同一站不一定是冲突——追踪运行（前后列车沿同一进路依次通过）属于正常的进路复用，不是冲突。真正的冲突是不同进路汇聚到同一段轨道，当前通过 `ResolvedStep.Destination` Connection 的 `Name` 进行交集检测。

## 项目结构

```
RailRouteAssistant/           # BepInEx 插件 (.NET Framework 4.7.2)
├── Plugin.cs                  # 插件入口，初始化 HTTP 服务器
├── TrainPatch.cs              # Harmony 补丁 + ReflectCache 反射缓存 + 数据采集
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
