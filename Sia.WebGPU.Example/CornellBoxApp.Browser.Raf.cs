using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;

namespace Sia.WebGPU.Example;

#if BROWSER
internal sealed unsafe partial class CornellBoxApp
{
    [DllImport(
        "__Internal_emscripten",
        EntryPoint = "emscripten_request_animation_frame_loop",
        ExactSpelling = true,
        CallingConvention = CallingConvention.Cdecl)]
    private static extern void EmscriptenRequestAnimationFrameLoop(
        delegate* unmanaged[Cdecl]<double, void*, int> callback,
        void* userData);

    private Task RunAnimationFrameLoopAsync()
    {
        var state = new AnimationFrameLoopState(this);
        var handle = GCHandle.Alloc(state);
        try {
            EmscriptenRequestAnimationFrameLoop(
                &OnAnimationFrame,
                (void*)GCHandle.ToIntPtr(handle));
        }
        catch {
            handle.Free();
            throw;
        }

        return state.Completion.Task;
    }

    [UnmanagedCallersOnly(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static int OnAnimationFrame(double timestamp, void* userData)
    {
        var handle = GCHandle.FromIntPtr((nint)userData);
        var state = (AnimationFrameLoopState)handle.Target!;

        try {
            if (state.App.RenderAnimationFrame(timestamp)) {
                return 1;
            }

            handle.Free();
            state.Completion.TrySetResult();
        }
        catch (Exception exception) {
            handle.Free();
            state.Completion.TrySetException(exception);
        }

        return 0;
    }

    private sealed class AnimationFrameLoopState(CornellBoxApp app)
    {
        public CornellBoxApp App { get; } = app;

        public TaskCompletionSource Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
}
#endif
