using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sia.WebGPU;

public static unsafe partial class Wgpu
{
    public static Task OnSubmittedWorkDoneAsync(
        WgpuHandle<WGPUQueue> queue,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var state = AsyncRequestState<bool>.Create();
        try {
            var callback = new WGPUQueueWorkDoneCallbackInfo {
                Mode = WGPUCallbackMode.AllowSpontaneous,
                Callback = (delegate* unmanaged[Cdecl]<WGPUQueueWorkDoneStatus,
                    WGPUStringView, void*, void*, void>)(delegate* unmanaged[Cdecl]<WGPUQueueWorkDoneStatus,
                    WgpuCallbackMessage, void*, void*, void>)&OnSubmittedWorkDone,
                Userdata1 = state.UserData,
            };
            WgpuUnsafe.wgpuQueueOnSubmittedWorkDone(GetPointer(queue), callback);
            return state.Task.WaitAsync(cancellationToken);
        }
        catch {
            state.Dispose();
            throw;
        }
    }

    [UnmanagedCallersOnly(CallConvs = [typeof(CallConvCdecl)])]
    private static void OnSubmittedWorkDone(
        WGPUQueueWorkDoneStatus status,
        WgpuCallbackMessage message,
        void* userdata1,
        void* userdata2)
    {
        var state = AsyncRequestState<bool>.FromUserData(userdata1);
        try {
            if (status == WGPUQueueWorkDoneStatus.Success) {
                state.TrySetResult(true);
            }
            else {
                state.TrySetException(CreateRequestException(
                    "OnSubmittedWorkDone", status.ToString(), ReadCallbackMessage(message)));
            }
        }
        finally {
            state.Dispose();
        }
    }
}
