using System.Text;
using System.Text.Json.Nodes;

namespace CodexProviderKimiRouter;

/// <summary>
/// Short-lived, bounded response history needed by Responses'
/// <c>previous_response_id</c> continuation. The cache stores only the
/// translated Chat Completions messages and never credentials or raw prompts
/// beyond the configured in-memory bound.
/// </summary>
internal sealed class KimiResponseHistoryCache
{
    private const int MaxEntries = 32;
    private const int MaxMessagesPerEntry = 128;
    private const int MaxBytesPerEntry = 2 * 1024 * 1024;
    private const int MaxTotalBytes = 16 * 1024 * 1024;
    private static readonly TimeSpan EntryLifetime = TimeSpan.FromMinutes(30);
    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public bool TryGet(string responseId, out JsonArray messages)
    {
        messages = new JsonArray();
        if (string.IsNullOrWhiteSpace(responseId))
        {
            return false;
        }

        lock (_gate)
        {
            if (!_entries.TryGetValue(responseId.Trim(), out var entry))
            {
                return false;
            }

            if (DateTimeOffset.UtcNow - entry.CreatedAt > EntryLifetime)
            {
                _entries.Remove(responseId.Trim());
                return false;
            }

            messages = (JsonArray)entry.Messages.DeepClone();
            return true;
        }
    }

    public void Store(string responseId, JsonArray messages)
    {
        if (string.IsNullOrWhiteSpace(responseId))
        {
            return;
        }

        var bounded = new JsonArray();
        foreach (var message in messages
                     .Where(message => !IsSystemMessage(message))
                     .TakeLast(MaxMessagesPerEntry))
        {
            bounded.Add(message?.DeepClone());
        }

        var encodedBytes = EncodedSize(bounded);
        while (bounded.Count > 0 && encodedBytes > MaxBytesPerEntry)
        {
            bounded.RemoveAt(0);
            encodedBytes = EncodedSize(bounded);
        }

        lock (_gate)
        {
            RemoveExpiredEntries();
            _entries[responseId.Trim()] = new Entry(
                DateTimeOffset.UtcNow,
                bounded,
                encodedBytes);
            while (_entries.Count > MaxEntries ||
                   _entries.Values.Sum(entry => entry.EncodedBytes) > MaxTotalBytes)
            {
                var oldest = _entries.OrderBy(pair => pair.Value.CreatedAt).First().Key;
                _entries.Remove(oldest);
            }
        }
    }

    public static void Prepend(JsonObject request, JsonArray previousMessages)
    {
        if (previousMessages.Count == 0 || request["messages"] is not JsonArray current)
        {
            return;
        }

        var combined = new JsonArray();
        foreach (var message in current.Where(IsSystemMessage))
        {
            combined.Add(message?.DeepClone());
        }

        foreach (var message in previousMessages)
        {
            if (!IsSystemMessage(message))
            {
                combined.Add(message?.DeepClone());
            }
        }

        foreach (var message in current.Where(message => !IsSystemMessage(message)))
        {
            combined.Add(message?.DeepClone());
        }

        request["messages"] = combined;
    }

    private void RemoveExpiredEntries()
    {
        var now = DateTimeOffset.UtcNow;
        foreach (var key in _entries
                     .Where(pair => now - pair.Value.CreatedAt > EntryLifetime)
                     .Select(pair => pair.Key)
                     .ToArray())
        {
            _entries.Remove(key);
        }
    }

    private static bool IsSystemMessage(JsonNode? message) =>
        message is JsonObject value &&
        string.Equals(
            value["role"]?.GetValue<string>(),
            "system",
            StringComparison.OrdinalIgnoreCase);

    private static int EncodedSize(JsonArray messages) =>
        Encoding.UTF8.GetByteCount(messages.ToJsonString());

    private sealed record Entry(
        DateTimeOffset CreatedAt,
        JsonArray Messages,
        int EncodedBytes);
}
