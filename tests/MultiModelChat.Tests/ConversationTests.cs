using System.Diagnostics;
using MultiModelChat.Simulation;
using static MultiModelChat.Tests.TestRows;

namespace MultiModelChat.Tests;

public class ConversationTests
{
    [Fact]
    public async Task Second_request_carries_first_prompt_then_that_models_own_answer_then_second_prompt()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = (r, _) => Task.FromResult(r.Path.EndsWith(SimulatedFoundry.MessagesPath)
            ? SimulatedFoundry.Message(r.Model!, r.UserTurns == 1 ? "Porto (claude)" : "3")
            : SimulatedFoundry.ChatCompletion(r.Model!, r.UserTurns == 1 ? $"Porto ({r.Model})" : "3"));

        var report = await Runner(server).RunAsync(
            Config(ChatRow("model-a"), ChatRow("model-b", keyEnv: "KEY_B"), ClaudeRow("claude-x")), TestContext.Current.CancellationToken);

        Assert.All(report.Models, m => Assert.All(m.Turns, t => Assert.Equal(TurnStatus.Ok, t.Status)));
        foreach (var (deployment, ownAnswer) in new[] { ("model-a", "Porto (model-a)"), ("model-b", "Porto (model-b)") })
        {
            var second = Messages(server.For(deployment)[1]);
            Assert.Equal(["user", "assistant", "user"], second.Select(m => m!["role"]!.GetValue<string>()));
            Assert.Equal(Prompt1, Text(second[0]!["content"]));
            Assert.Equal(ownAnswer, Text(second[1]!["content"]));
            Assert.Equal(Prompt2, Text(second[2]!["content"]));
        }
        var claudeSecond = Messages(server.For("claude-x")[1]);
        Assert.Equal(["user", "assistant", "user"], claudeSecond.Select(m => m!["role"]!.GetValue<string>()));
        Assert.Equal("Porto (claude)", Text(claudeSecond[1]!["content"]));
    }

    /// <summary>Message content is either a string or an array of typed blocks; returns the text either way.</summary>
    static string Text(System.Text.Json.Nodes.JsonNode? content) => content switch
    {
        System.Text.Json.Nodes.JsonValue v => v.GetValue<string>(),
        System.Text.Json.Nodes.JsonArray blocks => string.Concat(blocks
            .Where(b => b?["type"]?.GetValue<string>() == "text")
            .Select(b => b!["text"]!.GetValue<string>())),
        _ => "",
    };

    [Fact]
    public async Task Claude_signed_thinking_block_with_empty_text_survives_into_the_second_request_in_order()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = (r, _) => Task.FromResult(
            SimulatedFoundry.Message(r.Model!, r.UserTurns == 1 ? "Porto" : "3", thinkingSignature: "sig-turn-1"));

        var report = await Runner(server).RunAsync(Config(ClaudeRow("claude-opus-5-5")), TestContext.Current.CancellationToken);

        Assert.Equal(1, report.Models[0].Turns[0].SignedThinkingBlocks);
        var assistant = Messages(server.For("claude-opus-5-5")[1])[1]!["content"]!.AsArray();
        Assert.Equal(2, assistant.Count);
        Assert.Equal("thinking", assistant[0]!["type"]!.GetValue<string>());
        Assert.Equal("", assistant[0]!["thinking"]!.GetValue<string>());
        Assert.Equal("sig-turn-1", assistant[0]!["signature"]!.GetValue<string>());
        Assert.Equal("text", assistant[1]!["type"]!.GetValue<string>());
        Assert.Equal("Porto", assistant[1]!["text"]!.GetValue<string>());
    }

    [Fact]
    public async Task Reasoning_content_is_shown_apart_and_not_sent_back()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = (r, _) => Task.FromResult(r.Model == "DeepSeek-V4-Flash"
            ? SimulatedFoundry.ChatCompletion(r.Model, r.UserTurns == 1 ? "Porto" : "3", reasoning: "The note names Porto.")
            : SimulatedFoundry.ChatCompletion(r.Model!, r.UserTurns == 1 ? "Porto" : "3"));

        var report = await Runner(server).RunAsync(Config(ChatRow("DeepSeek-V4-Flash"), ChatRow("grok-4.7", keyEnv: "KEY_B")),
            TestContext.Current.CancellationToken);

        var deepseek = report.Models.Single(m => m.Entry.Deployment == "DeepSeek-V4-Flash").Turns[0];
        Assert.Equal("Porto", deepseek.Answer);
        Assert.Equal("The note names Porto.", deepseek.Reasoning);
        Assert.Null(report.Models.Single(m => m.Entry.Deployment == "grok-4.7").Turns[0].Reasoning); // shown as "absent"
        var resent = server.For("DeepSeek-V4-Flash")[1].Body;
        Assert.DoesNotContain("reasoning_content", resent);
        Assert.DoesNotContain("The note names Porto.", resent);
    }

    [Theory]
    [InlineData(Protocols.OpenAIChat, "length")]
    [InlineData(Protocols.AnthropicMessages, "max_tokens")]
    public async Task Http_200_stopped_by_the_token_limit_is_truncated_not_ok(string protocol, string reason)
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = (r, _) => Task.FromResult(protocol == Protocols.OpenAIChat
            ? SimulatedFoundry.ChatCompletion(r.Model!, "Por", finishReason: reason)
            : SimulatedFoundry.Message(r.Model!, "Por", stopReason: reason));
        var row = protocol == Protocols.OpenAIChat ? ChatRow("m") : ClaudeRow("m");

        var report = await Runner(server).RunAsync(Config(row), TestContext.Current.CancellationToken);

        Assert.Equal(TurnStatus.Truncated, report.Models[0].Turns[0].Status);
        Assert.Equal(reason, report.Models[0].Turns[0].FinishReason);
        Assert.Equal(TurnStatus.NotRun, report.Models[0].Turns[1].Status);
    }

    [Theory]
    [InlineData(Protocols.OpenAIChat, "content_filter")]
    [InlineData(Protocols.AnthropicMessages, "refusal")]
    public async Task Http_200_blocked_by_the_provider_is_filtered_not_ok(string protocol, string reason)
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = (r, _) => Task.FromResult(protocol == Protocols.OpenAIChat
            ? SimulatedFoundry.ChatCompletion(r.Model!, "blocked", finishReason: reason)
            : SimulatedFoundry.Message(r.Model!, "blocked", stopReason: reason));
        var row = protocol == Protocols.OpenAIChat ? ChatRow("m") : ClaudeRow("m");

        var report = await Runner(server).RunAsync(Config(row), TestContext.Current.CancellationToken);

        var turns = report.Models[0].Turns;
        Assert.Equal(TurnStatus.Filtered, turns[0].Status);
        Assert.Equal(reason, turns[0].FinishReason);
        Assert.Contains(reason, turns[0].Detail);
        Assert.Equal(TurnStatus.NotRun, turns[1].Status);
        Assert.Single(server.Requests);
    }

    [Theory]
    [InlineData(Protocols.OpenAIChat)]
    [InlineData(Protocols.AnthropicMessages)]
    public async Task Network_error_keeps_its_cause_for_both_protocols(string protocol)
    {
        var row = protocol == Protocols.OpenAIChat ? ChatRow("m") : ClaudeRow("m");
        var runner = new ConversationRunner(() => new FailingHandler(), name => Keys.GetValueOrDefault(name));

        var report = await runner.RunAsync(Config(row), TestContext.Current.CancellationToken);

        var turn = report.Models[0].Turns[0];
        Assert.Equal(TurnStatus.Failed, turn.Status);
        Assert.Contains("Name or service not known (res1.example:443)", turn.Detail);
    }

    sealed class FailingHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            throw new HttpRequestException("Name or service not known (res1.example:443)");
    }

    [Fact]
    public async Task Cancelling_the_run_returns_a_report_with_cancelled_turns()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = async (r, ct) =>
        {
            await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return SimulatedFoundry.Respond(r);
        };
        using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(500));

        var report = await Runner(server).RunAsync(Config(ChatRow("a"), ClaudeRow("b")), cancel.Token);

        Assert.All(report.Models, m =>
        {
            Assert.Equal(TurnStatus.Cancelled, m.Turns[0].Status);
            Assert.Equal(TurnStatus.NotRun, m.Turns[1].Status);
        });
    }

    [Fact]
    public async Task Http_200_with_empty_answer_is_a_failure()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = (r, _) => Task.FromResult(SimulatedFoundry.ChatCompletion(r.Model!, ""));

        var report = await Runner(server).RunAsync(Config(ChatRow("m")), TestContext.Current.CancellationToken);

        Assert.Equal(TurnStatus.Empty, report.Models[0].Turns[0].Status);
    }

    [Fact]
    public async Task Failed_first_turn_marks_second_turn_not_run_with_the_reason()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = (r, _) => Task.FromResult(new FakeResponse(404, """{"error":{"code":"DeploymentNotFound","message":"The API deployment for this resource does not exist."}}"""));

        var report = await Runner(server).RunAsync(Config(ChatRow("gpt-6-astra")), TestContext.Current.CancellationToken);

        var turns = report.Models[0].Turns;
        Assert.Equal(TurnStatus.Failed, turns[0].Status);
        Assert.Equal(TurnStatus.NotRun, turns[1].Status);
        Assert.Contains("turn 1 did not succeed", turns[1].Detail);
        Assert.Single(server.Requests);
    }

    [Fact]
    public async Task Each_row_sends_the_key_named_by_its_own_apiKeyEnv_to_the_documented_route()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        var rows = new[]
        {
            ChatRow("oa-1", keyEnv: "KEY_A"),
            ChatRow("oa-2", keyEnv: "KEY_B", baseUrl: "https://res2.services.ai.azure.com/openai/v1/"),
            ClaudeRow("cl-1", keyEnv: "KEY_C"),
            ClaudeRow("cl-2", keyEnv: "KEY_D", resourceName: "res3"),
        };

        await Runner(server).RunAsync(Config(rows), TestContext.Current.CancellationToken);

        var oa1 = server.For("oa-1")[0];
        Assert.Equal("Bearer key-a", oa1.Headers["authorization"]);
        Assert.Equal(("res1.openai.azure.com", "/openai/v1/chat/completions", ""), (oa1.Host, oa1.Path, oa1.Query));
        var oa2 = server.For("oa-2")[0];
        Assert.Equal("Bearer key-b", oa2.Headers["authorization"]);
        Assert.Equal(("res2.services.ai.azure.com", "/openai/v1/chat/completions", ""), (oa2.Host, oa2.Path, oa2.Query));
        Assert.DoesNotContain("api-version", oa2.Query);

        var cl1 = server.For("cl-1")[0];
        Assert.Equal("key-c", cl1.Headers["x-api-key"]);
        Assert.Equal(("res1.services.ai.azure.com", "/anthropic/v1/messages"), (cl1.Host, cl1.Path));
        var cl2 = server.For("cl-2")[0];
        Assert.Equal("key-d", cl2.Headers["x-api-key"]);
        Assert.Equal("res3.services.ai.azure.com", cl2.Host);
        Assert.Equal("https", cl2.Headers["x-original-scheme"]);
    }

    [Fact]
    public async Task Missing_key_is_reported_before_any_call_and_the_other_rows_still_run()
    {
        await using var server = await FakeFoundryServer.StartAsync();

        var report = await Runner(server).RunAsync(
            Config(ChatRow("no-key", keyEnv: "NOT_SET"), ChatRow("has-key")), TestContext.Current.CancellationToken);

        var skipped = report.Models.Single(m => m.Entry.Deployment == "no-key");
        Assert.True(skipped.Skipped);
        Assert.Contains(skipped.ConfigProblems, p => p.Contains("NOT_SET"));
        Assert.Empty(server.For("no-key"));
        Assert.Equal(2, server.For("has-key").Count);
    }

    [Fact]
    public async Task Slow_model_hits_its_own_deadline_without_holding_the_others()
    {
        await using var server = await FakeFoundryServer.StartAsync();
        server.Responder = async (r, ct) =>
        {
            if (r.Model == "slow") await Task.Delay(TimeSpan.FromSeconds(20), ct);
            return SimulatedFoundry.Respond(r);
        };
        var stopwatch = Stopwatch.StartNew();

        var report = await Runner(server).RunAsync(
            Config(ChatRow("slow", timeoutSeconds: 1), ChatRow("fast", keyEnv: "KEY_B"), ClaudeRow("claude-fast")),
            TestContext.Current.CancellationToken);

        stopwatch.Stop();
        Assert.True(stopwatch.Elapsed < TimeSpan.FromSeconds(10), $"took {stopwatch.Elapsed}");
        var slow = report.Models.Single(m => m.Entry.Deployment == "slow").Turns;
        Assert.Equal(TurnStatus.TimedOut, slow[0].Status);
        Assert.Contains("within 1s", slow[0].Detail);
        Assert.Equal(1, slow[0].Attempts);
        Assert.Equal(TurnStatus.NotRun, slow[1].Status);
        Assert.All(report.Models.Where(m => m.Entry.Deployment != "slow"), m => Assert.All(m.Turns, t => Assert.Equal(TurnStatus.Ok, t.Status)));
    }

    [Theory]
    [InlineData(Protocols.OpenAIChat)]
    [InlineData(Protocols.AnthropicMessages)]
    public async Task Sdk_retries_follow_the_row_and_every_attempt_is_counted(string protocol)
    {
        await using var server = await FakeFoundryServer.StartAsync();
        var calls = 0;
        server.Responder = (r, _) => Task.FromResult(Interlocked.Increment(ref calls) == 1
            ? (protocol == Protocols.OpenAIChat
                ? new FakeResponse(503, """{"error":{"code":"ServiceUnavailable","message":"busy"}}""")
                : SimulatedFoundry.MessagesError(529, "overloaded_error", "Overloaded"))
            : SimulatedFoundry.Respond(r));
        var row = protocol == Protocols.OpenAIChat ? ChatRow("m", maxRetries: 1) : ClaudeRow("m", maxRetries: 1);

        var report = await Runner(server).RunAsync(Config(row), TestContext.Current.CancellationToken);

        var turns = report.Models[0].Turns;
        Assert.Equal(TurnStatus.Ok, turns[0].Status);
        Assert.Equal(2, turns[0].Attempts);
        Assert.Equal(1, turns[1].Attempts);
        Assert.Equal(3, server.Requests.Count);
    }

    [Fact]
    public async Task Shipped_configuration_runs_both_turns_on_every_model_in_simulation()
    {
        var config = ConfigLoader.Load(Path.Combine(AppContext.BaseDirectory, "models.json"));
        var runner = new ConversationRunner(() => new SimulatedFoundryHandler(), _ => "simulated-key", allowPlaceholders: true);

        var report = await runner.RunAsync(config, TestContext.Current.CancellationToken);

        Assert.Equal(9, report.Models.Count);
        Assert.All(report.Models, m =>
        {
            Assert.Equal("Porto", m.Turns[0].Answer);
            Assert.Equal("3", m.Turns[1].Answer);
        });
    }
}
