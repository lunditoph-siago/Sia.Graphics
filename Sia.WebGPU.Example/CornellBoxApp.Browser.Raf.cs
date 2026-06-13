using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sia.WebGPU.Example;

#if BROWSER
internal sealed unsafe partial class CornellBoxApp
{
    [DllImport(
        "__Internal_emscripten",
        EntryPoint = "emscripten_request_animation_frame",
        ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    private static extern int EmscriptenRequestAnimationFrame(
        delegate* unmanaged[Cdecl]<double, void*, int> callback,
        void* userData);

    private static Task RequestAnimationFrameAsync()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handle = GCHandle.Alloc(tcs);
        var result = EmscriptenRequestAnimationFrame(
            &OnAnimationFrame,
            (void*)GCHandle.ToIntPtr(handle));
        if (result != 0) {
            handle.Free();
            throw new InvalidOperationException(
                $"Failed to request an animation frame. Emscripten result: {result}.");
        }

        return tcs.Task;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnAnimationFrame(double _, void* userData)
    {
        var handle = GCHandle.FromIntPtr((nint)userData);
        var tcs = (TaskCompletionSource)handle.Target!;
        handle.Free();
        tcs.SetResult();
        return 0;
    }
}
#endif
