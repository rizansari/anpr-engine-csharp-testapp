var repoEnv = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", ".env"));
Helpers.LoadDotEnv(repoEnv);
Helpers.LoadDotEnv(Path.GetFullPath(Path.Combine(Directory.GetCurrentDirectory(), ".env")));

var argv = Environment.GetCommandLineArgs().Skip(1).ToArray();
var command = argv.FirstOrDefault();
var remaining = argv.Skip(1).ToArray();

return command switch
{
    "testapi"   => await TestApiCommand.RunAsync(remaining),
    "benchmark" => await BenchmarkCommand.RunAsync(remaining),
    null or "" or "-h" or "--help" => PrintUsage(),
    _ => PrintUsage($"Unknown command: '{command}'"),
};

static int PrintUsage(string? error = null)
{
    if (error is not null)
        Console.Error.WriteLine($"Error: {error}\n");
    Console.WriteLine("""
        Usage: hvanpr <command> [options]

        Commands:
          testapi    Test the ANPR API with a single image or process a batch folder
          benchmark  Load-test the ANPR API and measure throughput / latency

        Run 'hvanpr <command> --help' for command-specific options.
        """);
    return error is null ? 0 : 1;
}
