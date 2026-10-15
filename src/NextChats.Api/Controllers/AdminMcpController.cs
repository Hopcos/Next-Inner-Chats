using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Mvc;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;
using NextChats.Core.Entities;
using NextChats.Core.Localization;
using NextChats.Core.Services;

namespace NextChats.Api.Controllers;

/// <summary>
/// 管理端：MCP Server 配置。
/// 手工填写基础信息（Name / Endpoint / Header JSON）→ “获取”后自动带出 description/tools/prompts/resources；
/// 可禁用不需要的 tool/prompt/resource。
/// </summary>
[Route("api/admin/mcp-servers")]
public sealed class AdminMcpController(IAdminStore store, IMcpDriver driver, ISecurityService security, IConfigStore config, IAuditLogger audit) : AdminControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var list = await store.ListMcpServersAsync(includeItems: true);
        return Ok(list.Select(m => new
        {
            m.Id, m.Name, transport = m.Transport.ToString(), endpoint = LogSanitizer.MaskUri(m.Endpoint),
            headersMasked = m.IsHeadersEncrypted ? LogSanitizer.MaskHeaders(security.DecryptSecret(m.HeadersJson!)) : LogSanitizer.MaskHeaders(m.HeadersJson),
            m.Enabled, m.IsVision, m.TimeoutSeconds, m.Description, m.Instructions, m.MetadataJson, m.LastError, m.LastFetchedAt,
            toolCount = m.Items.Count(i => i.Kind == McpItemKind.Tool),
            promptCount = m.Items.Count(i => i.Kind == McpItemKind.Prompt),
            resourceCount = m.Items.Count(i => i.Kind == McpItemKind.Resource),
            items = m.Items.Select(ItemDto).ToList(),
        }));
    }

    [HttpGet("{id:guid}")]
    public async Task<IActionResult> Get(Guid id)
    {
        var m = await store.GetMcpServerAsync(id, includeItems: true);
        if (m is null) return NotFound(Err("MCP_SERVER_NOT_FOUND"));
        return Ok(new
        {
            m.Id, m.Name, transport = m.Transport.ToString(), endpoint = LogSanitizer.MaskUri(m.Endpoint),
            headersMasked = m.IsHeadersEncrypted ? LogSanitizer.MaskHeaders(security.DecryptSecret(m.HeadersJson!)) : LogSanitizer.MaskHeaders(m.HeadersJson),
            m.Enabled, m.IsVision, m.TimeoutSeconds, m.Description, m.Instructions, m.MetadataJson, m.LastError, m.LastFetchedAt,
            m.StdioCommand, m.StdioArgsJson,
            items = m.Items.Select(ItemDto).ToList(),
        });
    }

    public sealed record McpInput(
        string Name, string Transport, string? Endpoint, string? HeadersJson, bool Enabled, bool? IsVision,
        int? TimeoutSeconds, string? StdioCommand, string? StdioArgsJson, string? Instructions = null);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] McpInput input)
    {
        if (HasInvalidEncoding(input)) return BadRequest(Err("INVALID_ENCODING"));
        if (HeaderJsonInvalid(input.HeadersJson)) return BadRequest(Err("MCP_HEADERS_INVALID"));
        var server = new McpServer
        {
            Name = input.Name,
            Transport = Enum.TryParse<McpTransportType>(input.Transport, true, out var t) ? t : McpTransportType.Http,
            Endpoint = input.Endpoint,
            Enabled = input.Enabled,
            IsVision = input.IsVision ?? false,
            TimeoutSeconds = input.TimeoutSeconds ?? 60,
            StdioCommand = input.StdioCommand,
            StdioArgsJson = input.StdioArgsJson,
            Instructions = string.IsNullOrWhiteSpace(input.Instructions) ? null : input.Instructions.Trim(),
        };
        EncryptHeaders(server, input.HeadersJson);
        var saved = await store.CreateMcpServerAsync(server);
        await config.InvalidateConfigCacheAsync();
        await audit.RecordAsync(AuditCategory.Config, "MCP.CREATE", $"trc_{Guid.NewGuid():N}"[..24], UserId, saved.Name);
        return Ok(saved.Id);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] McpInput input)
    {
        if (HasInvalidEncoding(input)) return BadRequest(Err("INVALID_ENCODING"));
        if (HeaderJsonInvalid(input.HeadersJson)) return BadRequest(Err("MCP_HEADERS_INVALID"));
        var row = await store.GetMcpServerAsync(id);
        if (row is null) return NotFound(Err("MCP_SERVER_NOT_FOUND"));
        row.Name = input.Name;
        row.Transport = Enum.TryParse<McpTransportType>(input.Transport, true, out var t) ? t : McpTransportType.Http;
        row.Endpoint = input.Endpoint;
        if (!string.IsNullOrWhiteSpace(input.HeadersJson)) EncryptHeaders(row, input.HeadersJson);
        row.Enabled = input.Enabled;
        row.IsVision = input.IsVision ?? row.IsVision;
        row.TimeoutSeconds = input.TimeoutSeconds ?? 60;
        row.StdioCommand = input.StdioCommand;
        row.StdioArgsJson = input.StdioArgsJson;
        if (!string.IsNullOrWhiteSpace(input.Instructions)) row.Instructions = input.Instructions.Trim();
        else if (input.Instructions is not null) row.Instructions = null; // 显式清空
        await store.UpdateMcpServerAsync(row);
        await driver.InvalidateAsync(id);
        await config.InvalidateConfigCacheAsync();
        await audit.RecordAsync(AuditCategory.Config, "MCP.UPDATE", $"trc_{Guid.NewGuid():N}"[..24], UserId, row.Name);
        return NoContent();
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        await store.DeleteMcpServerAsync(id);
        await driver.InvalidateAsync(id);
        await config.InvalidateConfigCacheAsync();
        await audit.RecordAsync(AuditCategory.Config, "MCP.DELETE", $"trc_{Guid.NewGuid():N}"[..24], UserId, id.ToString());
        return NoContent();
    }

    /// <summary>“获取”：连接 MCP 自动带出 description / tools / prompts / resources</summary>
    [HttpPost("{id:guid}/fetch")]
    public async Task<IActionResult> Fetch(Guid id)
    {
        var server = await store.GetMcpServerAsync(id);
        if (server is null) return NotFound(Err("MCP_SERVER_NOT_FOUND"));
        if (!server.Enabled)
        {
            return BadRequest(Err("MCP_DISABLED"));
        }

        try
        {
            var result = await driver.DiscoverAsync(server, HttpContext.RequestAborted);

            server.MetadataJson = new JsonObject
            {
                ["toolCount"] = result.Items.Count(i => i.Kind == McpItemKind.Tool),
                ["promptCount"] = result.Items.Count(i => i.Kind == McpItemKind.Prompt),
                ["resourceCount"] = result.Items.Count(i => i.Kind == McpItemKind.Resource),
                ["fetchedAt"] = DateTimeOffset.UtcNow,
            }.ToJsonString();
            server.LastFetchedAt = DateTimeOffset.UtcNow;
            server.LastError = null;
            if (!string.IsNullOrWhiteSpace(result.Description)) server.Description = result.Description;
            // 服务器 Instructions（系统级使用指南）：获取后自动回填（仍可手工编辑覆盖）
            if (!string.IsNullOrWhiteSpace(result.Instructions)) server.Instructions = result.Instructions.Trim();

            await store.SyncMcpCatalogAsync(id, result.Items);
            await store.UpdateMcpServerAsync(server);
            await driver.InvalidateAsync(id);
            await config.InvalidateConfigCacheAsync();

            await audit.RecordAsync(AuditCategory.Config, "MCP.FETCH", $"trc_{Guid.NewGuid():N}"[..24], UserId, server.Name,
                detail: new { toolCount = result.Items.Count(i => i.Kind == McpItemKind.Tool), promptCount = result.Items.Count(i => i.Kind == McpItemKind.Prompt), resourceCount = result.Items.Count(i => i.Kind == McpItemKind.Resource), hasInstructions = !string.IsNullOrWhiteSpace(result.Instructions) });

            var fresh = await store.GetMcpServerAsync(id, includeItems: true);
            return Ok(new { ok = true, instructions = fresh!.Instructions, items = fresh.Items.OrderBy(i => i.Kind).ThenBy(i => i.Name).Select(ItemDto).ToList() });
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "MCP 元数据获取失败 server={Server}", server.Name);
            // 仅透出脱敏提示（HTTP 状态码 / timeout），不暴露 URL 与响应内容
            var message = Texts.Get("MCP_CONNECT_FAILED", Lang);
            var hint = ConnectHint(ex);
            if (hint is not null) message += $" ({hint})";
            server.LastError = $"{Texts.Get("MCP_FETCH_FAILED", Lang)} {hint ?? string.Empty}".Trim();
            await store.UpdateMcpServerAsync(server);
            return BadRequest(new { code = "MCP_CONNECT_FAILED", message });
        }
    }

    /// <summary>禁用/启用某个 tool / prompt / resource</summary>
    [HttpPut("items/{itemId:guid}/enabled")]
    public async Task<IActionResult> SetItemEnabled(Guid itemId, [FromBody] SetEnabledInput input)
    {
        await store.SetMcpItemEnabledAsync(itemId, input.Enabled);
        await audit.RecordAsync(AuditCategory.Config, "MCP.ITEM_TOGGLE", $"trc_{Guid.NewGuid():N}"[..24], UserId, itemId.ToString(),
            detail: new { enabled = input.Enabled });
        return NoContent();
    }

    public sealed record SetEnabledInput(bool Enabled);

    /// <summary>发送测试 Ping（不保存）</summary>
    [HttpPost("{id:guid}/ping")]
    public async Task<IActionResult> Ping(Guid id)
    {
        var server = await store.GetMcpServerAsync(id);
        if (server is null) return NotFound(Err("MCP_SERVER_NOT_FOUND"));
        var (ok, error, latency) = await driver.PingAsync(server, HttpContext.RequestAborted);
        return ok ? Ok(new { ok, latencyMs = latency }) : BadRequest(new { code = "MCP_UNREACHABLE", message = error ?? Texts.Get("MCP_UNREACHABLE", Lang) });
    }

    private void EncryptHeaders(McpServer server, string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return;
        // Header 可能包含 Bearer Token 等敏感值 → AES-GCM 加密落库，对外只展示脱敏摘要
        server.HeadersJson = security.EncryptSecret(headersJson);
        server.IsHeadersEncrypted = true;
    }

    /// <summary>授权头 JSON 非法（空 / 空白视为未填，合法）。切入点是 JsonNode.Parse 能否解析为对象</summary>
    private static bool HeaderJsonInvalid(string? headersJson)
    {
        if (string.IsNullOrWhiteSpace(headersJson)) return false;
        try
        {
            return JsonNode.Parse(headersJson) is not JsonObject;
        }
        catch (JsonException)
        {
            return true;
        }
    }

    /// <summary>连接失败时仅提供脱敏提示（HTTP 状态码 / timeout），不暴露 URL 与响应内容</summary>
    private static string? ConnectHint(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: { } s } => $"HTTP {(int)s}",
        TimeoutException or OperationCanceledException => "timeout",
        _ => null,
    };

    private static object ItemDto(McpCatalogItem i) => new
    {
        i.Id, kind = i.Kind.ToString(), i.Name, i.Description, i.SchemaJson, i.Enabled,
    };

    /// <summary>拒绝含 U+FFFD 替换字符的乱码输入（防编码损坏数据入库）</summary>
    private static bool HasInvalidEncoding(McpInput input) =>
        input.Name?.Contains('\uFFFD') == true
        || input.StdioCommand?.Contains('\uFFFD') == true
        || input.StdioArgsJson?.Contains('\uFFFD') == true;
}
