namespace Sia.Spirv.Bootstrap;

internal static class CommandLine
{
    public static BootstrapOptions? Parse(string[] args)
    {
        if (args.Length == 0 || args[0] is "-h" or "--help" or "help") {
            return null;
        }

        var installWorkload = args[0] switch {
            "install" => true,
            "bootstrap" => false,
            _ => throw new ArgumentException($"Unknown command '{args[0]}'.")
        };
        var dotnetPath = "dotnet";
        var sources = new List<string>();
        for (var index = 1; index < args.Length; index++) {
            switch (args[index]) {
                case "--dotnet":
                    dotnetPath = ReadValue(args, ref index, "--dotnet");
                    break;
                case "--source":
                    sources.Add(ReadValue(args, ref index, "--source"));
                    break;
                default:
                    throw new ArgumentException($"Unknown option '{args[index]}'.");
            }
        }

        return new BootstrapOptions(installWorkload, dotnetPath, sources);
    }

    public static void PrintHelp()
    {
        Console.WriteLine("Sia SPIR-V workload bootstrap");
        Console.WriteLine();
        Console.WriteLine("Usage:");
        Console.WriteLine("  dotnet spirv install [--dotnet <path>] [--source <feed>]");
        Console.WriteLine("  dotnet spirv bootstrap [--dotnet <path>]");
    }

    private static string ReadValue(string[] args, ref int index, string option)
    {
        index++;
        if (index >= args.Length || string.IsNullOrWhiteSpace(args[index])) {
            throw new ArgumentException($"Option '{option}' requires a value.");
        }
        return args[index];
    }
}
