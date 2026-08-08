using System;
using System.Threading;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using HarmonyLib;

namespace RailRouteAssistant
{
    /// <summary>
    /// Rail Route 调度助手插件入口
    /// 纯 Harmony 补丁模式 - 不依赖 MonoBehaviour 生命周期
    /// 数据通过 HTTP 服务器暴露给外部桌面程序
    /// </summary>
    [BepInPlugin(PluginGuid, PluginName, PluginVersion)]
    public class Plugin : BaseUnityPlugin
    {
        public const string PluginGuid = "com.railroute.assistant";
        public const string PluginName = "Rail Route Assistant";
        public const string PluginVersion = "2.7.1";

        internal static ManualLogSource Log;

        // 配置
        internal static ConfigEntry<int> HttpPort;
        internal static ConfigEntry<float> UpdateInterval;

        private Harmony _harmony;
        private HttpServer _httpServer;
        private Thread _pollThread;
        private volatile bool _running = true;

        private void Awake()
        {
            Log = Logger;
            Log.LogInfo($"[{PluginName}] v{PluginVersion} 启动中...");

            HttpPort = Config.Bind("General", "HttpPort", 8787, "HTTP 服务端口");
            UpdateInterval = Config.Bind("General", "UpdateInterval", 1.0f, "数据更新间隔(秒)");

            // 应用 Harmony 补丁 - 修改游戏方法的 IL，不依赖 GameObject
            _harmony = new Harmony(PluginGuid);

            // 补丁 Train.Move - 每帧每列车调用
            var trainType = AccessTools.TypeByName("Game.Train.Train");
            if (trainType != null)
            {
                var moveMethod = AccessTools.Method(trainType, "Move");
                if (moveMethod != null)
                {
                    _harmony.Patch(moveMethod, postfix: new HarmonyMethod(typeof(TrainPatch), nameof(TrainPatch.Move_Postfix)));
                    Log.LogInfo("已 Hook Train.Move");
                }
                else
                {
                    Log.LogWarning("未找到 Train.Move 方法");
                }
            }
            else
            {
                Log.LogWarning("未找到 Game.Train.Train 类型");
            }

            // 精确捕获列车越过信号机的时刻：用于监视紧邻的下一座同向信号，
            // 而不是跳过多个已开放信号后才得到的远方阻挡信号。
            var semaphoreType = AccessTools.TypeByName("Game.Railroad.Semaphore");
            var afterTrainEntered = semaphoreType != null
                ? AccessTools.Method(semaphoreType, "AfterTrainEntered")
                : null;
            if (afterTrainEntered != null)
            {
                _harmony.Patch(afterTrainEntered,
                    postfix: new HarmonyMethod(typeof(TrainPatch), nameof(TrainPatch.SemaphoreAfterTrainEntered_Postfix)));
                Log.LogInfo("已 Hook Semaphore.AfterTrainEntered");
            }
            else
            {
                Log.LogWarning("未找到 Semaphore.AfterTrainEntered 方法");
            }

            // 启动 HTTP 服务器（后台线程）
            _httpServer = new HttpServer(HttpPort.Value);
            _httpServer.Start();
            Log.LogInfo($"HTTP 服务器已启动: http://localhost:{HttpPort.Value}");

            // 启动后台轮询线程（兜底：Move_Postfix 未被调用时也能采集数据）
            _pollThread = new Thread(PollLoop) { IsBackground = true, Name = "RailRoutePoll" };
            _pollThread.Start();
            Log.LogInfo("后台轮询线程已启动");

            Log.LogInfo($"[{PluginName}] 启动完成！");
        }

        /// <summary>
        /// 后台轮询 - 每 3 秒采集一次（兜底，Move_Postfix 也会采集但需要列车在移动）
        /// </summary>
        private void PollLoop()
        {
            while (_running)
            {
                try
                {
                    Thread.Sleep(3000);
                    TrainPatch.CollectAllTrains();
                }
                catch (Exception ex)
                {
                    Log.LogError($"轮询异常: {ex.Message}");
                }
            }
        }

        private void OnDestroy()
        {
            // 不停止任何东西！GameObject 被销毁是正常的
            Log.LogInfo($"[{PluginName}] 插件 GameObject 被销毁（补丁、HTTP 服务器继续运行）");
        }
    }
}
