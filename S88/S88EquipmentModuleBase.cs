using PCBasedController.gRPC;
using PCBasedController.S88;
using System.Collections.Concurrent;
using System.Runtime.InteropServices;

public abstract class S88EquipmentModuleBase(EquipmentModuleCfg cfg, ILogger<S88EquipmentModuleBase> logger) : IEquipmentModule
{
    // ==========================================
    // IEquipmentModule 接口方法
    // ==========================================
    public string Name => _cfg.Name;
    public EMState Status { get; private set; } = EMState.Idle;
    public void ExecuteCommand(InternalCommand command)
    {
        if (command.TargetObject == _cfg.Name)
        {
            _commandQueue.Enqueue(command);
            return;
        }

        if (_cMs.TryGetValue(command.TargetObject, out var cm))
        {
            cm.ExecuteCommand(command);
            return;
        }

        command.CallbackTcs?.TrySetResult(new CommandResult(CommandResultType.Rejected, $"指令目标未知：{command.TargetUnit}.{command.TargetObject}"));
    }
    public void Refresh(long currentTimestampMs)
    {
        _currentTimestampMs = currentTimestampMs;

        IsNewStep = _stepChangedPending;
        _stepChangedPending = false;

        try
        {
            CheckHardwareInterlocks();

            ProcessCommandQueue();

            if (Status == EMState.Busy)
                OnExecute();

            foreach (var cm in _cMs.Values)
                cm.Refresh(currentTimestampMs);
        }
        catch (Exception ex)
        {
            if (Status != EMState.Fault)
            {
                Status = EMState.Fault;
                _logger.LogError(ex, $"EM [{Name}] 发生内部异常，强制进入 Fault 状态");
            }
            ToSafe();
        }
    }
    public void ToSafe()
    {
        PurgeCommands();
        foreach (var cm in _cMs.Values)
            cm.ToSafe();
    }
    public bool TryGetCm(string name, out IControlModule? cm) => _cMs.TryGetValue(name, out cm);


    // ==========================================
    // 供子类重写的逻辑钩子 (Hooks)
    // ==========================================
    protected abstract bool ProcessCommand(InternalCommand cmd);
    protected virtual void OnExecute() { }
    protected virtual void CheckHardwareInterlocks() { }


    // ==========================================
    // 供子类调用的接口
    // ==========================================
    protected bool IsNewStep { get; private set; }
    protected long StepTime => _currentTimestampMs - _stepStartTimestamp;
    protected int Step
    {
        get => _step;
        set
        {
            if (_step != value)
            {
                _step = value;
                _stepChangedPending = true;
                _stepStartTimestamp = _currentTimestampMs;
            }
        }
    }
    protected bool StepTimeout(long ms) => StepTime > ms;
    protected void RegisterCm(IControlModule cm) => _cMs.TryAdd(cm.Name, cm);


    // ==========================================
    // 私有成员
    // ==========================================
    private int _step = 0;
    private long _currentTimestampMs;
    private long _stepStartTimestamp;
    private bool _stepChangedPending = true;
    private readonly EquipmentModuleCfg _cfg = cfg;
    private readonly ILogger<S88EquipmentModuleBase> _logger = logger;
    private readonly ConcurrentDictionary<string, IControlModule> _cMs = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentQueue<InternalCommand> _commandQueue = new();
    private void PurgeCommands()
    {
        while (_commandQueue.TryDequeue(out var cmd))
        {
            if (cmd?.CallbackTcs != null)
            {
                cmd.CallbackTcs.TrySetResult(new CommandResult(
                    CommandResultType.Rejected,
                    "指令被系统强制清理，未执行"
                ));
                _logger.LogWarning($"指令 [{cmd.TargetUnit}.{cmd.TargetObject}.{cmd.CommandName}] 被系统强制清理，未执行");
            }
        }
    }
    private void ProcessCommandQueue()
    {
        while (_commandQueue.TryDequeue(out var cmd))
        {
            if (cmd.CancelToken.IsCancellationRequested)
            {
                _logger.LogWarning($"指令 [{cmd.TargetUnit}.{cmd.TargetObject}.{cmd.CommandName}] 在排队期间已被调用方取消或超时 (3s)，已作为僵尸指令安全丢弃");
                continue;
            }

            ProcessCommand(cmd);
        }
    }
}

public class EquipmentModuleCfg
{
    public required string Name { get; init; }
    public required Func<uint> ReadSafetyDeviceState { get; init; }
}

public enum EMState
{
    Idle,
    Busy,
    Done,
    Fault
}
