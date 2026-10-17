using System;
using System.Net.Http;
using System.Threading.Tasks;
using RevitAi.Abstractions.Common;
using RevitAi.Core.AI;
using RevitAi.Core.Security;

namespace RevitAi.Core.Authentication;

/// <summary>
/// Remote profile surface used by the AI services. Implementations are optional;
/// the stub below is the default and performs no network calls.
/// </summary>
public interface IRemoteProfileClient : IDisposable
{
    string BaseUrl { get; }

    HttpClient HttpClient { get; }

    Task WarmUpConnectionAsync();

    Task<Result<EncryptedApiKeyRecord>> GetEncryptedApiKeyAsync(string apiUrl);

    Task<Result<EncryptedApiKeyRecord>> GetEncryptedApiKeyByNotesAsync(string notes);

    Task<Result<AIConfig>> GetDefaultAIConfigAsync();

    Task<Result<int>> GetUnreadReplyCountAsync(string deviceId, Guid? userId = null);
}

/// <summary>
/// Minimal stub: no network, no storage, neutral defaults.
/// Callers fall back to local settings when a remote value is unavailable.
/// </summary>
internal sealed class RemoteProfileStub : IRemoteProfileClient
{
    private readonly HttpClient _httpClient = new HttpClient
    {
        Timeout = TimeSpan.FromSeconds(5)
    };

    public string BaseUrl => string.Empty;

    public HttpClient HttpClient => _httpClient;

    public Task WarmUpConnectionAsync() => Task.CompletedTask;

    public Task<Result<EncryptedApiKeyRecord>> GetEncryptedApiKeyAsync(string apiUrl)
        => Task.FromResult(Result<EncryptedApiKeyRecord>.Failure("Remote profile is not configured."));

    public Task<Result<EncryptedApiKeyRecord>> GetEncryptedApiKeyByNotesAsync(string notes)
        => Task.FromResult(Result<EncryptedApiKeyRecord>.Failure("Remote profile is not configured."));

    public Task<Result<AIConfig>> GetDefaultAIConfigAsync()
        => Task.FromResult(Result<AIConfig>.Failure("Remote profile is not configured."));

    public Task<Result<int>> GetUnreadReplyCountAsync(string deviceId, Guid? userId = null)
        => Task.FromResult(Result<int>.Success(0));

    public void Dispose() => _httpClient.Dispose();
}
