using Microsoft.Extensions.AI;
using MultiModelChat.ConsoleApp;
using MultiModelChat.Simulation;
using static MultiModelChat.Tests.TestRows;

namespace MultiModelChat.Tests;

public class ReportTests
{
    [Fact]
    public async Task Multiline_answer_longer_than_the_table_cell_is_printed_in_full()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        const string json = "{\n  \"city\": \"Porto\",\n  \"guest\": \"Morgan\",\n  \"rooms\": 3\n}";
        server.Responder = (r, _) => Task.FromResult(SimulatedFoundry.ChatCompletion(r.Model!, json));

        var report = await Runner(server).RunAsync(Config(ChatRow("gpt-5.4-mini")), TestContext.Current.CancellationToken);
        var output = ReportWriter.Markdown(report, simulated: true);

        Assert.True(json.Length > 40);
        Assert.Contains(json, output);
        Assert.Contains("gpt-5.4-mini, turn 1 (ok):", output);
    }

    [Fact]
    public async Task Readme_reuse_example_applies_the_row_options()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        var row = ClaudeRow("claude-opus-5-5", options: """{"max_tokens":2048,"output_config":{"effort":"low"}}""");
        using var httpClient = new HttpClient(server.CreateTransport());
        var apiKey = "key-c";
        List<ChatMessage> messages = [new(ChatRole.User, Prompt1)];
        var cancellationToken = TestContext.Current.CancellationToken;

        // Same code as README, "Using the code in another app".
        using var client = ChatClientFactory.Create(row, apiKey, httpClient);

        var response = await client.GetResponseAsync(
            messages,
            options: OptionBridge.CreateChatOptions(row),
            cancellationToken: cancellationToken);

        Assert.Equal("Porto", response.Text);
        var body = Assert.Single(server.Requests).Json!;
        Assert.Equal(2048, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("low", body["output_config"]!["effort"]!.GetValue<string>());
    }

    [Fact]
    public async Task OpenAI_message_refusal_with_finish_reason_stop_is_refused_and_keeps_the_text()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = (r, _) => Task.FromResult(
            SimulatedFoundry.ChatCompletion(r.Model!, content: null, finishReason: "stop", refusal: "I cannot help with that request."));

        var report = await Runner(server).RunAsync(Config(ChatRow("gpt-6-astra")), TestContext.Current.CancellationToken);

        var turns = report.Models[0].Turns;
        Assert.Equal(TurnStatus.Refused, turns[0].Status);
        Assert.Equal("stop", turns[0].FinishReason);
        Assert.Contains("I cannot help with that request.", turns[0].Detail);
        Assert.Equal(TurnStatus.NotRun, turns[1].Status);
    }

    [Fact]
    public async Task Unexpected_error_envelopes_never_lose_the_report()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = (r, _) => Task.FromResult(r.Model switch
        {
            "null-error" => new FakeResponse(400, """{"error": null}"""),
            "string-error" => new FakeResponse(400, """{"error": "bad request"}"""),
            _ => SimulatedFoundry.Respond(r),
        });

        var report = await Runner(server).RunAsync(
            Config(ChatRow("null-error"), ChatRow("string-error", keyEnv: "KEY_B"), ChatRow("normal"), ClaudeRow("null-error-claude")),
            TestContext.Current.CancellationToken);

        Assert.Equal(4, report.Models.Count);
        var nullError = report.Models.Single(m => m.Entry.Deployment == "null-error").Turns[0];
        Assert.Equal(TurnStatus.Failed, nullError.Status);
        Assert.Contains("""{"error": null}""", nullError.Detail);
        var stringError = report.Models.Single(m => m.Entry.Deployment == "string-error").Turns[0];
        Assert.Equal(TurnStatus.Failed, stringError.Status);
        Assert.Contains("bad request", stringError.Detail);
        Assert.All(report.Models.Single(m => m.Entry.Deployment == "normal").Turns, t => Assert.Equal(TurnStatus.Ok, t.Status));
        Assert.Contains("normal", ReportWriter.Markdown(report, simulated: true));
    }

    [Fact]
    public async Task Conversation_total_is_marked_incomplete_when_a_count_is_missing()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = (r, _) => Task.FromResult(r.Model == "complete"
            ? SimulatedFoundry.ChatCompletion(r.Model, r.UserTurns == 1 ? "Porto" : "3", promptTokens: 40, completionTokens: 2)
            : SimulatedFoundry.ChatCompletion(r.Model!, r.UserTurns == 1 ? "Porto" : "3", promptTokens: 40, completionTokens: 2,
                includeUsage: r.UserTurns == 1));

        var report = await Runner(server).RunAsync(Config(ChatRow("complete"), ChatRow("missing", keyEnv: "KEY_B")),
            TestContext.Current.CancellationToken);

        var complete = report.Models.Single(m => m.Entry.Deployment == "complete").TotalTokens;
        Assert.True(complete.Complete);
        Assert.Equal("80 / 4", complete.ToString());
        var missing = report.Models.Single(m => m.Entry.Deployment == "missing").TotalTokens;
        Assert.False(missing.Complete);
        Assert.Equal([2], missing.TurnsWithoutCount);
        Assert.Equal("40 / 2, incomplete (no count for turn 2)", missing.ToString());
    }
}
