using System;
using System.Net.Http;
using System.Threading.Tasks;
using RevitAi.Abstractions.Common;
using RevitAi.Core.AI;
using RevitAi.Core.Security;

namespace RevitAi.Core.Authentication;

/// <summary>
/// Optional remote profile service (model defaults, encrypted keys, notifications).
/// The tool layer works without it; an offline adapter is provided by default.
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
