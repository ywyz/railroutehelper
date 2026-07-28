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
    └── 每 1 秒轮询 http://localhost:8787/data -> 置顶显示
```

## 功能

### 数据采集
通过 Harmony 补丁和反射读取游戏内部数据：

- 列车基本信息：车号、速度、目标速度、延误、最大速度
- 运行状态：是否在线、是否可发车、是否已完成、是否故障
- 信号状态：前方信号灯状态（开放/关闭/等待/无信号）
- 停车信息：停车原因、停车时长
- 下一站信息：站名 + 站台号

### 信号状态

告警基于**信号状态**而非铁轨段数。信号状态含义：

| 显示 | 含义 |
|------|------|
| 开放 | `IsActing=True`，当前信号区间可以通行 |
| 关闭 | `IsActing=False`，信号未开放 |
| 等待 | `PendingRoute=True`，进路等待中 |
| 开放 等待 | 信号已开放但进路待定 |
| 关闭 等待 | 信号关闭且进路待定 |
| 手动 | `PendingRouteManual=True`，手动操作 |
| 无信号 | 前方无信号灯 |

### 告警规则

告警核心逻辑：**信号开放时不告警**（列车可以继续走），信号关闭/等待/无信号时告警。

#### 运行中列车

| 场景 | 级别 | 触发条件 |
|------|------|----------|
| 前方进路未配置 | 紧急 | `LookaheadCount=0`（前方完全无铁轨段） |
| 前方信号关闭/等待 | 紧急 | 信号未开放 且 速度≤10km/h |
| 前方信号关闭/等待 | 警告 | 信号未开放 且 速度>10km/h |
| 前方信号开放 + 需配进路 | 信息 | 信号开放 + `NeedsRouteAhead=true` + 剩余铁轨段≤2 |
| 即将停车 | 警告 | 速度≤5km/h 且 目标速度≈0 |

#### 已停车列车

| 场景 | 级别 | 触发条件 |
|------|------|----------|
| 可发车但无进路 | 紧急 | `CanDepart=true` 且 `LookaheadCount=0` |
| 信号关闭导致停车 | 紧急 | 停车 且 信号未开放 |
| 前方进路未配置 | 紧急 | 停车 且 `LookaheadCount=0` |
| 线路停车超时 | 警告 | 非到站停车超过 10 秒 |

#### 其他告警

| 场景 | 级别 | 触发条件 |
|------|------|----------|
| 即将发车 | 警告 | `CanDepart=true` 且在停车 |
| 即将进入地图 | 警告/信息 | 等待入图且 5 分钟内 |
| 进路冲突 | 警告 | 两列车均需进路且下一站相同 |
| 列车故障 | 紧急 | `IsBrokenDown=true` |

### 桌面程序 UI

- **上半部分（告警区）**：按紧急 > 警告 > 信息排序
- **下半部分（列车列表）**：按状态排序，不同车次类型用不同背景色区分

列车列表列：`车号 | km/h | 延误 | 信号 | 状态 | 前方停站`

- **信号列**：显示开放/关闭/等待/无信号
- **前方停站列**：显示站名 + 站台号（如 `南京站 3台`）

列车排序优先级：故障 > 运行中 > 在线停车 > 在线其他 > 等待入图 > 其他 > 已完成

### 右键菜单

在列车列表或告警列表上右键，可：
- **复制选中行**：复制当前选中的行（Tab 分隔，可粘贴到 Excel）
- **复制全部列车数据**：复制全部列车数据（含表头）

车次背景色：

| 车次前缀 | 背景色 |
|----------|--------|
| G（高铁）| 暗红 |
| D（动车）| 暗蓝 |
| Z（直达）| 暗绿 |
| T（特快）| 暗橙 |
| K（快速）| 暗黄 |
| 数字 | 默认深灰 |

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
      "signalState": "开放",
      "platform": 3,
      "nextStation": "南京站",
      "stopReasons": ""
    }
  ],
  "alerts": [
    {
      "level": "warning",
      "train": "1228",
      "message": "前方信号开放 前方1段 速度120km/h -> 南京站"
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
| `Game.Train.Train` | 列车主类，包含速度、信号、下一站等 |
| `Game.Train.TrainRepository` | 列车仓库，`Trains` 属性返回所有列车 |
| `Game.Schedule.StationVisit` | 站台访问信息，包含 Station 和 PlatformNumber |
| `Game.Railroad.SavedSemaphoreState` | 信号灯状态，包含 IsActing 和 PendingRoute |

### 概念说明

- **信号区间** = 两个信号灯（传感器）之间的路
- **信号开放（`IsActing=True`）** = 当前信号区间可以通行，列车不需要停车
- **信号关闭（`IsActing=False`）** = 信号未开放，列车将被迫减速停车
- **`NeedsRouteAhead`** = 列车前方某处最终需要配置进路（但信号可能仍然开放）
- **`LookaheadCount`** = 前方铁轨段数（仅用于判断完全无进路的情况）
- **`PlatformNumber`** = 站台号（如 3台）

### 进路说明

本项目中的进路全部为**手动配置**，不涉及自动进路。告警系统帮助玩家判断：
- 信号是否开放（列车能否继续通行）
- 何时需要提前配置下一个信号区间
- 哪些列车因信号关闭而停车

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
