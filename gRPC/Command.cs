using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace PCBasedController.gRPC
{
    public enum CommandResultType
    {
        Accepted,    // 指令已入队
        Rejected,    // 被拒绝（如：模式不对、联锁不满足）
    }

    public record CommandResult(CommandResultType Type, string Message);

    public record InternalCommand(string TargetUnit, string TargetObject, string CommandName, Dictionary<string, string> Params,string JsonPayload = "", 
        TaskCompletionSource<CommandResult>? CallbackTcs = null,
        CancellationToken CancelToken = default);
}
