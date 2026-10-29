using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;
using NextChats.Core.Services;

namespace NextChats.Api.Controllers;

/// <summary>管理端：沉浸式工具栏维护（唯一标识/图标/名称/描述/外部 API 端点与授权头/启用 + 角色绑定）</summary>
[Route("api/admin/tools")]
public sealed class AdminToolController(IAdminStore store, IConfigStore config, IAuditLogger audit, ISecurityService security) : AdminControllerBase
{
    public sealed record ToolInput(string ToolKey, string Name, string? Icon, string? Description, string? BaseUrl, string? HeadersJson, bool Enabled, int SortOrder, Guid[]? RoleIds);

    /// <summary>http_fetch 白名单请求体（域名数组；自动规范化：trim / 去前导 * . / 小写 / 去重）</summary>
    public sealed record HttpFetchAllowHostsInput(string[]? AllowHosts);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var tools = await store.ListToolsAsync();
        return Ok(tools.Select(x => new
        {
            x.Id, x.ToolKey, x.Name, x.Icon, x.Description, x.BaseUrl, x.SortOrder, x.Enabled, x.CreatedAt, x.UpdatedAt,
            // 授权头含敏感值：展示时解密后整体脱敏（仅管理端可见掩码，不落地明文）
            headersMasked = x.IsHeadersEncrypted ? LogSanitizer.MaskHeaders(security.DecryptSecret(x.HeadersJson!)) : LogSanitizer.MaskHeaders(x.HeadersJson),
            roleIds = x.AllowedRoles.Select(r => r.Id).ToList(),
            roleNames = x.AllowedRoles.Select(r => r.Name).ToList(),
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ToolInput input)
    {
        var error = await ValidateAsync(input, currentId: null);
        if (error is not null) return BadRequest(Err(error));
        if (HeaderJsonInvalid(input.HeadersJson)) return BadRequest(Err("TOOL_HEADERS_INVALID"));
        var (headersJson, headersEncrypted) = EncryptHeaders(input.HeadersJson) ?? ((string?)null, false);
        var saved = await store.SaveToolAsync(
            Guid.Empty, input.ToolKey.Trim(), input.Name.Trim(), (input.Icon ?? "puzzle").Trim(),
            string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            string.IsNullOrWhiteSpace(input.BaseUrl) ? null : input.BaseUrl.Trim(),
            headersJson, headersEncrypted,
            input.Enabled, input.SortOrder, input.RoleIds ?? [], HttpContext.RequestAborted);
        await audit.RecordAsync(AuditCategory.Admin, "TOOL.CREATE", $"trc_{Guid.NewGuid():N}"[..24], UserId,
            detail: new { toolKey = saved.ToolKey, name = saved.Name, sortOrder = saved.SortOrder });
        return Ok(saved.Id);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ToolInput input)
    {
        var error = await ValidateAsync(input, currentId: id);
        if (error is not null) return BadRequest(Err(error));
        if (HeaderJsonInvalid(input.HeadersJson)) return BadRequest(Err("TOOL_HEADERS_INVALID"));

        // 编辑时授权头留空 = 保持不变（复用已加密值）；填写则整体重新加密替换
        var existing = await store.GetToolByKeyAsync(input.ToolKey.Trim(), HttpContext.RequestAborted);
        var (headersJson, headersEncrypted) = EncryptHeaders(input.HeadersJson)
            ?? (existing is { Id: var eid } && eid == id
                ? (existing.HeadersJson, existing.IsHeadersEncrypted)
                : ((string?)null, false));
        try
        {
            await store.SaveToolAsync(
                id, input.ToolKey.Trim(), input.Name.Trim(), (input.Icon ?? "puzzle").Trim(),
                string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
                string.IsNullOrWhiteSpace(input.BaseUrl) ? null : input.BaseUrl.Trim(),
                headersJson, headersEncrypted,
                input.Enabled, input.SortOrder, input.RoleIds ?? [], HttpContext.RequestAborted);
            await audit.RecordAsync(AuditCategory.Admin, "TOOL.UPDATE", $"trc_{Guid.NewGuid():N}"[..24], UserId,
                detail: new { toolKey = input.ToolKey.Trim(), sortOrder = input.SortOrder });
            return NoContent();
        }
        catch (KeyNotFoundException)
        {
            return NotFound(Err("TOOL_CONFIG_NOT_FOUND"));
        }
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await store.DeleteToolAsync(id, HttpContext.RequestAborted);
        await audit.RecordAsync(AuditCategory.Admin, "TOOL.DELETE", $"trc_{Guid.NewGuid():N}"[..24], UserId, id.ToString());
        return NoContent();
    }

    // ---------- 内置工具 · http_fetch 域名白名单（系统设置持久化，运行时即时生效） ----------

    /// <summary>当前生效的白名单（系统设置优先，未配置时为空数组 —— 端面展示默认来源由前端取 appsettings 文案）</summary>
    [HttpGet("http-fetch-allow-hosts")]
    public async Task<IActionResult> GetHttpFetchAllowHosts()
    {
        var raw = await config.GetSystemSettingAsync(SystemSettingKeys.HttpFetchAllowHosts, HttpContext.RequestAborted);
        return Ok(new { allowHosts = ParseHosts(raw), fromDb = !string.IsNullOrWhiteSpace(raw) });
    }

    /// <summary>保存白名单（自动规范化：trim / 去前导 * 与 . / 小写 / 去空 / 去重；空列表拒绝保存）</summary>
    [HttpPut("http-fetch-allow-hosts")]
    public async Task<IActionResult> SetHttpFetchAllowHosts([FromBody] HttpFetchAllowHostsInput input)
    {
        var hosts = (input.AllowHosts ?? [])
            .Select(h => h?.Trim().TrimStart('*', '.').Trim().ToLowerInvariant())
            .Where(h => !string.IsNullOrWhiteSpace(h))
            .Distinct(StringComparer.Ordinal)
            .ToList();
        if (hosts.Count == 0) return BadRequest(Err("HTTP_FETCH_ALLOW_EMPTY"));
        await config.SetSystemSettingAsync(SystemSettingKeys.HttpFetchAllowHosts, JsonSerializer.Serialize(hosts), HttpContext.RequestAborted);
        await audit.RecordAsync(AuditCategory.Admin, "TOOL.HTTP_FETCH_ALLOWLIST", $"trc_{Guid.NewGuid():N}"[..24], UserId,
            detail: new { count = hosts.Count, hosts });
        return NoContent();
    }

    /// <summary>解析白名单 JSON（非法/空 → 空数组）</summary>
    private static string[] ParseHosts(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return [];
        try
        {
            return JsonSerializer.Deserialize<string[]>(raw) ?? [];
        }
        catch (JsonException)
        {
            return [];
        }
    }

    /// <summary>授权头非空时整体 AES-GCM 加密；空/未填返回 null（调用方按「编辑留空 = 保持原值」处理）</summary>
    private (string? encrypted, bool isEncrypted)? EncryptHeaders(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return null;
        return (security.EncryptSecret(headersJson), true);
    }

    /// <summary>授权头 JSON 非法（空 / 空白视为未填，合法）。与 MCP 同口径：JsonNode.Parse 能否解析为对象</summary>
    private static bool HeaderJsonInvalid(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return false;
        try
        {
            return System.Text.Json.Nodes.JsonNode.Parse(headersJson) is not System.Text.Json.Nodes.JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return true;
        }
    }

    /// <summary>必填校验 + 唯一标识查重（排除自身）</summary>
    private async Task<string?> ValidateAsync(ToolInput input, Guid? currentId)
    {
        if (string.IsNullOrWhiteSpace(input.ToolKey) || string.IsNullOrWhiteSpace(input.Name)) return "TOOL_FIELDS_REQUIRED";
        var existing = await store.GetToolByKeyAsync(input.ToolKey.Trim(), HttpContext.RequestAborted);
        if (existing is not null && existing.Id != currentId) return "TOOL_KEY_EXISTS";
        return null;
    }
}
