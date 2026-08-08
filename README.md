# Rail Route Helper

Rail Route Helper 是一个社区工具，通过 BepInEx 插件在 Rail Route 游戏运行时实时采集列车数据，并提供桌面程序显示告警和列车状态。

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
- 实时列车状态：车号、速度、延误、进路、信号、当前停站与股道
- 告警引擎：越过信号后提前监视紧邻下一信号、停车超时、发车预告、入图预告、站台冲突（时间重叠判定）、进路相交
- 语音播报：等待入图接近、通过站前三分钟接车预告、到站/通过正晚点、中间站发车前预告和发车自动播报；下一站为通过站时明确加读“通过”
- 车次始发终到查询：按车号精确查询 12306；失败时依次降级到随桌面程序发布的授权路路通表和冻结的 12306 `train_list.js` 快照；查询时自动移除地图专用“通”和前导 `0`，相邻或斜杠形式的复合车次在首个停车站后切换到第二段编号
- 特殊车号播报：`DJ54` 读作“动检五四”，`Y1` 读作“游一”，`0G2524` 保留前导零读作“零高二五二四”
- 中文括注车号：部分地图（如沈阳枢纽）在车号后附加中文括注（如 `Z212(技停不办客)`），首次入图播报读出完整车号，后续播报只读主车号；12306 查询按纯车号 `Z212` 处理
- 车次分类着色：识别前导 `0/通` 后的真实字头；G、D、DJ、C、Z、T、K、Y、S、数字及其他特殊字头均有背景色
- 智能排序：运行中 > 停车 > 等待入图 > 已完成
- 车次搜索：列表上方提供搜索框，按完整或部分车号即时筛选
- 车次详情：游戏时刻表包含停车站和标注“（通过）”的通过站，并显示游戏中起点/终点、股道、到发时刻及 12306 全程数据
- 版本辨识：窗口标题栏显示桌面程序版本号，便于确认没有误启动旧版 EXE
- Windows 一体包：单文件 EXE 内置 BepInEx，首次运行自动发现或询问游戏目录，安装后自动启动 Steam 游戏

详见 [实时调度助手文档](docs/railroute-assistant.md)。

本项目是非官方社区工具，与 Bitrich.info 或 Valve 无隶属、背书或合作关系。

## 开发

需要 [.NET 8 SDK](https://dotnet.microsoft.com/download/dotnet/8.0)。

桌面程序依赖 `src/RailRouteHelper.Core`（车次编号规则），构建桌面程序会自动还原该引用：

```shell
dotnet build RailRouteAssistantDesktop/RailRouteAssistantDesktop.csproj
```

`tools/ExportLulutongTrainRoutes` 用于从用户本机的路路通 APK 导出离线车次路线 JSON，供桌面程序降级查询使用，不会把 APK 或导出的时刻数据写入仓库。

```shell
dotnet run --project tools/ExportLulutongTrainRoutes -- --apk <lulutong.apk> [--output <json>] [--report <json>]
```
