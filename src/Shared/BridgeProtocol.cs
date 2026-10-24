using System.Collections.Concurrent;
using System.Text.Json.Nodes;

namespace BimAi.Protocol;

public static class BridgeProtocol
{
    public const string Version = "3.0";
    public static readonly string[] DisabledTools = ["import_satellite_map", "load_family_from_library", "search_family_library", "web_search"];
    public static JsonObject Error(string? id, string code, string message) => new()
    {
        ["type"] = "result", ["protocolVersion"] = Version, ["requestId"] = id,
        ["success"] = false, ["errorCode"] = code, ["errorMessage"] = message,
        ["state"] = code == "cancelled" ? "cancelled" : "failed"
    };

    public static void RequireCommitted(string status)
    {
        if (status != "Committed") throw new InvalidOperationException($"transaction_not_committed: {status}");
    }

    public static void ValidateTarget(string? expected, string? actual)
    {
        if (expected is not null && !string.Equals(expected, actual, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("document_mismatch: active document changed before execution.");
    }

    public static void Validate(JsonNode? value, JsonNode? schema, string path = "arguments")
    {
        if (schema is not JsonObject s) return;
        if (s["allOf"] is JsonArray all) foreach (var part in all) Validate(value, part, path);
        foreach (var keyword in new[] { "anyOf", "oneOf" })
        {
            if (s[keyword] is not JsonArray alternatives) continue;
            var matches = 0;
            foreach (var alternative in alternatives)
            {
                try { Validate(value, alternative, path); matches++; }
                catch (ArgumentException) { }
            }
            if (matches == 0 || keyword == "oneOf" && matches != 1)
                throw new ArgumentException($"{path}: does not match {keyword}.");
        }
        if (s["enum"] is JsonArray choices && !choices.Any(c => JsonNode.DeepEquals(c, value)))
            throw new ArgumentException($"{path}: unsupported value.");
        var kind = value?.GetValueKind();
        var type = s["type"]?.GetValue<string>();
        bool valid = type switch
        {
            "object" => value is JsonObject, "array" => value is JsonArray,
            "string" => kind == System.Text.Json.JsonValueKind.String,
            "number" => kind == System.Text.Json.JsonValueKind.Number,
            "integer" => kind == System.Text.Json.JsonValueKind.Number && double.TryParse(value!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out var n) && Math.Truncate(n) == n,
            "boolean" => kind is System.Text.Json.JsonValueKind.True or System.Text.Json.JsonValueKind.False,
            "null" => value is null, _ => true
        };
        if (!valid) throw new ArgumentException($"{path}: expected {type}.");
        if (value is JsonObject obj)
        {
            if (s["required"] is JsonArray required)
                foreach (var key in required)
                    if (!obj.ContainsKey(key!.GetValue<string>())) throw new ArgumentException($"{path}.{key}: required.");
            foreach (var pair in obj)
            {
                var properties = s["properties"] as JsonObject;
                if (properties?.TryGetPropertyValue(pair.Key, out var child) == true) Validate(pair.Value, child, path + "." + pair.Key);
                else if (s["additionalProperties"]?.ToJsonString() == "false") throw new ArgumentException($"{path}.{pair.Key}: unknown property.");
            }
        }
        if (value is JsonArray array)
        {
            if (s["minItems"] is JsonValue minimum && array.Count < minimum.GetValue<int>()) throw new ArgumentException($"{path}: too few items.");
            if (s["maxItems"] is JsonValue maximum && array.Count > maximum.GetValue<int>()) throw new ArgumentException($"{path}: too many items.");
            for (var i = 0; i < array.Count; i++) Validate(array[i], s["items"], $"{path}[{i}]");
        }
        if (kind == System.Text.Json.JsonValueKind.Number && double.TryParse(value!.ToJsonString(), System.Globalization.CultureInfo.InvariantCulture, out var number))
        {
            if (s["minimum"] is JsonValue min && number < min.GetValue<double>()) throw new ArgumentException($"{path}: below minimum.");
            if (s["maximum"] is JsonValue max && number > max.GetValue<double>()) throw new ArgumentException($"{path}: above maximum.");
        }
    }
}

// A request ID is bound to its full payload. Never replay a mutation after an
// ambiguous disconnect. Keep the bounded journal for the lifetime of this host.
public sealed class RequestJournal
{
    private sealed record Entry(string Payload, CancellationTokenSource Cancellation, TaskCompletionSource<JsonObject> Completion);
    private readonly ConcurrentDictionary<string, Entry> _entries = new();
    private const int Capacity = 10000;
    public async Task<JsonObject> ExecuteAsync(string id, string payload, Func<CancellationToken, Task<JsonObject>> action, CancellationToken shutdown)
    {
        if (_entries.Count >= Capacity && !_entries.ContainsKey(id)) return BridgeProtocol.Error(id, "journal_full", "Restart the host before submitting new requests.");
        var candidate = new Entry(payload, CancellationTokenSource.CreateLinkedTokenSource(shutdown), new(TaskCreationOptions.RunContinuationsAsynchronously));
        var entry = _entries.GetOrAdd(id, candidate);
        if (!ReferenceEquals(entry, candidate))
        {
            candidate.Cancellation.Dispose();
            if (entry.Payload == "__cancelled_before_dispatch__") return entry.Completion.Task.Result.DeepClone().AsObject();
            if (entry.Payload != payload) return BridgeProtocol.Error(id, "request_conflict", "Request ID was already used with a different payload.");
            return (await entry.Completion.Task).DeepClone().AsObject();
        }
        try { entry.Completion.TrySetResult(await action(entry.Cancellation.Token)); }
        catch (OperationCanceledException) { entry.Completion.TrySetResult(BridgeProtocol.Error(id, "cancelled", "Cancelled before completion; committed operations are not undone.")); }
        catch (Exception ex) { entry.Completion.TrySetResult(BridgeProtocol.Error(id, "tool_error", ex.GetBaseException().Message)); }
        return (await entry.Completion.Task).DeepClone().AsObject();
    }
    public JsonObject Cancel(string id)
    {
        if (!_entries.TryGetValue(id, out var entry))
        {
            if (_entries.Count >= Capacity) return BridgeProtocol.Error(id, "journal_full", "Request journal full.");
            var tombstone = new Entry("__cancelled_before_dispatch__", new(), new(TaskCreationOptions.RunContinuationsAsynchronously));
            tombstone.Completion.SetResult(BridgeProtocol.Error(id, "cancelled", "Cancelled before dispatch."));
            entry = _entries.GetOrAdd(id, tombstone);
        }
        if (!entry.Completion.Task.IsCompleted) entry.Cancellation.Cancel();
        return Status(id);
    }
    public JsonObject Status(string id)
    {
        if (!_entries.TryGetValue(id, out var entry)) return BridgeProtocol.Error(id, "request_not_found", "Unknown request ID.");
        if (entry.Completion.Task.IsCompletedSuccessfully) return entry.Completion.Task.Result.DeepClone().AsObject();
        return new() { ["success"] = true, ["requestId"] = id, ["state"] = entry.Cancellation.IsCancellationRequested ? "cancelling" : "running" };
    }
}
