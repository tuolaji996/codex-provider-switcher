using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CodexProviderKimiRouter;

/// <summary>
/// Wire conversion between Codex Responses requests and Kimi's
/// OpenAI-compatible Chat Completions protocol. This class is deliberately
/// transport-independent so fixtures can exercise the protocol without a
/// network or an API key.
/// </summary>
public static class KimiResponsesTranslator
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false
    };

    public static JsonObject BuildChatCompletionsRequest(JsonElement responsesRequest)
    {
        var body = new JsonObject
        {
            ["model"] = ReadString(responsesRequest, "model") ?? KimiRouterOptions.DefaultModelName,
            ["messages"] = BuildMessages(responsesRequest)
        };

        var stream = ReadBoolean(responsesRequest, "stream");
        if (stream.HasValue)
        {
            body["stream"] = stream.Value;
            if (stream.Value)
            {
                var streamOptions = TryGetProperty(responsesRequest, "stream_options", out var suppliedOptions)
                    ? CloneNode(suppliedOptions)
                    : new JsonObject();
                if (streamOptions is JsonObject streamOptionsObject &&
                    !streamOptionsObject.ContainsKey("include_usage"))
                {
                    streamOptionsObject["include_usage"] = true;
                }

                body["stream_options"] = streamOptions;
            }
        }

        CopyProperty(responsesRequest, body, "temperature");
        CopyProperty(responsesRequest, body, "top_p");
        CopyProperty(responsesRequest, body, "frequency_penalty");
        CopyProperty(responsesRequest, body, "presence_penalty");
        CopyProperty(responsesRequest, body, "stop");
        CopyProperty(responsesRequest, body, "seed");
        CopyProperty(responsesRequest, body, "response_format");
        if (TryGetProperty(responsesRequest, "tool_choice", out var toolChoice))
        {
            body["tool_choice"] = BuildChatToolChoice(toolChoice);
        }
        CopyProperty(responsesRequest, body, "parallel_tool_calls");
        var reasoningEffort = ReadReasoningEffort(responsesRequest);
        if (reasoningEffort is not null)
        {
            body["reasoning_effort"] = reasoningEffort;
        }

        if (TryGetProperty(responsesRequest, "max_tokens", out _))
        {
            CopyProperty(responsesRequest, body, "max_tokens");
        }
        else if (TryGetProperty(responsesRequest, "max_output_tokens", out var maxOutputTokens))
        {
            body["max_tokens"] = CloneNode(maxOutputTokens);
        }

        if (TryGetProperty(responsesRequest, "tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
        {
            body["tools"] = BuildTools(tools);
        }

        return body;
    }

    public static string BuildChatCompletionsRequestJson(string responsesJson)
    {
        using var document = JsonDocument.Parse(responsesJson);
        return BuildChatCompletionsRequest(document.RootElement).ToJsonString(JsonOptions);
    }

    public static string TranslateNonStreaming(
        string chatCompletionsJson,
        string requestedModel,
        string? responseId = null,
        IEnumerable<string>? customToolNames = null)
    {
        using var document = JsonDocument.Parse(chatCompletionsJson);
        return BuildResponse(document.RootElement, requestedModel, responseId, customToolNames).ToJsonString(JsonOptions);
    }

    public static JsonObject BuildResponse(
        JsonElement chatCompletion,
        string requestedModel,
        string? responseId = null,
        IEnumerable<string>? customToolNames = null)
    {
        var customNames = customToolNames is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(customToolNames, StringComparer.Ordinal);
        var upstreamId = ReadString(chatCompletion, "id");
        var id = NormalizeResponseId(responseId ?? upstreamId);
        var model = string.IsNullOrWhiteSpace(requestedModel)
            ? ReadString(chatCompletion, "model") ?? KimiRouterOptions.DefaultModelName
            : requestedModel.Trim();
        var createdAt = ReadInt64(chatCompletion, "created") ?? DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var output = new JsonArray();
        var outputText = new StringBuilder();
        string? finishReason = null;

        if (TryGetFirstChoice(chatCompletion, out var choice) &&
            TryGetProperty(choice, "message", out var message))
        {
            finishReason = ReadString(choice, "finish_reason");
            var content = ExtractText(ReadProperty(message, "content"));
            var reasoningContent = ReadString(message, "reasoning_content");
            if (!string.IsNullOrEmpty(reasoningContent))
            {
                output.Add(BuildCompletedReasoningItem(id, reasoningContent));
            }

            if (!string.IsNullOrEmpty(content))
            {
                outputText.Append(content);
                output.Add(BuildCompletedMessageItem(id, content));
            }

            if (TryGetProperty(message, "tool_calls", out var toolCalls) &&
                toolCalls.ValueKind == JsonValueKind.Array)
            {
                foreach (var toolCall in toolCalls.EnumerateArray())
                {
                    output.Add(BuildCompletedFunctionItem(toolCall, id, output.Count, customNames));
                }
            }
        }

        var status = TryGetProperty(chatCompletion, "error", out var upstreamError)
            ? "failed"
            : GetResponseStatus(finishReason);
        var response = BuildResponseShell(
            id,
            model,
            createdAt,
            status,
            output);
        response["output_text"] = outputText.ToString();
        response["usage"] = BuildUsage(ReadProperty(chatCompletion, "usage"));
        if (status == "incomplete")
        {
            response["incomplete_details"] = new JsonObject
            {
                ["reason"] = GetIncompleteReason(finishReason)
            };
        }

        if (status == "failed" && TryGetProperty(chatCompletion, "error", out upstreamError))
        {
            response["error"] = CloneNode(upstreamError);
        }

        return response;
    }

    private static string GetResponseStatus(string? finishReason)
    {
        return finishReason?.Trim().ToLowerInvariant() switch
        {
            "length" or "max_tokens" or "content_filter" => "incomplete",
            "error" or "failed" => "failed",
            _ => "completed"
        };
    }

    internal static string GetResponseStatusForStream(string? finishReason) => GetResponseStatus(finishReason);

    private static string GetIncompleteReason(string? finishReason)
    {
        return finishReason?.Trim().ToLowerInvariant() switch
        {
            "content_filter" => "content_filter",
            _ => "max_output_tokens"
        };
    }

    internal static string GetIncompleteReasonForStream(string? finishReason) => GetIncompleteReason(finishReason);

    public static string TranslateStreaming(
        string chatCompletionsSse,
        string requestedModel,
        string? responseId = null,
        IEnumerable<string>? customToolNames = null)
    {
        var builder = new StringBuilder();
        var state = new KimiResponsesStreamState(
            requestedModel,
            responseId,
            customToolNames,
            item => builder.Append(item.ToSse()));
        state.EmitInitialEvents();
        try
        {
            foreach (var chunk in ParseChatCompletionsSse(chatCompletionsSse))
            {
                state.ProcessChunk(chunk);
            }
        }
        catch (JsonException)
        {
            state.Fail("malformed_sse", "The Kimi upstream returned malformed streaming JSON.");
            return builder.ToString();
        }

        if (ContainsDoneMarker(chatCompletionsSse))
        {
            state.Complete();
        }
        else
        {
            state.Fail("upstream_truncated", "The Kimi upstream stream ended before [DONE].");
        }

        return builder.ToString();
    }

    private static bool ContainsDoneMarker(string sse)
    {
        using var reader = new StringReader(sse);
        while (reader.ReadLine() is { } line)
        {
            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase) &&
                string.Equals(line[5..].Trim(), "[DONE]", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    internal static IEnumerable<JsonElement> ParseChatCompletionsSse(string sse)
    {
        using var reader = new StringReader(sse);
        var dataLines = new List<string>();
        while (reader.ReadLine() is { } line)
        {
            if (line.Length == 0)
            {
                if (dataLines.Count > 0)
                {
                    var data = string.Join("\n", dataLines);
                    dataLines.Clear();
                    if (data == "[DONE]")
                    {
                        yield break;
                    }

                    if (!TryParseJson(data, out var document))
                    {
                        throw new JsonException("The upstream SSE data frame was not valid JSON.");
                    }

                    using (document)
                    {
                        yield return document.RootElement.Clone();
                    }
                }

                continue;
            }

            if (line.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                var data = line[5..].TrimStart();
                if (data.Length > 0)
                {
                    dataLines.Add(data);
                }
            }
        }

        if (dataLines.Count > 0)
        {
            var data = string.Join("\n", dataLines);
            if (data != "[DONE]")
            {
                if (!TryParseJson(data, out var document))
                {
                    throw new JsonException("The upstream SSE data frame was not valid JSON.");
                }

                using (document)
                {
                    yield return document.RootElement.Clone();
                }
            }
        }
    }

    internal static string NormalizeResponseId(string? id)
    {
        if (!string.IsNullOrWhiteSpace(id))
        {
            var trimmed = id.Trim();
            return trimmed.StartsWith("resp_", StringComparison.Ordinal)
                ? trimmed
                : "resp_" + trimmed;
        }

        return "resp_" + Guid.NewGuid().ToString("N");
    }

    internal static HashSet<string> GetCustomToolNames(JsonElement responsesRequest)
    {
        var result = new HashSet<string>(StringComparer.Ordinal);
        if (!TryGetProperty(responsesRequest, "tools", out var tools) || tools.ValueKind != JsonValueKind.Array)
        {
            return result;
        }

        foreach (var tool in tools.EnumerateArray())
        {
            if (ReadString(tool, "type") is not { } type ||
                !string.Equals(type, "custom", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var name = ReadString(tool, "name");
            if (!string.IsNullOrWhiteSpace(name))
            {
                result.Add(name);
            }
        }

        return result;
    }

    /// <summary>
    /// Responses and Chat Completions use different object shapes for a
    /// forced function choice.  Normalize both the Responses function and
    /// custom-tool forms to Chat's <c>{type:function,function:{name}}</c>
    /// shape while preserving the simple auto/none/required values.
    /// </summary>
    internal static JsonNode? BuildChatToolChoice(JsonElement toolChoice)
    {
        if (toolChoice.ValueKind != JsonValueKind.Object)
        {
            return CloneNode(toolChoice);
        }

        var name = ReadString(toolChoice, "name");
        if (string.IsNullOrWhiteSpace(name) &&
            TryGetProperty(toolChoice, "function", out var function) &&
            function.ValueKind == JsonValueKind.Object)
        {
            name = ReadString(function, "name");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            // Keep unknown provider-specific fields rather than silently
            // changing a request we do not understand.
            return CloneNode(toolChoice);
        }

        return new JsonObject
        {
            ["type"] = "function",
            ["function"] = new JsonObject
            {
                ["name"] = name
            }
        };
    }

    internal static JsonObject? BuildAssistantMessageForHistory(JsonElement chatCompletion)
    {
        if (!TryGetFirstChoice(chatCompletion, out var choice) ||
            !TryGetProperty(choice, "message", out var message) ||
            message.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var result = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = CloneNode(ReadProperty(message, "content"))
        };
        if (TryGetProperty(message, "reasoning_content", out var reasoningContent))
        {
            result["reasoning_content"] = CloneNode(reasoningContent);
        }

        if (TryGetProperty(message, "tool_calls", out var toolCalls))
        {
            result["tool_calls"] = CloneNode(toolCalls);
        }

        return result;
    }

    internal static bool TryGetPropertyForStream(JsonElement value, string propertyName, out JsonElement property) =>
        TryGetProperty(value, propertyName, out property);

    internal static bool TryGetFirstChoiceForStream(JsonElement completion, out JsonElement choice) =>
        TryGetFirstChoice(completion, out choice);

    internal static JsonElement ReadPropertyForStream(JsonElement value, string propertyName) =>
        ReadProperty(value, propertyName);

    internal static string? ReadStringForStream(JsonElement value, string propertyName) =>
        ReadString(value, propertyName);

    internal static int? ReadInt32ForStream(JsonElement value, string propertyName)
    {
        var number = ReadInt64(value, propertyName);
        return number is >= int.MinValue and <= int.MaxValue ? (int)number.Value : null;
    }

    internal static JsonObject BuildResponseShell(
        string id,
        string model,
        long createdAt,
        string status,
        JsonArray output)
    {
        return new JsonObject
        {
            ["id"] = id,
            ["object"] = "response",
            ["created_at"] = createdAt,
            ["status"] = status,
            ["model"] = model,
            ["output"] = output
        };
    }

    internal static JsonNode? BuildUsage(JsonElement usage)
    {
        var result = new JsonObject
        {
            ["input_tokens"] = 0,
            ["output_tokens"] = 0,
            ["total_tokens"] = 0,
            ["input_tokens_details"] = new JsonObject
            {
                ["cached_tokens"] = 0
            },
            ["output_tokens_details"] = new JsonObject
            {
                ["reasoning_tokens"] = 0
            }
        };
        if (usage.ValueKind != JsonValueKind.Object)
        {
            return result;
        }

        CopyProperty(usage, result, "prompt_tokens", "input_tokens");
        CopyProperty(usage, result, "completion_tokens", "output_tokens");
        CopyProperty(usage, result, "total_tokens");
        if (TryGetProperty(usage, "prompt_tokens_details", out var promptDetails))
        {
            var details = CloneNode(promptDetails);
            if (details is JsonObject detailsObject)
            {
                if (!detailsObject.ContainsKey("cached_tokens"))
                {
                    detailsObject["cached_tokens"] = 0;
                }

                result["input_tokens_details"] = detailsObject;
            }
        }

        if (TryGetProperty(usage, "completion_tokens_details", out var completionDetails))
        {
            var details = CloneNode(completionDetails);
            if (details is JsonObject detailsObject)
            {
                if (!detailsObject.ContainsKey("reasoning_tokens"))
                {
                    detailsObject["reasoning_tokens"] = 0;
                }

                result["output_tokens_details"] = detailsObject;
            }
        }

        return result;
    }

    private static JsonArray BuildMessages(JsonElement request)
    {
        var messages = new JsonArray();
        var instructions = ReadString(request, "instructions");
        if (!string.IsNullOrWhiteSpace(instructions))
        {
            messages.Add(new JsonObject
            {
                ["role"] = "system",
                ["content"] = instructions
            });
        }

        if (!TryGetProperty(request, "input", out var input))
        {
            return messages;
        }

        if (input.ValueKind == JsonValueKind.String)
        {
            messages.Add(new JsonObject { ["role"] = "user", ["content"] = input.GetString() ?? string.Empty });
            return messages;
        }

        if (input.ValueKind == JsonValueKind.Object)
        {
            AddInputItem(messages, input);
            return messages;
        }

        if (input.ValueKind != JsonValueKind.Array)
        {
            return messages;
        }

        var pendingText = new StringBuilder();
        JsonObject? pendingAssistant = null;
        foreach (var item in input.EnumerateArray())
        {
            if (IsAssistantOutputItem(item))
            {
                FlushPendingUserText(messages, pendingText);
                AddAssistantOutputItem(ref pendingAssistant, item);
                continue;
            }

            if (IsTextInputItem(item))
            {
                FlushPendingAssistant(messages, ref pendingAssistant);
                var text = ExtractText(item);
                if (!string.IsNullOrEmpty(text))
                {
                    if (pendingText.Length > 0)
                    {
                        pendingText.Append('\n');
                    }

                    pendingText.Append(text);
                }

                continue;
            }

            FlushPendingUserText(messages, pendingText);
            FlushPendingAssistant(messages, ref pendingAssistant);
            AddInputItem(messages, item);
        }

        FlushPendingUserText(messages, pendingText);
        FlushPendingAssistant(messages, ref pendingAssistant);
        return messages;
    }

    private static void AddInputItem(JsonArray messages, JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return;
        }

        var type = ReadString(item, "type");
        if (string.Equals(type, "function_call_output", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "custom_tool_call_output", StringComparison.OrdinalIgnoreCase))
        {
            var output = ReadProperty(item, "output");
            messages.Add(new JsonObject
            {
                ["role"] = "tool",
                ["tool_call_id"] = ReadString(item, "call_id") ?? ReadString(item, "id") ?? string.Empty,
                ["content"] = ExtractText(output)
            });
            return;
        }

        if (IsAssistantOutputItem(item))
        {
            JsonObject? pendingAssistant = null;
            AddAssistantOutputItem(ref pendingAssistant, item);
            FlushPendingAssistant(messages, ref pendingAssistant);
            return;
        }

        var role = ReadString(item, "role") ?? "user";
        var message = new JsonObject
        {
            ["role"] = role,
            ["content"] = ExtractText(ReadProperty(item, "content"))
        };

        if (TryGetProperty(item, "reasoning_content", out var reasoningContent))
        {
            message["reasoning_content"] = CloneNode(reasoningContent);
        }

        if (TryGetProperty(item, "tool_calls", out var existingCalls) &&
            existingCalls.ValueKind == JsonValueKind.Array)
        {
            message["tool_calls"] = CloneNode(existingCalls);
        }

        if (string.Equals(role, "tool", StringComparison.OrdinalIgnoreCase))
        {
            message["tool_call_id"] = ReadString(item, "tool_call_id") ?? ReadString(item, "call_id") ?? string.Empty;
        }

        messages.Add(message);
    }

    private static bool IsAssistantOutputItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = ReadString(item, "type");
        return string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "custom_tool_call", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(ReadString(item, "role"), "assistant", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddAssistantOutputItem(ref JsonObject? pendingAssistant, JsonElement item)
    {
        pendingAssistant ??= new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = null
        };

        var type = ReadString(item, "type");
        if (string.Equals(type, "reasoning", StringComparison.OrdinalIgnoreCase))
        {
            var reasoning = ExtractReasoningText(item);
            if (!string.IsNullOrEmpty(reasoning))
            {
                var existing = ReadStringFromNode(pendingAssistant, "reasoning_content");
                pendingAssistant["reasoning_content"] = string.IsNullOrEmpty(existing)
                    ? reasoning
                    : existing + reasoning;
            }

            return;
        }

        if (string.Equals(type, "function_call", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "custom_tool_call", StringComparison.OrdinalIgnoreCase))
        {
            var isCustomCall = string.Equals(type, "custom_tool_call", StringComparison.OrdinalIgnoreCase);
            var callArguments = ReadArguments(item, isCustomCall ? "input" : "arguments");
            var call = new JsonObject
            {
                ["id"] = ReadString(item, "call_id") ?? ReadString(item, "id") ?? string.Empty,
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = ReadString(item, "name") ?? string.Empty,
                    ["arguments"] = isCustomCall ? WrapCustomInput(callArguments) : callArguments
                }
            };

            if (pendingAssistant["tool_calls"] is not JsonArray calls)
            {
                calls = new JsonArray();
                pendingAssistant["tool_calls"] = calls;
            }

            calls.Add(call);
            return;
        }

        var content = ExtractText(ReadProperty(item, "content"));
        if (!string.IsNullOrEmpty(content))
        {
            var existing = ReadStringFromNode(pendingAssistant, "content");
            pendingAssistant["content"] = string.IsNullOrEmpty(existing) ? content : existing + content;
        }

        if (TryGetProperty(item, "reasoning_content", out var reasoningContent))
        {
            pendingAssistant["reasoning_content"] = CloneNode(reasoningContent);
        }

        if (TryGetProperty(item, "tool_calls", out var existingCalls) &&
            existingCalls.ValueKind == JsonValueKind.Array)
        {
            if (pendingAssistant["tool_calls"] is not JsonArray calls)
            {
                calls = new JsonArray();
                pendingAssistant["tool_calls"] = calls;
            }

            foreach (var existingCall in existingCalls.EnumerateArray())
            {
                calls.Add(CloneNode(existingCall));
            }
        }
    }

    private static void FlushPendingAssistant(JsonArray messages, ref JsonObject? pendingAssistant)
    {
        if (pendingAssistant is null)
        {
            return;
        }

        messages.Add(pendingAssistant);
        pendingAssistant = null;
    }

    private static string ExtractReasoningText(JsonElement item)
    {
        var reasoning = ReadString(item, "reasoning_content");
        if (reasoning is not null)
        {
            return reasoning;
        }

        var summary = ReadProperty(item, "summary");
        if (summary.ValueKind != JsonValueKind.Undefined)
        {
            return ExtractText(summary);
        }

        return ExtractText(item);
    }

    private static string? ReadStringFromNode(JsonObject value, string propertyName)
    {
        return value[propertyName] is JsonValue node && node.TryGetValue<string>(out var text)
            ? text
            : null;
    }

    private static JsonArray BuildTools(JsonElement tools)
    {
        var result = new JsonArray();
        foreach (var tool in tools.EnumerateArray())
        {
            if (tool.ValueKind != JsonValueKind.Object)
            {
                continue;
            }

            var type = ReadString(tool, "type") ?? "function";
            var isCustom = string.Equals(type, "custom", StringComparison.OrdinalIgnoreCase);
            if (!isCustom && !string.Equals(type, "function", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            JsonElement function;
            if (TryGetProperty(tool, "function", out var nestedFunction) && nestedFunction.ValueKind == JsonValueKind.Object)
            {
                function = nestedFunction;
            }
            else
            {
                function = tool;
            }

            var functionParameters = isCustom
                ? BuildCustomInputParameters()
                : CloneNode(ReadProperty(function, "parameters"));
            var description = ReadString(function, "description") ?? string.Empty;
            if (isCustom)
            {
                description = string.IsNullOrWhiteSpace(description)
                    ? "Pass the complete raw custom-tool input in the input field."
                    : description + " Pass the complete raw custom-tool input in the input field.";
            }

            var mapped = new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = ReadString(function, "name") ?? string.Empty,
                    ["description"] = description,
                    ["parameters"] = functionParameters ?? new JsonObject()
                }
            };

            if (TryGetProperty(function, "strict", out var strict))
            {
                ((JsonObject)mapped["function"]!)["strict"] = CloneNode(strict);
            }

            result.Add(mapped);
        }

        return result;
    }

    private static JsonObject BuildCustomInputParameters()
    {
        return new JsonObject
        {
            ["type"] = "object",
            ["properties"] = new JsonObject
            {
                ["input"] = new JsonObject
                {
                    ["type"] = "string",
                    ["description"] = "Complete raw input for the custom tool."
                }
            },
            ["required"] = new JsonArray("input"),
            ["additionalProperties"] = false
        };
    }

    private static JsonObject BuildCompletedMessageItem(string responseId, string content)
    {
        return new JsonObject
        {
            ["id"] = responseId + "_msg",
            ["type"] = "message",
            ["status"] = "completed",
            ["role"] = "assistant",
            ["content"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "output_text",
                    ["text"] = content,
                    ["annotations"] = new JsonArray()
                }
            }
        };
    }

    private static JsonObject BuildCompletedReasoningItem(string responseId, string text)
    {
        return new JsonObject
        {
            ["id"] = responseId + "_reasoning",
            ["type"] = "reasoning",
            ["status"] = "completed",
            ["summary"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "summary_text",
                    ["text"] = text
                }
            }
        };
    }

    private static JsonObject BuildCompletedFunctionItem(
        JsonElement toolCall,
        string responseId,
        int index,
        ISet<string> customToolNames)
    {
        var function = ReadProperty(toolCall, "function");
        var id = ReadString(toolCall, "id") ?? responseId + "_call_" + index.ToString(CultureInfo.InvariantCulture);
        var arguments = ReadString(function, "arguments") ?? ReadArguments(toolCall, "arguments");
        var isCustom = customToolNames.Contains(ReadString(function, "name") ?? ReadString(toolCall, "name") ?? string.Empty);
        return new JsonObject
        {
            ["id"] = responseId + "_fc_" + index.ToString(CultureInfo.InvariantCulture),
            ["type"] = isCustom ? "custom_tool_call" : "function_call",
            ["status"] = "completed",
            ["call_id"] = id,
            ["name"] = ReadString(function, "name") ?? ReadString(toolCall, "name") ?? string.Empty,
            [isCustom ? "input" : "arguments"] = isCustom
                ? RestoreCustomInput(arguments)
                : arguments
        };
    }

    internal static string RestoreCustomInput(string arguments)
    {
        try
        {
            using var document = JsonDocument.Parse(arguments);
            var root = document.RootElement;
            if (root.ValueKind == JsonValueKind.Object &&
                root.TryGetProperty("input", out var input) &&
                input.ValueKind == JsonValueKind.String)
            {
                return input.GetString() ?? string.Empty;
            }
        }
        catch (JsonException)
        {
            // Preserve raw arguments from providers that ignore the wrapper.
        }

        return arguments;
    }

    private static string WrapCustomInput(string input) =>
        new JsonObject { ["input"] = input }.ToJsonString(JsonOptions);

    private static string ReadArguments(JsonElement value, string propertyName)
    {
        var property = ReadProperty(value, propertyName);
        if (property.ValueKind == JsonValueKind.String)
        {
            return property.GetString() ?? string.Empty;
        }

        return property.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? string.Empty
            : property.GetRawText();
    }

    private static string ExtractText(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String)
        {
            return value.GetString() ?? string.Empty;
        }

        if (value.ValueKind == JsonValueKind.Array)
        {
            var builder = new StringBuilder();
            foreach (var part in value.EnumerateArray())
            {
                var text = ExtractText(part);
                if (!string.IsNullOrEmpty(text))
                {
                    builder.Append(text);
                }
            }

            return builder.ToString();
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            var text = ReadString(value, "text");
            if (text is not null)
            {
                return text;
            }

            var content = ReadProperty(value, "content");
            return content.ValueKind == JsonValueKind.Undefined ? string.Empty : ExtractText(content);
        }

        return string.Empty;
    }

    private static bool IsTextInputItem(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object)
        {
            return false;
        }

        var type = ReadString(item, "type");
        return string.Equals(type, "input_text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "text", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(type, "output_text", StringComparison.OrdinalIgnoreCase);
    }

    private static void FlushPendingUserText(JsonArray messages, StringBuilder pendingText)
    {
        if (pendingText.Length == 0)
        {
            return;
        }

        messages.Add(new JsonObject
        {
            ["role"] = "user",
            ["content"] = pendingText.ToString()
        });
        pendingText.Clear();
    }

    private static string? ReadReasoningEffort(JsonElement request)
    {
        var direct = ReadString(request, "reasoning_effort");
        if (direct is null && TryGetProperty(request, "reasoning", out var reasoning))
        {
            direct = ReadString(reasoning, "effort");
        }

        if (string.IsNullOrWhiteSpace(direct))
        {
            return null;
        }

        return direct.Trim().ToLowerInvariant() switch
        {
            "minimal" or "low" => "low",
            "medium" or "high" => "high",
            "xhigh" or "max" or "ultra" => "max",
            _ => null
        };
    }

    private static bool TryGetFirstChoice(JsonElement completion, out JsonElement choice)
    {
        choice = default;
        if (!TryGetProperty(completion, "choices", out var choices) ||
            choices.ValueKind != JsonValueKind.Array ||
            choices.GetArrayLength() == 0)
        {
            return false;
        }

        choice = choices[0];
        return true;
    }

    private static JsonElement ReadProperty(JsonElement value, string propertyName)
    {
        return TryGetProperty(value, propertyName, out var property) ? property : default;
    }

    private static string? ReadString(JsonElement value, string propertyName)
    {
        var property = ReadProperty(value, propertyName);
        return property.ValueKind == JsonValueKind.String ? property.GetString() : null;
    }

    private static bool? ReadBoolean(JsonElement value, string propertyName)
    {
        var property = ReadProperty(value, propertyName);
        return property.ValueKind is JsonValueKind.True or JsonValueKind.False
            ? property.GetBoolean()
            : null;
    }

    private static long? ReadInt64(JsonElement value, string propertyName)
    {
        var property = ReadProperty(value, propertyName);
        return property.ValueKind == JsonValueKind.Number && property.TryGetInt64(out var result)
            ? result
            : null;
    }

    private static bool TryGetProperty(JsonElement value, string propertyName, out JsonElement property)
    {
        if (value.ValueKind == JsonValueKind.Object && value.TryGetProperty(propertyName, out property))
        {
            return true;
        }

        if (value.ValueKind == JsonValueKind.Object)
        {
            foreach (var candidate in value.EnumerateObject())
            {
                if (string.Equals(candidate.Name, propertyName, StringComparison.OrdinalIgnoreCase))
                {
                    property = candidate.Value;
                    return true;
                }
            }
        }

        property = default;
        return false;
    }

    private static JsonNode? CloneNode(JsonElement value)
    {
        return value.ValueKind is JsonValueKind.Undefined or JsonValueKind.Null
            ? null
            : JsonNode.Parse(value.GetRawText());
    }

    private static void CopyProperty(
        JsonElement source,
        JsonObject target,
        string sourceName,
        string? targetName = null)
    {
        if (TryGetProperty(source, sourceName, out var value))
        {
            target[targetName ?? sourceName] = CloneNode(value);
        }
    }

    private static bool TryParseJson(string value, out JsonDocument document)
    {
        try
        {
            document = JsonDocument.Parse(value);
            return true;
        }
        catch (JsonException)
        {
            document = null!;
            return false;
        }
    }
}

public sealed record KimiResponsesSseEvent(string EventType, JsonObject Data)
{
    public string ToSse()
    {
        return $"event: {EventType}\ndata: {Data.ToJsonString(new JsonSerializerOptions(JsonSerializerDefaults.Web))}\n\n";
    }
}

internal sealed class KimiResponsesStreamState
{
    private readonly Action<KimiResponsesSseEvent> _emit;
    private readonly JsonArray _output = new();
    private readonly List<StreamOutputItem> _items = new();
    private readonly Dictionary<int, StreamFunctionItem> _functions = new();
    private readonly ISet<string> _customToolNames;
    private readonly string _id;
    private readonly string _model;
    private readonly long _createdAt;
    private StreamMessageItem? _message;
    private StreamReasoningItem? _reasoning;
    private JsonElement _usage;
    private string? _finishReason;
    private long _sequenceNumber;
    private bool _completed;

    public KimiResponsesStreamState(
        string requestedModel,
        string? responseId,
        IEnumerable<string>? customToolNames,
        Action<KimiResponsesSseEvent> emit)
    {
        _emit = emit;
        _customToolNames = customToolNames is null
            ? new HashSet<string>(StringComparer.Ordinal)
            : new HashSet<string>(customToolNames, StringComparer.Ordinal);
        _id = KimiResponsesTranslator.NormalizeResponseId(responseId);
        _model = string.IsNullOrWhiteSpace(requestedModel) ? KimiRouterOptions.DefaultModelName : requestedModel.Trim();
        _createdAt = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
    }

    public string ResponseId => _id;

    internal JsonObject BuildAssistantMessageForHistory()
    {
        var message = new JsonObject
        {
            ["role"] = "assistant",
            ["content"] = _message is null || _message.Text.Length == 0 ? null : _message.Text.ToString()
        };
        if (_reasoning is not null && _reasoning.Text.Length > 0)
        {
            message["reasoning_content"] = _reasoning.Text.ToString();
        }

        if (_functions.Count > 0)
        {
            var calls = new JsonArray();
            foreach (var function in _functions.Values.OrderBy(value => value.OutputIndex))
            {
                calls.Add(new JsonObject
                {
                    ["id"] = function.Item["call_id"]?.DeepClone(),
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = function.Item["name"]?.DeepClone(),
                        ["arguments"] = function.Arguments.ToString()
                    }
                });
            }

            message["tool_calls"] = calls;
        }

        return message;
    }

    public void EmitInitialEvents()
    {
        EmitResponseEvent("response.created", "in_progress");
        EmitResponseEvent("response.in_progress", "in_progress");
    }

    public void ProcessChunk(JsonElement chunk)
    {
        if (_completed)
        {
            return;
        }

        if (KimiResponsesTranslator.TryGetPropertyForStream(chunk, "usage", out var usage))
        {
            _usage = usage.Clone();
        }

        if (KimiResponsesTranslator.TryGetPropertyForStream(chunk, "error", out var error))
        {
            var errorData = EventData("response.error");
            errorData["error"] = JsonNode.Parse(error.GetRawText());
            _emit(new KimiResponsesSseEvent("response.error", errorData));
            Fail(
                KimiResponsesTranslator.ReadStringForStream(error, "code") ?? "upstream_error",
                KimiResponsesTranslator.ReadStringForStream(error, "message") ?? "The Kimi upstream returned an error.",
                JsonNode.Parse(error.GetRawText()));
            return;
        }

        if (!KimiResponsesTranslator.TryGetFirstChoiceForStream(chunk, out var choice))
        {
            return;
        }

        if (KimiResponsesTranslator.TryGetPropertyForStream(choice, "finish_reason", out var finishReason) &&
            finishReason.ValueKind == JsonValueKind.String)
        {
            _finishReason = finishReason.GetString();
        }

        if (!KimiResponsesTranslator.TryGetPropertyForStream(choice, "delta", out var delta))
        {
            return;
        }

        var content = KimiResponsesTranslator.ReadStringForStream(delta, "content");
        var reasoningContent = KimiResponsesTranslator.ReadStringForStream(delta, "reasoning_content");
        if (!string.IsNullOrEmpty(reasoningContent))
        {
            EnsureReasoning();
            _reasoning!.Text.Append(reasoningContent);
            var reasoningDelta = EventData("response.reasoning_summary_text.delta");
            reasoningDelta["item_id"] = _reasoning.Id;
            reasoningDelta["output_index"] = _reasoning.OutputIndex;
            reasoningDelta["summary_index"] = 0;
            reasoningDelta["delta"] = reasoningContent;
            _emit(new KimiResponsesSseEvent("response.reasoning_summary_text.delta", reasoningDelta));
        }

        if (!string.IsNullOrEmpty(content))
        {
            EnsureMessage();
            _message!.Text.Append(content);
            var data = EventData("response.output_text.delta");
            data["item_id"] = _message.Id;
            data["output_index"] = _message.OutputIndex;
            data["content_index"] = 0;
            data["delta"] = content;
            _emit(new KimiResponsesSseEvent("response.output_text.delta", data));
        }

        if (KimiResponsesTranslator.TryGetPropertyForStream(delta, "tool_calls", out var toolCalls) &&
            toolCalls.ValueKind == JsonValueKind.Array)
        {
            var arrayIndex = 0;
            foreach (var toolCall in toolCalls.EnumerateArray())
            {
                ProcessToolCall(toolCall, arrayIndex++);
            }
        }
    }

    public void Complete(string? finishReason = null)
    {
        if (_completed)
        {
            return;
        }

        if (!string.IsNullOrWhiteSpace(finishReason))
        {
            _finishReason = finishReason;
        }

        var responseStatus = KimiResponsesTranslator.GetResponseStatusForStream(_finishReason);
        if (responseStatus == "failed")
        {
            Fail("upstream_error", "The Kimi upstream reported an error.");
            return;
        }

        _completed = true;
        var itemStatus = responseStatus == "completed" ? "completed" : "incomplete";
        if (_reasoning is not null)
        {
            var reasoningDone = EventData("response.reasoning_summary_text.done");
            reasoningDone["item_id"] = _reasoning.Id;
            reasoningDone["output_index"] = _reasoning.OutputIndex;
            reasoningDone["summary_index"] = 0;
            reasoningDone["text"] = _reasoning.Text.ToString();
            _emit(new KimiResponsesSseEvent("response.reasoning_summary_text.done", reasoningDone));

            _reasoning.Item["status"] = itemStatus;
            _reasoning.Item["summary"] = new JsonArray
            {
                new JsonObject
                {
                    ["type"] = "summary_text",
                    ["text"] = _reasoning.Text.ToString()
                }
            };
            var reasoningItemDone = EventData("response.output_item.done");
            reasoningItemDone["output_index"] = _reasoning.OutputIndex;
            reasoningItemDone["item"] = _reasoning.Item.DeepClone();
            _emit(new KimiResponsesSseEvent("response.output_item.done", reasoningItemDone));
        }

        if (_message is not null)
        {
            var done = EventData("response.output_text.done");
            done["item_id"] = _message.Id;
            done["output_index"] = _message.OutputIndex;
            done["content_index"] = 0;
            done["text"] = _message.Text.ToString();
            _emit(new KimiResponsesSseEvent("response.output_text.done", done));

            var partDone = EventData("response.content_part.done");
            partDone["item_id"] = _message.Id;
            partDone["output_index"] = _message.OutputIndex;
            partDone["content_index"] = 0;
            partDone["part"] = BuildOutputTextPart(_message.Text.ToString());
            _emit(new KimiResponsesSseEvent("response.content_part.done", partDone));

            _message.Item["status"] = itemStatus;
            _message.Item["content"] = new JsonArray
            {
                BuildOutputTextPart(_message.Text.ToString())
            };
            var itemDone = EventData("response.output_item.done");
            itemDone["output_index"] = _message.OutputIndex;
            itemDone["item"] = _message.Item.DeepClone();
            _emit(new KimiResponsesSseEvent("response.output_item.done", itemDone));
        }

        foreach (var function in _functions.Values.OrderBy(value => value.OutputIndex))
        {
            EnsureFunctionAnnounced(function);
            var finalArguments = function.IsCustom
                ? KimiResponsesTranslator.RestoreCustomInput(function.Arguments.ToString())
                : function.Arguments.ToString();
            if (function.IsCustom && finalArguments.Length > 0)
            {
                var delta = EventData("response.custom_tool_call_input.delta");
                delta["item_id"] = function.ItemId;
                delta["output_index"] = function.OutputIndex;
                delta["delta"] = finalArguments;
                _emit(new KimiResponsesSseEvent(
                    "response.custom_tool_call_input.delta",
                    delta));
            }
            else if (!function.IsCustom)
            {
                EmitPendingFunctionArguments(function);
            }

            var argumentsDoneType = function.IsCustom
                ? "response.custom_tool_call_input.done"
                : "response.function_call_arguments.done";
            var argumentsDone = EventData(argumentsDoneType);
            argumentsDone["item_id"] = function.ItemId;
            argumentsDone["output_index"] = function.OutputIndex;
            argumentsDone[function.IsCustom ? "input" : "arguments"] = finalArguments;
            _emit(new KimiResponsesSseEvent(argumentsDoneType, argumentsDone));

            function.Item["status"] = itemStatus;
            function.Item[function.IsCustom ? "input" : "arguments"] = finalArguments;
            var itemDone = EventData("response.output_item.done");
            itemDone["output_index"] = function.OutputIndex;
            itemDone["item"] = function.Item.DeepClone();
            _emit(new KimiResponsesSseEvent("response.output_item.done", itemDone));
        }

        var terminalType = responseStatus == "completed" ? "response.completed" : "response.incomplete";
        var completed = EventData(terminalType);
        completed["response"] = KimiResponsesTranslator.BuildResponseShell(
            _id,
            _model,
            _createdAt,
            responseStatus,
            BuildFinalOutput());
        var terminalResponse = (JsonObject)completed["response"]!;
        terminalResponse["output_text"] = _message?.Text.ToString() ?? string.Empty;
        terminalResponse["usage"] = KimiResponsesTranslator.BuildUsage(_usage);
        if (responseStatus == "incomplete")
        {
            terminalResponse["incomplete_details"] = new JsonObject
            {
                ["reason"] = KimiResponsesTranslator.GetIncompleteReasonForStream(_finishReason)
            };
        }

        _emit(new KimiResponsesSseEvent(terminalType, completed));
    }

    /// <summary>
    /// Terminates a stream when the upstream connection, SSE framing, or
    /// payload is invalid. A failed response is terminal and is never followed
    /// by response.completed.
    /// </summary>
    public void Fail(string code, string message, JsonNode? details = null)
    {
        if (_completed)
        {
            return;
        }

        _completed = true;
        var failed = EventData("response.failed");
        var response = KimiResponsesTranslator.BuildResponseShell(
            _id,
            _model,
            _createdAt,
            "failed",
            BuildFinalOutput());
        response["output_text"] = _message?.Text.ToString() ?? string.Empty;
        response["usage"] = KimiResponsesTranslator.BuildUsage(_usage);
        response["error"] = new JsonObject
        {
            ["code"] = string.IsNullOrWhiteSpace(code) ? "upstream_error" : code,
            ["message"] = string.IsNullOrWhiteSpace(message) ? "The Kimi upstream failed." : message
        };
        if (details is not null)
        {
            ((JsonObject)response["error"]!)["details"] = details.DeepClone();
        }

        failed["response"] = response;
        _emit(new KimiResponsesSseEvent("response.failed", failed));
    }

    /// <summary>
    /// Marks a stream incomplete without requiring a Chat finish_reason. This
    /// is useful when a bounded proxy detects truncation after partial output.
    /// </summary>
    public void MarkIncomplete(string reason = "max_output_tokens")
    {
        Complete(string.Equals(reason, "content_filter", StringComparison.OrdinalIgnoreCase)
            ? "content_filter"
            : "length");
    }

    private void EnsureMessage()
    {
        if (_message is not null)
        {
            return;
        }

        var outputIndex = NextOutputIndex();
        var id = _id + "_msg";
        var item = new JsonObject
        {
            ["id"] = id,
            ["type"] = "message",
            ["status"] = "in_progress",
            ["role"] = "assistant",
            ["content"] = new JsonArray()
        };
        _message = new StreamMessageItem(id, outputIndex, item);
        _items.Add(new StreamOutputItem(outputIndex, item));
        var added = EventData("response.output_item.added");
        added["output_index"] = outputIndex;
        added["item"] = item.DeepClone();
        _emit(new KimiResponsesSseEvent("response.output_item.added", added));

        var partAdded = EventData("response.content_part.added");
        partAdded["item_id"] = id;
        partAdded["output_index"] = outputIndex;
        partAdded["content_index"] = 0;
        partAdded["part"] = BuildOutputTextPart(string.Empty);
        _emit(new KimiResponsesSseEvent("response.content_part.added", partAdded));
    }

    private void EnsureReasoning()
    {
        if (_reasoning is not null)
        {
            return;
        }

        var outputIndex = NextOutputIndex();
        var id = _id + "_reasoning";
        var item = new JsonObject
        {
            ["id"] = id,
            ["type"] = "reasoning",
            ["status"] = "in_progress",
            ["summary"] = new JsonArray()
        };
        _reasoning = new StreamReasoningItem(id, outputIndex, item);
        _items.Add(new StreamOutputItem(outputIndex, item));
        var added = EventData("response.output_item.added");
        added["output_index"] = outputIndex;
        added["item"] = item.DeepClone();
        _emit(new KimiResponsesSseEvent("response.output_item.added", added));
        var summaryAdded = EventData("response.reasoning_summary_part.added");
        summaryAdded["item_id"] = id;
        summaryAdded["output_index"] = outputIndex;
        summaryAdded["summary_index"] = 0;
        summaryAdded["part"] = new JsonObject
        {
            ["type"] = "summary_text",
            ["text"] = string.Empty
        };
        _emit(new KimiResponsesSseEvent("response.reasoning_summary_part.added", summaryAdded));
    }

    private void ProcessToolCall(JsonElement toolCall, int arrayIndex)
    {
        var upstreamIndex = KimiResponsesTranslator.ReadInt32ForStream(toolCall, "index") ?? arrayIndex;
        if (!_functions.TryGetValue(upstreamIndex, out var function))
        {
            var outputIndex = NextOutputIndex();
            var functionObject = KimiResponsesTranslator.ReadPropertyForStream(toolCall, "function");
            var functionName = KimiResponsesTranslator.ReadStringForStream(functionObject, "name") ?? string.Empty;
            var callId = KimiResponsesTranslator.ReadStringForStream(toolCall, "id") ??
                _id + "_call_" + upstreamIndex.ToString(CultureInfo.InvariantCulture);
            var itemId = _id + "_fc_" + upstreamIndex.ToString(CultureInfo.InvariantCulture);
            var item = new JsonObject
            {
                ["id"] = itemId,
                ["type"] = "function_call",
                ["status"] = "in_progress",
                ["call_id"] = callId,
                ["name"] = functionName,
                ["arguments"] = string.Empty
            };
            var isCustom = _customToolNames.Contains(functionName);
            if (isCustom)
            {
                item["type"] = "custom_tool_call";
                item.Remove("arguments");
                item["input"] = string.Empty;
            }

            function = new StreamFunctionItem(upstreamIndex, outputIndex, itemId, item, new StringBuilder(), isCustom);
            _functions[upstreamIndex] = function;
            _items.Add(new StreamOutputItem(outputIndex, item));
            if (!string.IsNullOrWhiteSpace(functionName))
            {
                EnsureFunctionAnnounced(function);
            }
        }

        var functionDelta = KimiResponsesTranslator.ReadPropertyForStream(toolCall, "function");
        var name = KimiResponsesTranslator.ReadStringForStream(functionDelta, "name");
        if (!string.IsNullOrWhiteSpace(name) && string.IsNullOrWhiteSpace(function.Item["name"]?.GetValue<string>()))
        {
            function.Item["name"] = name;
            function.IsCustom = _customToolNames.Contains(name);
            if (function.IsCustom)
            {
                function.Item["type"] = "custom_tool_call";
                function.Item.Remove("arguments");
                function.Item["input"] = string.Empty;
            }

            EnsureFunctionAnnounced(function);
        }

        var arguments = KimiResponsesTranslator.ReadStringForStream(functionDelta, "arguments");
        if (string.IsNullOrEmpty(arguments))
        {
            return;
        }

        function.Arguments.Append(arguments);
        if (function.IsCustom || !function.Added)
        {
            // JSON argument fragments cannot be safely unwrapped into the raw
            // custom-tool stream until the complete object has arrived.
            return;
        }

        EmitPendingFunctionArguments(function);
    }

    private void EnsureFunctionAnnounced(StreamFunctionItem function)
    {
        if (function.Added)
        {
            return;
        }

        function.Added = true;
        var added = EventData("response.output_item.added");
        added["output_index"] = function.OutputIndex;
        added["item"] = function.Item.DeepClone();
        _emit(new KimiResponsesSseEvent("response.output_item.added", added));
    }

    private void EmitPendingFunctionArguments(StreamFunctionItem function)
    {
        if (function.IsCustom || !function.Added ||
            function.EmittedArgumentLength >= function.Arguments.Length)
        {
            return;
        }

        var deltaText = function.Arguments.ToString(
            function.EmittedArgumentLength,
            function.Arguments.Length - function.EmittedArgumentLength);
        function.EmittedArgumentLength = function.Arguments.Length;
        var data = EventData("response.function_call_arguments.delta");
        data["item_id"] = function.ItemId;
        data["output_index"] = function.OutputIndex;
        data["delta"] = deltaText;
        _emit(new KimiResponsesSseEvent(
            "response.function_call_arguments.delta",
            data));
    }

    private int NextOutputIndex()
    {
        return _items.Count == 0 ? 0 : _items.Max(item => item.OutputIndex) + 1;
    }

    private JsonArray BuildFinalOutput()
    {
        var output = new JsonArray();
        foreach (var item in _items.OrderBy(item => item.OutputIndex))
        {
            output.Add(item.Item.DeepClone());
        }

        return output;
    }

    private void EmitResponseEvent(string eventType, string status)
    {
        var data = EventData(eventType);
        data["response"] = KimiResponsesTranslator.BuildResponseShell(
            _id,
            _model,
            _createdAt,
            status,
            new JsonArray());
        _emit(new KimiResponsesSseEvent(eventType, data));
    }

    private JsonObject EventData(string type)
    {
        return new JsonObject
        {
            ["type"] = type,
            ["response_id"] = _id,
            ["sequence_number"] = _sequenceNumber++
        };
    }

    private static JsonObject BuildOutputTextPart(string text)
    {
        return new JsonObject
        {
            ["type"] = "output_text",
            ["text"] = text,
            ["annotations"] = new JsonArray()
        };
    }

    private sealed record StreamOutputItem(int OutputIndex, JsonObject Item);

    private sealed class StreamMessageItem
    {
        public StreamMessageItem(string id, int outputIndex, JsonObject item)
        {
            Id = id;
            OutputIndex = outputIndex;
            Item = item;
        }

        public string Id { get; }

        public int OutputIndex { get; }

        public JsonObject Item { get; }

        public StringBuilder Text { get; } = new();
    }

    private sealed class StreamReasoningItem
    {
        public StreamReasoningItem(string id, int outputIndex, JsonObject item)
        {
            Id = id;
            OutputIndex = outputIndex;
            Item = item;
        }

        public string Id { get; }

        public int OutputIndex { get; }

        public JsonObject Item { get; }

        public StringBuilder Text { get; } = new();
    }

    private sealed class StreamFunctionItem
    {
        public StreamFunctionItem(
            int upstreamIndex,
            int outputIndex,
            string itemId,
            JsonObject item,
            StringBuilder arguments,
            bool isCustom)
        {
            UpstreamIndex = upstreamIndex;
            OutputIndex = outputIndex;
            ItemId = itemId;
            Item = item;
            Arguments = arguments;
            IsCustom = isCustom;
        }

        public int UpstreamIndex { get; }

        public int OutputIndex { get; }

        public string ItemId { get; }

        public JsonObject Item { get; }

        public StringBuilder Arguments { get; }

        public bool IsCustom { get; set; }

        public bool Added { get; set; }

        public int EmittedArgumentLength { get; set; }
    }
}
