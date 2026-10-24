using BimAi.Protocol;
using System.Text.Json.Nodes;
using System.Diagnostics;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using Autodesk.Revit.UI;

namespace ReVitAI.Bridge;

internal sealed class BridgeServer : IAsyncDisposable
{
    private readonly ToolDispatcher _dispatcher;
    private readonly RequestJournal _journal = new();
    private readonly ExternalEventInvoker _invoker;
    private readonly CancellationTokenSource _shutdown = new();
    private Task? _acceptLoop;
    private readonly string _discoveryPath;
    private readonly string _instanceDiscoveryPath;
    private string? _lastDocumentGuid;
    private string? _lastDocumentTitle;
    private string? _lastDocumentPath;

    public BridgeServer(ToolDispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _invoker = new ExternalEventInvoker(dispatcher);
        PipeName = $"ReVitAI.Bridge.{Environment.ProcessId}.{Guid.NewGuid():N}";
        _instanceDiscoveryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReVitAI",
            $"revitai-bridge.{Environment.ProcessId}.json");
        _discoveryPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "ReVitAI",
            "revitai-bridge.json");
    }

    public string PipeName { get; }
    public ExternalEventInvoker Invoker => _invoker;

    public void Start()
    {
        WriteDiscovery();
        _acceptLoop = Task.Run(() => AcceptLoopAsync(_shutdown.Token));
        BridgeLog.Write($"Bridge started on pipe '{PipeName}'.");
    }

    public async ValueTask DisposeAsync()
    {
        _shutdown.Cancel();
        if (_acceptLoop is not null)
        {
            try
            {
                await _acceptLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        TryRemoveDiscovery();
        _shutdown.Dispose();
    }

    private async Task AcceptLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var server = new NamedPipeServerStream(
                    PipeName,
                    PipeDirection.InOut,
                    8,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous | PipeOptions.CurrentUserOnly);
                await server.WaitForConnectionAsync(cancellationToken).ConfigureAwait(false);
                _ = HandleClientAsync(server, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"Accept loop error: {ex}");
                await Task.Delay(250, cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task HandleClientAsync(
        NamedPipeServerStream server,
        CancellationToken cancellationToken)
    {
        await using (server.ConfigureAwait(false))
        using (var reader = new StreamReader(
            server,
            new UTF8Encoding(false),
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 16 * 1024,
            leaveOpen: true))
        using (var writer = new StreamWriter(
            server,
            new UTF8Encoding(false),
            bufferSize: 16 * 1024,
            leaveOpen: true)
        {
            AutoFlush = true,
        })
        {
            try
            {
                var line = await reader.ReadLineAsync(cancellationToken).ConfigureAwait(false);
                if (string.IsNullOrWhiteSpace(line))
                {
                    return;
                }

                using var document = JsonDocument.Parse(line);
                var response = await ProcessRequestAsync(document.RootElement, cancellationToken)
                    .ConfigureAwait(false);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json.Options))
                    .ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                BridgeLog.Write($"Client request error: {ex}");
                var response = ErrorResponse(
                    null,
                    null,
                    "bridge_error",
                    ex.Message);
                await writer.WriteLineAsync(JsonSerializer.Serialize(response, Json.Options))
                    .ConfigureAwait(false);
            }
        }
    }

    private async Task<object> ProcessRequestAsync(JsonElement request, CancellationToken cancellationToken)
    {
        var type = Json.String(request, "type") ?? "invoke";
        var id = Json.String(request, "requestId") ?? Guid.NewGuid().ToString("N");
        if (type == "request.cancel") return _journal.Cancel(id);
        if (type == "request.status") return _journal.Status(id);
        var hostId = Json.String(request, "hostId");
        if (hostId is not null && hostId != PipeName) return BridgeProtocol.Error(id, "host_mismatch", "The target host changed.");
        var version = Json.String(request, "protocolVersion");
        if (version is not null && version != BridgeProtocol.Version) return BridgeProtocol.Error(id, "protocol_mismatch", "Unsupported bridge protocol version.");
        if (type is "invoke" or "agent.chat")
            return await _journal.ExecuteAsync(id, request.GetRawText(), async token =>
                JsonSerializer.SerializeToNode(await ProcessCoreAsync(request, token), Json.Options)!.AsObject(), cancellationToken);
        return await ProcessCoreAsync(request, cancellationToken);
    }

    private async Task<object> ProcessCoreAsync(
        JsonElement request,
        CancellationToken cancellationToken)
    {
        var type = Json.String(request, "type")?.Trim() ?? "invoke";
        var requestId = Json.String(request, "requestId");
        var stopwatch = Stopwatch.StartNew();

        object response;
        switch (type.ToLowerInvariant())
        {
            case "tools.list":
            case "ping":
                if (type.Equals("ping", StringComparison.OrdinalIgnoreCase))
                {
                    var pong = await _dispatcher.ExecuteAsync(
                        _invoker,
                        "ping",
                        Json.EmptyObject()).ConfigureAwait(false);
                    object? document = null;
                    try
                    {
                        document = await _dispatcher.ExecuteAsync(
                            _invoker,
                            "get_document_snapshot",
                            Json.EmptyObject()).ConfigureAwait(false);
                        UpdateDiscovery(document);
                    }
                    catch (Exception ex)
                    {
                        BridgeLog.Write($"Discovery document snapshot unavailable: {ex.Message}");
                    }

                    response = new
                    {
                        Type = "pong",
                        RequestId = requestId,
                        Success = true,
                        Payload = pong,
                        Document = document,
                        DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                    };
                }
                else
                {
                    var catalog = _dispatcher.Catalog(includeSchema: true);
                    response = new
                    {
                        Type = "tools.list",
                        RequestId = requestId,
                        Success = true,
                        Tools = catalog.GetType().GetProperty("Tools")!.GetValue(catalog),
                        Count = catalog.GetType().GetProperty("Count")!.GetValue(catalog),
                        DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                    };
                }
                break;

            case "agent.status":
                response = BuildAgentStatus(requestId, stopwatch.Elapsed.TotalMilliseconds);
                break;

            case "agent.chat":
                response = await ProcessSimpleChatAsync(
                    request,
                    requestId,
                    stopwatch,
                    cancellationToken).ConfigureAwait(false);
                break;

            case "invoke":
                response = await ProcessInvokeAsync(
                    request,
                    requestId,
                    stopwatch,
                    cancellationToken).ConfigureAwait(false);
                break;

            default:
                response = ErrorResponse(
                    type,
                    requestId,
                    "invalid_request",
                    $"Unsupported request type '{type}'.");
                break;
        }

        return response;
    }

    private async Task<object> ProcessInvokeAsync(
        JsonElement request,
        string? requestId,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var tool = Json.String(request, "tool")
            ?? throw new InvalidOperationException("The invoke request requires a tool name.");
        var arguments = request.TryGetProperty("arguments", out var argElement)
            && argElement.ValueKind == JsonValueKind.Object
            ? argElement.Clone()
            : Json.EmptyObject();

        try
        {
            var expectedDocumentGuid = StringAny(request, "expectedDocumentGuid", "expected_document_guid", "documentGuid", "document_guid");
            if (_dispatcher.IsMutating(tool) && string.IsNullOrWhiteSpace(expectedDocumentGuid))
                return BridgeProtocol.Error(requestId, "document_required", "Bind mutations to expectedDocumentGuid from get_document_snapshot.");
            var payload = await _dispatcher.ExecuteAsync(_invoker, tool, arguments, cancellationToken, expectedDocumentGuid).ConfigureAwait(false);
            if (tool.Equals("get_document_snapshot", StringComparison.OrdinalIgnoreCase))
            {
                UpdateDiscovery(payload);
            }

            return new
            {
                Type = "result",
                ProtocolVersion = BridgeProtocol.Version,
                State = "completed",
                HostId = PipeName,
                RequestId = requestId,
                Success = true,
                Tool = tool,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                Payload = payload,
                ErrorCode = (string?)null,
                ErrorMessage = (string?)null,
            };
        }
        catch (Exception ex)
        {
            BridgeLog.Write($"Tool '{tool}' failed:{Environment.NewLine}{ex}");
            return ErrorResponse(
                tool,
                requestId,
                ex is ArgumentException ? "invalid_arguments" : ex is OperationCanceledException ? "cancelled" : ex.Message.StartsWith("document_mismatch") ? "document_mismatch" : "tool_error",
                ex.GetBaseException().Message,
                stopwatch.Elapsed.TotalMilliseconds);
        }
    }

    private async Task<object> ProcessSimpleChatAsync(
        JsonElement request,
        string? requestId,
        Stopwatch stopwatch,
        CancellationToken cancellationToken)
    {
        var message = Json.String(request, "message")?.Trim() ?? string.Empty;
        var normalized = message.ToLowerInvariant();
        string? tool = null;
        object? payload = null;

        if (normalized.Contains("ping") || normalized.Contains("连接"))
        {
            tool = "ping";
        }
        else if (normalized.Contains("当前视图") || normalized.Contains("active view"))
        {
            tool = "get_active_view";
        }
        else if (normalized.Contains("轴网") || normalized.Contains("grid"))
        {
            tool = "get_all_grids";
        }
        else if (normalized.Contains("标高") || normalized.Contains("level"))
        {
            tool = "get_all_levels";
        }
        else if (normalized.Contains("文档") || normalized.Contains("document"))
        {
            tool = "get_document_info";
        }

        if (tool is not null)
        {
            payload = await _dispatcher.ExecuteAsync(_invoker, tool, Json.EmptyObject())
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
            return new
            {
                Type = "agent.chat",
                RequestId = requestId,
                Success = true,
                FinalText = $"简单命令已执行：{tool}",
                Tool = tool,
                Payload = payload,
                DurationMs = stopwatch.Elapsed.TotalMilliseconds,
                ToolResults = Array.Empty<object>(),
            };
        }

        return new
        {
            Type = "agent.chat",
            RequestId = requestId,
            Success = true,
            FinalText = "复杂绘图任务请生成结构化 drawing plan，并调用 apply_drawing_plan 一次提交。",
            Tool = (string?)null,
            Payload = (object?)null,
            DurationMs = stopwatch.Elapsed.TotalMilliseconds,
            ToolResults = Array.Empty<object>(),
        };
    }

    private object BuildAgentStatus(string? requestId, double durationMs)
    {
        var catalog = _dispatcher.Catalog(includeSchema: true);
        return new
        {
            Type = "agent.status",
            RequestId = requestId,
            Success = true,
            Status = new
            {
                ProtocolVersion = BridgeProtocol.Version,
                HostId = PipeName,
                Capabilities = new[] { "tools.list", "request.cancel", "request.status", "document.binding", "request.deduplication" },
                Configured = false,
                ToolsReady = true,
                Model = "deterministic-revitai-bridge",
                HistoryMessages = 0,
                SessionId = Convert.ToHexString(
                    System.Security.Cryptography.RandomNumberGenerator.GetBytes(16)).ToLowerInvariant(),
                SessionTitle = "ReVitAI Bridge",
                Rounds = 0,
                Build = "v2.1.0-dual-host-headless",
                ToolCount = catalog.GetType().GetProperty("Count")!.GetValue(catalog),
                ToolSets = _dispatcher.Tools.Select(t => t.Category).Distinct().OrderBy(t => t).ToArray(),
                Tools = catalog.GetType().GetProperty("Tools")!.GetValue(catalog),
            },
            DurationMs = durationMs,
        };
    }

    private static object ErrorResponse(
        string? tool,
        string? requestId,
        string errorCode,
        string errorMessage,
        double durationMs = 0)
    {
        return new
        {
            Type = "result",
            ProtocolVersion = BridgeProtocol.Version,
            State = errorCode == "cancelled" ? "cancelled" : "failed",
            RequestId = requestId,
            Success = false,
            Tool = tool,
            DurationMs = durationMs,
            Payload = new { Error = errorMessage },
            ErrorCode = errorCode,
            ErrorMessage = errorMessage,
        };
    }

    private void WriteDiscovery()
    {
        var directory = Path.GetDirectoryName(_discoveryPath)!;
        Directory.CreateDirectory(directory);
        WriteDiscoveryFile(_instanceDiscoveryPath);

        if (ShouldPreservePrimaryDiscovery())
        {
            BridgeLog.Write(
                $"Primary ReVitAI discovery is owned by another live Revit process. Instance file: {_instanceDiscoveryPath}");
            return;
        }

        WriteDiscoveryFile(_discoveryPath);
    }

    private void WriteDiscoveryFile(string path)
    {
        var temporaryPath = path + ".tmp";
        var json = JsonSerializer.Serialize(new
        {
            PipeName,
            ProcessId = Environment.ProcessId,
            StartedAt = DateTimeOffset.UtcNow.ToString("O"),
            UpdatedAt = DateTimeOffset.UtcNow.ToString("O"),
            Server = "ReVitAI.Bridge",
            Version = "3.0.0",
            ProtocolVersion = BridgeProtocol.Version,
            HostId = PipeName,
            DocumentGuid = _lastDocumentGuid,
            DocumentTitle = _lastDocumentTitle,
            DocumentPath = _lastDocumentPath,
        }, Json.Options);
        File.WriteAllText(temporaryPath, json, new UTF8Encoding(false));
        File.Move(temporaryPath, path, overwrite: true);
    }

    private bool ShouldPreservePrimaryDiscovery()
    {
        try
        {
            if (!File.Exists(_discoveryPath))
            {
                return false;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(_discoveryPath));
            var root = document.RootElement;
            if (Json.String(root, "pipeName") == PipeName)
            {
                return false;
            }

            if (!root.TryGetProperty("processId", out var processIdElement)
                || !processIdElement.TryGetInt32(out var processId)
                || processId == Environment.ProcessId)
            {
                return File.Exists(_discoveryPath);
            }

            using var process = Process.GetProcessById(processId);
            return !process.HasExited;
        }
        catch
        {
            return false;
        }
    }

    private void UpdateDiscovery(object? payload)
    {
        var documentGuid = ReadPropertyString(payload, "DocumentGuid");
        var title = ReadPropertyString(payload, "Title");
        var path = ReadPropertyString(payload, "Path");
        if (string.IsNullOrWhiteSpace(documentGuid)
            && string.IsNullOrWhiteSpace(title)
            && string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        _lastDocumentGuid = documentGuid ?? _lastDocumentGuid;
        _lastDocumentTitle = title ?? _lastDocumentTitle;
        _lastDocumentPath = path ?? _lastDocumentPath;
        WriteDiscovery();
    }

    private static string? ReadPropertyString(object? value, string propertyName)
    {
        return value?.GetType().GetProperty(propertyName)?.GetValue(value)?.ToString();
    }

    private static string? StringAny(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            var value = Json.String(element, name);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return value;
            }
        }

        return null;
    }

    private void TryRemoveDiscovery()
    {
        TryRemoveDiscoveryFile(_instanceDiscoveryPath);
        TryRemoveDiscoveryFile(_discoveryPath);
    }

    private void TryRemoveDiscoveryFile(string path)
    {
        try
        {
            if (!File.Exists(path))
            {
                return;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (Json.String(document.RootElement, "pipeName") == PipeName)
            {
                File.Delete(path);
            }
        }
        catch (Exception ex)
        {
            BridgeLog.Write($"Discovery cleanup error for '{path}': {ex.Message}");
        }
    }
}
