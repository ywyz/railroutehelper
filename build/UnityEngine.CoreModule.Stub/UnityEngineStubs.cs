namespace UnityEngine
{
    // 仅用于无游戏 DLL 的 CI 编译。运行时始终由 Rail Route 自带 UnityEngine 提供这些类型。
    public class Object { }
    public class Component : Object { }
    public class Behaviour : Component { }
    public class MonoBehaviour : Behaviour { }
}
