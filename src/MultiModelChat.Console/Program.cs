using System.Text;
using MultiModelChat;
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
        .First(File.Exists);

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

var runner = simulate
    ? new ConversationRunner(() => new SimulatedFoundryHandler(), _ => "simulated-key", allowPlaceholders: true)
    : new ConversationRunner();

using var cancel = new CancellationTokenSource();
Console.CancelKeyPress += (_, e) => { e.Cancel = true; cancel.Cancel(); };

var report = await runner.RunAsync(config, cancel.Token);
Console.Write(ReportWriter.Markdown(report, simulate));
return report.Models.All(m => !m.Skipped && m.Turns.All(t => t.Succeeded)) ? 0 : 1;

static class ReportWriter
{
    public static string Markdown(RunReport report, bool simulated)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run {report.StartedAt:yyyy-MM-dd HH:mm} UTC, {(simulated
            ? "SIMULATED: no Azure endpoint was called; answers come from a local stand-in that returns the documented response shapes."
            : "LIVE: requests sent to the endpoints in models.json.")}");
        sb.AppendLine();
        for (var i = 0; i < report.Prompts.Count; i++) sb.AppendLine($"Turn {i + 1}: {report.Prompts[i]}  ");
        sb.AppendLine();

        var ran = report.Models.Where(m => !m.Skipped).ToList();
        if (ran.Count > 0) WriteResults(sb, report, ran);

        var skipped = report.Models.Where(m => m.Skipped).ToList();
        if (skipped.Count > 0)
        {
            sb.AppendLine("Not run (checked before any request)");
            sb.AppendLine();
            foreach (var m in skipped) sb.AppendLine($"- {m.Entry.Name}: {string.Join(" ", m.ConfigProblems)}");
        }
        return sb.ToString();
    }

    static void WriteResults(StringBuilder sb, RunReport report, List<ModelReport> ran)
    {
        sb.Append("| Model | API |");
        for (var i = 1; i <= report.Prompts.Count; i++) sb.Append($" Turn {i} |");
        for (var i = 1; i <= report.Prompts.Count; i++) sb.Append($" Tokens turn {i} (in / out) |");
        sb.AppendLine(" Status |");
        sb.AppendLine("|" + string.Concat(Enumerable.Repeat("---|", 3 + 2 * report.Prompts.Count)));
        foreach (var m in ran)
        {
            sb.Append($"| {m.Entry.Name} | {m.Entry.Protocol} |");
            foreach (var t in m.Turns) sb.Append($" {Cell(t.Answer, t.Status)} |");
            foreach (var t in m.Turns) sb.Append($" {t.Tokens.InOut} |");
            sb.AppendLine($" {Status(m)} |");
        }
        sb.AppendLine();
        if (report.Prompts.Count > 1)
        {
            sb.AppendLine("The API keeps no state: each later turn resends the earlier prompts and that model's own answers, which is why its input count is larger.");
            sb.AppendLine();
        }

        sb.AppendLine("Details");
        sb.AppendLine();
        foreach (var m in ran)
        {
            sb.AppendLine($"- {m.Entry.Name} (deployment `{m.Entry.Deployment}`)");
            foreach (var t in m.Turns)
            {
                var details = string.Join(", ", t.Tokens.Details.Select(d => $"{d.Key} {TokenCounts.Show(d.Value)}"));
                sb.AppendLine($"  - turn {t.Index}: {t.Status.ToString().ToLowerInvariant()}, " +
                    $"reasoning {(t.Reasoning is not null ? $"\"{Short(t.Reasoning, 80)}\"" : t.SignedThinkingBlocks > 0 ? $"text omitted ({t.SignedThinkingBlocks} signed thinking block kept in the history)" : "absent")}, " +
                    $"attempts {t.Attempts}, {(t.Latency is { } l ? $"{l.TotalMilliseconds:0} ms" : "n/a")}, " +
                    $"finish {t.FinishReason ?? "n/a"}{(details.Length > 0 ? $", {details}" : "")}" +
                    $"{(t.Detail is null ? "" : $", {t.Detail}")}");
            }
        }
        sb.AppendLine();
    }

    static string Cell(string? answer, TurnStatus status) =>
        status == TurnStatus.Ok ? Short(answer ?? "", 40).Replace("|", "\\|") : status == TurnStatus.NotRun ? "not run" : status.ToString().ToLowerInvariant();

    static string Status(ModelReport m)
    {
        var bad = m.Turns.FirstOrDefault(t => !t.Succeeded);
        return bad is null ? "ok" : Short($"turn {bad.Index} {bad.Status.ToString().ToLowerInvariant()}: {bad.Detail}", 90).Replace("|", "\\|");
    }

    static string Short(string text, int max)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= max ? flat : flat[..(max - 3)] + "...";
    }
}
