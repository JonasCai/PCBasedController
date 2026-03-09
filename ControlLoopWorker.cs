using PCBasedController.Hardware;
using System.Diagnostics;
using System.Runtime.InteropServices;

namespace PCBasedController
{
    public class ControlLoopWorker : BackgroundService
    {
        private readonly ILogger<ControlLoopWorker> _logger;
        private readonly LeadshineDmcDriver _driver;
        private readonly IONodes _nodes;
        private readonly HwData _hwData;

        // 引入系统时钟精度设置
        [DllImport("winmm.dll", EntryPoint = "timeBeginPeriod")]
        private static extern uint TimeBeginPeriod(uint uMilliseconds);

        [DllImport("winmm.dll", EntryPoint = "timeEndPeriod")]
        private static extern uint TimeEndPeriod(uint uMilliseconds);

        private const int TargetCycleTimeMs = 10; // 设定 10ms 周期

        public ControlLoopWorker(ILogger<ControlLoopWorker> logger, LeadshineDmcDriver driver, IONodes nodes, HwData hwData)
        {
            _logger = logger;
            _driver = driver;
            _nodes = nodes;
            _hwData = hwData;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation($"控制循环已启动，目标周期: {TargetCycleTimeMs} ms");

            TimeBeginPeriod(1);// 提升系统时钟分辨率 -》1ms

            try
            {
                while (!_driver.Initialize(out short cardCount))
                {
                    var msg = cardCount == 0
                        ? "初始化失败，未找到控制卡或控制卡异常"
                        : $"初始化失败，存在重名卡或系统冲突, 代码={Math.Abs(cardCount) - 1}";
                    TriggerHardwareFault(msg, -1, false);

                    await Task.Delay(10000, stoppingToken);
                }

                ResetHardwareFault();

                var stopwatch = new Stopwatch();
                int errorCode = 0;
                long currentTick = Environment.TickCount64;

                while (!stoppingToken.IsCancellationRequested)
                {
                    stopwatch.Restart();

                    try
                    {
                        // 检查 UI 是否发起控制卡复位指令
                        if (_hwData.ConsumeResetRequest() && _hwData.IsFaulted && _hwData.IsResetNeeded)
                        {
                            _logger.LogInformation("正在重置控制卡...");
                            _driver.Reset();
                            Thread.Sleep(5000);//等待复位完成
                            ResetHardwareFault();
                            continue;
                        }

                        // --- 控制卡状态检测 ---
                        if (!CheckHardwareSafe(out errorCode))
                        {
                            TriggerHardwareFault("检测到控制卡故障", errorCode, true);
                        }

                        // --- 读取输入 ---
                        _nodes.PullAll();

                        if (!_hwData.IsFaulted)
                        {
                            currentTick = Environment.TickCount64;

                            // --- 执行控制逻辑 ---
                            //ProcessCell.Refresh(currentTick);//控制逻辑不抛出异常
                        }
                        else
                        {
                            // 硬件故障状态下：执行ProcessCell.ToSafe()
                        }

                        // --- 刷新输出 ---
                        _nodes.PushAll();

                    }
                    catch (DmcException ex)
                    {
                        TriggerHardwareFault(ex.Message, errorCode, ex.ErrorCode != 0);
                        // 硬件故障状态下：执行ProcessCell.ToSafe()
                        try { _nodes.PushAll(); } catch { /* 尽最大努力安全输出 */ }
                    }
                    catch (Exception e)
                    {
                        TriggerHardwareFault($"未知异常：{e.Message}", -1, true);
                        // 硬件故障状态下：执行ProcessCell.ToSafe()
                        try { _nodes.PushAll(); } catch { /* 尽最大努力安全输出 */ }
                    }

                    // --- 周期对齐 ---
                    var elapsed = stopwatch.ElapsedMilliseconds;
                    int sleepTime = TargetCycleTimeMs - (int)elapsed;

                    if (sleepTime > 0)
                    {
                        Thread.Sleep(sleepTime);
                    }
                    else if (sleepTime < 0) // 如果超时，记录警告
                    {
                        _logger.LogWarning($"周期超时! 耗时: {elapsed} ms");
                    }
                }
            }
            finally
            {
                _logger.LogWarning("控制服务正在关闭，执行安全停机序列...");
                try
                {
                    // 执行ProcessCell.ToSafe()
                    _nodes.PushAll(); // 退出前执行，确保物理硬件安全
                }
                catch { /* 忽略通讯错误 */ }
                _driver.Dispose();
                TimeEndPeriod(1);
            }
        }

        private bool CheckHardwareSafe(out int errorCode)
        {
            errorCode = _driver.ErrorCode;
            return errorCode == 0;
        }

        private void TriggerHardwareFault(string msg, int code, bool needsReset)
        {
            if (!_hwData.IsFaulted)
            {
                _logger.LogError($"系统进入故障安全状态: {msg}");
            }
            _hwData.IsFaulted = true;
            _hwData.Message = msg;
            _hwData.IsResetNeeded = needsReset;
            _hwData.ErrorCode = code;
        }

        private void ResetHardwareFault()
        {
            _hwData.IsFaulted = false;
            _hwData.Message = string.Empty;
            _hwData.IsResetNeeded = false;
            _hwData.ErrorCode = 0;
        }

    }
}
