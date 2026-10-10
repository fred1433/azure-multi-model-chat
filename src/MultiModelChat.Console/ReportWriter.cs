using System.Text;
using MultiModelChat;

namespace MultiModelChat.ConsoleApp;

/// <summary>Console display only: the runner returns data, this turns it into Markdown.</summary>
public static class ReportWriter
{
    public static string Markdown(RunReport report, bool simulated)
    {
        var sb = new StringBuilder();
        sb.AppendLine($"Run {report.StartedAt:yyyy-MM-dd HH:mm} UTC, {(simulated
            ? "SIMULATED: no Azure endpoint was called; answers come from a local stand-in that returns the documented response shapes."
            : $"LIVE MODE: {report.Models.Sum(m => m.Trace.Count)} HTTP attempts.")}");
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
            sb.AppendLine("Each later turn resends the earlier prompts and that model's own answers.");
            sb.AppendLine();
        }

        sb.AppendLine("Answers (full text; the table shows the first 40 characters)");
        sb.AppendLine();
        foreach (var m in ran)
        {
            foreach (var t in m.Turns.Where(t => t.Answer is not null))
            {
                sb.AppendLine($"{m.Entry.Name}, turn {t.Index} ({t.Status.ToString().ToLowerInvariant()}):");
                sb.AppendLine();
                sb.AppendLine("```text");
                sb.AppendLine(t.Answer!.ReplaceLineEndings("\n").TrimEnd());
                sb.AppendLine("```");
                sb.AppendLine();
            }
        }

        sb.AppendLine("Details");
        sb.AppendLine();
        foreach (var m in ran)
        {
            sb.AppendLine($"- {m.Entry.Name} (deployment `{m.Entry.Deployment}`), conversation total (in / out): {m.TotalTokens}");
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
