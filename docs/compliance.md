# 合规边界

最后核对：2026-07-25

> 本文是项目的工程风险边界，不构成法律意见。

## 结论

没有找到 Bitrich.info 对第三方运行时插件的明确官方认可。UGC 协议中的 “mainly”
表明 UGC 不必只限地图，但第 2.2 条是用户向公司授予权利，不能单独解释为公司向
用户授予读取或反编译游戏的许可。

与此同时，捷克《版权法》§66 和欧盟 Directive 2009/24/EC Articles 5(3)、6
为合法用户观察、研究、测试程序运行，以及在严格条件下为独立程序互操作取得必要
信息提供法定例外。它们不是不受限制的插件授权，具体合同效力和适用条件仍需专业
法律意见。

工程上因此拆成两个门禁：私有、本机、单人、最小必要、只读的互操作原型可以继续
开发；对外发布加载器、自动安装、捆绑 BepInEx 或面向用户宣传为“官方认可”，仍
需 Bitrich.info 的明确确认或独立法律审查。BepInEx 的开源许可只处理 BepInEx
本身，不授予 Rail Route 权利。

## 私有实时原型允许范围

- 只在用户合法取得的本机副本、单人游戏中观察运行时对象；不写字段、不调用控制
  命令、不修改游戏状态。
- 只提取互操作所需的列车、信号、道岔、轨道连接、车站、游戏时间和占用状态；
  输出规范化 DTO，不做任意对象转储。
- 采集桥只向 `127.0.0.1` 发送数据，不开放局域网/公网监听，不访问多人游戏、
  Steam 账号、排行榜或游戏网络协议。
- 游戏 DLL、BepInEx 文件、反编译输出、类型探测结果和本机映射配置不得提交到
  仓库或 CI artifact。
- 独立进程仍可只读分析用户明确选择的存档，但这只是离线工具，不作为实时功能
  的实现。
- 测试仅使用项目自行生成的最小合成夹具；不提交真实存档、地图名称、列车数据或
  创意工坊作品。
- 测试优先使用项目自行生成的最小合成快照。

## 禁止范围

- 不分发 Rail Route、Unity 或创意工坊作品中的文件或提取资源。
- 不绕过加密、访问控制、反作弊、付费功能或 Steam DRM。
- 不实现自动操作、作弊、联机优势或排行榜功能。
- 不复制反编译方法体或使用取得的信息开发表达方式实质相似的程序。
- 未跨越公开发布门禁前，不发布加载器二进制、不提供自动注入/安装、不捆绑
  BepInEx 或游戏文件。

## 实时插件许可门禁

公开发布实时插件前，需要取得 Bitrich.info 的书面确认或独立法律审查，至少覆盖：

1. 允许为个人、非商业用途使用只读 BepInEx 插件；
2. 允许读取运行中游戏的公开托管对象状态，但不修改状态；
3. 允许为互操作目的检查必要的类型和字段名称；
4. 明确禁止在联机、排行榜和竞技场景使用，并确认这一限制是否足够；
5. 允许以开源形式发布插件源代码，但不附带任何游戏文件。

若开发商拒绝、未回复或附加条件，公开仓库只保留游戏无关的协议、接收端、合成
测试与离线分析器；私有原型不得被包装为用户发布物。

## 主要依据

- [Steam Subscriber Agreement](https://store.steampowered.com/subscriber_agreement/)
- [Rail Route License Agreement for User Generated Content](https://railroute.eu/license-agreement-for-user-generated-content/)
- [Rail Route Steam 商店页](https://store.steampowered.com/app/1124180/Rail_Route/)
- [捷克文化部《版权法》非官方英译（截至 2025-07-01）](https://mk.gov.cz/doc/cms_library/czech-republics-copyright-act-no-121_2000-as-amended_last-no-218_2025unofficial-010725_corr160226-21322.pdf)
- [Directive 2009/24/EC](https://eur-lex.europa.eu/legal-content/EN/ALL/?uri=CELEX%3A32009L0024)
- [BepInEx 官方仓库与 LGPL-2.1 许可](https://github.com/BepInEx/BepInEx)
