# Third-party notices

Rail Route Helper currently references:

| Component | Purpose | License | Source |
| --- | --- | --- | --- |
| MessagePack for C# 3.1.8 | MessagePack and LZ4BlockArray decoding | MIT | <https://github.com/MessagePack-CSharp/MessagePack-CSharp> |
| xUnit.net v3 3.2.2 | Test framework only | Apache-2.0 | <https://github.com/xunit/xunit> |
| NAudio 2.2.1 | Desktop audio playback | MIT | <https://github.com/naudio/NAudio> |
| BepInEx 5.4.23.5 | Unity Mono plugin loader in the Windows bundle | MIT | <https://github.com/BepInEx/BepInEx/tree/v5.4.23.5> |

NuGet lock files record direct and transitive package versions. The Windows one-click bundle
redistributes the unmodified BepInEx 5.4.23.5 x64 release with its license. It does not
redistribute any Rail Route or Unity files. The `build/UnityEngine.CoreModule.Stub` project is
an original minimal compile-time type stub for CI and is not included in release artifacts.

