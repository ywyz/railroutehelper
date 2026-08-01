# Windows 一体包安装说明

1. 解压 `RailRouteAssistant-Windows-x64.zip`，不要直接在压缩包内运行程序。
2. 双击 `RailRouteAssistantDesktop.exe`。标题栏会显示当前版本号；v2.6.3 的标题应为
   `Rail Route 调度助手 v2.6.3`。如果没有版本号或版本更旧，请关闭程序并确认
   启动的是刚解压的 EXE，而不是此前目录中的旧副本。
3. 程序会自动查找 Steam 中的 Rail Route；未找到时，请选择包含
   `Rail Route.exe` 的游戏安装目录。
4. 确认后，程序会安装 BepInEx 5 与 `RailRouteAssistant.dll`。Steam 默认目录
   位于 Program Files 时，Windows 只会在本次安装阶段请求管理员权限。
5. 安装完成后程序会通过 Steam 自动启动游戏。以后运行同一个
   `RailRouteAssistantDesktop.exe`，它会记住游戏目录并自动启动游戏。

首次进入游戏地图后，桌面助手会连接本机 `http://localhost:8787`。如果一直显示
“未连接游戏”，请关闭游戏后重新运行助手，并查看：

`<游戏目录>\BepInEx\LogOutput.log`

## 列车搜索与详情

- “所有列车”标题下方是搜索框，输入完整或部分车号即可即时筛选；按 `Esc`
  清空搜索。
- 双击列车行可打开车次详情；也可以先选中车次后按 `Enter`，或右键选择
  “查看车次详情（双击）”。
- 详情弹窗显示始发站、终点站、列车进入/离开地图的计划时刻，以及当前游戏地图内
  各停车站的到站时间、发车时间和停车间隔。点击"切换到 12306 全程"可查看当日
  12306 全程时刻表，再次点击切回游戏时刻表。
- 列车列表“当前停站”列在列车到站停车时显示站名和股道；“前方停站/站台”仍显示下一站。
- 地图车号 `0G2524` 保留原样显示和播报，但始发终到查询使用 `G2524`；`DJ` 读作“动检”。
- `0G6642G6641`、`DJ8598G3401` 等复合车次在首个计划停车站前使用第一段，停车后列表、查询和播报自动切换到第二段。

## 语音设置

- 预录的车号、数字、已有词句和提示音始终优先，素材库没有的内容才由 TTS 补全。
- 顶部“语音”菜单可以选择百度、OneCore 或 SAPI5 补全语音；“试听当前补全语音”可立即确认切换结果。
- “补全语音速度”提供 1–7 档，默认 7，设置会在重启后保留。

一体包只支持 Windows x64 Steam 版 Rail Route。程序不会包含或修改游戏 DLL；
安装内容仅为 BepInEx 文件和 `BepInEx\plugins\RailRouteAssistant.dll`。

BepInEx 5.4.23.5 按 MIT License 再分发，许可证见包内
`LICENSE-BepInEx.txt`。

仅更新桌面程序且插件文件版本未提高时，无需重启游戏；如果安装器明确提示插件需要更新，请关闭游戏后再运行助手。

## Windows Defender 与文件校验

正式 GitHub Release 同时提供 `SHA256SUMS.txt`。可用 Windows 文件属性或可信的哈希工具核对下载文件；助手的正常安装和运行不要求执行任何 PowerShell 命令，也不要求关闭 Defender 或添加排除项。若安全软件报告的是一条命令行而不是 `RailRouteAssistantDesktop.exe`，请不要放行该命令，并先核对其来源和发布页哈希。
