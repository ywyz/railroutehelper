# 合规边界

最后核对：2026-07-23

> 本文是项目的工程风险边界，不构成法律意见。

## 结论

完整的“BepInEx 注入式实时插件”目前不能被判定为无条件合规。Rail Route 的公开
UGC 协议主要覆盖玩家制作的地图；没有找到 Bitrich.info 对第三方代码插件、运行时
注入或游戏程序集逆向的明确公开授权。Steam Subscriber Agreement 同时对修改、
反编译、逆向工程以及未经授权干预软件运行设置了限制。

因此，项目可以继续的第一阶段被限定为独立、只读、非注入式工具。BepInEx 自身
采用 LGPL-2.1 许可，这只解决 BepInEx 代码的使用与再分发条件，并不授予修改或
注入 Rail Route 的权利。

## 第一阶段允许范围

- 仅读取用户明确选择的、本机已有的存档文件；默认不改写、移动或删除存档。
- 解析工作在独立进程完成，不加载到游戏进程，不修改游戏内存或游戏文件。
- 以公开的 MessagePack/LZ4 格式实现通用容器读取，不复制或提交游戏程序集。
- 测试仅使用项目自行生成的最小合成夹具；不提交真实存档、地图名称、列车数据或
  创意工坊作品。
- 不访问 Steam 账号、网络协议、排行榜或多人游戏状态。
- 协议和 Adapter 保持游戏进程无关，以便未来在取得许可后接入实时数据源。

## 禁止范围

- 不分发 Rail Route、Unity 或创意工坊作品中的文件或提取资源。
- 不绕过加密、访问控制、反作弊、付费功能或 Steam DRM。
- 不实现自动操作、作弊、联机优势或排行榜功能。
- 未获 Bitrich.info 明确许可前，不发布或安装 BepInEx 插件，不向游戏进程注入
  代码，也不基于反编译源码复制实现。

## 实时插件许可门禁

开始实时插件开发前，需要取得 Bitrich.info 的书面确认，至少覆盖：

1. 允许为个人、非商业用途使用只读 BepInEx 插件；
2. 允许读取运行中游戏的公开托管对象状态，但不修改状态；
3. 允许为互操作目的检查必要的类型和字段名称；
4. 明确禁止在联机、排行榜和竞技场景使用，并确认这一限制是否足够；
5. 允许以开源形式发布插件源代码，但不附带任何游戏文件。

若开发商拒绝、未回复或附加条件，本项目仍保持为独立存档分析器，不跨越该门禁。

## 主要依据

- [Steam Subscriber Agreement](https://store.steampowered.com/subscriber_agreement/)
- [Rail Route License Agreement for User Generated Content](https://railroute.eu/license-agreement-for-user-generated-content/)
- [Rail Route Steam 商店页](https://store.steampowered.com/app/1124180/Rail_Route/)
- [BepInEx 官方仓库与 LGPL-2.1 许可](https://github.com/BepInEx/BepInEx)

