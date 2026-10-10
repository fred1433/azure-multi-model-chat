using System.Text.Json.Nodes;
using MultiModelChat.Simulation;
using static MultiModelChat.Tests.TestRows;

namespace MultiModelChat.Tests;

public class OptionTests
{
    [Fact]
    public async Task OpenAI_options_reach_the_wire_including_nested_objects()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        var row = ChatRow("gpt-5.4-mini", options: """{"max_completion_tokens":321,"metadata":{"run":{"tag":"t1","n":[1,2]}}}""");

        await Runner(server).RunAsync(Config(row), TestContext.Current.CancellationToken);

        var body = server.For("gpt-5.4-mini")[0].Json!;
        Assert.Equal(321, body["max_completion_tokens"]!.GetValue<int>());
        Assert.Equal("t1", body["metadata"]!["run"]!["tag"]!.GetValue<string>());
        Assert.Equal(2, body["metadata"]!["run"]!["n"]!.AsArray().Count);
    }

    [Fact]
    public async Task Anthropic_options_reach_the_wire_including_nested_objects()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        var row = ClaudeRow("claude-opus-5-5", options: """{"max_tokens":2048,"output_config":{"effort":"low"},"metadata":{"user_id":"u-1"}}""");

        await Runner(server).RunAsync(Config(row), TestContext.Current.CancellationToken);

        var body = server.For("claude-opus-5-5")[0].Json!;
        Assert.Equal(2048, body["max_tokens"]!.GetValue<int>());
        Assert.Equal("low", body["output_config"]!["effort"]!.GetValue<string>());
        Assert.Equal("u-1", body["metadata"]!["user_id"]!.GetValue<string>());
        Assert.Null(body["thinking"]); // nothing added that was not configured
        Assert.Null(body["temperature"]);
    }

    [Theory]
    [InlineData("""{"messages":[]}""", "the runner sends it")]
    [InlineData("""{"stream":true}""", "the runner sends it")]
    [InlineData("""{"reasoning.effort":"low"}""", "cannot be transmitted")]
    public async Task Option_that_cannot_be_transmitted_fails_before_any_request(string options, string expected)
    {
        await using var server = await FakeFoundryServer.StartAsync();
        var report = await Runner(server).RunAsync(Config(ChatRow("grok-4.7", options: options)), TestContext.Current.CancellationToken);

        var model = Assert.Single(report.Models);
        Assert.True(model.Skipped);
        Assert.Contains(model.ConfigProblems, p => p.Contains(expected));
        Assert.Empty(server.Requests);
    }

    [Fact]
    public void Factory_builds_a_new_native_options_object_on_every_call()
    {
        foreach (var row in new[] { ChatRow("gpt-6-astra", options: """{"max_completion_tokens":10}"""), ClaudeRow("claude-haiku-5-5") })
        {
            var factory = OptionBridge.CreateFactory(row);
            Assert.NotSame(factory(null!), factory(null!));
        }
    }

    [Fact]
    public async Task Parallel_models_never_share_options()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        var a = ChatRow("model-a", options: """{"max_completion_tokens":111}""");
        var b = ChatRow("model-b", keyEnv: "KEY_B", options: """{"max_completion_tokens":222,"seed":7}""");

        await Runner(server).RunAsync(Config(a, b), TestContext.Current.CancellationToken);

        Assert.All(server.For("model-a"), r => { Assert.Equal(111, r.Json!["max_completion_tokens"]!.GetValue<int>()); Assert.Null(r.Json["seed"]); });
        Assert.All(server.For("model-b"), r => Assert.Equal(222, r.Json!["max_completion_tokens"]!.GetValue<int>()));
        Assert.Equal(2, server.For("model-a").Count);
        Assert.Equal(2, server.For("model-b").Count);
    }

    [Fact]
    public async Task OpenAI_option_refused_by_the_server_is_shown_as_sent_with_one_request()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        // Error envelope documented by Microsoft for Chat Completions; the message text is illustrative.
        server.Responder = (r, _) => Task.FromResult(SimulatedFoundry.ChatCompletionsError(400,
            "Unsupported parameter: 'temperature' is not supported with this model.", "unsupported_parameter"));

        var report = await Runner(server).RunAsync(
            Config(ChatRow("gpt-5.6-terra", options: """{"temperature":0.2}""", maxRetries: 2)), TestContext.Current.CancellationToken);

        var turns = report.Models[0].Turns;
        Assert.Equal(TurnStatus.Failed, turns[0].Status);
        Assert.Contains("HTTP 400: Unsupported parameter: 'temperature'", turns[0].Detail);
        Assert.Equal(1, turns[0].Attempts);
        Assert.Equal(TurnStatus.NotRun, turns[1].Status);
        var only = Assert.Single(server.Requests);
        Assert.Equal(0.2, only.Json!["temperature"]!.GetValue<double>()); // sent as configured, not removed and retried
    }

    [Fact]
    public async Task Anthropic_option_refused_by_the_server_is_shown_as_sent_with_one_request()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        // Message text documented for Claude Opus 5.5 (platform.claude.com/docs/en/api/errors#thinking-cannot-be-disabled).
        server.Responder = (r, _) => Task.FromResult(SimulatedFoundry.MessagesError(400, "invalid_request_error",
            "\"thinking.type.disabled\" is not supported for this model. Use \"thinking.type.adaptive\" and \"output_config.effort\" to control thinking behavior."));

        var report = await Runner(server).RunAsync(
            Config(ClaudeRow("claude-opus-5-5", options: """{"max_tokens":1024,"thinking":{"type":"disabled"}}""", maxRetries: 2)),
            TestContext.Current.CancellationToken);

        var turn = report.Models[0].Turns[0];
        Assert.Equal(TurnStatus.Failed, turn.Status);
        Assert.Contains("thinking.type.disabled", turn.Detail);
        Assert.Single(server.Requests);
        Assert.Equal("disabled", server.Requests.Single().Json!["thinking"]!["type"]!.GetValue<string>());
    }
}
