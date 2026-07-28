# 项目进度

更新时间：2026-07-28

## 第一阶段：基础设施

状态：已完成。

- [x] 合规边界与只读原则
- [x] Windows/Linux 共用的 .NET 8 解决方案
- [x] 版本化 JSON Lines 实时协议
- [x] `.mp.lz4` 只读 MessagePack Adapter
- [x] 协议记录与回放测试
- [x] Ubuntu 与 Windows GitHub Actions 验证

## 第二阶段：版本化存档语义

状态：进行中。

- [x] 对本机 100 份存档进行只读解码，0 份失败
- [x] 识别 15 个存档内嵌游戏版本：`1.7.5` 至 `2.3.24`
- [x] 定位列车、轨道、车站和节点分配状态的字段路径
- [x] 确认 `2.3.17`—`2.3.24` 的目标字段形状一致
- [x] 建立“游戏版本—字段 schema mapper”注册表
- [x] 映射当前列车、轨道、车站和进路开通证据
- [x] 用合成 fixture 覆盖已知 schema 和未知版本拒绝行为
- [x] 对 100 份真实存档运行只读语料验证
- [x] 用“南通站1/南通站2”受控存档对照确认分配码与进路目标字段
- [x] 用“太原枢纽全自动调度1/2/3”对照普通自动调度与手动进路表示
- [x] 识别 `PerpetualAutoRoute=true` 这一显式永久自动进路标记
- [ ] 找到能在单份存档中区分普通传感器自动进路与手动进路的稳定字段

当前 mapper 支持有真实语料的 `2.3.17`、`2.3.18`、`2.3.22`、`2.3.23`、
`2.3.24`。62 份受支持版本存档全部映射成功；其余 38 份旧版本仍可无损读取，
但会被语义 mapper 明确拒绝。

该阶段完成时的自动验证结果：

- 24 项测试通过（其中 schema mapper 7 项、Operations 5 项、Monitoring 3 项）；
- 62/62 份受支持语料映射成功，0 失败；
- 共映射 2,234 个车站快照、61,206 条活动轨道、692 个当前列车快照和
  28,917 条进路开通观察。

## 当前证据边界

- `savedTrainRepository.savedTrains` 可提供列车标识、车次、速度、占用节点、
  朝向目标、停止时间与停止原因；当前列车需同时满足 `disposed=false` 和
  `initialized=true`。
- `savedNodeRepository.nodes` 可提供轨道端点、站台关联和
  `allocationState`。
- 在抽样存档中，`allocationState=2` 的 82 个节点全部出现在列车占用集合中；
  `allocationState=1` 主要表现为未被列车占用的已分配节点。
- 南通站受控对照进一步确认：开通后形成 20 个 `allocationState=1` 的连续
  进路节点；列车当前所在节点为 `allocationState=2`；入口相关信号的
  `Connected` 指向南通站2道轨道。
- 太原枢纽自动调度对照显示，入口信号的 `Connected` 会随 Z21、D267 的进站
  目标在太原站5道、2道之间切换并在列车到达后释放；普通自动进路仍使用与手动
  进路相同的分配和连接字段。
- `PerpetualAutoRoute=true` 可直接证明该已分配信号处于永久自动进路模式；
  太原样本中有 2 个此类信号，mapper 将其来源标为 `Automatic`。
- `ArrivalSensor`、`DepartureSensor`、`RoutingSensor` 的配置只能证明地图具有
  自动化规则。普通传感器触发的当前进路实例没有显式来源字段，仍输出
  `Origin=Unknown`，也不会将全部已分配节点宣称为手动进路。

## 手动测试门禁

基础字段读取和 mapper 行为不需要现在手动测试，可以用合成数据与真实存档语料
自动验证。只有在继续确认普通传感器自动进路与手动进路的来源、以及一条完整进路
的边界时，才需要新的受控对照：在游戏内记录触发前、已建立、列车通过后的画面和
存档。

字段表、版本注册与调用方式见 [schema-mapping.md](schema-mapping.md)。

## 第三阶段：运行态推理

状态：前三个垂直切片已完成。

- [x] 建立纯内存 `OperationsAnalyzer` 深模块
- [x] 从轨道端点构建前向拓扑，并按 `Connected` 约束已选分支
- [x] 识别当前站台、`currentStopIndex` 下一停站和连续进路可达性
- [x] 输出进路开通终点、第一个未开通节点和歧义状态
- [x] 识别 `Established`、`Retargeted`、`Released` 节点级进路事件
- [x] 区分进站、在站、出站、等待进路、驶向进路终点和可能受阻
- [x] 提供跨平台 `analyze-save` / `compare-saves` CLI
- [x] 用南通手动进路与太原自动进路的脱敏合成快照建立回放测试
- [x] 在本机对五份受控真实存档执行只读 CLI 验收
- [x] 将 Operations 报告接入版本化实时协议和连续记录回放
- [x] 提供持续目录监听、启动补扫、文件稳定检查和周期性漏报补扫
- [x] 按文件修订去重，以拓扑身份隔离地图，并按各地图游戏时间排序
- [x] 对损坏、未知版本和 schema 不匹配存档输出脱敏诊断后继续
- [x] 提供 `watch-saves`、`--once` 和不覆盖已有文件的 `--record`
- [x] 建立南通/太原脱敏连续 JSONL 回放测试
- [x] 建立线程安全 `LiveOperationsProjector`，按地图投影最新运行图
- [x] 保留有界进路事件时间线和已解决告警历史
- [x] 为 `PossibleBlocked` 建立 Warning 的打开、持续、恢复和复发生命周期
- [x] 提供只绑定 loopback 的 localhost Web 仪表盘与 `/api/live`
- [x] 建立南通/太原 JSONL→ReplayReader→Projector 端到端回放测试
- [x] 在随机 loopback 端口对真实 Kestrel 页面和 API 执行 HTTP 测试
- [ ] 在更多地图上验证复杂咽喉、环路和多个同时分配分支

本机验收结果：

- 南通对照：C3804 的下一站为南通站2道，当前连续进路可达该站台，入口信号事件
  为 `Established`；
- 太原 `1→2`：D267 可达太原站2道，Z21 位于5道，入口信号事件为
  `Retargeted`（5道→2道）；
- 太原 `2→3`：D267 位于2道，Z21 正驶离5道，入口信号事件为 `Released`。
- `watch-saves --once` 对南通两份存档输出连续序号 `0,1` 和
  `Established`；对太原三份存档输出 `0,1,2` 和
  `Retargeted`、`Released`。
- 两组 JSONL 记录分别为 2 行和 3 行；再次指定同一记录路径会拒绝覆盖。
- 南通投影回放最终显示下一站2道可达和 `Established`；太原投影回放保留
  `Retargeted`、`Released` 时间线并最终显示列车位于2道。

最新自动验证结果：

- 29 项测试通过（其中 Operations/Live Operations 9 项、Web 1 项）；
- localhost Web HTTP 测试使用真实 Kestrel 和随机 loopback 端口；
- 全部回放 fixture 均由代码构造，不包含玩家原始存档。

运行态算法、证据等级和命令行用法见 [operations.md](operations.md)。
持续监听、协议负载和记录行为见 [monitoring.md](monitoring.md)。
投影器、告警生命周期和 localhost Web 见
[live-operations.md](live-operations.md)。

## 实时调度助手（RailRouteAssistant / RailRouteAssistantDesktop）

状态：持续迭代中。

实时调度助手通过 BepInEx 插件在游戏运行时采集列车数据，经 HTTP 提供给桌面程序
显示告警和列车状态。插件目标为 .NET Framework 4.7.2，桌面程序目标为
.NET 8 Windows Forms。

### 已完成

- [x] BepInEx 插件：Harmony 补丁 `Train.Move` + 后台轮询线程采集数据
- [x] HTTP 服务器（localhost:8787）提供 JSON API
- [x] 桌面程序：告警列表 + 列车列表，车次分类着色，智能排序
- [x] 告警引擎：进路未配置、信号关闭、即将停车、停车超时、发车预告、入图预告
- [x] 站台冲突检测：同站同站台，一列在站一列接近时告急
- [x] 进路相交检测：两列车前方进路轨道段有交集时告急
- [x] 通过 `TargetSpeed` 判断信号开放/关闭
- [x] 从信号机 `Front` Connection 获取 `AllocationState`，信号开放但前方轨道 Free(灰) 时提前预警
- [x] 从 `ResolvedStep.Destination` Connection 获取轨道标识用于碰撞检测
- [x] 桌面程序列布局：前方停站与站台号分列显示
- [x] GitHub Actions 工作流编译插件和桌面 exe

### 已移除

- 旧版进路冲突检测（基于下一站名匹配）：两列列车前往同一站不一定是冲突，
  追踪运行属于正常的进路复用。已替换为基于轨道段交集的碰撞检测。

### 待验证

- [ ] 在复杂咽喉和多信号区间场景下验证 `AllocationState` 提前预警准确性
- [ ] 在追踪运行场景下验证碰撞检测不误报
