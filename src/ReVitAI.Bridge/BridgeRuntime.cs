using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.UI;
using BimAi.Protocol;

namespace ReVitAI.Bridge;

// Construct in a valid Revit API context. Both UI and standalone pipe share
// this dispatcher; no Revit API access occurs in the background transport.
public sealed class BridgeRuntime
{
    private readonly ToolDispatcher _dispatcher = new();
    private readonly ExternalEventInvoker _invoker;
    public BridgeRuntime() { _invoker = new ExternalEventInvoker(_dispatcher); }
    public JsonObject Catalog() => JsonSerializer.SerializeToNode(_dispatcher.Catalog(true), Json.Options)!.AsObject();
    public async Task<JsonObject> InvokeAsync(string tool, JsonObject arguments, CancellationToken cancellationToken = default, string? expectedDocumentGuid = null)
    {
        try
        {
            var element = JsonSerializer.Deserialize<JsonElement>(arguments.ToJsonString());
            var result = await _dispatcher.ExecuteAsync(_invoker, tool, element, cancellationToken, expectedDocumentGuid);
            return new() { ["success"] = true, ["payload"] = JsonSerializer.SerializeToNode(result, Json.Options), ["protocolVersion"] = BridgeProtocol.Version, ["state"] = "completed" };
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { return BridgeProtocol.Error(null, ex is ArgumentException ? "invalid_arguments" : "tool_error", ex.GetBaseException().Message); }
    }
}
