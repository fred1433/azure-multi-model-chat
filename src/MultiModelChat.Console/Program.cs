using MultiModelChat;
using MultiModelChat.ConsoleApp;
using MultiModelChat.Simulation;

// Usage:
//   dotnet run --project src/MultiModelChat.Console                 live, against the keys named in models.json
//   dotnet run --project src/MultiModelChat.Console -- --simulate    local stand-in, no network, no key
//   ... -- --config path/to/models.json

var simulate = args.Contains("--simulate");
var configIndex = Array.IndexOf(args, "--config");
var configPath = configIndex >= 0 && configIndex + 1 < args.Length
    ? args[configIndex + 1]
    : new[] { Path.Combine(Directory.GetCurrentDirectory(), "models.json"), Path.Combine(AppContext.BaseDirectory, "models.json") }
        .FirstOrDefault(File.Exists) ?? "models.json";

RunConfig config;
try
{
    config = ConfigLoader.Load(configPath);
}
catch (ConfigException e)
{
    Console.Error.WriteLine(e.Message);
    return 2;
}
catch (Exception e) when (e is IOException or UnauthorizedAccessException)
{
    Console.Error.WriteLine($"Cannot read the configuration file \"{configPath}\": {e.Message}");
    return 2;
}

var runner = simulate
    ? new ConversationRunner(() => new SimulatedFoundryHandler(), _ => "simulated-key", allowPlaceholders: true)
    : new ConversationRunner();

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

var report = await runner.RunAsync(config, cancel.Token);
Console.Write(ReportWriter.Markdown(report, simulate));
if (cancel.IsCancellationRequested) return 130;
return report.Models.All(m => !m.Skipped && m.Turns.All(t => t.Succeeded)) ? 0 : 1;
