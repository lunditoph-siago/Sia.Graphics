namespace Sia.Spirv.Bootstrap;

internal static class Program
{
    public static async Task<int> Main(string[] args)
    {
        try {
            var options = CommandLine.Parse(args);
            if (options is null) {
                CommandLine.PrintHelp();
                return 0;
            }
            return await WorkloadBootstrapper.RunAsync(
                options, CancellationToken.None);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"error: {exception.Message}");
            return 1;
        }
    }
}
