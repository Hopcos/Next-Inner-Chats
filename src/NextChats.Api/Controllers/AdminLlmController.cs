using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NextChats.Core.Abstractions;
using NextChats.Core.Entities;

namespace NextChats.Api.Controllers;

/// <summary>管理端：LLM 供应商配置（基础信息 → “获取模型”自动带出 → 模型独立配置：视觉/上下文/成本/启用）</summary>
[Route("api/admin/llm-providers")]
public sealed class AdminLlmController(
    IAdminStore store,
    ISecurityService security,
    ILlmRouter router,
    IConfigStore config,
    IHttpClientProvider http,
    IAuditLogger audit) : AdminControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var list = await store.ListProvidersAsync();
        return Ok(list.Select(p => new
        {
            p.Id, p.Name, kind = p.Kind.ToString(), p.BaseUrl, p.TimeoutSeconds, p.Enabled, p.Priority, p.IsHealthy,
            p.LastError, apiKeyMasked = security.MaskApiKey(p.ApiKeyEncrypted), p.CreatedAt, p.UpdatedAt,
            p.ThinkingParam,
            models = p.Models.OrderBy(m => m.Priority).Select(ModelDto),
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var p = await store.GetProviderAsync(id);
        if (p is null) return NotFound(Err("PROVIDER_NOT_FOUND"));
        return Ok(new
        {
            p.Id, p.Name, kind = p.Kind.ToString(), p.BaseUrl, p.TimeoutSeconds, p.Enabled, p.Priority, p.IsHealthy,
            p.LastError, apiKeyMasked = security.MaskApiKey(p.ApiKeyEncrypted), p.CreatedAt, p.UpdatedAt,
            p.ThinkingParam,
            models = p.Models.OrderBy(m => m.Priority).Select(ModelDto),
        });
    }

    public sealed record ProviderInput(string Name, string Kind, string? BaseUrl, string? ApiKey, int? TimeoutSeconds, bool Enabled, int? Priority, string? ThinkingParam);

    public sealed record ModelInput(
        string Name, bool? Enabled, bool? IsVision, int? ContextWindow,
        decimal? PriceInPer1K, decimal? PriceOutPer1K, int? Priority);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ProviderInput input)
    {
        var provider = Map(input, new LlmProvider());
        provider.ApiKeyEncrypted = string.IsNullOrWhiteSpace(input.ApiKey) ? null : security.EncryptSecret(input.ApiKey.Trim());
        var saved = await store.CreateProviderAsync(provider);
        await config.InvalidateConfigCacheAsync();
        await audit.RecordAsync(NextChats.Core.Domain.AuditCategory.Config, "LLM_PROVIDER.CREATE", $"trc_{Guid.NewGuid():N}"[..24], UserId, saved.Name);
        return Ok(saved.Id);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ProviderInput input)
    {
        var row = await store.GetProviderAsync(id);
        if (row is null) return NotFound(Err("PROVIDER_NOT_FOUND"));
        row = Map(input, row);
        if (!string.IsNullOrWhiteSpace(input.ApiKey)) row.ApiKeyEncrypted = security.EncryptSecret(input.ApiKey.Trim());
        await store.UpdateProviderAsync(row);
        await config.InvalidateConfigCacheAsync();
        await audit.RecordAsync(NextChats.Core.Domain.AuditCategory.Config, "LLM_PROVIDER.UPDATE", $"trc_{Guid.NewGuid():N}"[..24], UserId, row.Name);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await store.DeleteProviderAsync(id);
        await config.InvalidateConfigCacheAsync();
        await audit.RecordAsync(NextChats.Core.Domain.AuditCategory.Config, "LLM_PROVIDER.DELETE", $"trc_{Guid.NewGuid():N}"[..24], UserId, id.ToString());
        return NoContent();
    }

    // ---------------- 模型子资源（供应商下的模型，独立配置） ----------------

    [HttpPost("{providerId:guid}/models")]
    public async Task<IActionResult> AddModel(Guid providerId, [FromBody] ModelInput input)
    {
        var provider = await store.GetProviderAsync(providerId);
        if (provider is null) return NotFound(Err("PROVIDER_NOT_FOUND"));
        var model = new LlmModel
        {
            ProviderId = providerId,
            Name = input.Name,
            Enabled = input.Enabled ?? true,
            IsVision = input.IsVision ?? false,
            ContextWindow = input.ContextWindow ?? 128_000,
            PriceInPer1K = input.PriceInPer1K ?? 0,
            PriceOutPer1K = input.PriceOutPer1K ?? 0,
            Priority = input.Priority ?? (provider.Models.Count == 0 ? 1 : provider.Models.Max(m => m.Priority) + 1),
        };
        var saved = await store.AddModelAsync(model);
        await config.InvalidateConfigCacheAsync();
        await audit.RecordAsync(NextChats.Core.Domain.AuditCategory.Config, "LLM_MODEL.CREATE", $"trc_{Guid.NewGuid():N}"[..24], UserId, saved.Name);
        return Ok(saved.Id);
    }

    [HttpPut("models/{modelId:guid}")]
    public async Task<IActionResult> UpdateModel(Guid modelId, [FromBody] ModelInput input)
    {
        var row = await store.GetModelAsync(modelId);
        if (row is null) return NotFound(Err("MODEL_NOT_FOUND"));
        row.Name = input.Name;
        row.Enabled = input.Enabled ?? row.Enabled;
        row.IsVision = input.IsVision ?? row.IsVision;
        row.ContextWindow = input.ContextWindow ?? row.ContextWindow;
        row.PriceInPer1K = input.PriceInPer1K ?? row.PriceInPer1K;
        row.PriceOutPer1K = input.PriceOutPer1K ?? row.PriceOutPer1K;
        row.Priority = input.Priority ?? row.Priority;
        await store.UpdateModelAsync(row);
        await config.InvalidateConfigCacheAsync();
        await audit.RecordAsync(NextChats.Core.Domain.AuditCategory.Config, "LLM_MODEL.UPDATE", $"trc_{Guid.NewGuid():N}"[..24], UserId, row.Name);
        return NoContent();
    }

    [HttpDelete("models/{modelId:guid}")]
    public async Task<IActionResult> DeleteModel(Guid modelId)
    {
        await store.DeleteModelAsync(modelId);
        await config.InvalidateConfigCacheAsync();
        await audit.RecordAsync(NextChats.Core.Domain.AuditCategory.Config, "LLM_MODEL.DELETE", $"trc_{Guid.NewGuid():N}"[..24], UserId, modelId.ToString());
        return NoContent();
    }

    /// <summary>获取模型：自动带出供应商的所有模型（OpenAI 兼容 GET {BaseUrl}/models；Mock 类型直接返回内置演示模型）</summary>
    [HttpPost("{providerId:guid}/fetch-models")]
    public async Task<IActionResult> FetchModels(Guid providerId)
    {
        var provider = await store.GetProviderAsync(providerId);
        if (provider is null) return NotFound(Err("PROVIDER_NOT_FOUND"));

        IReadOnlyList<string> names;
        if (provider.Kind == NextChats.Core.Domain.LlmProviderKind.Mock)
        {
            // 演示：无真实 /models 端点，直接内置两个模型演示“自动带出”
            names = ["Mock Demo Model", "Mock Fast Mini"];
        }
        else
        {
            try
            {
                names = await FetchOpenAiModelNamesAsync(provider);
            }
            catch (Exception ex)
            {
                var msg = ex is LlmFetchException lfe ? lfe.Message : ex.Message;
                return BadRequest(Err("LLM_FETCH_MODELS_FAILED", msg));
            }
        }

        var existing = new HashSet<string>(provider.Models.Select(m => m.Name), StringComparer.Ordinal);
        var added = new List<string>();
        var renamed = new List<object>(); // { old, name }：上游模型名大小写与原库不一致时原地对齐（严格区分大小写）
        var nextPriority = provider.Models.Count == 0 ? 1 : provider.Models.Max(m => m.Priority) + 1;
        foreach (var name in names.Distinct(StringComparer.Ordinal))
        {
            var exact = provider.Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.Ordinal));
            if (exact is not null) continue;
            // 大小写不一致的存量行：以上游精确名为准原地更新（new-api 渠道按模型名严格区分大小写，
            // 名称对不上渠道就会 503「无可用渠道」；OrdinalIgnoreCase 去重会让错误大小写永远得不到纠正）。
            var stale = provider.Models.FirstOrDefault(m => string.Equals(m.Name, name, StringComparison.OrdinalIgnoreCase));
            if (stale is not null)
            {
                var oldName = stale.Name;
                stale.Name = name;
                stale.UpdatedAt = DateTimeOffset.UtcNow;
                await store.UpdateModelAsync(stale);
                renamed.Add(new { old = oldName, name });
                continue;
            }
            existing.Add(name);
            await store.AddModelAsync(new LlmModel
            {
                ProviderId = providerId,
                Name = name,
                Enabled = true,
                ContextWindow = 128_000,
                Priority = nextPriority++,
            });
            added.Add(name);
        }
        await config.InvalidateConfigCacheAsync();
        await audit.RecordAsync(NextChats.Core.Domain.AuditCategory.Config, "LLM_MODEL.FETCH", $"trc_{Guid.NewGuid():N}"[..24], UserId, provider.Name,
            detail: new { added, renamed });
        var fresh = await store.GetProviderAsync(providerId);
        return Ok(new
        {
            added,
            renamed,
            models = fresh?.Models.OrderBy(m => m.Priority).Select(ModelDto) ?? [],
        });
    }

    /// <summary>健康检查 / 路由 Ping（失败自动熔断）</summary>
    [HttpPost("{id:guid}/ping")]
    public async Task<IActionResult> Ping(Guid id)
    {
        var (ok, error, latency, code) = await router.PingAsync(id);
        return ok ? Ok(new { ok, latencyMs = latency }) : BadRequest(Err(code ?? "LLM_UNREACHABLE", error ?? ""));
    }

    // ---------------- helpers ----------------

    private static object ModelDto(LlmModel m) => new
    {
        m.Id, m.Name, m.Enabled, m.IsVision, m.ContextWindow, m.PriceInPer1K, m.PriceOutPer1K, m.Priority, m.CreatedAt, m.UpdatedAt,
    };

    private async Task<IReadOnlyList<string>> FetchOpenAiModelNamesAsync(LlmProvider provider)
    {
        var client = http.Create("llm");
        var baseUrl = (provider.BaseUrl ?? "").TrimEnd('/');
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
        {
            baseUrl = baseUrl[..baseUrl.LastIndexOf('/')];
        }
        using var req = new HttpRequestMessage(HttpMethod.Get, $"{baseUrl}/models");
        var key = security.DecryptSecret(provider.ApiKeyEncrypted);
        if (!string.IsNullOrWhiteSpace(key))
        {
            req.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", key);
        }
        using var resp = await client.SendAsync(req);
        var body = await resp.Content.ReadAsStringAsync();
        if (!resp.IsSuccessStatusCode)
        {
            throw new LlmFetchException($"HTTP {(int)resp.StatusCode}: {NextChats.Core.Services.LogSanitizer.MaskUri(body[..Math.Min(200, body.Length)])}");
        }
        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;
        if (!root.TryGetProperty("data", out var data)) return [];
        return data.EnumerateArray()
            .Select(e => e.TryGetProperty("id", out var id) && id.ValueKind == JsonValueKind.String ? id.GetString()! : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
    }

    private static LlmProvider Map(ProviderInput input, LlmProvider p)
    {
        p.Name = input.Name;
        p.Kind = Enum.TryParse<NextChats.Core.Domain.LlmProviderKind>(input.Kind, true, out var kind) ? kind : NextChats.Core.Domain.LlmProviderKind.OpenAiCompatible;
        p.BaseUrl = input.BaseUrl?.TrimEnd('/');
        p.TimeoutSeconds = input.TimeoutSeconds ?? 120;
        p.Enabled = input.Enabled;
        p.Priority = input.Priority ?? 100;
        if (!string.IsNullOrWhiteSpace(input.ThinkingParam)) p.ThinkingParam = input.ThinkingParam;
        p.IsHealthy = true;
        p.LastError = null;
        p.UpdatedAt = DateTimeOffset.UtcNow;
        return p;
    }
}

/// <summary>获取模型列表失败（OpenAI 兼容 /models 拉取异常）</summary>
public sealed class LlmFetchException(string message) : Exception(message);
