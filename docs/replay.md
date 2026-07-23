# 协议回放

`ProtocolReplayReader` 从可读 Stream 逐行产出 `RealtimeEnvelope`：

```csharp
var reader = new ProtocolReplayReader();
await foreach (var message in reader.ReadAllAsync(stream, cancellationToken))
{
    // 将 message 交给状态投影器或界面
}
```

行为约束：

- 接受 UTF-8 JSONL；LF 与 CRLF 都由 `StreamReader` 正确分行。
- 第一条消息可以从任意序号开始，之后必须连续加一。
- 重复、倒退或缺号抛出 `ReplaySequenceException`，包含行号、期望序号和实际
  序号。
- JSON 损坏或协议版本不支持时抛出 `ReplayLineException`，包含行号并保留原始
  异常。
- 取消令牌会停止异步枚举。
- Reader 不拥有调用方传入的 Stream；枚举结束或失败后仍由调用方决定何时关闭。

测试使用代码生成的内存记录，不包含游戏或玩家数据。

