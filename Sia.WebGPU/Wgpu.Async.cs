namespace Sia.WebGPU;

public static partial class Wgpu
{
    private static async Task AwaitBufferMapAsync(
        Task<bool> nativeTask,
        WgpuHandle<WGPUBuffer> buffer,
        CancellationToken cancellationToken)
    {
        try {
            await nativeTask;
            if (cancellationToken.IsCancellationRequested) {
                UnmapBuffer(buffer);
                cancellationToken.ThrowIfCancellationRequested();
            }
        }
        finally {
            Release(ref buffer);
        }
    }

    private static Task<WgpuHandle<T>> AwaitHandleAsync<T>(
        Task<WgpuHandle<T>> nativeTask,
        CancellationToken cancellationToken)
        where T : unmanaged =>
        cancellationToken.CanBeCanceled
            ? AwaitCancelableHandleAsync(nativeTask, cancellationToken)
            : nativeTask;

    private static async Task<WgpuHandle<T>> AwaitCancelableHandleAsync<T>(
        Task<WgpuHandle<T>> nativeTask,
        CancellationToken cancellationToken)
        where T : unmanaged
    {
        try {
            return await nativeTask.WaitAsync(cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) {
            _ = ReleaseWhenCompletedAsync(nativeTask);
            throw;
        }

        static async Task ReleaseWhenCompletedAsync(Task<WgpuHandle<T>> task)
        {
            WgpuHandle<T> result;
            try {
                result = await task;
            }
            catch {
                return;
            }
            Release(ref result);
        }
    }
}
