namespace Sia.WebGPU.Example;

public static class Program
{
#if !BROWSER
    public static int Main()
    {
        try {
            using var app = new CornellBoxApp();
            app.Run();
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
#else
    public static async Task<int> Main()
    {
        try {
            using var app = new CornellBoxApp();
            await app.RunAsync();
            return 0;
        }
        catch (Exception exception) {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }
#endif
}
