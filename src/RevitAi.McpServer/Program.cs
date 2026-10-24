using System.Collections.Concurrent;
using BimAi.Protocol;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

// RevitAi MCP server: exposes the running Revit add-in as MCP tools so a
// manually started Codex session can drive Revit. The add-in publishes its
// named pipe in %LOCALAPPDATA%\ReVitAI\revitai-bridge.json; this process only forwards
// JSON requests and never touches Revit directly.

var bridgePipe = Environment.GetEnvironmentVariable("REVIT_AI_BRIDGE_PIPE");
var serverVersion = "3.0.0";
var pending = new ConcurrentDictionary<string, CancellationTokenSource>();
var tasks = new List<Task>();

string? line;
while ((line = await Console.In.ReadLineAsync()) is not null)
{
    if (string.IsNullOrWhiteSpace(line))
    {
        continue;
    }

    JsonObject? request;
    try
    {
        request = JsonNode.Parse(line)?.AsObject();
    }
    catch (Exception ex)
    {
        Log($"bad request json: {ex.Message}");
        continue;
    }

    if (request is null)
    {
        continue;
    }

    var method = request["method"]?.GetValue<string>() ?? string.Empty;
    var id = request["id"];
    Log($"<- {method}");

    switch (method)
    {
        case "initialize":
            Respond(id, new JsonObject
            {
                ["protocolVersion"] = "2024-11-05",
                ["capabilities"] = new JsonObject
                {
                    ["tools"] = new JsonObject()
                },
                ["serverInfo"] = new JsonObject
                {
                    ["name"] = "revit-ai",
                    ["version"] = serverVersion
                }
            });
            break;

        case "notifications/initialized": break;
        case "notifications/cancelled":
            var cancelledId = request["params"]?["requestId"]?.ToJsonString();
            if (cancelledId is not null && pending.TryGetValue(cancelledId, out var cancellation)) cancellation.Cancel();
            break;

        case "ping":
            Respond(id, new JsonObject());
            break;

        case "tools/list":
            var catalogPipe = await ResolveLivePipeAsync(bridgePipe, CancellationToken.None);
            var toolStatus = catalogPipe is null ? "{}" : await SendAsync(catalogPipe, new JsonObject
            {
                ["type"] = "agent.status"
            });
            Respond(id, new JsonObject { ["tools"] = BuildTools(toolStatus) });
            break;

        case "tools/call":
            var key = id?.ToJsonString() ?? Guid.NewGuid().ToString("N");
            var source = new CancellationTokenSource();
            if (!pending.TryAdd(key, source)) { RespondError(id!, -32600, "Duplicate active JSON-RPC id."); source.Dispose(); break; }
            tasks.RemoveAll(task => task.IsCompleted);
            tasks.Add(RunCallAsync(request, id, bridgePipe, key, source, pending));
            break;

        default:
            if (id is not null)
            {
                RespondError(id, -32601, $"method not found: {method}");
            }

            break;
    }
}

await Task.WhenAll(tasks);
return 0;

static JsonArray BuildTools(string? statusJson = null)
{
    var tools = new JsonArray
    {
        Tool("revit_request_status", "Read the final outcome of an earlier request before retrying a mutation.", new JsonObject { ["properties"] = new JsonObject { ["requestId"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("requestId") }),
        Tool("revit_cancel_request", "Request cancellation of a queued/running request; committed work is preserved.", new JsonObject { ["properties"] = new JsonObject { ["requestId"] = new JsonObject { ["type"] = "string" } }, ["required"] = new JsonArray("requestId") }),
        Tool(
            "revit_status",
            "返回当前 Revit 会话状态（连接状态、模型、工具数量、技能数量）。",
            new JsonObject()),
        Tool(
            "revit_list_tools",
            "列出当前 Revit 会话可用的全部工具名称与分类。",
            new JsonObject()),
        Tool(
            "revit_invoke_tool",
            "兼容入口：按名称调用一个 Revit 工具。新调用应优先使用直接工具名。",
            new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["tool"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "工具名称"
                    },
                    ["arguments"] = new JsonObject
                    {
                        ["type"] = "object",
                        ["description"] = "工具参数（键值对）"
                    },
                    ["expectedDocumentGuid"] = new JsonObject { ["type"] = "string", ["description"] = "Target document GUID; otherwise capture the current document before dispatch." }
                },
                ["required"] = new JsonArray("tool")
            }),
        Tool(
            "revit_agent_chat",
            "把一段简单自然语言任务交给隐藏 Revit 桥接执行。复杂工程绘图应直接调用具名工具。",
            new JsonObject
            {
                ["properties"] = new JsonObject
                {
                    ["message"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "交给 Revit 桥接的任务描述"
                    }
                },
                ["required"] = new JsonArray("message")
            })
    };

    if (string.IsNullOrWhiteSpace(statusJson))
    {
        return tools;
    }

    try
    {
        var status = JsonNode.Parse(statusJson)?["status"]?.AsObject();
        var catalog = status?["tools"]?.AsArray();
        if (catalog is null)
        {
            return tools;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "revit_status",
            "revit_list_tools",
            "revit_invoke_tool",
            "revit_agent_chat"
        };

        foreach (var item in catalog)
        {
            var name = item?["name"]?.GetValue<string>()?.Trim();
            if (string.IsNullOrWhiteSpace(name)
                || name.StartsWith("revit_", StringComparison.OrdinalIgnoreCase)
                || !seen.Add(name))
            {
                continue;
            }

            var description = item?["description"]?.GetValue<string>() ?? string.Empty;
            var toolSet = item?["toolSet"]?.GetValue<string>() ?? "未分类";
            var schema = item?["inputSchema"]?.DeepClone() as JsonObject
                ?? new JsonObject
                {
                    ["properties"] = new JsonObject(),
                    ["additionalProperties"] = true
                };
            tools.Add(Tool(
                name,
                $"{description} [{toolSet}]",
                schema));
        }
    }
    catch
    {
        // Keep the compatibility wrappers when the live catalog cannot be read.
    }

    return tools;
}

static JsonObject Tool(string name, string description, JsonObject schema)
{
    var inputSchema = schema.DeepClone().AsObject();
    inputSchema["type"] ??= "object";
    inputSchema["properties"] ??= new JsonObject();

    return new JsonObject
    {
        ["name"] = name,
        ["description"] = description,
        ["inputSchema"] = inputSchema
    };
}

static async Task HandleToolCallAsync(
    JsonObject request,
    JsonNode? id,
    string? configuredPipe, CancellationToken cancellationToken = default)
{
    var parameters = request["params"]?.AsObject();
    var name = parameters?["name"]?.GetValue<string>() ?? string.Empty;
    var arguments = parameters?["arguments"]?.AsObject();

    var selectedPipe = await ResolveLivePipeAsync(configuredPipe, cancellationToken);
    if (selectedPipe is null) { Respond(id, ToolResult("No ready bridge. Select a host with REVIT_AI_BRIDGE_PIPE or REVIT_AI_PROCESS_ID.", true)); return; }
    configuredPipe = selectedPipe;
    JsonObject bridgeRequest;
    switch (name)
    {
        case "revit_request_status":
        case "revit_cancel_request":
            var operation = new JsonObject { ["type"] = name == "revit_request_status" ? "request.status" : "request.cancel", ["requestId"] = arguments?["requestId"]?.GetValue<string>() };
            var operationResult = await SendAsync(selectedPipe, operation, cancellationToken);
            Respond(id, ToolResult(operationResult, IsError(operationResult)));
            return;
        case "revit_status":
            bridgeRequest = new JsonObject { ["type"] = "agent.status" };
            break;

        case "revit_list_tools":
            var status = await SendAsync(configuredPipe, new JsonObject
            {
                ["type"] = "agent.status"
            });
            Respond(id, ToolResult(SummarizeTools(status), IsError(status)));
            return;

        case "revit_agent_chat":
            bridgeRequest = new JsonObject
            {
                ["type"] = "agent.chat",
                ["message"] = arguments?["message"]?.GetValue<string>() ?? string.Empty
            };
            break;

        default:
            var wrapper = name == "revit_invoke_tool";
            bridgeRequest = new JsonObject
            {
                ["type"] = "invoke",
                ["tool"] = wrapper ? arguments?["tool"]?.GetValue<string>() ?? "" : name,
                ["arguments"] = wrapper ? arguments?["arguments"]?.DeepClone() ?? new JsonObject() : arguments?.DeepClone() ?? new JsonObject()
            };
            var targetDocument = wrapper ? arguments?["expectedDocumentGuid"]?.GetValue<string>() : null;
            targetDocument ??= Environment.GetEnvironmentVariable("REVIT_AI_DOCUMENT_GUID");
            if (targetDocument is null && bridgeRequest["tool"]?.GetValue<string>() != "get_document_snapshot")
            {
                var snapshot = JsonNode.Parse(await SendAsync(selectedPipe, new JsonObject { ["type"] = "invoke", ["tool"] = "get_document_snapshot", ["arguments"] = new JsonObject(), ["requestId"] = Guid.NewGuid().ToString("N") }, cancellationToken));
                targetDocument = snapshot?["payload"]?["documentGuid"]?.GetValue<string>();
                if (string.IsNullOrWhiteSpace(targetDocument)) { Respond(id, ToolResult(snapshot?.ToJsonString() ?? "Cannot bind document.", true)); return; }
            }
            if (targetDocument is not null) bridgeRequest["expectedDocumentGuid"] = targetDocument;
            break;
    }

    var bridgeId = Guid.NewGuid().ToString("N");
    bridgeRequest["requestId"] = bridgeId;
    bridgeRequest["hostId"] = selectedPipe;
    bridgeRequest["protocolVersion"] = BridgeProtocol.Version;
    using var registration = cancellationToken.Register(() => { _ = SendAsync(selectedPipe, new JsonObject { ["type"] = "request.cancel", ["requestId"] = bridgeId }); });
    var response = await SendAsync(configuredPipe, bridgeRequest, cancellationToken);
    Respond(id, ToolResult(response, IsError(response)));
}

static JsonObject ToolResult(string text, bool isError)
{
    return new JsonObject
    {
        ["content"] = new JsonArray
        {
            new JsonObject
            {
                ["type"] = "text",
                ["text"] = text
            }
        },
        ["isError"] = isError
    };
}

static string SummarizeTools(string statusJson)
{
    try
    {
        var status = JsonNode.Parse(statusJson)?["status"]?.AsObject();
        var tools = status?["tools"]?.AsArray();
        if (tools is null)
        {
            return statusJson;
        }

        var names = tools
            .Select(tool => tool?["name"]?.GetValue<string>())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .ToList();
        var builder = new StringBuilder();
        builder.Append("共 ").Append(names.Count).Append(" 个工具：").AppendLine();
        foreach (var tool in tools)
        {
            builder.Append("- ")
                .Append(tool?["name"]?.GetValue<string>())
                .Append(" [")
                .Append(tool?["toolSet"]?.GetValue<string>() ?? "未分类")
                .Append(']')
                .AppendLine();
        }

        return builder.ToString().TrimEnd();
    }
    catch
    {
        return statusJson;
    }
}

static bool IsError(string responseJson)
{
    try
    {
        var node = JsonNode.Parse(responseJson)?.AsObject();
        return node?["success"]?.GetValue<bool>() == false;
    }
    catch
    {
        return true;
    }
}

static async Task<string> SendAsync(string? configuredPipe, JsonObject request, CancellationToken cancellationToken = default)
{
    var pipeName = ResolvePipeName(configuredPipe);
    if (string.IsNullOrWhiteSpace(pipeName))
    {
        return JsonSerializer.Serialize(new
        {
            success = false,
            error = "未找到 Revit 桥接管道，请确认 Revit 与 Revit AI 面板已打开。"
        });
    }

    try
    {
        await using var pipe = new NamedPipeClientStream(
            ".",
            pipeName,
            PipeDirection.InOut,
            PipeOptions.Asynchronous);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromMinutes(10));
        await pipe.ConnectAsync(15000, timeout.Token);
        using var reader = new StreamReader(pipe, Encoding.UTF8, leaveOpen: true);
        await using var writer = new StreamWriter(
            pipe,
            new UTF8Encoding(false),
            leaveOpen: true)
        {
            AutoFlush = true
        };
        timeout.Token.ThrowIfCancellationRequested();
        await writer.WriteLineAsync(request.ToJsonString().AsMemory(), timeout.Token);
        var response = await reader.ReadLineAsync(timeout.Token);
        return response ?? JsonSerializer.Serialize(new
        {
            success = false,
            error = "Revit 桥接未返回结果。"
        });
    }
    catch (Exception ex)
    {
        return JsonSerializer.Serialize(new
        {
            success = false,
            errorCode = ex is OperationCanceledException ? "outcome_unknown" : "bridge_unavailable",
            requestId = request["requestId"]?.GetValue<string>(),
            hostId = configuredPipe,
            error = $"Bridge result unavailable; query request.status before retrying a mutation: {ex.GetBaseException().Message}"
        });
    }
}

static string? ResolvePipeName(string? configuredPipe)
{
    if (!string.IsNullOrWhiteSpace(configuredPipe))
    {
        return configuredPipe;
    }

    try
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "RevitAi");
        foreach (var fileName in new[] { "revitai-bridge.json", "bridge.json" })
        {
            var path = Path.Combine(directory, fileName);
            if (!File.Exists(path))
            {
                continue;
            }

            var json = JsonNode.Parse(File.ReadAllText(path))?.AsObject();
            var pipeName = json?["pipeName"]?.GetValue<string>();
            if (!string.IsNullOrWhiteSpace(pipeName))
            {
                return pipeName;
            }
        }

        return null;
    }
    catch (Exception ex)
    {
        Log($"bridge discovery read failed: {ex.Message}");
        return null;
    }
}

static void Respond(JsonNode? id, JsonObject result)
{
    if (id is null)
    {
        return;
    }

    WriteLine(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["result"] = result
    });
}

static void RespondError(JsonNode id, int code, string message)
{
    WriteLine(new JsonObject
    {
        ["jsonrpc"] = "2.0",
        ["id"] = id.DeepClone(),
        ["error"] = new JsonObject
        {
            ["code"] = code,
            ["message"] = message
        }
    });
}

static void WriteLine(JsonObject payload)
{
    lock (Console.Out)
    {
        Console.Out.WriteLine(payload.ToJsonString());
        Console.Out.Flush();
    }
}

static void Log(string message)
{
    // stdout is reserved for JSON-RPC; diagnostics go to stderr.
    Console.Error.WriteLine($"[revit-ai-mcp] {message}");
}

static async Task RunCallAsync(JsonObject request, JsonNode? id, string? pipe, string key, CancellationTokenSource source, ConcurrentDictionary<string, CancellationTokenSource> pending)
{
    try { await HandleToolCallAsync(request, id, pipe, source.Token); }
    catch (OperationCanceledException) { Respond(id, ToolResult("Cancelled before dispatch.", true)); }
    catch (Exception ex) { Respond(id, ToolResult(ex.GetBaseException().Message, true)); }
    finally { pending.TryRemove(key, out _); source.Dispose(); }
}

static async Task<string?> ResolveLivePipeAsync(string? configuredPipe, CancellationToken token)
{
    if (!string.IsNullOrWhiteSpace(configuredPipe)) return configuredPipe;
    var directory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "RevitAi");
    var candidates = new List<(string Pipe, int Pid)>();
    var selectedPid = Environment.GetEnvironmentVariable("REVIT_AI_PROCESS_ID");
    if (!Directory.Exists(directory)) return null;
    foreach (var file in Directory.EnumerateFiles(directory, "*bridge*.json"))
    {
        try
        {
            var node = JsonNode.Parse(File.ReadAllText(file));
            var pipe = node?["pipeName"]?.GetValue<string>();
            var pid = node?["processId"]?.GetValue<int>() ?? 0;
            if (string.IsNullOrEmpty(pipe) || selectedPid is not null && selectedPid != pid.ToString()) continue;
            using var process = Process.GetProcessById(pid);
            if (!process.HasExited) candidates.Add((pipe, pid));
        }
        catch { /* An invalid/stale file must not hide another live host. */ }
    }
    if (candidates.Select(c => c.Pid).Distinct().Count() > 1) throw new InvalidOperationException("Multiple Revit processes found; set REVIT_AI_PROCESS_ID or REVIT_AI_BRIDGE_PIPE.");
    foreach (var candidate in candidates.Distinct())
    {
        using var probe = CancellationTokenSource.CreateLinkedTokenSource(token);
        probe.CancelAfter(TimeSpan.FromSeconds(2));
        var status = await SendAsync(candidate.Pipe, new JsonObject { ["type"] = "agent.status" }, probe.Token);
        try
        {
            if (JsonNode.Parse(status)?["status"]?["toolsReady"]?.GetValue<bool>() == true) return candidate.Pipe;
        }
        catch { }
    }
    return null;
}
