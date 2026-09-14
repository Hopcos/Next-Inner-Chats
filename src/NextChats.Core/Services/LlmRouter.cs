using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NextChats.Core.Abstractions;
using NextChats.Core.Clients;
using NextChats.Core.Configuration;
using NextChats.Core.Domain;
using NextChats.Core.Entities;
using NextChats.Core.Localization;

namespace NextChats.Core.Services;

/// <summary>
/// LLM Router 规则引擎：
///  规则 = 启用开关 → 健康度 → 优先级(小优先) → 同优先级轮询（负载均衡）；
///  调用失败自动故障转移到下一个可用供应商并标记不健康（熔断）。
/// </summary>
public sealed class LlmRouter : ILlmRouter
{
    private readonly IConfigStore _store;
    private readonly IAdminStore _admin;
    private readonly IHttpClientProvider _http;
    private readonly ILogger _logger;
    private readonly IOptions<SecurityOptions> _security;
    private readonly IOptions<LlmConcurrencyOptions> _concurrency;
    private readonly ISecurityService _securityService;
    private readonly object _lock = new();
    private readonly Dictionary<Guid, int> _roundRobin = [];

    public LlmRouter(
        IConfigStore store,
        IAdminStore admin,
        IHttpClientProvider http,
        IOptions<SecurityOptions> security,
        ISecurityService securityService,
        IOptions<LlmConcurrencyOptions> concurrency,
        ILogger<LlmRouter> logger)
    {
        _store = store;
        _admin = admin;
        _http = http;
        _security = security;
        _securityService = securityService;
        _concurrency = concurrency;
        _logger = logger;
    }

    public async Task<LlmProvider> SelectAsync(Guid? preferredId = null, CancellationToken ct = default)
    {
        var providers = await _store.GetActiveProvidersAsync(ct);
        var pool = providers.Where(p => p.Enabled && p.IsHealthy).OrderBy(p => p.Priority).ToList();
        if (pool.Count == 0)
        {
            throw new LlmUnavailableException();
        }

        if (preferredId.HasValue)
        {
            var preferred = pool.FirstOrDefault(p => p.Id == preferredId.Value);
            if (preferred is not null) return preferred;
        }

        var topPriority = pool[0].Priority;
        var candidates = pool.Where(p => p.Priority == topPriority).ToList();
        LlmProvider selected;
        lock (_lock)
        {
            var idx = _roundRobin.TryGetValue(Guid.Empty, out var rr) ? rr : 0;
            selected = candidates[idx % candidates.Count];
            _roundRobin[Guid.Empty] = idx + 1;
        }
        return selected;
    }

    public async Task<ILlmClient> SelectClientAsync(Guid? preferredId = null, Guid? preferredModelId = null, string? lang = null, CancellationToken ct = default, Guid[]? allowedModelIds = null)
    {
        var provider = await SelectAsync(preferredId, ct);
        try
        {
            return CreateClient(provider, SelectModel(provider, preferredModelId, allowedModelIds), lang);
        }
        catch (OperationCanceledException)
        {
            throw; // 取消不应触发故障转移
        }
        catch
        {
            // 首选供应商客户端构建失败 → 故障转移
            var pool = (await _store.GetActiveProvidersAsync(ct)).Where(p => p.Enabled && p.IsHealthy && p.Id != provider.Id).ToList();
            foreach (var fallback in pool.OrderBy(p => p.Priority))
            {
                try
                {
                    return CreateClient(fallback, SelectModel(fallback, null, allowedModelIds), lang);
                }
                catch
                {
                    // 继续尝试下一个
                }
            }
            throw new LlmUnavailableException();
        }
    }

    public async Task<ILlmClient> GetClientAsync(Guid providerId, Guid? modelId = null, CancellationToken ct = default)
    {
        var provider = await _store.GetProviderAsync(providerId, ct);
        if (provider is null || !provider.Enabled)
        {
            throw new LlmUnavailableException($"LLM provider not found or disabled (id={providerId})");
        }
        return CreateClient(provider, SelectModel(provider, modelId));
    }

    /// <summary>供应商内选择模型：首选模型 → 启用模型按优先级（小优先）→ 无可用模型抛异常；
    /// allowedModelIds 非空时为角色绑定白名单（未授权模型不可选，首选不在白名单内则回落白名单内优先级最高的启用模型）</summary>
    private static LlmModel SelectModel(LlmProvider provider, Guid? preferredModelId, Guid[]? allowedModelIds = null)
    {
        var pool = provider.Models.Where(m => m.Enabled).OrderBy(m => m.Priority).ToList();
        if (allowedModelIds is { Length: > 0 })
        {
            pool = pool.Where(m => allowedModelIds.Contains(m.Id)).ToList();
        }
        if (pool.Count == 0)
        {
            throw new LlmUnavailableException($"provider '{provider.Name}' has no enabled model for current role");
        }
        if (preferredModelId.HasValue)
        {
            var preferred = pool.FirstOrDefault(m => m.Id == preferredModelId.Value);
            if (preferred is not null) return preferred;
        }
        return pool[0];
    }

    private ILlmClient CreateClient(LlmProvider provider, LlmModel model, string? lang = null)
    {
        var logger = _logger;
        return provider.Kind switch
        {
            LlmProviderKind.OpenAiCompatible => new OpenAiCompatibleLlmClient(provider, model.Name, _http.Create("llm"), _securityService, logger, _concurrency.Value.MaxConcurrentStreams),
            LlmProviderKind.Mock => new MockLlmClient(provider, model.Name, logger, lang),
            _ => throw new NotSupportedException($"Unsupported LLM provider kind: {provider.Kind}"),
        };
    }

    public async Task MarkUnhealthyAsync(Guid providerId, string error, CancellationToken ct = default)
    {
        var provider = await _admin.GetProviderAsync(providerId, ct);
        if (provider is null) return;
        provider.IsHealthy = false;
        provider.LastError = error[..Math.Min(250, error.Length)];
        provider.UpdatedAt = DateTimeOffset.UtcNow;
        await _admin.UpdateProviderAsync(provider, ct);
        await _store.InvalidateConfigCacheAsync(ct);
        _logger.LogWarning("LLM 供应商 {Provider} 标记为不健康: {Error}", provider.Name, error);
    }

    public async Task<(bool Ok, string? Error, int LatencyMs, string? ErrCode)> PingAsync(Guid providerId, CancellationToken ct = default)
    {
        var provider = await _store.GetProviderAsync(providerId, ct);
        if (provider is null) return (false, Texts.Get("PROVIDER_NOT_FOUND", "en"), 0, "PROVIDER_NOT_FOUND");
        try
        {
            var client = CreateClient(provider, SelectModel(provider, null));
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var result = await client.CompleteAsync(new LlmRequest
            {
                Messages = [LlmChatMessage.User("ping")],
                Stream = false,
                MaxTokens = 8,
            }, ct);
            sw.Stop();
            if (provider.IsHealthy is false)
            {
                provider.IsHealthy = true;
                provider.LastError = null;
                provider.UpdatedAt = DateTimeOffset.UtcNow;
                await _admin.UpdateProviderAsync(provider, ct);
                await _store.InvalidateConfigCacheAsync(ct);
            }
            return (true, null, (int)sw.ElapsedMilliseconds, null);
        }
        catch (Exception ex)
        {
            var msg = ex is LlmHttpException http ? $"HTTP {http.StatusCode}: {http.Message}" : ex.Message;
            var code = ex is LlmHttpException { StatusCode: 401 or 403 } ? "LLM_AUTH_FAILED" : "LLM_UNREACHABLE";
            await MarkUnhealthyAsync(providerId, msg, ct);
            return (false, msg, 0, code);
        }
    }
}

/// <summary>无可用 LLM 供应商（唯一会让用户看到"服务不可用"的场景）</summary>
public sealed class LlmUnavailableException : Exception
{
    public LlmUnavailableException(string? message = null) : base(message ?? Texts.Get("LLM_UNAVAILABLE", "en"))
    {
    }
}