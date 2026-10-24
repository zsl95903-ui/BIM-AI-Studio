using System.Text.Json.Nodes;
using BimAi.Protocol;

var passed = 0;
void Check(bool condition, string message) { if (!condition) throw new Exception(message); passed++; }
void Invalid(string json, string schema)
{
    try { BridgeProtocol.Validate(JsonNode.Parse(json), JsonNode.Parse(schema)); throw new Exception("Expected rejection: " + json); }
    catch (ArgumentException) { passed++; }
}
var schema = """{"type":"object","properties":{"height":{"type":"number","minimum":1},"ids":{"type":"array","items":{"type":"integer"}},"point":{"type":"object","required":["x"],"properties":{"x":{"type":"number"}}}},"required":["height"],"additionalProperties":false}""";
BridgeProtocol.Validate(JsonNode.Parse("""{"height":3,"ids":[1,2],"point":{"x":5}}"""), JsonNode.Parse(schema)); passed++;
Invalid("{}", schema); Invalid("""{"height":0}""", schema); Invalid("""{"height":3,"ids":[1.5]}""",schema); Invalid("""{"height":3,"point":{}}""",schema); Invalid("""{"height":3,"extra":1}""",schema);
Invalid("\"bad\"", """{"anyOf":[{"type":"number"},{"type":"array","items":{"type":"number"}}]}""");
BridgeProtocol.ValidateTarget("document", "DOCUMENT"); passed++;
try { BridgeProtocol.ValidateTarget("document-a", "document-b"); throw new Exception("Wrong document accepted"); }
catch (InvalidOperationException) { passed++; }
try { BridgeProtocol.ValidateTarget("document-a", null); throw new Exception("Closed document accepted"); }
catch (InvalidOperationException) { passed++; }
BridgeProtocol.RequireCommitted("Committed"); passed++;
foreach (var state in new[] { "RolledBack", "Pending" })
{
    try { BridgeProtocol.RequireCommitted(state); throw new Exception("Failed transaction accepted"); }
    catch (InvalidOperationException) { passed++; }
}
var journal = new RequestJournal();
var executions = 0;
var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
async Task<JsonObject> Action(CancellationToken token) { Interlocked.Increment(ref executions); await release.Task.WaitAsync(token); return new() { ["success"] = true, ["state"] = "completed", ["payload"] = new JsonObject { ["id"] = 42 } }; }
var first = journal.ExecuteAsync("same", "payload", Action, default);
var duplicate = journal.ExecuteAsync("same", "payload", Action, default);
Check(executions == 1, "Duplicate mutation executed twice");
Check(journal.Status("same")["state"]!.GetValue<string>() == "running", "Missing pending state");
var conflict = await journal.ExecuteAsync("same", "different", Action, default);
Check(conflict["errorCode"]!.GetValue<string>() == "request_conflict", "ID conflict was accepted");
release.SetResult();
var results = await Task.WhenAll(first,duplicate);
Check(JsonNode.DeepEquals(results[0],results[1]), "Duplicate did not replay same result");
results[0]["payload"]!["id"] = 99;
Check(journal.Status("same")["payload"]!["id"]!.GetValue<int>() == 42, "Caller mutated journal result");
var cancelled = journal.ExecuteAsync("cancel", "pending", async token => { await Task.Delay(Timeout.Infinite, token); return new JsonObject(); }, default);
journal.Cancel("cancel");
Check((await cancelled)["errorCode"]!.GetValue<string>() == "cancelled", "Pending request did not cancel");
Check(journal.Status("unknown")["errorCode"]!.GetValue<string>() == "request_not_found", "Unknown outcome lost");
journal.Cancel("early");
var earlyExecuted = false;
var early = await journal.ExecuteAsync("early", "payload", token => { earlyExecuted = true; return Task.FromResult(new JsonObject()); }, default);
Check(!earlyExecuted && early["errorCode"]!.GetValue<string>() == "cancelled", "Early cancellation allowed late execution");
Console.WriteLine($"Protocol: {passed} assertions passed.");
