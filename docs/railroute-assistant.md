# Rail Route Assistant - 实时调度助手插件

Rail Route Assistant 是一个 BepInEx 插件 + 桌面程序，用于在 Rail Route 游戏运行时实时采集列车数据，并通过 HTTP 提供给桌面端显示告警和列车状态。

## 架构

```
Rail Route 游戏 (Unity 进程)
├── BepInEx 插件 (RailRouteAssistant.dll, .NET Framework 4.7.2)
│   ├── Harmony 补丁 Train.Move / Semaphore.AfterTrainEntered
│   │   └── 移动时采集；越过信号时锁定紧邻下一座同向信号
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

- 列车基本信息：车号、速度、目标速度、游戏原始累计延误、最大速度、是否需调向
- 运行状态：是否在线、是否可发车、是否已完成、是否故障
- 信号状态：紧邻下一座同向运营信号的状态（开放/未开放/占用/调车/未知）
- 信号机详情：AllocationState、Type（Manual/Auto/Shunting）、IsPendingRoute
- 停车信息：停车原因、停车时长
- 下一站信息：站名 + 站台号 + 是否通过
- 游戏时刻表：完整计划访问序列；通过站标记“（通过）”，到发时刻相同且停车间隔为 0
- 地图进出信息：列车进入/离开游戏地图的站名、股道、是否通过和计划时刻

### 车次始发、终到查询

`train_list.js` 是已冻结的历史静态表，不能覆盖当前运行图；桌面端不会在启动时联网下载它。现在只对当前地图中实际出现的车号发起查询，并在后台加载随程序发布的离线表，数据优先级如下：

| 优先级 | 数据源 | 行为 |
|---|---|---|
| 1 | 12306 按车号查询（含当天缓存） | 请求 `https://search.12306.cn/search/v1/train/search?keyword={车次}&date={yyyyMMdd}`，在响应 `data[]` 中不区分大小写地精确匹配 `station_train_code`，直接读取 `from_station` / `to_station`。同一运行图日期内的成功查询缓存在 `%LOCALAPPDATA%\RailRouteAssistant\train_routes_online_cache.json`。 |
| 2 | 路路通离线降级表 | 在线超时、HTTP/解析失败、`data=[]` 或没有精确车号时，读取随桌面程序发布的 `data\train_routes_offline.json`；如用户后来刷新本机表，`%LOCALAPPDATA%\RailRouteAssistant\train_routes_offline.json` 会覆盖发布表。 |
| 3 | 冻结的 12306 静态快照 | 路路通也没有该车次时，读取随桌面程序发布的 `data\train_list_12306_legacy.js`。该快照来自 `https://kyfw.12306.cn/otn/resources/js/query/train_list.js?scriptVersion=1.5462`，文件内最新日期为 2022-09-01，仅作最后降级，不会覆盖前两级结果。 |

12306 的查询结果可能同时含有 `Z51`、`Z510` 等前缀相同的车次，因此绝不能取数组第一项。网络请求最长 5 秒、最多同时 3 个；同一失败车次会退避 10 分钟，不会因桌面端每秒刷新而反复请求。成功结果按“车次 + 当前运行图日期”缓存，且覆盖同车号的离线结果。

部分游戏地图会给通过列车添加“通”前缀，例如 `通D7823`，或添加前导 `0`，
例如 `0G2524`。这些地图标记在显示和语音播报中保留；查询 12306、路路通离线表
和冻结快照前分别规范化为 `D7823`、`G2524`。因此 `0G2524` 仍读作“零高二五二四”，
但始发终到和全程时刻表使用真实车次 `G2524` 查询。

部分地图（如沈阳枢纽）会在车号后附加中文括注，例如 `Z212(技停不办客)`。
车次库查询前会去掉括注及所有中文，按纯车号 `Z212` 查询 12306、路路通和冻结快照。
语音播报中，列车首次进入地图的“接近”播报读出完整车号（含括注内容，如
“直二一二技停不办客”），后续所有播报只读主车号“直二一二”。

全程时刻表和车型只在用户打开车次详情时按需请求
`https://mobile.12306.cn/wxxcx/wechat/main/travelServiceQrcodeTrainInfo`，请求参数为
规范化车号和当天 `yyyyMMdd` 日期。响应的 `stopTime[]` 提供全程站名、到发时刻、
停站分钟和跨日标记；`trainsetTypeInfo`、`train_style`、`jiaolu_train_style` 中可用的
车型会去重展示。`train_style` 形如 `CRH380D_554H` 时会移除具体车组号，只显示
车型 `CRH380D`。详情请求使用独立并发通道，不会排在列车列表的批量始发终到查询后面。

路路通 APK 中的离线资料是私有二进制分片而非 SQLite 文件。经授权，本发布包随附由该数据生成的规范化离线表；原始 APK 不会被包含。项目也提供本地导出器，它将 `res/DO`（显示车号索引）、`res/k5.dat`（同索引的 12306 内部车号）和 `res/hU.dat`（内部车号对应的始发、终到和经由站）精确关联。路线表的第一个经由站是始发站，独立的 `endStation` 字段才是终到站；不能把最后一个经由站误作终点。冻结 12306 快照的 SHA-256 为 `626F26355F71FD33C1BB304171C7B9284CE63B334B86B2FE8961EF551A5E18D4`，用于确认发布包内的静态文件未被意外替换。

在用户自己的电脑上运行以下命令即可刷新降级表：

```powershell
dotnet run --project tools\ExportLulutongTrainRoutes\ExportLulutongTrainRoutes.csproj -- `
  --apk "C:\Users\yw980\Downloads\lulutong.apk"
```

默认会生成：

```text
%LOCALAPPDATA%\RailRouteAssistant\train_routes_offline.json
%LOCALAPPDATA%\RailRouteAssistant\train_routes_offline_report.json
```

导出器会校验三个索引的条数、二进制边界和路线表是否完全读完，并为斜杠复车号建立别名。找不到路线的车次、或同一车号对应不同始发终到的冲突项会写入报告并跳过，绝不以任意一条覆盖另一条。原始 APK 和本机导出报告不包含在发布包中。

```json
{
  "schemaVersion": 1,
  "source": "lulutong-local-export",
  "generatedAtUtc": "2026-07-30T00:00:00Z",
  "routes": {
    "Z51": { "origin": "北京丰台", "destination": "启东" }
  }
}
```

### 信号状态

列车越过一座信号机时，插件在游戏线程调用该信号的
`PathToNextSemaphore(true)`，按列车 UUID 记录沿当前道岔路径的**紧邻下一座
同向运营信号**。后续快照持续读取同一信号的实际状态，因此玩家随后开通进路时，
告警会立即消失。

| AllocationState | 含义 | 告警行为 |
|---|---|---|
| `1` Allocated | 已开通 | 不告警 |
| `0` Free | 未开放 | 提前预警 |
| `2` Occupied | 前方区间占用 | 提前预警 |
| `3` Shunting | 调车状态 | 提前预警 |
| `-1` / 无信号 | 未知或线路边缘 | 不猜测、不报“关闭” |

`Train.ActingSignalAhead` 会跳过已开通的信号并指向更远处的首个阻挡点，不能
表示“下一座物理信号”；告警不再使用它。`Semaphore.Front` 实际是信号接近侧
连接，也不再作为信号后方进路的依据。`TargetSpeed` 仅用于标注已确认阻挡后的
制动程度，不单独产生“信号关闭”告警。

### 告警规则（当前实现）

告警核心逻辑：**越过当前信号后立即检查紧邻下一信号**。只有该信号明确不能
通行时才告警；普通限速、进站减速或信号状态未知都不会被写成“前方信号关闭”。

#### 运行中列车

| 场景 | 级别 | 触发条件 |
|------|------|----------|
| 下一信号未开放/占用/调车 | 警告 | 越过当前信号后监视到的下一座信号 `AllocationState != 1`，且非正常进站 |
| 下一信号阻挡，列车正在制动 | 紧急 | 上述真实阻挡存在，且 `TargetSpeed≈0` |
| 即将进站停车 | 信息 | 减速中 且 `StopReasons` 含 `Station` |

#### 已停车列车

> 到站停车（`StopReasons` 含 `Station`）是正常状态。只有发车条件已满足但紧邻
> 下一信号实际不能通行时，才会给出信号/进路紧急告警。

| 场景 | 级别 | 触发条件 |
|------|------|----------|
| 可发车但下一信号不能通行 | 紧急 | `CanDepart=true` 且下一信号 `AllocationState != 1` |
| 信号/区间阻挡导致停车 | 紧急 | 非到站停车且下一信号 `AllocationState != 1` |
| 游戏明确报信号停车 | 紧急 | `StopReasons` 含 `Semaphore`；即使紧邻信号状态暂时读不到，也归类为信号停车，不再误报线路停车超时 |
| 信号停车（含无法读取信号状态时） | 紧急 | 非到站停车超过 10 秒且 `StopReasons` 含 `Semaphore`，显示“下一信号未开放，列车正在减速/停车” |

停车时长统一四舍五入到整秒并按中文分秒显示，例如 `95.6` 秒显示为
`1分36秒`。当 `StopReasons=Semaphore` 但信号状态暂时不可读时，告警显示
“前方信号未开放（可能因区间占用或进路未办理）”；只有读取到
`AllocationState=Occupied` 时才明确写“前方区间占用”。

> **非信号停车不再告警**：除上述信号相关停车外，其他非到站停车（如 `Semaphore`
> 之外的 `StopReasons`）视为误报，不再生成“线路停车超时”告警。

#### 其他告警

| 场景 | 级别 | 触发条件 |
|------|------|----------|
| 即将发车 | 警告 | `CanDepart=true` 且在停车 |
| 即将进入地图 | 警告/信息 | 等待入图且 5 分钟内 |
| 站台冲突（停站车 + 接近车） | 紧急 | 一列已停在站台、另一列接近同一站台，且接近车到达时刻早于停站车发车时刻 + 10 秒余量（时间重叠才报，错开不报；站名含"方向"的方向站不报） |
| 站台冲突（两列均在运行中） | 紧急 | 两列车都在运行中且前往同一站同一站台，根据计划时刻表计算两列车在该站的占用窗 `[到达, 发车]`，两窗重叠（10 秒余量）才报；不重叠（如 G9425 到达 6:40 晚于 G7031 发车 6:22）不报 |
| 站台可能冲突 | 警告 | 两列车均在运行中、前往同一站台但无法读取计划时刻表时，保守报警告 |
| 站台冲突（两列已停站） | 紧急 | 两列车同时停在同一个站台 |
| 进路相交 | 紧急 | 两列车前方进路经过同一段轨道 |
| 列车故障 | 紧急 | `IsBrokenDown=true` |

> **站台冲突的时间重叠判定**：站台冲突检测对三种情形都按时间重叠判定，不再仅凭"前往同一站台"就告警。
>
> - **停站车 + 接近车**：停站车占用站台至本次访问的计划发车时刻（`departureRemainingSec`），接近车到达于 `NextArrivalSec`。若接近车到达晚于停站车发车 + 10 秒清空余量，则时间错开，不报冲突。
> - **两列均在运行中**：到达时刻取自 `ContractLeg.NextArrival`（绝对游戏时刻换算的剩余秒数，可靠），发车时刻优先取 `ScheduledStops` 中 `RelativeTimes=false` 的绝对发车时刻；若时刻表只有相对时刻则用停站时长估算占用窗。计算两列车各自的占用窗 `[到达, 发车]`，两窗重叠（10 秒余量）才报紧急冲突。例如 G1509 在镇江南站 7:19 到 7:21、G7505 在 7:32 到 7:34，两个占用窗不重叠，不报冲突。时刻表完全不可读时回退为"可能冲突"警告。
> - **两列已停站**：同一站台两列车同时停站，直接报真实冲突。

### 语音播报

桌面程序内置语音播报引擎（`VoiceEngine`），在列车状态变化时自动播报。所有模式均为“预录素材优先、缺失内容由所选 TTS 补全”，不会再把整条广播强制改成纯 TTS。

**播报类型与触发点**：

| 类型 | 触发条件 | 播报内容 |
|------|----------|----------|
| 列车接近 | `Waiting` 由 false→true；首站通过未提供等待态时以 `OnBoard` false→true 兜底 | 开往xxx方向的列车 车号 接近。 |
| 通过预告 | 下一访问为通过站，`NextArrivalSec` 首次进入 180 秒以内 | 车号次列车即将通过xx站x道，请做好接车准备。 |
| 列车到站 | 实际访问次数增加且本次不是通过站 | 开往xxx方向的列车 车号 早点x分/正点/晚点x分到达xx站x道，本次停车x分。 |
| 列车通过 | 实际访问次数增加且本次为通过站 | 车号次列车早点x分/正点/晚点x分通过xx站x道；下一站为通过站时加读“通过”。 |
| 到站调向 | 游戏原生 `StopAndReverse` 或 `ReverseOnceStopped` 为真，且本次为停车访问 | 接在到站播报末尾：本次列车需要调向。若标志在到站后才刷新，则在停站期间补播一次。 |
| 发车前预告 | 中间停站的 `departureRemainingSec` 首次进入 60 秒以内 | xx站xx道 车号列车 即将发车，请做好准备。 |
| 列车发车 | 最近一次实际访问的 `Departed` 由 false→true（速度变化兜底） | 开往xxx方向的列车 车号 正点发车/晚点x分发车；地图内仍有下一访问时追加下一站，若为通过站则加读“通过”。 |

- **终到站**：由车次库（12306 数据）查询拆分后的车号得到，查不到时省略"开往xxx方向"段
- **到站正晚点**：插件在一次 `ActualVisit` 首次出现时固定“游戏时钟 - `StationVisit.From`”；负数为早点、正数为晚点，绝对值不超过 60 秒按正点播报。游戏没有单独的早点字段，早点由此计算。
- **分钟读法**：停车和正晚点分钟使用中文基数词；例如 7、15、48、120 分分别读“七分、十五分、四十八分、一百二十分”。车号继续逐位读。
- **防重复**：同一车号 + 同一播报类型，30 秒内不重复触发；发车前预告对同一次实际访问只播报一次
- **复合车号**：支持相邻形式 `G6642G6641`、`DJ8598G3401`，也支持斜杠形式 `0G1703/G1704`、`0Y2/Y1`。后者分别拆为 `0G1703`/`G1704`、`0Y2`/`0Y1`；列表在首个计划停车站后切换到第二段。
- **特殊字头**：`DJ` 逐字头按“动”“检”拼接，因此 `DJ54` 读作“动检五四”；`Y` 读作“游”。前导 `0` 在播报时保留为“零”，只在车次数据库查询和颜色分类时移除。
- **中文括注**：部分地图（如沈阳枢纽）在车号后附加中文括注，例如 `Z212(技停不办客)`。列车首次进入地图的“接近”播报读出完整车号（含括注内容），后续所有播报只读主车号；12306 查询和颜色分类按去掉括注后的纯车号 `Z212` 处理。
- **中文站名**：中英混合名称只显示和播报中文；没有中文的全英文站名保持原样。
- **首站与末站**：通过访问同样播报；发车前一分钟预告仍只针对中间停车站。
- **时刻不可用时**：若游戏仅提供相对时刻，桌面端会播报“已经发车”，不会把旧的累计延误误报为本次晚点。

**音频素材**：预录音频片段位于 `RailRouteAssistantDesktop/assets/audio/`，编译时自动复制到输出目录。素材来源：[gaotieguangboyinyuan](https://github.com/wangyetuoguan/gaotieguangboyinyuan)。

| 素材类型 | 文件 | 用途 |
|----------|------|------|
| 数字 | `0-9.mp3` | 车号数字、站台号、晚点分钟数 |
| 字母读音 | `A/B/D/G/K/Z.mp3` | 车号字母（A/B 仅备用） |
| 方向词 | `高/动/快/直.wav` | G/D/K/Z 字母的实际读音 |
| 句式片段 | `列车停靠在.wav` `站台.wav` `有乘坐.wav` `次列车.wav` `到.wav` | 播报句式拼接 |
| 提示音 | `广播开始音1.mp3` `广播结束音1.mp3` | 每条播报首尾提示音 |

**TTS 合成与语音来源**：预录音频覆盖车号字母/数字、已有方向词、完整句式片段和提示音；只有素材库没有的句式、字母和站名才由 TTS 补全。用户在“语音”菜单中选择首选补全引擎；该引擎失败时才自动降级：

| 优先级 | 引擎 | 适用场景 |
|--------|------|----------|
| 首选 | 在线百度 TTS（`fanyi.baidu.com/gettts`） | 默认选项，国内可达、无需 API key。百度公开接口只有一个默认中文女声，不再显示无效的“晓晓/云希”伪音色切换。 |
| 可选 | Windows OneCore（`Windows.Media.SpeechSynthesis`） | Win10 1809+ 自带，可分别选择 Huihui/Kangkang/Yaoyao 等实际 OneCore voice。 |
| 可选 | System.Speech（SAPI5） | 分别列出系统中已安装的中文 SAPI5 voice；与 OneCore 选项独立。 |

TTS 合成的内容包括：

- 字母 `C/T/X/S`（读「城/特/行/市域」，音频库缺这些字母）
- 句式词 `开往` `方向` `方向的列车` `接近` `即将发车，请做好准备` `正点发车` `晚点` `分发车`
- 所有站名（无法预录全量站名）

**语音设置菜单**：窗口顶部“语音”菜单用于选择缺词补全引擎和速度；切换 TTS 不影响预录素材：

- **在线 · 百度中文女声** — 默认补全引擎
- **系统 OneCore · ... / 系统 SAPI5 · ...** — 使用菜单中对应的真实本地 voice 补全
- **补全语音速度** — 1（最慢）到 7（最快），默认 7；同时作用于百度、OneCore 和 SAPI5 补全部分
- **试听当前补全语音** — 立即播放测试句，便于确认音色切换和语速设置已经生效
- **安装更多语音…** — 弹窗指引通过 Windows 设置添加更多中文 voice

引擎与语速持久化到 `%LOCALAPPDATA%\RailRouteAssistant\voice.json`，重启后保持。v2.6.2 及更早版本保存的 `edge:*` 伪音色键会自动迁移为百度选项。

**播报速度**：百度 TTS 直接使用菜单选择的有效范围 `spd=1..7`；OneCore/System.Speech 映射为 `-30%..+30%` 的 SSML 速度。相邻的缺词文本会合并后一次合成，减少短句之间的网络等待和停顿。数字、车号字母和首尾提示音继续按原始预录速度播放，以免变速造成音调变化或失真。

> 网吧或精简系统本地 TTS 组件缺失时，在线百度 TTS 仍可正常播报站名和句式；断网时回退本地，本地也不可用则只播预录音频。

### 桌面程序 UI

`2.7.0` 起主窗口分为“实时调度”“告警中心”“时距运行图”“会话回放”四页。
实时页保留原来的即时告警和列车列表；告警中心提供防抖、确认、静音、失联和恢复
历史；时距图按选定基准列车生成车站走廊；会话页负责安全记录和确定性回放。详细
规则见 [assistant-sessions.md](assistant-sessions.md)。

- **上半部分（列车列表区）**：按状态排序，不同车次类型用不同背景色区分
- **版本号**：窗口标题栏显示当前桌面程序版本，例如 `Rail Route 调度助手 v2.7.0`；若标题没有版本号，说明启动的是旧版 EXE
- **搜索框**：位于“所有列车”标题下方；输入完整或部分车号即时筛选，Enter 选中第一项，Esc 清空；点击被筛选隐藏的告警车次时会自动切换搜索条件
- **下半部分（告警区）**：按紧急 > 警告 > 信息排序

列车列表列：`车号 | 始发 | 终到 | km/h | 延误 | 信号 | 状态 | 当前停站 | 前方停站 | 站台`

“当前停站”仅在列车确实因 `Station` 停车时显示，例如 `南京站高速场 1道`；运行中、信号机前停车和等待入图时留空。“前方停站/站台”仍表示下一计划停站。

- **信号列**：显示开放/关闭/无信号
- **状态列**：显示运行中/停站/可发车/停车/等待入图/故障/完成
  - 运行中：在线且速度 > 0
  - 停站：到站停车（`StopReasons` 含 `Station`），附带停车时长和发车倒计时；需调向时额外显示“需调向”，如 `停站 需调向 已停2分15秒 还有1分30秒开车`
  - 停车：非到站停车，附带停车时长，如 `停车 已停15秒`
  - 可发车：达到发车条件但未启动
- **延误列**：停站时优先显示由本站计划发车时刻与游戏时钟计算的值；运行中仍保留游戏原始累计值作诊断。
- **前方停站列**：仅显示站名
- **站台列**：显示站台号（如 `3台`）

列车排序优先级：故障 > 在线停车（按剩余发车时间从少到多）> 运行中 > 在线其他 > 等待入图 > 其他 > 已完成

### 状态栏

顶部状态栏显示游戏连接状态与游戏内模拟时钟：

```
游戏时间 14:30:25  |  已连接  |  在线 5  等待 2  总计 12
```

游戏时间来自插件读取的 `Game.Time.ITimeController.CurrentTime`（TimeSpan），格式为 HH:MM:SS。游戏未就绪或读取失败时不显示该字段。

### 告警交互

- **点击告警条目**：在列车列表中定位并高亮对应车次，自动滚动到可见位置，焦点切换到列车列表
- 站台冲突/进路相交类告警的车次形如 `G123/G456`，点击时取第一个车次定位
- 列车列表每秒刷新会保留当前选中行，避免刷新清掉刚定位的车次

### 车次详情弹窗

可以通过以下任一种方式打开只读车次详情弹窗：

- 双击列车列表中的车次；
- 选中车次后按 `Enter`；
- 在列车行上右键，选择“查看车次详情（双击）”。

弹窗顶部显示车次、始发站、终点站、列车进入/离开地图的站名和计划时刻（即游戏地图内的起讫站），以及 12306 当天能够提供的全部车型。下方默认
按游戏 `ScheduledVisits` 顺序显示当前地图内的停车站点：

`序号 | 停车站点 | 站台 | 到站时间 | 发车时间 | 停车间隔`

- `NonStop=true` 的通过站不列入停车站点。
- 绝对计划时刻显示为 `HH:mm:ss`。
- 游戏只提供相对时刻时显示为 `+HH:mm:ss`，并在弹窗底部注明。
- 时刻缺失时显示 `--`；停车间隔优先使用游戏的 `StopDurationMinutes`，缺失时由到发时刻差计算。
- 点击“切换到 12306 全程”后，表格显示当天实际运行图中的全部站点、到站时间、
  发车时间、停站分钟和跨日标记；再次点击可切回游戏地图内时刻表。
- 12306 暂时不可用或当天没有该车次时，游戏时刻表仍可正常查看，在线页明确显示
  “暂未返回”，不会用离线始发终到表臆造全程停站。

### 窗口置顶

桌面程序默认 `TopMost = true` 浮在游戏窗口上方。为避免被游戏窗口遮挡：

- 失去焦点（`Deactivate`）时通过 `BeginInvoke` 切换 `TopMost = false → true` 重新置顶，不抢焦点、不干扰游戏操作
- 每秒刷新数据时检查并维持 `TopMost` 状态

> 注意：游戏以**独占全屏**运行时，Windows 不允许任何窗口显示在其上，这不是 `TopMost` 能解决的。请将 Rail Route 设为**窗口模式**或**无边框窗口模式**，桌面助手才能稳定浮在游戏上方。

### 右键菜单

在列车列表或告警列表上右键，可：
- **查看车次详情（双击）**：在列车列表中打开当前选中车次的始发、终到和计划停车表。
- **复制选中行**：复制当前选中的行（Tab 分隔，可粘贴到 Excel）。右键点击时会自动选中点击的行，并通过 `_lastRightClickedList` 记录操作的列表，避免 `Focused` 判断不准导致无反应。
- **复制全部列车数据**：复制全部列车数据（含表头）

车次背景色：

| 车次前缀 | 背景色 | 说明 |
|----------|--------|------|
| G（高铁）| 暗红 | |
| D（动车）| 暗蓝 | |
| DJ（动检）| 暗蓝绿 | 与普通 D 字头动车区分；播报读作“动检” |
| C 三字（Cxxx）| 暗绿 | 城际三字车次 |
| C 四字（Cxxxx）| 暗青 | 城际四字车次 |
| X（行包）| 暗紫 | |
| Z（直达）| 暗绿 | |
| T（特快）| 暗橙 | |
| K（快速）| 暗黄 | |
| L（临客）| 暗灰蓝 | |
| S（市域）| 暗紫 | 播报读作「市域」 |
| Y（游车）| 暗玫红 | 播报读作“游” |
| J | 暗蓝绿 | 检测或地图特殊列车 |
| A/N/P/Q | 暗褐红、暗橄榄、暗棕、暗青绿 | 地图特殊字头 |
| 数字 | 暗棕灰 | 纯数字普速车次 |
| 其他英文字头 | 暗紫灰 | 未单列字头的统一兜底色 |

颜色分类会先忽略地图专用“通”和字母前的 `0`，因此 `0G1703` 与 G 字头使用同一
暗红背景，`0Y2` 与 Y 字头使用同一暗玫红背景，但列表和语音仍保留原车号。

列车行颜色基于信号状态：

| 信号状态 | 速度 | 颜色 |
|----------|------|------|
| 关闭/等待 | ≤10km/h 或停车 | 红色（紧急） |
| 关闭/等待 | >10km/h | 橙色（警告） |
| 开放 | 任意 | 白色（正常） |

## 安装

### Release 一体包（推荐）

1. 从 GitHub Release 下载 `RailRouteAssistantDesktop.exe` 直接运行，或下载并解压
   `RailRouteAssistant-Windows-x64.zip`。v2.6.4 起单文件 EXE 已内置完整安装载荷，
   不再依赖相邻的 `payload` 目录。
2. 运行 `RailRouteAssistantDesktop.exe`。程序会扫描 Steam 库；找不到时让用户选择包含 `Rail Route.exe` 的游戏目录。
3. 确认后自动安装包内的 BepInEx 5.4.23.5 x64 和插件；默认 Steam 目录需要写权限时，只在安装阶段请求管理员权限。
4. 安装完成后自动通过 Steam 启动 Rail Route。以后运行同一桌面程序会记住游戏目录并自动启动游戏。

一体包为 self-contained，不要求目标电脑另装 .NET 8。BepInEx 5.4.23.5 原样按 MIT License 再分发并随包附许可证；不包含任何 Rail Route 或 Unity 文件。内置载荷会先校验并释放到 `%LOCALAPPDATA%\RailRouteAssistant\installer-payload`，再复制到游戏目录。

安装器按插件文件版本判断是否需要覆盖：仅桌面 UI/语音功能升级且插件版本不变时，可以保持游戏运行并只重启桌面助手；只有 `RailRouteAssistant.dll` 版本升高时才需要关闭并重新启动游戏。

v2.6.4 同步了程序集文件版本与 BepInEx 插件声明版本。由更早版本升级到
v2.6.4 时安装器会覆盖插件，因此升级完成后需要重启游戏一次。

### 开发构建

1. 本机安装 BepInEx 5.x 到 Rail Route 游戏目录。
2. 编译插件：
   ```shell
   dotnet build RailRouteAssistant/RailRouteAssistant.csproj -c Release
   ```
   编译后会自动复制到 BepInEx plugins 目录。

3. 编译桌面程序：
   ```shell
   dotnet build RailRouteAssistantDesktop/RailRouteAssistantDesktop.csproj -c Release
   ```
   发布为单文件时，两级离线车次表会一并封装并在启动时自动解压，无需另行复制 `data` 目录：
   ```shell
   dotnet publish RailRouteAssistantDesktop/RailRouteAssistantDesktop.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
   ```

4. 启动游戏，进入有列车的地图。

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
  "lastUpdate": "14:30:25",
  "serverTime": "14:30:25",
  "gameTime": "14:30:25",
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
      "frontAllocationState": -1,
      "routeTotal": 5,
      "routeCur": 1,
      "routeRemain": 3,
      "platform": 3,
      "nextStation": "南京站",
      "scheduledStops": [
        {
          "station": "南京站",
          "platform": 3,
          "arrivalTimeSec": 52200,
          "departureTimeSec": 52500,
          "stopMinutes": 5,
          "relativeTimes": false
        }
      ],
      "lastVisitDeparted": false,
      "lastArrivalScheduleDeviationSec": -125,
      "lastDepartureScheduleDelaySec": null,
      "requiresDirectionChange": false,
      "departureRemainingSec": 150,
      "currentDepartureScheduleDelaySec": null,
      "stopReasons": "",
      "nextPrepareSec": 150,
      "nextArrivalSec": 300,
      "notMovingSince": null,
      "mapEntryTimeSec": 52200,
      "mapExitTimeSec": 54000
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

字段说明：

| 字段 | 类型 | 说明 |
|------|------|------|
| `apiVersion` | number | localhost API 契约版本；结构化告警首次发布为 `1` |
| `pluginVersion` | string \| null | 当前 BepInEx 插件版本，用于桌面端诊断版本不一致 |
| `gameTime` | string \| null | 游戏内模拟时钟，格式 `HH:MM:SS`，由 `Game.Time.ITimeController.CurrentTime` 读取 |
| `gameTimeSeconds` | number \| null | 未取模的游戏绝对秒数，供会话跨日记录和时距图使用 |
| `delay` | number | 游戏原始的累计延误值；不用于判断单次发车是否晚点 |
| `scheduledStops` | array | 当前地图内计划停车表；含站名、站台、到发时刻、停车分钟和相对时刻标志，通过站已排除 |
| `lastArrivalScheduleDeviationSec` | number \| null | 最近一次实际到站首次观察时固定的“游戏时钟 - 该站计划到达时刻”；负数为早点、正数为晚点 |
| `lastDepartureScheduleDelaySec` | number \| null | 最近一次实际发车时，首次按“游戏时钟 - 该站计划发车时刻”固定的晚点秒数；供发车播报使用 |
| `currentDepartureScheduleDelaySec` | number \| null | 当前到站停车相对本站计划发车时刻的晚点秒数；供“即将发车”告警与停站列表使用 |
| `requiresDirectionChange` | boolean | 游戏原生调向标志（`StopAndReverse` 或 `ReverseOnceStopped`）；仅停站播报使用 |
| `nextPrepareSec` | number \| null | 下一站发车准备剩余秒数（停站时为发车倒计时，等待入图时为入图倒计时） |
| `nextArrivalSec` | number \| null | 下一站到达剩余秒数 |
| `notMovingSince` | number \| null | 列车停车起始 unix 时间戳（秒），桌面端据此计算"已停 X 分 Y 秒" |
| `mapEntryTimeSec` | number \| null | 列车进入当前游戏地图的计划时刻（游戏内绝对秒数），取自首个 `StationVisit.From`（含通过站） |
| `mapExitTimeSec` | number \| null | 列车离开当前游戏地图的计划时刻（游戏内绝对秒数），取自末个 `StationVisit.To`（含通过站） |
| `mapEntryStation` | string | 列车进入当前游戏地图的站名（取自首个 `StationVisit`，含通过站） |
| `mapExitStation` | string | 列车离开当前游戏地图的站名（取自末个 `StationVisit`，含通过站），即游戏地图内终点站 |

`alerts[]` 保留旧版 `level`、`train`、`message`，并增加 `kind`、`severity`、
`primaryTrainId`、`relatedTrainIds`、`stationName`、`platformNumber`、
`routeTrackIds`、`summary`、`fingerprint` 和 `timestampMs`。fingerprint 只由稳定语义
字段计算，不包含展示文案、严重度或时间戳。

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
| `Game.Context.Ctx` | 静态服务定位入口，`Deps` 返回 `IControllers` |
| `Game.Context.IControllers` | 控制器集合，`GameControllers` 返回 `IGameControllers` |
| `Game.IGameControllers` | 游戏控制器集合，`TimeController` 返回 `ITimeController` |
| `Game.Time.ITimeController` | 游戏时钟，`CurrentTime` 返回 `TimeSpan`（游戏内模拟时间） |

### 游戏内时间访问链

```
Game.Context.Ctx.Deps                       // static，返回 Game.Context.IControllers
              .GameControllers               // 返回 Game.IGameControllers
              .TimeController                // 返回 Game.Time.ITimeController
              .CurrentTime                   // System.TimeSpan  ← 游戏内模拟时钟
```

`ITimeController` 其他可用属性：`RealPlayTime`（真实游玩时长）、`TimeMultiplier`（时间倍速）、`Paused`/`Stopped`。本项目仅读取 `CurrentTime`，不调用任何会改变游戏状态的方法。

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
- **紧邻下一信号 AllocationState!=Allocated** = 下一信号不能通行
- **TargetSpeed ≈ 0** = 列车已进入制动距离；仅与真实信号阻挡组合后升级告警
- **NeedsRouteAhead** = 列车前方有 Auto 类型信号机但没有 pending route
- **LookaheadCount** = 前方铁轨段数（仅用于判断完全无进路的情况）
- **PlatformNumber** = 站台号（如 3台）

### 进路说明

本项目中的进路全部为**手动配置**，不涉及自动进路。告警系统帮助玩家判断：
- 信号是否开放（列车能否继续通行）
- 刚越过一个信号后，紧邻下一信号是否已开通
- 何时需要提前配置下一信号区间
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
├── GameInstallationManager.cs # 一体包首次安装、Steam 路径发现与自动启动
├── TrainDetailsForm.cs        # 双击车次后的始发终到与地图内停车表弹窗
├── MainForm.cs                # 主窗口，告警列表 + 列车列表
├── AssistantApiClient.cs       # localhost API 强类型解析
├── AssistantSessionAdapter.cs  # HTTP DTO 与会话模型转换
├── WorkspaceUi.cs              # 告警中心、时距图、记录与回放工作区
├── TimetableGraphControl.cs     # 双缓冲车站—时间自绘控件
├── TrainInfoService.cs        # 12306 在线 → 路路通 → 静态 12306 快照的车次始发终到查询
├── VoiceEngine.cs             # 语音播报引擎，音频拼接 + TTS 兜底
├── assets/audio/              # 预录音频素材（69 个文件）
├── data/                      # 路路通离线表与冻结的 12306 train_list.js 快照
└── RailRouteAssistantDesktop.csproj

tools/ExportLulutongTrainRoutes/ # 从用户本机 APK 导出离线车次降级表

src/RailRouteHelper.AssistantSessions/ # 会话协议、记录回放、告警与时距图投影
```

## 免责声明

本项目是非官方社区工具，与 Bitrich.info 或 Valve 无隶属、 endorsements 或合作关系。使用风险自负。
