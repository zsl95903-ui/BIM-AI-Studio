using System;
using System.Net.Http;
using System.Threading.Tasks;
using RevitAi.Abstractions.Common;
using RevitAi.Core.AI;
using RevitAi.Core.Security;

namespace RevitAi.Core.Authentication;

/// <summary>
/// Offline adapter. Performs no network calls and returns neutral defaults,
/// so hosts can run without any account or remote configuration service.
/// </summary>
public sealed class NullRemoteProfileClient : IRemoteProfileClient
{
    private readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public string BaseUrl => string.Empty;

    public HttpClient HttpClient => _httpClient;

    public Task WarmUpConnectionAsync() => Task.CompletedTask;

    public Task<Result<EncryptedApiKeyRecord>> GetEncryptedApiKeyAsync(string apiUrl)
        => Task.FromResult(Result<EncryptedApiKeyRecord>.Failure("Remote profile service is not configured."));

    public Task<Result<EncryptedApiKeyRecord>> GetEncryptedApiKeyByNotesAsync(string notes)
        => Task.FromResult(Result<EncryptedApiKeyRecord>.Failure("Remote profile service is not configured."));

    public Task<Result<AIConfig>> GetDefaultAIConfigAsync()
        => Task.FromResult(Result<AIConfig>.Failure("Remote profile service is not configured."));

    public Task<Result<int>> GetUnreadReplyCountAsync(string deviceId, Guid? userId = null)
        => Task.FromResult(Result<int>.Success(0));

    public void Dispose() => _httpClient.Dispose();
}
