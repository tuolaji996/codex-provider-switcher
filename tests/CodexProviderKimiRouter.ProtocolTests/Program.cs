using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using CodexProviderKimiRouter;

var failures = new List<string>();

void Check(bool condition, string message)
{
    if (!condition)
    {
        failures.Add(message);
    }
}

var requestJson = """
{
  "model": "requested-kimi-model",
  "instructions": "Be concise.",
  "input": [
    {"role":"user","content":[{"type":"input_text","text":"hello"}]},
    {"type":"function_call_output","call_id":"call_1","output":"done"}
  ],
  "stream": true,
  "max_output_tokens": 128,
  "reasoning": {"effort":"xhigh"},
  "tool_choice": {"type":"custom","name":"apply_patch"},
  "tools": [
    {"type":"function","name":"lookup","description":"Look up a value","parameters":{"type":"object","properties":{"q":{"type":"string"}}}},
    {"type":"custom","name":"apply_patch","description":"Apply a patch","format":{"type":"text"}}
  ]
}
""";
using (var requestDocument = JsonDocument.Parse(
           KimiResponsesTranslator.BuildChatCompletionsRequestJson(requestJson)))
{
    var root = requestDocument.RootElement;
    Check(root.GetProperty("model").GetString() == "requested-kimi-model", "The requested model was not preserved.");
    Check(root.GetProperty("stream_options").GetProperty("include_usage").GetBoolean(), "Streaming usage was not requested.");
    Check(root.GetProperty("reasoning_effort").GetString() == "max", "Reasoning effort was not mapped to Kimi max.");
    Check(root.GetProperty("tool_choice").GetProperty("type").GetString() == "function" &&
        root.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString() == "apply_patch",
        "A forced Responses custom-tool choice was not mapped to Chat's function choice shape.");
    Check(root.GetProperty("messages").GetArrayLength() == 3, "Instructions and input messages were not mapped.");
    Check(root.GetProperty("messages")[2].GetProperty("role").GetString() == "tool", "function_call_output was not mapped to a tool message.");
    Check(root.GetProperty("tools").GetArrayLength() == 2, "Function and custom tools were not forwarded.");
    Check(root.GetProperty("tools")[1].GetProperty("type").GetString() == "function", "Custom tools were not reversibly mapped to Kimi functions.");
    var customParameters = root.GetProperty("tools")[1]
        .GetProperty("function").GetProperty("parameters");
    Check(
        customParameters.GetProperty("required")[0].GetString() == "input" &&
        customParameters.GetProperty("properties").GetProperty("input")
            .GetProperty("type").GetString() == "string",
        "A raw custom tool was not wrapped in a reversible string parameter.");
}

var replayRequestJson = """
{
  "instructions":"Continue the coding task.",
  "input":[
    {"type":"reasoning","summary":[{"type":"summary_text","text":"first think"}]},
    {"type":"function_call","call_id":"call_lookup","name":"lookup","arguments":"{\"q\":\"x\"}"},
    {"type":"custom_tool_call","call_id":"call_patch","name":"apply_patch","input":"*** Begin Patch"},
    {"type":"function_call_output","call_id":"call_lookup","output":"value"}
  ]
}
""";
using (var replayDocument = JsonDocument.Parse(
           KimiResponsesTranslator.BuildChatCompletionsRequestJson(replayRequestJson)))
{
    var messages = replayDocument.RootElement.GetProperty("messages");
    Check(messages.GetArrayLength() == 3, "Reasoning/tool replay did not preserve system, assistant, and tool turns.");
    var assistant = messages[1];
    Check(assistant.GetProperty("role").GetString() == "assistant" &&
        assistant.GetProperty("reasoning_content").GetString() == "first think" &&
        assistant.GetProperty("tool_calls").GetArrayLength() == 2,
        "Reasoning and following function/custom calls were not combined into one assistant message.");
    Check(assistant.GetProperty("tool_calls")[1].GetProperty("function").GetProperty("arguments")
        .GetString() == "{\"input\":\"*** Begin Patch\"}",
        "Custom-tool replay did not preserve its reversible input wrapper.");
}

var namespacedToolsRequestJson = """
{
  "input":[
    {"type":"function_call","call_id":"call_old","name":"functions.exec","arguments":"{\"cmd\":\"pwd\"}"},
    {"type":"function_call_output","call_id":"call_old","output":"/workspace"},
    {"role":"assistant","content":null,"tool_calls":[
      {"id":"call_existing","type":"function","function":{"name":"github:github","arguments":"{}"}}
    ]}
  ],
  "tool_choice":{"type":"function","name":"collaboration.spawn_agent"},
  "tools":[
    {"type":"function","name":"functions.exec","parameters":{"type":"object"}},
    {"type":"function","name":"functions_exec","parameters":{"type":"object"}},
    {"type":"function","name":"collaboration.spawn_agent","parameters":{"type":"object"}},
    {"type":"function","name":"github:github","parameters":{"type":"object"}},
    {"type":"custom","name":"plugin:apply.patch","format":{"type":"text"}}
  ]
}
""";
KimiToolNameMap namespacedToolMap;
using (var namespacedRequestDocument = JsonDocument.Parse(namespacedToolsRequestJson))
{
    var mappedRequest = KimiResponsesTranslator.BuildChatCompletionsRequest(
        namespacedRequestDocument.RootElement,
        out namespacedToolMap);
    using var mappedDocument = JsonDocument.Parse(mappedRequest.ToJsonString());
    var mappedRoot = mappedDocument.RootElement;
    var mappedNames = mappedRoot.GetProperty("tools")
        .EnumerateArray()
        .Select(tool => tool.GetProperty("function").GetProperty("name").GetString() ?? string.Empty)
        .ToArray();
    Check(mappedNames.Distinct(StringComparer.Ordinal).Count() == mappedNames.Length,
        "Namespaced tool normalization produced a collision.");
    Check(mappedNames.All(name =>
            name.Length is > 0 and <= 64 &&
            char.IsAsciiLetter(name[0]) &&
            name.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-')),
        "A namespaced tool name was not normalized to the K3 Chat Completions contract.");
    Check(namespacedToolMap.ToUpstream("functions.exec") != "functions.exec" &&
        namespacedToolMap.ToUpstream("github:github") != "github:github" &&
        namespacedToolMap.ToUpstream("functions_exec") == "functions_exec",
        "Dot and colon names were not mapped while a valid name was unexpectedly changed.");
    Check(
        mappedRoot.GetProperty("tool_choice").GetProperty("function").GetProperty("name").GetString() ==
        namespacedToolMap.ToUpstream("collaboration.spawn_agent"),
        "A forced namespaced tool choice did not use the same upstream name mapping.");
    Check(mappedRoot.GetProperty("messages")[0].GetProperty("tool_calls")[0]
            .GetProperty("function").GetProperty("name").GetString() ==
        namespacedToolMap.ToUpstream("functions.exec"),
        "A replayed Responses tool call was not mapped for K3.");
    Check(mappedRoot.GetProperty("messages")[2].GetProperty("tool_calls")[0]
            .GetProperty("function").GetProperty("name").GetString() ==
        namespacedToolMap.ToUpstream("github:github"),
        "An existing assistant tool call was not mapped for K3.");
}

var namespacedToolCompletion = new JsonObject
{
    ["id"] = "chat-namespaced-tool",
    ["choices"] = new JsonArray
    {
        new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["content"] = null,
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "call_exec",
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = namespacedToolMap.ToUpstream("functions.exec"),
                            ["arguments"] = "{\"cmd\":\"pwd\"}"
                        }
                    }
                }
            },
            ["finish_reason"] = "tool_calls"
        }
    }
}.ToJsonString();
using (var historyDocument = JsonDocument.Parse(namespacedToolCompletion))
{
    var assistantHistory = KimiResponsesTranslator.BuildAssistantMessageForHistory(historyDocument.RootElement);
    Check(assistantHistory?["tool_calls"]?[0]?["function"]?["name"]?.GetValue<string>() ==
        namespacedToolMap.ToUpstream("functions.exec"),
        "Cached non-streaming history did not retain the legal upstream tool name.");
}

using (var namespacedToolResponse = JsonDocument.Parse(
           KimiResponsesTranslator.TranslateNonStreaming(
               namespacedToolCompletion,
               "k3",
               null,
               null,
               toolNameMap: namespacedToolMap)))
{
    Check(namespacedToolResponse.RootElement.GetProperty("output")[0].GetProperty("name").GetString() ==
        "functions.exec",
        "A non-streaming K3 tool call did not restore the original Codex tool name.");
}

var namespacedStreamChunk = new JsonObject
{
    ["id"] = "chat-namespaced-stream",
    ["choices"] = new JsonArray
    {
        new JsonObject
        {
            ["index"] = 0,
            ["delta"] = new JsonObject
            {
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["id"] = "call_exec",
                        ["function"] = new JsonObject
                        {
                            ["name"] = namespacedToolMap.ToUpstream("functions.exec"),
                            ["arguments"] = "{\"cmd\":\"pwd\"}"
                        }
                    }
                }
            }
        }
    }
}.ToJsonString();
var namespacedStreamFixture = $"data: {namespacedStreamChunk}\n\ndata: [DONE]\n\n";
var namespacedTranslatedStream = KimiResponsesTranslator.TranslateStreaming(
    namespacedStreamFixture,
    "k3",
    null,
    null,
    toolNameMap: namespacedToolMap);
Check(namespacedTranslatedStream.Contains("\"name\":\"functions.exec\"", StringComparison.Ordinal) &&
    !namespacedTranslatedStream.Contains(
        $"\"name\":\"{namespacedToolMap.ToUpstream("functions.exec")}\"",
        StringComparison.Ordinal),
    "A streaming K3 tool call did not restore the original Codex tool name.");

var lateNamespacedStreamChunkOne = new JsonObject
{
    ["choices"] = new JsonArray
    {
        new JsonObject
        {
            ["delta"] = new JsonObject
            {
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["id"] = "call_late_exec",
                        ["function"] = new JsonObject { ["arguments"] = "{\"cmd\":" }
                    }
                }
            }
        }
    }
}.ToJsonString();
var lateNamespacedStreamChunkTwo = new JsonObject
{
    ["choices"] = new JsonArray
    {
        new JsonObject
        {
            ["delta"] = new JsonObject
            {
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["index"] = 0,
                        ["function"] = new JsonObject
                        {
                            ["name"] = namespacedToolMap.ToUpstream("functions.exec"),
                            ["arguments"] = "\"pwd\"}"
                        }
                    }
                }
            }
        }
    }
}.ToJsonString();
var lateNamespacedTranslatedStream = KimiResponsesTranslator.TranslateStreaming(
    $"data: {lateNamespacedStreamChunkOne}\n\ndata: {lateNamespacedStreamChunkTwo}\n\ndata: [DONE]\n\n",
    "k3",
    null,
    null,
    toolNameMap: namespacedToolMap);
Check(lateNamespacedTranslatedStream.Contains("\"name\":\"functions.exec\"", StringComparison.Ordinal) &&
    lateNamespacedTranslatedStream.Contains("pwd", StringComparison.Ordinal) &&
    !lateNamespacedTranslatedStream.Contains("event: response.failed", StringComparison.Ordinal),
    "A namespaced tool whose name arrived after its first stream chunk was rejected or not restored.");

var customNamespacedToolCompletion = new JsonObject
{
    ["id"] = "chat-namespaced-custom",
    ["choices"] = new JsonArray
    {
        new JsonObject
        {
            ["message"] = new JsonObject
            {
                ["role"] = "assistant",
                ["tool_calls"] = new JsonArray
                {
                    new JsonObject
                    {
                        ["id"] = "call_custom",
                        ["type"] = "function",
                        ["function"] = new JsonObject
                        {
                            ["name"] = namespacedToolMap.ToUpstream("plugin:apply.patch"),
                            ["arguments"] = "{\"input\":\"*** Begin Patch\"}"
                        }
                    }
                }
            },
            ["finish_reason"] = "tool_calls"
        }
    }
}.ToJsonString();
using (var customNamespacedToolResponse = JsonDocument.Parse(
           KimiResponsesTranslator.TranslateNonStreaming(
               customNamespacedToolCompletion,
               "k3",
               null,
               new[] { "plugin:apply.patch" },
               toolNameMap: namespacedToolMap)))
{
    var customItem = customNamespacedToolResponse.RootElement.GetProperty("output")[0];
    Check(customItem.GetProperty("type").GetString() == "custom_tool_call" &&
        customItem.GetProperty("name").GetString() == "plugin:apply.patch" &&
        customItem.GetProperty("input").GetString() == "*** Begin Patch",
        "A custom namespaced tool call did not round-trip through the K3 function wrapper.");
}

var unknownMappedToolCompletion = """
{
  "id":"chat-unknown-tool",
  "choices":[{"message":{"role":"assistant","tool_calls":[
    {"id":"call_unknown","type":"function","function":{"name":"not_registered","arguments":"{}"}}
  ]},"finish_reason":"tool_calls"}]
}
""";
try
{
    _ = KimiResponsesTranslator.TranslateNonStreaming(
        unknownMappedToolCompletion,
        "k3",
        null,
        null,
        toolNameMap: namespacedToolMap);
    Check(false, "An unknown upstream tool name did not fail closed in non-streaming translation.");
}
catch (UnknownKimiMappedToolNameException)
{
}

var unknownMappedToolStream = KimiResponsesTranslator.TranslateStreaming(
    "data: {\"choices\":[{\"delta\":{\"tool_calls\":[{\"index\":0,\"function\":{\"name\":\"not_registered\",\"arguments\":\"{}\"}}]}}]}\n\ndata: [DONE]\n\n",
    "k3",
    null,
    null,
    toolNameMap: namespacedToolMap);
Check(unknownMappedToolStream.Contains("event: response.failed", StringComparison.Ordinal) &&
    unknownMappedToolStream.Contains("upstream_protocol_error", StringComparison.Ordinal) &&
    !unknownMappedToolStream.Contains("event: response.completed", StringComparison.Ordinal),
    "An unknown upstream tool name did not fail closed in streaming translation.");

var longName = "a" + new string('x', 80) + ".tool";
var reversedNamespacedToolsRequestJson = """
{
  "tools":[
    {"type":"custom","name":"plugin:apply.patch"},
    {"type":"function","name":"github:github","parameters":{"type":"object"}},
    {"type":"function","name":"collaboration.spawn_agent","parameters":{"type":"object"}},
    {"type":"function","name":"functions_exec","parameters":{"type":"object"}},
    {"type":"function","name":"functions.exec","parameters":{"type":"object"}}
  ]
}
""";
using (var reversedNamespacedToolsDocument = JsonDocument.Parse(reversedNamespacedToolsRequestJson))
{
    var reversedMap = KimiToolNameMap.Create(reversedNamespacedToolsDocument.RootElement);
    foreach (var originalName in new[]
             {
                 "functions.exec",
                 "functions_exec",
                 "collaboration.spawn_agent",
                 "github:github",
                 "plugin:apply.patch"
             })
    {
        Check(reversedMap.ToUpstream(originalName) == namespacedToolMap.ToUpstream(originalName),
            "Tool-name mapping changed when the tool declaration order changed.");
    }
}

var longNameRequest = new JsonObject
{
    ["tools"] = new JsonArray
    {
        new JsonObject
        {
            ["type"] = "function",
            ["name"] = longName,
            ["parameters"] = new JsonObject { ["type"] = "object" }
        }
    }
}.ToJsonString();
using (var longNameDocument = JsonDocument.Parse(longNameRequest))
{
    var longNameMap = KimiToolNameMap.Create(longNameDocument.RootElement);
    Check(longNameMap.ToUpstream(longName).Length <= 64,
        "An overlength tool name was not bounded for the K3 contract.");
}

var textCompletion = """
{
  "id":"chat-text-1",
  "model":"requested-kimi-model",
  "created":1730000000,
  "choices":[{"index":0,"message":{"role":"assistant","content":"hello from kimi"},"finish_reason":"stop"}],
  "usage":{"prompt_tokens":11,"completion_tokens":4,"total_tokens":15}
}
""";
using (var textResponse = JsonDocument.Parse(
           KimiResponsesTranslator.TranslateNonStreaming(textCompletion, "requested-kimi-model")))
{
    var root = textResponse.RootElement;
    Check(root.GetProperty("id").GetString() == "resp_chat-text-1", "The Responses id was not deterministic from the upstream id.");
    Check(root.GetProperty("status").GetString() == "completed", "Non-streaming response was not completed.");
    Check(root.GetProperty("output_text").GetString() == "hello from kimi", "Text output was not translated.");
    Check(root.GetProperty("usage").GetProperty("input_tokens_details").GetProperty("cached_tokens").GetInt32() == 0, "cached_tokens default was omitted.");
    Check(root.GetProperty("usage").GetProperty("output_tokens_details").GetProperty("reasoning_tokens").GetInt32() == 0, "reasoning_tokens default was omitted.");
}

var incompleteCompletion = """
{
  "id":"chat-length-1",
  "choices":[{"index":0,"message":{"role":"assistant","content":"partial"},"finish_reason":"length"}]
}
""";
using (var incompleteResponse = JsonDocument.Parse(
           KimiResponsesTranslator.TranslateNonStreaming(incompleteCompletion, "requested-kimi-model")))
{
    Check(incompleteResponse.RootElement.GetProperty("status").GetString() == "incomplete",
        "length finish_reason did not produce an incomplete Responses status.");
    Check(incompleteResponse.RootElement.GetProperty("incomplete_details").GetProperty("reason").GetString() == "max_output_tokens",
        "length finish_reason did not produce max_output_tokens incomplete details.");
}

var filteredCompletion = """
{"id":"chat-filtered-1","choices":[{"index":0,"message":{"role":"assistant","content":"blocked"},"finish_reason":"content_filter"}]}
""";
using (var filteredResponse = JsonDocument.Parse(
           KimiResponsesTranslator.TranslateNonStreaming(filteredCompletion, "requested-kimi-model")))
{
    Check(filteredResponse.RootElement.GetProperty("status").GetString() == "incomplete" &&
        filteredResponse.RootElement.GetProperty("incomplete_details").GetProperty("reason").GetString() == "content_filter",
        "content_filter finish_reason did not produce content_filter incomplete details.");
}

var failedCompletion = """
{"id":"chat-error-1","error":{"code":"upstream_error","message":"temporary"}}
""";
using (var failedResponse = JsonDocument.Parse(
           KimiResponsesTranslator.TranslateNonStreaming(failedCompletion, "requested-kimi-model")))
{
    Check(failedResponse.RootElement.GetProperty("status").GetString() == "failed" &&
        failedResponse.RootElement.GetProperty("error").GetProperty("code").GetString() == "upstream_error",
        "An upstream non-streaming error did not produce a failed Responses status.");
}

var toolCompletion = """
{
  "id":"chat-tool-1",
  "choices":[{"index":0,"message":{"role":"assistant","content":null,"tool_calls":[
    {"id":"call_apply","type":"function","function":{"name":"apply_patch","arguments":"{\"input\":\"*** Begin Patch\\n*** Update File: a.txt\\n*** End Patch\"}"}}
  ]},"finish_reason":"tool_calls"}]
}
""";
using (var toolResponse = JsonDocument.Parse(
           KimiResponsesTranslator.TranslateNonStreaming(
               toolCompletion,
               "requested-kimi-model",
               "resp_tool",
               new[] { "apply_patch" })))
{
    var item = toolResponse.RootElement.GetProperty("output")[0];
    Check(item.GetProperty("type").GetString() == "custom_tool_call", "Custom tool output was not restored.");
    Check(
        item.GetProperty("input").GetString() ==
            "*** Begin Patch\n*** Update File: a.txt\n*** End Patch",
        "Raw custom-tool input was not exactly restored.");
}

var streamFixture = """
data: {"id":"chat-stream-1","model":"requested-kimi-model","choices":[{"index":0,"delta":{"role":"assistant","content":"hello "}}]}

data: {"id":"chat-stream-1","model":"requested-kimi-model","choices":[{"index":0,"delta":{"content":"stream"}}]}

data: {"id":"chat-stream-1","model":"requested-kimi-model","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_apply","function":{"name":"apply_patch","arguments":"{\"input\":\"*** Begin "}}]}}]}

data: {"id":"chat-stream-1","model":"requested-kimi-model","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"arguments":"Patch*** End Patch\"}"}}]}}]}

data: {"id":"chat-stream-1","model":"requested-kimi-model","choices":[],"usage":{"prompt_tokens":3,"completion_tokens":2,"total_tokens":5}}

data: [DONE]

""";
var translatedStream = KimiResponsesTranslator.TranslateStreaming(
    streamFixture,
    "requested-kimi-model",
    "resp_stream",
    new[] { "apply_patch" });
Check(translatedStream.Contains("event: response.created\n", StringComparison.Ordinal), "response.created was omitted.");
Check(translatedStream.Contains("event: response.in_progress\n", StringComparison.Ordinal), "response.in_progress was omitted.");
Check(translatedStream.Contains("event: response.output_item.added\n", StringComparison.Ordinal), "response.output_item.added was omitted.");
Check(translatedStream.Contains("event: response.content_part.added\n", StringComparison.Ordinal), "response.content_part.added was omitted.");
Check(translatedStream.Contains("event: response.output_text.delta\n", StringComparison.Ordinal), "Text delta was omitted.");
Check(translatedStream.Contains("event: response.custom_tool_call_input.delta\n", StringComparison.Ordinal), "Custom tool input delta was omitted.");
Check(translatedStream.Contains("*** Begin Patch*** End Patch", StringComparison.Ordinal), "Streamed raw custom-tool input was not restored.");
Check(translatedStream.Contains("event: response.completed\n", StringComparison.Ordinal), "response.completed was omitted.");
Check(translatedStream.Contains("hello stream", StringComparison.Ordinal), "Stream text was not preserved.");
Check(translatedStream.Contains("reasoning_tokens", StringComparison.Ordinal), "Stream usage did not include reasoning_tokens.");

var sequenceNumbers = new List<long>();
foreach (var line in translatedStream.Split('\n'))
{
    if (!line.StartsWith("data: ", StringComparison.Ordinal))
    {
        continue;
    }

    using var eventDocument = JsonDocument.Parse(line[6..]);
    Check(eventDocument.RootElement.TryGetProperty("sequence_number", out var sequence) &&
        sequence.TryGetInt64(out var sequenceValue),
        "Every Responses SSE event must include sequence_number.");
    if (eventDocument.RootElement.TryGetProperty("sequence_number", out sequence) &&
        sequence.TryGetInt64(out sequenceValue))
    {
        sequenceNumbers.Add(sequenceValue);
    }
}
for (var index = 1; index < sequenceNumbers.Count; index++)
{
    Check(sequenceNumbers[index] == sequenceNumbers[index - 1] + 1,
        "Responses SSE sequence_number values were not strictly increasing.");
}

var incompleteStream = """
data: {"id":"chat-incomplete","choices":[{"index":0,"delta":{"content":"partial"},"finish_reason":"length"}]}

data: [DONE]

""";
var incompleteTranslatedStream = KimiResponsesTranslator.TranslateStreaming(
    incompleteStream, "requested-kimi-model", "resp_incomplete");
Check(incompleteTranslatedStream.Contains("event: response.incomplete\n", StringComparison.Ordinal),
    "A length-terminated stream did not produce response.incomplete.");
Check(!incompleteTranslatedStream.Contains("event: response.completed\n", StringComparison.Ordinal),
    "An incomplete stream incorrectly emitted response.completed.");

var truncatedStream = """
data: {"id":"chat-truncated","choices":[{"index":0,"delta":{"content":"partial"}}]}

""";
var truncatedTranslatedStream = KimiResponsesTranslator.TranslateStreaming(
    truncatedStream, "requested-kimi-model", "resp_truncated");
Check(truncatedTranslatedStream.Contains("event: response.failed\n", StringComparison.Ordinal) &&
    !truncatedTranslatedStream.Contains("event: response.completed\n", StringComparison.Ordinal),
    "A truncated stream did not fail closed without response.completed.");

var malformedStream = "data: {not-json}\n\ndata: [DONE]\n";
var malformedTranslatedStream = KimiResponsesTranslator.TranslateStreaming(
    malformedStream, "requested-kimi-model", "resp_malformed");
Check(malformedTranslatedStream.Contains("event: response.failed\n", StringComparison.Ordinal) &&
    !malformedTranslatedStream.Contains("event: response.completed\n", StringComparison.Ordinal),
    "A malformed SSE stream did not fail closed without response.completed.");

var errorStream = "data: {\"error\":{\"code\":\"upstream_error\",\"message\":\"bad gateway\"}}\n\ndata: [DONE]\n";
var errorTranslatedStream = KimiResponsesTranslator.TranslateStreaming(
    errorStream, "requested-kimi-model", "resp_error");
Check(errorTranslatedStream.Contains("event: response.failed\n", StringComparison.Ordinal) &&
    !errorTranslatedStream.Contains("event: response.completed\n", StringComparison.Ordinal),
    "An upstream error SSE frame did not fail closed without response.completed.");

var lateCustomNameFixture = """
data: {"id":"chat-late-name","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"id":"call_late","function":{"arguments":"{\"input\":\"raw "}}]}}]}

data: {"id":"chat-late-name","choices":[{"index":0,"delta":{"tool_calls":[{"index":0,"function":{"name":"apply_patch","arguments":"patch\"}"}}]}}]}

data: [DONE]

""";
var lateCustomNameStream = KimiResponsesTranslator.TranslateStreaming(
    lateCustomNameFixture,
    "requested-kimi-model",
    "resp_late_name",
    new[] { "apply_patch" });
Check(
    lateCustomNameStream.Contains("\"type\":\"custom_tool_call\"", StringComparison.Ordinal) &&
    !lateCustomNameStream.Contains("\"type\":\"function_call\"", StringComparison.Ordinal) &&
    lateCustomNameStream.Contains("raw patch", StringComparison.Ordinal),
    "A custom tool whose name arrived in a later delta was misclassified or lost.");
foreach (var line in translatedStream.Split('\n'))
{
    if (!line.StartsWith("data: ", StringComparison.Ordinal))
    {
        continue;
    }

    try
    {
        using var eventDocument = JsonDocument.Parse(line[6..]);
        Check(eventDocument.RootElement.ValueKind == JsonValueKind.Object, "A translated SSE data line was not an object.");
    }
    catch (JsonException)
    {
        Check(false, "A translated SSE data line was not valid JSON.");
    }
}

Check(
    KimiRouterOptions.DefaultUpstreamBaseUrl == "https://sui-xiang.com/v1" &&
    KimiRouterOptions.DefaultModelName == "k3",
    "The production Kimi router defaults were not pinned to the SuiXiang k3 route.");
Check(
    KimiRouterOptions.BuildChatCompletionsUri(new Uri("https://sui-xiang.com/v1")) ==
    new Uri("https://sui-xiang.com/v1/chat/completions"),
    "The default Kimi Chat Completions endpoint was built incorrectly.");
Check(
    new KimiRouterOptions(new Uri("https://sui-xiang.com/v1")).ListenUri ==
    new Uri("http://127.0.0.1:17866/"),
    "The router did not default to the stable IPv4 loopback port.");

var history = new KimiResponseHistoryCache();
history.Store(
    "resp_previous",
    new JsonArray
    {
        new JsonObject { ["role"] = "system", ["content"] = "old instructions" },
        new JsonObject { ["role"] = "user", ["content"] = "old user turn" },
        new JsonObject { ["role"] = "assistant", ["content"] = "old answer" }
    });
Check(history.TryGet("resp_previous", out var previous), "Previous response history was not cached.");
var continuation = new JsonObject
{
    ["messages"] = new JsonArray
    {
        new JsonObject { ["role"] = "system", ["content"] = "new instructions" },
        new JsonObject { ["role"] = "user", ["content"] = "new user turn" }
    }
};
KimiResponseHistoryCache.Prepend(continuation, previous);
var continuedMessages = (JsonArray)continuation["messages"]!;
Check(
    continuedMessages.Count == 4 &&
    continuedMessages[0]?["role"]?.GetValue<string>() == "system" &&
    continuedMessages[0]?["content"]?.GetValue<string>() == "new instructions" &&
    continuedMessages.Count(message =>
        message?["role"]?.GetValue<string>() == "system") == 1,
    "previous_response_id incorrectly carried old instructions or reordered the new instructions.");
history.Store(
    "resp_oversized",
    new JsonArray
    {
        new JsonObject
        {
            ["role"] = "user",
            ["content"] = new string('x', 2 * 1024 * 1024 + 1024)
        }
    });
Check(
    history.TryGet("resp_oversized", out var boundedHistory) &&
    boundedHistory.Count == 0,
    "The previous_response_id cache retained an oversized message.");

async Task VerifyRouterScenarioAsync(
    string scenarioName,
    Func<HttpResponseMessage> upstreamResponseFactory,
    bool stream,
    HttpStatusCode expectedStatus,
    Func<string, bool> bodyAssertion,
    string requestModel = "k3",
    bool expectUpstreamRequest = true)
{
    var portListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    portListener.Start();
    var port = ((System.Net.IPEndPoint)portListener.LocalEndpoint).Port;
    portListener.Stop();

    var upstream = new FakeUpstreamHandler(upstreamResponseFactory);
    var options = new KimiRouterOptions(
        new Uri("https://example.invalid/v1"),
        listenPort: port);
    await using var server = new KimiRouterServer(options, upstream);
    using var stop = new CancellationTokenSource();
    var serverTask = server.RunAsync(stop.Token);
    try
    {
        using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var healthReady = false;
        for (var attempt = 0; attempt < 30 && !healthReady; attempt++)
        {
            try
            {
                using var health = await client.GetAsync(
                    $"http://127.0.0.1:{port}/health");
                healthReady = health.StatusCode == HttpStatusCode.OK;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(25);
            }
        }

        Check(healthReady, $"{scenarioName}: loopback router did not become ready.");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"http://127.0.0.1:{port}/v1/responses");
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue(
            "Bearer",
            "server-test-secret");
        request.Content = new StringContent(
            $$"""
            {
              "model":"{{requestModel}}",
              "input":"hello",
              "stream":{{stream.ToString().ToLowerInvariant()}}
            }
            """,
            System.Text.Encoding.UTF8,
            "application/json");
        using var response = await client.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead);
        var body = await response.Content.ReadAsStringAsync();
        Check(
            response.StatusCode == expectedStatus,
            $"{scenarioName}: expected HTTP {(int)expectedStatus}, got {(int)response.StatusCode}.");
        Check(
            bodyAssertion(body),
            $"{scenarioName}: response body assertion failed.");
        var sequenceNumbers = new List<long>();
        foreach (var line in body.Split('\n'))
        {
            if (!line.StartsWith("data: ", StringComparison.Ordinal))
            {
                continue;
            }

            try
            {
                using var data = JsonDocument.Parse(line[6..]);
                if (data.RootElement.TryGetProperty("sequence_number", out var sequence) &&
                    sequence.TryGetInt64(out var value))
                {
                    sequenceNumbers.Add(value);
                }
            }
            catch (JsonException)
            {
                // The scenario-specific assertion reports malformed data.
            }
        }

        Check(
            sequenceNumbers.Zip(sequenceNumbers.Skip(1), (previous, next) => next > previous).All(value => value),
            $"{scenarioName}: response sequence_number values were not strictly increasing.");
        if (expectUpstreamRequest)
        {
            Check(
                upstream.LastAuthorization == "Bearer server-test-secret",
                $"{scenarioName}: Bearer credential was not forwarded exactly once.");
            using var upstreamRequestDocument = JsonDocument.Parse(
                upstream.LastRequestBody ?? "{}");
            Check(
                upstreamRequestDocument.RootElement.GetProperty("model").GetString() == "k3",
                $"{scenarioName}: the upstream request did not use the canonical k3 model.");
        }
        else
        {
            Check(
                upstream.LastAuthorization is null && upstream.LastRequestBody is null,
                $"{scenarioName}: an unsupported model reached the upstream.");
        }
    }
    catch (Exception exception)
    {
        Check(false, $"{scenarioName}: server test threw {exception.GetType().Name}: {exception.Message}");
    }
    finally
    {
        stop.Cancel();
        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            Check(false, $"{scenarioName}: router did not stop after cancellation.");
        }
    }
}

async Task VerifyStreamingFirstByteAsync()
{
    const string scenarioName = "stream first byte before upstream headers";
    var portListener = new System.Net.Sockets.TcpListener(IPAddress.Loopback, 0);
    portListener.Start();
    var port = ((System.Net.IPEndPoint)portListener.LocalEndpoint).Port;
    portListener.Stop();

    var upstream = new DelayedUpstreamHandler();
    var options = new KimiRouterOptions(
        new Uri("https://example.invalid/v1"),
        listenPort: port);
    await using var server = new KimiRouterServer(options, upstream);
    using var stop = new CancellationTokenSource();
    var serverTask = server.RunAsync(stop.Token);
    try
    {
        using var healthClient = new HttpClient { Timeout = TimeSpan.FromSeconds(5) };
        var healthReady = false;
        for (var attempt = 0; attempt < 30 && !healthReady; attempt++)
        {
            try
            {
                using var health = await healthClient.GetAsync(
                    $"http://127.0.0.1:{port}/health");
                healthReady = health.StatusCode == HttpStatusCode.OK;
            }
            catch (HttpRequestException)
            {
                await Task.Delay(25);
            }
        }

        Check(healthReady, $"{scenarioName}: loopback router did not become ready.");
        using var client = new System.Net.Sockets.TcpClient();
        await client.ConnectAsync(IPAddress.Loopback, port);
        await using var stream = client.GetStream();
        const string requestBody = "{\"model\":\"k3\",\"input\":\"hello\",\"stream\":true}";
        var request =
            $"POST /v1/responses HTTP/1.1\r\n" +
            $"Host: 127.0.0.1:{port}\r\n" +
            "Authorization: Bearer server-test-secret\r\n" +
            "Content-Type: application/json\r\n" +
            $"Content-Length: {System.Text.Encoding.UTF8.GetByteCount(requestBody)}\r\n" +
            "Connection: close\r\n\r\n" +
            requestBody;
        await stream.WriteAsync(System.Text.Encoding.UTF8.GetBytes(request));
        await upstream.RequestObserved.Task.WaitAsync(TimeSpan.FromSeconds(2));

        var initialBytes = await ReadUntilAsync(
            stream,
            "response.in_progress",
            TimeSpan.FromSeconds(2));
        Check(
            initialBytes.Contains("HTTP/1.1 200 OK", StringComparison.Ordinal),
            $"{scenarioName}: local SSE headers were not sent before upstream headers.");
        Check(
            initialBytes.Contains("response.created", StringComparison.Ordinal) &&
            initialBytes.Contains("response.in_progress", StringComparison.Ordinal),
            $"{scenarioName}: initial Responses events were not sent before upstream headers.");

        upstream.Release(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "data: {\"id\":\"chat-first-byte\",\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\ndata: [DONE]\n\n",
                System.Text.Encoding.UTF8,
                "text/event-stream")
        });
        var remainder = await ReadUntilAsync(
            stream,
            "response.completed",
            TimeSpan.FromSeconds(2));
        Check(
            remainder.Contains("response.completed", StringComparison.Ordinal),
            $"{scenarioName}: the delayed upstream response did not complete.");
    }
    catch (Exception exception)
    {
        Check(false, $"{scenarioName}: server test threw {exception.GetType().Name}: {exception.Message}");
    }
    finally
    {
        upstream.Release(new HttpResponseMessage(HttpStatusCode.BadGateway));
        stop.Cancel();
        try
        {
            await serverTask.WaitAsync(TimeSpan.FromSeconds(3));
        }
        catch (OperationCanceledException)
        {
        }
        catch (TimeoutException)
        {
            Check(false, $"{scenarioName}: router did not stop after cancellation.");
        }
    }
}

static async Task<string> ReadUntilAsync(
    System.Net.Sockets.NetworkStream stream,
    string expected,
    TimeSpan timeout)
{
    using var cancellation = new CancellationTokenSource(timeout);
    var buffer = new byte[1024];
    var received = new System.Text.StringBuilder();
    while (!received.ToString().Contains(expected, StringComparison.Ordinal))
    {
        var count = await stream.ReadAsync(buffer, cancellation.Token);
        if (count == 0)
        {
            break;
        }

        received.Append(System.Text.Encoding.UTF8.GetString(buffer, 0, count));
    }

    return received.ToString();
}

await VerifyStreamingFirstByteAsync();

await VerifyRouterScenarioAsync(
    "non-stream success",
    () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"id\":\"chat-success\",\"model\":\"k3\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hello\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}",
            System.Text.Encoding.UTF8,
            "application/json")
    },
    stream: false,
    HttpStatusCode.OK,
    body => body.Contains("\"status\":\"completed\"", StringComparison.Ordinal) &&
            body.Contains("hello", StringComparison.Ordinal));

await VerifyRouterScenarioAsync(
    "non-k3 model is rejected",
    () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "{\"id\":\"chat-converted\",\"model\":\"k3\",\"choices\":[{\"message\":{\"role\":\"assistant\",\"content\":\"hello\"}}],\"usage\":{\"prompt_tokens\":1,\"completion_tokens\":1,\"total_tokens\":2}}",
            System.Text.Encoding.UTF8,
            "application/json")
    },
    stream: false,
    HttpStatusCode.BadRequest,
    body => body.Contains("unsupported_model", StringComparison.Ordinal) &&
            body.Contains("supports only k3", StringComparison.Ordinal),
    requestModel: "gpt-5.6-sol",
    expectUpstreamRequest: false);

await VerifyRouterScenarioAsync(
    "stream success",
    () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "data: {\"id\":\"chat-stream\",\"choices\":[{\"delta\":{\"content\":\"hello\"}}]}\n\ndata: [DONE]\n\n",
            System.Text.Encoding.UTF8,
            "text/event-stream")
    },
    stream: true,
    HttpStatusCode.OK,
    body => body.Contains("event: response.completed", StringComparison.Ordinal) &&
            body.Contains("hello", StringComparison.Ordinal) &&
            !body.Contains("response.error", StringComparison.Ordinal));

await VerifyRouterScenarioAsync(
    "upstream error",
    () => new HttpResponseMessage(HttpStatusCode.TooManyRequests)
    {
        Content = new StringContent(
            "{\"error\":{\"message\":\"rate limited\"}}",
            System.Text.Encoding.UTF8,
            "application/json")
    },
    stream: false,
    HttpStatusCode.TooManyRequests,
    body => body.Contains("rate limited", StringComparison.Ordinal) &&
            !body.Contains("server-test-secret", StringComparison.Ordinal));

await VerifyRouterScenarioAsync(
    "stream upstream error",
    () => new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)
    {
        Content = new StringContent(
            "{\"error\":{\"code\":\"upstream_busy\",\"message\":\"temporary unavailable server-test-secret\"}}",
            System.Text.Encoding.UTF8,
            "application/json")
    },
    stream: true,
    HttpStatusCode.OK,
    body => body.Contains("event: response.failed", StringComparison.Ordinal) &&
            body.Contains("upstream_busy", StringComparison.Ordinal) &&
            body.Contains("temporary unavailable", StringComparison.Ordinal) &&
            !body.Contains("server-test-secret", StringComparison.Ordinal));

await VerifyRouterScenarioAsync(
    "malformed stream",
    () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "data: {not-json}\n\n",
            System.Text.Encoding.UTF8,
            "text/event-stream")
    },
    stream: true,
    HttpStatusCode.OK,
    body => body.Contains("event: response.failed", StringComparison.Ordinal) &&
            body.Contains("sequence_number", StringComparison.Ordinal) &&
            !body.Contains("event: response.completed", StringComparison.Ordinal));

await VerifyRouterScenarioAsync(
    "truncated stream",
    () => new HttpResponseMessage(HttpStatusCode.OK)
    {
        Content = new StringContent(
            "data: {\"id\":\"chat-truncated\",\"choices\":[{\"delta\":{\"content\":\"partial\"}}]}\n\n",
            System.Text.Encoding.UTF8,
            "text/event-stream")
    },
    stream: true,
    HttpStatusCode.OK,
    body => body.Contains("event: response.failed", StringComparison.Ordinal) &&
            body.Contains("upstream_truncated", StringComparison.Ordinal) &&
            !body.Contains("event: response.completed", StringComparison.Ordinal));

if (failures.Count > 0)
{
    foreach (var failure in failures)
    {
        Console.Error.WriteLine(failure);
    }

    return 1;
}

Console.WriteLine("All Kimi router protocol tests passed.");
return 0;

sealed class FakeUpstreamHandler : HttpMessageHandler
{
    private readonly Func<HttpResponseMessage> _factory;

    public FakeUpstreamHandler(Func<HttpResponseMessage> factory)
    {
        _factory = factory;
    }

    public string? LastAuthorization { get; private set; }

    public string? LastRequestBody { get; private set; }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        LastAuthorization = request.Headers.Authorization?.ToString();
        LastRequestBody = request.Content?.ReadAsStringAsync(cancellationToken).GetAwaiter().GetResult();
        return Task.FromResult(_factory());
    }
}

sealed class DelayedUpstreamHandler : HttpMessageHandler
{
    private readonly TaskCompletionSource<HttpResponseMessage> _response = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public TaskCompletionSource<bool> RequestObserved { get; } = new(
        TaskCreationOptions.RunContinuationsAsynchronously);

    public void Release(HttpResponseMessage response)
    {
        if (!_response.TrySetResult(response))
        {
            response.Dispose();
        }
    }

    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        RequestObserved.TrySetResult(true);
        return _response.Task.WaitAsync(cancellationToken);
    }
}
