# `.mp.lz4` 存档读取

Rail Route 的 `.mp.lz4` 不是能由普通 LZ4 解压程序直接打开的“裸 LZ4 文件”。它是
MessagePack-CSharp 的压缩消息：外层使用 MessagePack 扩展码 `98`
（`Lz4BlockArray`），解压后的内容仍然是 MessagePack。

`MessagePackLz4SaveFileAdapter` 使用以下约束读取：

- `FileMode.Open` 与 `FileAccess.Read`，不会创建或写回文件；
- 允许游戏同时读写或替换存档，以便从独立进程观察；
- 默认拒绝超过 64 MiB 的源文件，并在读取期间再次检查增长；
- 使用 MessagePack 的 `UntrustedData` 安全模式；
- 单个 Array 或 Map 最多 1,000,000 项，并应用安全深度限制；
- 不使用 Typeless 反序列化，不按存档中的字符串加载任何 .NET 类型。

## 为什么不直接转成 JSON

存档内部的 MessagePack Map 允许任意值作为键，实际数据中可能出现 Map 作为另一
个 Map 的键。JSON 对象只允许字符串键；强制转成 JSON 会生成无效内容，或因键
字符串化而发生碰撞。

因此 Adapter 返回保留原始类型和顺序的 `SaveValue`：

- `SaveNil`
- `SaveBoolean`
- `SaveSignedInteger` / `SaveUnsignedInteger`
- `SaveFloat`
- `SaveString`
- `SaveBinary`
- `SaveArray`
- `SaveMap`（有序 `SaveMapEntry`，键和值都是 `SaveValue`）
- `SaveExtension`

已知是字符串键的 Map 可以通过索引器访问：

```csharp
ISaveFileAdapter adapter = new MessagePackLz4SaveFileAdapter();
var document = await adapter.ReadAsync(path, cancellationToken);
var root = (SaveMap)document.Root;
var value = root["knownStringKey"];
```

这个层只承诺“忠实读取”，不承诺字段名在游戏版本之间稳定。列车、车站、轨道和
手动交路的语义映射属于后续 schema mapper。

