# Windows 一体包安装说明

1. 解压 `RailRouteAssistant-Windows-x64.zip`，不要直接在压缩包内运行程序。
2. 双击 `RailRouteAssistantDesktop.exe`。
3. 程序会自动查找 Steam 中的 Rail Route；未找到时，请选择包含
   `Rail Route.exe` 的游戏安装目录。
4. 确认后，程序会安装 BepInEx 5 与 `RailRouteAssistant.dll`。Steam 默认目录
   位于 Program Files 时，Windows 只会在本次安装阶段请求管理员权限。
5. 安装完成后程序会通过 Steam 自动启动游戏。以后运行同一个
   `RailRouteAssistantDesktop.exe`，它会记住游戏目录并自动启动游戏。

首次进入游戏地图后，桌面助手会连接本机 `http://localhost:8787`。如果一直显示
“未连接游戏”，请关闭游戏后重新运行助手，并查看：

`<游戏目录>\BepInEx\LogOutput.log`

一体包只支持 Windows x64 Steam 版 Rail Route。程序不会包含或修改游戏 DLL；
安装内容仅为 BepInEx 文件和 `BepInEx\plugins\RailRouteAssistant.dll`。

BepInEx 5.4.23.5 按 MIT License 再分发，许可证见包内
`LICENSE-BepInEx.txt`。
