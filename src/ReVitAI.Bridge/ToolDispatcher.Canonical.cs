using System.Text.Json;
using System.Text.Json.Nodes;
using Autodesk.Revit.DB;
using RevitAi.Engine.Abstractions;
using RevitAi.Engine.Core;
using RevitAi.Engine.Revit;
using RevitAi.Engine.Revit.Implementations;
using BimAi.Protocol;

namespace ReVitAI.Bridge;

internal sealed partial class ToolDispatcher
{
    private AIToolRegistry? _registry;
    private RevitAdapter? _adapter;
    private readonly SessionAIToolDataCache _cache = new();
    private readonly UnitService _units = new();
    private readonly HashSet<string> _canonical = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _preview = new(StringComparer.OrdinalIgnoreCase);

    private void RegisterCanonicalTools()
    {
        var assembly = typeof(ToolDispatcher).Assembly;
        using var bridgeStream = assembly.GetManifestResourceStream("BimAi.BridgeContracts.json")!;
        foreach (var contract in JsonNode.Parse(bridgeStream)!.AsArray())
        {
            var name = contract!["name"]!.GetValue<string>();
            if (_tools.TryGetValue(name, out var existing)) existing.InputSchema = JsonSerializer.Deserialize<JsonElement>(contract["parameters"]!.ToJsonString());
        }
        foreach (var tool in _tools.Values.Where(t => t.Mutating))
        {
            tool.RequiresTransaction = true;
            if (tool.Name is not ("save_document" or "save_document_as" or "load_family")) _preview.Add(tool.Name);
        }
        _preview.Remove("batch"); // Subcommands may save/export; no blanket rollback promise.
        using var stream = assembly.GetManifestResourceStream("BimAi.ToolContracts.json")!;
        foreach (var contract in JsonNode.Parse(stream)!.AsArray())
        {
            var name = contract!["name"]!.GetValue<string>();
            if (BridgeProtocol.DisabledTools.Contains(name, StringComparer.OrdinalIgnoreCase)) { _tools.Remove(name); continue; }
            var schema = contract["parameters"]!.DeepClone().AsObject();
            schema["additionalProperties"] = false;
            // Explicit compatibility alias for the historical Bridge spelling.
            if (name == "create_level") schema["properties"]!["name"] = schema["properties"]!["levelName"]!.DeepClone();
            _canonical.Add(name);
            _preview.Remove(name);
            if (schema["properties"]?["dryRun"] is not null || schema["properties"]?["dry_run"] is not null) _preview.Add(name);
            _tools[name] = new ToolDefinition
            {
                Name = name, Description = contract["description"]!.GetValue<string>(),
                Category = contract["category"]!.GetValue<string>(),
                Mutating = contract["requiresModification"]!.GetValue<bool>(),
                RequiresTransaction = contract["requiresTransaction"]!.GetValue<bool>(),
                RequiresActiveDocument = contract["requiresActiveDocument"]!.GetValue<bool>(),
                InputSchema = JsonSerializer.Deserialize<JsonElement>(schema.ToJsonString()),
                Handler = context => ExecuteCanonical(name, context)
            };
        }
    }

    private object ExecuteCanonical(string name, InvocationContext context)
    {
        if (_registry is null)
        {
            _adapter = new RevitAdapter(context.UiApplication);
            _registry = AIToolRegistry.Instance;
            _registry.SetRevitAdapter(_adapter);
            _registry.SetDataCache(_cache);
            _registry.SetUnitService(_units);
            RegisterImplementations();
            _registry.SetAdapterAssembly(typeof(ExtToolLib.GeneratedToolBase).Assembly);
            _registry.EnsureToolsDiscovered();
        }
        var arguments = JsonNode.Parse(context.Arguments.GetRawText())!.AsObject();
        if (name == "create_level" && arguments["levelName"] is null && arguments["name"] is not null)
        { arguments["levelName"] = arguments["name"]!.DeepClone(); arguments.Remove("name"); }
        var parameters = arguments.ToDictionary(x => x.Key, x => ConvertNode(x.Value), StringComparer.OrdinalIgnoreCase);
        var result = _registry.GetTool(name)!.ExecuteAsync(new AIToolContext
        {
            Document = context.UiApplication.ActiveUIDocument?.Document, Transaction = context.Transaction,
            Parameters = parameters, RevitAdapter = _adapter, DataCache = _cache, UnitService = _units,
            SessionId = context.UiApplication.ActiveUIDocument?.Document.CreationGUID.ToString("D")
        }).GetAwaiter().GetResult();
        if (!result.Success) throw new InvalidOperationException(result.Message);
        return new { Message = result.Message, Data = result.Data, CacheId = result.CacheId };
    }

    private static object? ConvertNode(JsonNode? node) => node switch
    {
        null => null,
        JsonObject obj => obj.ToDictionary(x => x.Key, x => ConvertNode(x.Value), StringComparer.OrdinalIgnoreCase),
        JsonArray array => array.Select(ConvertNode).ToList(),
        _ => JsonSerializer.Deserialize<object>(node.ToJsonString()) is JsonElement value ? ConvertElement(value) : null
    };
    private static object? ConvertElement(JsonElement value) => value.ValueKind switch
    {
        JsonValueKind.String => value.GetString(), JsonValueKind.True => true, JsonValueKind.False => false,
        JsonValueKind.Number => value.TryGetInt64(out var n) ? (object)n : value.GetDouble(), _ => null
    };
    private bool SupportsDryRun(string name) => _preview.Contains(name);
    private void ValidateArguments(string name, JsonElement arguments)
    {
        if (arguments.ValueKind != JsonValueKind.Object) throw new ArgumentException("arguments must be an object.");
        var schema = JsonNode.Parse(JsonSerializer.Serialize(_tools[name].InputSchema, Json.Options));
        BridgeProtocol.Validate(JsonNode.Parse(arguments.GetRawText()), schema);
    }
    private static void EnsureCommitted(TransactionStatus status)
    {
        BridgeProtocol.RequireCommitted(status.ToString());
    }
    private static void RegisterImplementations()
    {
        ExtToolLibImplementations.RegisterAll();
        ExtToolLib.Implementations.ReadOnlyImplementations.RegisterAll();
        ExtToolLib.Implementations.ReadOnlyImplementations2.RegisterAll();
        ExtToolLib.Implementations.ReadOnlyImplementations3.RegisterAll();
        ExtToolLib.Implementations.WriteImplementations.RegisterAll();
        ExtToolLib.Implementations.WriteImplementations2.RegisterAll();
        ExtToolLib.Implementations.WriteImplementations3.RegisterAll();
        ExtToolLib.Implementations.ParameterImplementations.RegisterAll();
        ExtToolLib.Implementations.MaterialImplementations.RegisterAll();
        ExtToolLib.Implementations.FamilyElementImplementations.RegisterAll();
        ExtToolLib.Implementations.ProjectParameterImplementations.RegisterAll();
        ExtToolLib.Implementations.ModelingImplementations.RegisterAll();
        ExtToolLib.Implementations.ModelingImplementations2.RegisterAll();
        ExtToolLib.Implementations.ElementOpsImplementations.RegisterAll();
        ExtToolLib.Implementations.QueryInteractionImplementations.RegisterAll();
        ExtToolLib.Implementations.DomainImplementations.RegisterAll();
        ExtToolLib.Implementations.DomainImplementations2.RegisterAll();
        ExtToolLib.Implementations.DomainImplementations3.RegisterAll();
        ExtToolLib.Implementations.CodeToolImplementations.RegisterAll();
    }
}
