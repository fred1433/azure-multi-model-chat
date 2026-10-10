# azure-multi-model-chat

[![ci](https://github.com/fred1433/azure-multi-model-chat/actions/workflows/ci.yml/badge.svg)](https://github.com/fred1433/azure-multi-model-chat/actions/workflows/ci.yml)

A .NET 10 console app that sends the same two-turn conversation to every model listed in one configuration table, [`models.json`](models.json), on Microsoft Foundry (GPT, Grok, DeepSeek, Kimi and Claude alike) and prints each answer with its token counts.

**Verification status, 10 October 2026: 0 model run on Azure; tests against simulated responses built from the documented wire formats.** No Azure account was used to build this. It runs against your keys.

**Simulated output** (from the local stand-in, `--simulate`; these are not model answers):

| Model | API | Turn 1 | Turn 2 | Tokens turn 1 (in / out) | Tokens turn 2 (in / out) | Status |
|---|---|---|---|---|---|---|
| Kimi K2.6 | openai-chat | Porto | 3 | 38 / 10 | 63 / 8 | ok |
| Claude Haiku 5.5 | anthropic-messages | Porto | 3 | 38 / 26 | 63 / 25 | ok |
| Claude Opus 5.5 | anthropic-messages | Porto | 3 | 38 / 26 | 63 / 25 | ok |
| GPT-6 Astra | openai-chat | Porto | 3 | 38 / 2 | 63 / 1 | ok |
| GPT-5.4 mini | openai-chat | Porto | 3 | 38 / 2 | 63 / 1 | ok |
| Grok 4.7 | openai-chat | Porto | 3 | 38 / 2 | 63 / 1 | ok |
| GPT-5.6 Terra | openai-chat | Porto | 3 | 38 / 2 | 63 / 1 | ok |
| GPT-5.6 Luna | openai-chat | Porto | 3 | 38 / 2 | 63 / 1 | ok |
| DeepSeek V4 Flash | openai-chat | Porto | 3 | 38 / 10 | 63 / 8 | ok |

Turn 1: *Booking note: Morgan reserved three rooms in Porto. Which city is mentioned? Answer with the city only.*
Turn 2: *How many rooms were reserved? Answer with the number only.* (The room count is only in turn 1, so turn 2 needs the history.)

```bash
export FOUNDRY_API_KEY="<your key>"    # variable named by apiKeyEnv
dotnet run --project src/MultiModelChat.Console                # live
dotnet run --project src/MultiModelChat.Console -- --simulate  # no network, no key
```

Before a live run, replace `YOUR-RESOURCE` in [`models.json`](models.json) and set each `deployment` to your deployment name. Rows with a missing key or a placeholder are listed and skipped before any request is sent.

---

## Contents

- [One table, two protocols](#one-table-two-protocols)
- [Why two SDKs behind one interface](#why-two-sdks-behind-one-interface)
- [The configuration table](#the-configuration-table)
- [Options](#options)
- [Documented table](#documented-table)
- [Tokens, history and failures](#tokens-history-and-failures)
- [Tests](#tests)
- [Using the code in another app](#using-the-code-in-another-app)

## One table, two protocols

Foundry serves most models through an OpenAI-compatible route, and Claude through Anthropic's Messages API. The request shape, the token field names and the way reasoning comes back differ between the two, so "the same code for every model" holds above one small factory, not below it.

| | `openai-chat` rows (GPT, Grok, DeepSeek, Kimi) | `anthropic-messages` rows (Claude) |
|---|---|---|
| Route | `https://<resource>.openai.azure.com/openai/v1/chat/completions` (the `services.ai.azure.com` host is also accepted), no `api-version` ([Microsoft](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/endpoints)) | `https://<resource>.services.ai.azure.com/anthropic/v1/messages` ([Anthropic](https://platform.claude.com/docs/en/build-with-claude/claude-in-microsoft-foundry)) |
| Client | NuGet `OpenAI` 2.14.0 | NuGet `Anthropic.Foundry` 0.7.1 (`Anthropic` 12.55.0) |
| Key | `Authorization: Bearer <key>` | `x-api-key: <key>` |
| `model` field | deployment name | deployment name |
| Required fields | `model`, `messages` | `model`, `messages`, `max_tokens` |
| Token fields | `prompt_tokens`, `completion_tokens` | `input_tokens` + `cache_creation_input_tokens` + `cache_read_input_tokens`, `output_tokens` |
| Reasoning | `reasoning_content` on some non-OpenAI models ([Microsoft](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-chat-reasoning#read-reasoning-content)) | `thinking` blocks with a `signature`, text empty by default on Opus 5.5 ([Anthropic](https://platform.claude.com/docs/en/models/opus-5-5/migration-guide)) |

The C# Foundry client always builds its URL from the resource name and does not read `ANTHROPIC_FOUNDRY_BASE_URL` ([Anthropic](https://platform.claude.com/docs/en/build-with-claude/claude-in-microsoft-foundry#api-key-authentication)). Claude rows therefore take `resourceName`, and a Claude row that also carries a `baseUrl` is rejected at load time rather than silently ignored.

## Why two SDKs behind one interface

1. Microsoft's C# example for the v1 route uses the `OpenAI` package; Anthropic's Foundry page uses `Anthropic.Foundry`. Both are the vendors' own clients.
2. Both expose `Microsoft.Extensions.AI.IChatClient` (`AsIChatClient`), so the runner has one loop; the only protocol branch is [`ChatClientFactory.cs`](src/MultiModelChat.Core/ChatClientFactory.cs), under 50 lines.
3. A single hand-written `HttpClient` path would mean re-implementing two request formats, retries and Claude's thinking-block round trip.
4. Microsoft's sample [Azure-Samples/openai-anthropic-taste-test](https://github.com/Azure-Samples/openai-anthropic-taste-test) pairs the same two clients through `IChatClient` (`AnthropicFoundryClient(...).AsIChatClient(...)`); it authenticates with Entra ID and uses the Responses API, where this repo uses API keys and Chat Completions.
5. Package versions are pinned in [`Directory.Packages.props`](Directory.Packages.props).

## The configuration table

One row per model in [`models.json`](models.json):

| Field | Meaning |
|---|---|
| `name` | display name |
| `catalogId` | identifier in the Foundry catalog (documentation only) |
| `deployment` | value sent in the request's `model` field |
| `protocol` | `openai-chat` or `anthropic-messages` |
| `baseUrl` | `openai-chat` rows only |
| `resourceName` | `anthropic-messages` rows only |
| `apiKeyEnv` | name of the environment variable holding this row's key; the key never goes in the file |
| `options` | request-body fields sent as-is (see below) |
| `timeoutSeconds`, `maxRetries` | per-turn deadline and SDK retries (Anthropic's SDK retries twice by default and times out after 10 minutes, [Anthropic](https://platform.claude.com/docs/en/cli-sdks-libraries/sdks/csharp#retries); here both are explicit) |
| `enabled`, `suggested`, `notes`, `docs` | housekeeping; `docs` holds the pages each row was checked against |

Unknown fields are an error, so a typo is never ignored. The nine requested models are enabled. Two more are present but disabled, marked `suggested`, and untested:

- **Mistral-Large-3** (Preview): listed with eleven languages and Text or JSON output, a candidate when the text to parse is not in English ([Microsoft](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure?pivots=azure-direct-others#mistral-models-sold-by-azure)).
- **grok-4.1-fast-non-reasoning**: Microsoft describes the Grok models as designed for use cases such as data extraction; this variant is listed without a Preview tag and without reasoning ([Microsoft](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure?pivots=azure-direct-others#spacexai-models-sold-by-azure)).

## Options

"Options" here means the parameters of a text call. Files, tools and tool-calling loops are out of scope.

- Each row's `options` object holds top-level request-body fields, passed as-is: `max_completion_tokens` for a GPT row, `max_tokens` and `output_config` for Claude, anything else the model's API documents.
- They travel through `ChatOptions.RawRepresentationFactory`, not `AdditionalProperties`: `ChatCompletionOptions.Patch` on the OpenAI side, a raw `MessageCreateParams` body on the Anthropic side ([`OptionBridge.cs`](src/MultiModelChat.Core/OptionBridge.cs)). The factory builds a new object on every call, as [Microsoft recommends](https://learn.microsoft.com/en-us/dotnet/api/microsoft.extensions.ai.chatoptions.rawrepresentationfactory).
- Before anything is sent, each option is checked to be present, with the same JSON value, in what the SDK will serialize. Fields the runner owns (`model`, `messages`, `stream`) and keys the SDK cannot place at the top level are refused with a clear message.
- No option is added by default: no shared `temperature`, no `thinking` field.
- If the server refuses an option, the turn fails with the server's message. The option is never removed and the call never retried behind your back.

## Documented table

What the documentation says about each requested model, as of 10 October 2026. "Documented" links the page; "Not verified" means no source was found or nothing has been run. Nothing here was measured or refused by Azure.

| Model | API and status | Token fields | Parameter notes |
|---|---|---|---|
| `Kimi-K2.6` | Chat Completions, Preview, reasoning content: [Documented](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure?pivots=azure-direct-others#moonshot-ai-models-sold-by-azure) | `prompt_tokens` / `completion_tokens`: Not verified for this model | none listed: Not verified |
| `claude-haiku-5-5` | Messages: [Documented](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-from-partners#anthropic) | `input_tokens` + cache fields / `output_tokens`: [Documented](https://platform.claude.com/docs/en/api/messages/create) | `top_k`, `temperature`, `thinking={"type":"enabled"}` not supported; `top_p` must be 0.99: [Documented](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-from-partners#anthropic) |
| `claude-opus-5-5` | Messages: [Documented](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-from-partners#anthropic) | same as Haiku: [Documented](https://platform.claude.com/docs/en/api/messages/create) | thinking always on, `max_tokens` covers thinking plus text, sampling parameters rejected unless default: [Documented](https://platform.claude.com/docs/en/models/opus-5-5/migration-guide). `thinking={"type":"disabled"}`: rejected per Anthropic, "effort capped at high" per Microsoft. Documented, not tested. |
| `gpt-6-astra` | Chat Completions, reasoning: [Documented](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure#gpt-6) | `prompt_tokens` / `completion_tokens`, reasoning inside completion: [Documented](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning) | the one reasoning model exempt from the `temperature`/`top_p`/`max_tokens` restriction: [Documented](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning#unsupported-parameters) |
| `gpt-5.4-mini` | Chat Completions, reasoning: [Documented](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure#gpt-54) | as above: [Documented](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning) | `temperature`, `top_p`, penalties, `logprobs`, `logit_bias`, `max_tokens` unsupported; use `max_completion_tokens`: [Documented](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning#unsupported-parameters) |
| `grok-4.7` | Chat Completions and Responses: [Documented](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure?pivots=azure-direct-others#spacexai-models-sold-by-azure) | Not verified for this model | none listed: Not verified |
| `gpt-5.6-terra` | Chat Completions, reasoning: [Documented](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure#gpt-56) | as GPT-6 Astra: [Documented](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning) | as GPT-5.4 mini: [Documented](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning#unsupported-parameters) |
| `gpt-5.6-luna` | Chat Completions, reasoning: [Documented](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure#gpt-56) | as GPT-6 Astra: [Documented](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning) | as GPT-5.4 mini: [Documented](https://learn.microsoft.com/en-us/azure/foundry/openai/how-to/reasoning#unsupported-parameters) |
| `DeepSeek-V4-Flash` | Chat Completions, reasoning content: [Documented](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure?pivots=azure-direct-others#deepseek-models-sold-by-azure) | Not verified for this model | `temperature`, `top_p` and penalties often unsupported on reasoning models; check the catalog: [Documented](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-chat-reasoning#choose-parameters-for-reasoning-models) |

**Access.** Some quota tiers require a quota request before the GPT-6 family (Astra included) or `gpt-5.6` (Terra, Luna) can be deployed; Tier 5 and Tier 6 subscriptions have quota by default ([Microsoft](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-sold-directly-by-azure#gpt-6)). Claude needs a paid Azure subscription with a billing account in a country or region where Anthropic offers the models ([Microsoft](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/concepts/models-from-partners#anthropic)).

## Tokens, history and failures

- **Tokens, per turn.** Chat Completions: input `prompt_tokens`, output `completion_tokens`; cached and reasoning tokens are already inside those totals and are shown as details only. Messages: input is `input_tokens + cache_creation_input_tokens + cache_read_input_tokens` ([Anthropic](https://platform.claude.com/docs/en/build-with-claude/prompt-caching)), the three parts kept apart; output `output_tokens`. A field the server did not send prints `n/a`, never 0.
- **History.** The API keeps no state, so turn 2 resends turn 1. The history is the SDK's own message objects, never text rebuilt from the display. Claude's thinking blocks go back complete and in order, including the empty-text block that carries a signature ([Anthropic](https://platform.claude.com/docs/en/build-with-claude/preserved-thinking)). `reasoning_content` from Kimi or DeepSeek is shown apart from the answer and not sent back, as Microsoft advises ([Microsoft](https://learn.microsoft.com/en-us/azure/foundry/foundry-models/how-to/use-chat-reasoning#read-reasoning-content)).
- **Failures stay local.** Models run in parallel (bounded by `run.maxParallel`), each with its own deadline. A timeout, an HTTP error, an empty HTTP 200 or a response stopped by the token limit (`finish_reason: length`, `stop_reason: max_tokens`) marks that model's turn as failed; its turn 2 shows "not run" with the reason, and the other models carry on. Every HTTP attempt is counted, SDK retries included.

## Tests

`dotnet test` runs 38 xUnit tests. The stand-in Foundry endpoint is a real HTTP server on `127.0.0.1` (Kestrel); the SDKs' requests are redirected to it with their path and query unchanged, and it records each request body and header. Each response shape it returns cites its documentation page in [`SimulatedFoundry.cs`](src/MultiModelChat.Simulation/SimulatedFoundry.cs).

| Covered | Test file |
|---|---|
| each row sends the key named by its own `apiKeyEnv`; both v1 hosts accepted; no `api-version` on the wire; Claude URL built from the resource name | [ConversationTests](tests/MultiModelChat.Tests/ConversationTests.cs), [ConfigTests](tests/MultiModelChat.Tests/ConfigTests.cs) |
| Claude row with a `baseUrl` rejected; unknown field rejected; missing key reported before any call | ConfigTests, ConversationTests |
| simple and nested options present in the emitted body, both protocols; untransmittable option stops the row before sending; no shared options object between parallel models | [OptionTests](tests/MultiModelChat.Tests/OptionTests.cs) |
| option refused by the server: failed status, server message shown, one request | OptionTests |
| turn 2 request = first prompt, that model's own answer, second prompt; signed empty thinking block survives | ConversationTests |
| truncated or empty HTTP 200 counted as failures; turn 2 "not run" after a failed turn 1 | ConversationTests |
| slow model times out on its own deadline while the others finish; retries follow the row and every attempt is counted | ConversationTests |
| Anthropic 10 + 20 + 30 gives 60 input; OpenAI 60 with 30 cached stays 60; absent field is `n/a` | [TokenTests](tests/MultiModelChat.Tests/TokenTests.cs) |
| the shipped `models.json` runs both turns on all nine rows in simulation | ConversationTests |

What these tests cannot show: authentication against a real Foundry resource, deployment routing, and whether Azure accepts each parameter. Those need a live run with your keys.

CI ([workflow](.github/workflows/ci.yml)) restores, builds, runs the tests and the simulated run on every push.

## Using the code in another app

The runner is a class library, separate from the console display:

```csharp
using MultiModelChat;

var config = ConfigLoader.Load("models.json");
RunReport report = await new ConversationRunner().RunAsync(config);

foreach (var model in report.Models)
    foreach (var turn in model.Turns)
        Console.WriteLine($"{model.Entry.Name} turn {turn.Index}: {turn.Status} {turn.Answer} ({turn.Tokens.InOut})");
```

Or take a single `IChatClient` for one row with `ChatClientFactory.Create(row, apiKey, httpClient)` and call it like any other `Microsoft.Extensions.AI` client. The projects target `net10.0`, the current LTS ([.NET support policy](https://dotnet.microsoft.com/en-us/platform/support/policy/dotnet-core)).

```
src/MultiModelChat.Core         runner, client factory, options, token normalization (the reusable part)
src/MultiModelChat.Simulation   documented-shape stand-in used by --simulate and the tests
src/MultiModelChat.Console      the console app
tests/MultiModelChat.Tests      xUnit tests with the loopback HTTP server
```

## License

[MIT](LICENSE)
