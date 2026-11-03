using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
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
/// 远程服务器支持 OAuth 2.1：发现授权服务器 → 浏览器登录授权 → 令牌加密保存并自动刷新。
/// </summary>
[Route("api/admin/mcp-servers")]
public sealed class AdminMcpController(IAdminStore store, IMcpDriver driver, ISecurityService security, IConfigStore config, IAuditLogger audit, IMcpOAuthService oauth) : AdminControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var list = await store.ListMcpServersAsync(includeItems: true);
        return Ok(list.Select(m => new
        {
            m.Id, m.Name, transport = m.Transport.ToString(),
            endpoint = m.Transport == McpTransportType.Stdio ? null : LogSanitizer.MaskUri(m.Endpoint),
            headersMasked = m.IsHeadersEncrypted ? LogSanitizer.MaskHeaders(security.DecryptSecret(m.HeadersJson!)) : LogSanitizer.MaskHeaders(m.HeadersJson),
            m.Enabled, m.IsVision, m.TimeoutSeconds, m.Description, m.Instructions, m.MetadataJson, m.LastError, m.LastFetchedAt,
            m.StdioCommand, m.StdioArgsJson,
            authMode = m.AuthMode,
            oauth = OAuthDto(m),
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
            m.Id, m.Name, transport = m.Transport.ToString(),
            endpoint = m.Transport == McpTransportType.Stdio ? null : LogSanitizer.MaskUri(m.Endpoint),
            headersMasked = m.IsHeadersEncrypted ? LogSanitizer.MaskHeaders(security.DecryptSecret(m.HeadersJson!)) : LogSanitizer.MaskHeaders(m.HeadersJson),
            m.Enabled, m.IsVision, m.TimeoutSeconds, m.Description, m.Instructions, m.MetadataJson, m.LastError, m.LastFetchedAt,
            m.StdioCommand, m.StdioArgsJson,
            authMode = m.AuthMode,
            oauth = OAuthDto(m),
            items = m.Items.Select(ItemDto).ToList(),
        });
    }

    public sealed record McpInput(
        string Name, string Transport, string? Endpoint, string? HeadersJson, bool Enabled, bool? IsVision,
        int? TimeoutSeconds, string? StdioCommand, string? StdioArgsJson, string? Instructions = null,
        string? AuthMode = null);

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
            AuthMode = NormalizeAuthMode(input.AuthMode, "headers"),
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
        var endpointChanged = !string.Equals(row.Endpoint, input.Endpoint, StringComparison.OrdinalIgnoreCase);
        row.Endpoint = input.Endpoint;
        if (!string.IsNullOrWhiteSpace(input.HeadersJson)) EncryptHeaders(row, input.HeadersJson);
        row.Enabled = input.Enabled;
        row.IsVision = input.IsVision ?? row.IsVision;
        row.TimeoutSeconds = input.TimeoutSeconds ?? 60;
        row.StdioCommand = input.StdioCommand;
        row.StdioArgsJson = input.StdioArgsJson;
        if (!string.IsNullOrWhiteSpace(input.AuthMode)) row.AuthMode = NormalizeAuthMode(input.AuthMode, row.AuthMode);
        // 数据区域/端点变化后，旧 region 的 OAuth 注册与令牌不再适用，清空以便重新授权（授权页的数据区域由 endpoint 决定）
        if (endpointChanged && string.Equals(row.AuthMode, "oauth", StringComparison.OrdinalIgnoreCase))
        {
            row.OAuthConfigJson = null;
            row.IsOAuthConfigEncrypted = false;
            row.OAuthTokensJson = null;
            row.IsOAuthTokensEncrypted = false;
            row.OAuthAuthorizedAt = null;
        }
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

    // ---------------- OAuth 2.1（远程 MCP：浏览器授权 + PKCE + 令牌自动刷新） ----------------

    /// <summary>当前授权状态（含授权/令牌端点摘要；secret 不回传）</summary>
    [HttpGet("{id:guid}/oauth/status")]
    public async Task<IActionResult> OAuthStatus(Guid id)
    {
        var server = await store.GetMcpServerAsync(id);
        if (server is null) return NotFound(Err("MCP_SERVER_NOT_FOUND"));
        return Ok(OAuthDto(server));
    }

    /// <summary>发现授权服务器元数据（RFC 9728 / 8414）；有 registration_endpoint 时自动动态注册（RFC 7591）</summary>
    [HttpPost("{id:guid}/oauth/discover")]
    public async Task<IActionResult> OAuthDiscover(Guid id)
    {
        var server = await store.GetMcpServerAsync(id);
        if (server is null) return NotFound(Err("MCP_SERVER_NOT_FOUND"));
        if (server.Transport != McpTransportType.Http || string.IsNullOrWhiteSpace(server.Endpoint))
        {
            return BadRequest(new ApiErrorBody("MCP_OAUTH_ENDPOINT_REQUIRED", "OAuth 仅适用于带 Endpoint 的 HTTP MCP 服务器"));
        }
        try
        {
            var cfg = await oauth.DiscoverAsync(server, RedirectUri(id), HttpContext.RequestAborted);
            await driver.InvalidateAsync(id);
            await config.InvalidateConfigCacheAsync();
            await audit.RecordAsync(AuditCategory.Config, "MCP.OAUTH.DISCOVER", $"trc_{Guid.NewGuid():N}"[..24], UserId, server.Name,
                detail: new { clientId = cfg.ClientId, authorizationEndpoint = cfg.AuthorizationEndpoint });
            return Ok(new
            {
                ok = true,
                authorizationEndpoint = cfg.AuthorizationEndpoint,
                tokenEndpoint = cfg.TokenEndpoint,
                registrationEndpoint = cfg.RegistrationEndpoint,
                clientId = cfg.ClientId,
                scopes = cfg.Scopes,
                redirectUri = cfg.RedirectUri,
            });
        }
        catch (McpOAuthException ex)
        {
            Serilog.Log.Warning(ex, "MCP OAuth 发现失败 server={Server} code={Code}", server.Name, ex.Code);
            return BadRequest(new ApiErrorBody(ex.Code, ex.Message));
        }
    }

    /// <summary>生成浏览器授权 URL（state + PKCE S256）；前端打开该 URL，用户登录并点击 Authorize</summary>
    [HttpPost("{id:guid}/oauth/start")]
    public async Task<IActionResult> OAuthStart(Guid id)
    {
        var server = await store.GetMcpServerAsync(id);
        if (server is null) return NotFound(Err("MCP_SERVER_NOT_FOUND"));
        try
        {
            var redirectUri = RedirectUri(id);
            var url = await oauth.StartAuthorizationAsync(server, redirectUri, HttpContext.RequestAborted);
            await audit.RecordAsync(AuditCategory.Config, "MCP.OAUTH.START", $"trc_{Guid.NewGuid():N}"[..24], UserId, server.Name);
            return Ok(new { url, redirectUri });
        }
        catch (McpOAuthException ex)
        {
            return BadRequest(new ApiErrorBody(ex.Code, ex.Message));
        }
    }

    /// <summary>授权回调（浏览器直接跳转、匿名访问）：用授权码换取令牌并加密保存</summary>
    [AllowAnonymous]
    [HttpGet("{id:guid}/oauth/callback")]
    public async Task<IActionResult> OAuthCallback(
        Guid id, [FromQuery] string? code, [FromQuery] string? state,
        [FromQuery] string? error, [FromQuery] string? error_description)
    {
        var server = await store.GetMcpServerAsync(id);
        if (server is null)
        {
            return Content(HtmlPage(false, "MCP server not found", Lang), "text/html");
        }

        if (!string.IsNullOrWhiteSpace(error))
        {
            var detail = string.IsNullOrWhiteSpace(error_description) ? error : $"{error}: {error_description}";
            return Content(HtmlPage(false, detail, Lang), "text/html");
        }

        var (ok, message) = await oauth.CompleteAuthorizationAsync(server, code ?? string.Empty, state ?? string.Empty, HttpContext.RequestAborted);
        if (ok)
        {
            await driver.InvalidateAsync(id);
            await config.InvalidateConfigCacheAsync();
            // 回调是匿名请求（浏览器重定向），不能读取 UserId 声明，按匿名审计
            await audit.RecordAsync(AuditCategory.Config, "MCP.OAUTH.AUTHORIZED", $"trc_{Guid.NewGuid():N}"[..24], userId: null, target: server.Name);
        }

        var zh = Lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var text = ok
            ? (zh ? "授权成功，可以关闭此窗口" : "Authorization successful — you can close this window")
            : (zh ? $"授权失败：{message}" : $"Authorization failed: {message}");
        return Content(HtmlPage(ok, text, Lang), "text/html");
    }

    /// <summary>撤销授权（清除令牌；下次需重新授权）</summary>
    [HttpPost("{id:guid}/oauth/revoke")]
    public async Task<IActionResult> OAuthRevoke(Guid id)
    {
        var server = await store.GetMcpServerAsync(id);
        if (server is null) return NotFound(Err("MCP_SERVER_NOT_FOUND"));
        await oauth.RevokeAsync(server, HttpContext.RequestAborted);
        await driver.InvalidateAsync(id);
        await config.InvalidateConfigCacheAsync();
        await audit.RecordAsync(AuditCategory.Config, "MCP.OAUTH.REVOKE", $"trc_{Guid.NewGuid():N}"[..24], UserId, server.Name);
        return NoContent();
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

    private object OAuthDto(McpServer m)
    {
        try
        {
            var s = oauth.GetStatus(m);
            return new
            {
                authorized = s.Authorized,
                authorizedAt = s.AuthorizedAt,
                expiresAt = s.ExpiresAt,
                scopes = s.Scopes,
                clientId = s.ClientId,
                authorizationEndpoint = s.AuthorizationEndpoint,
                tokenEndpoint = s.TokenEndpoint,
                registrationEndpoint = s.RegistrationEndpoint,
                redirectUri = s.RedirectUri,
            };
        }
        catch
        {
            return new { authorized = false };
        }
    }

    /// <summary>授权回调地址（与浏览器实际访问的主机/端口一致，动态注册时使用）</summary>
    private string RedirectUri(Guid id)
    {
        var req = HttpContext.Request;
        var scheme = req.Scheme;
        var hostName = req.Host.Host;
        var hostValue = req.Host.Value; // 含端口
        // OAuth 2.1 原生客户端（RFC 8252）：动态注册通常只接受 http 的 loopback 回调（localhost / 127.0.0.1），
        // 非 loopback 域名会被拒绝（如 Coralogix 返回 invalid_redirect_uri）。
        if (string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase) && !IsLoopbackHost(hostName))
        {
            hostValue = $"localhost:{req.Host.Port ?? 80}";
        }
        return $"{scheme}://{hostValue}/api/admin/mcp-servers/{id}/oauth/callback";
    }

    private static bool IsLoopbackHost(string host) =>
        host is "localhost" or "127.0.0.1" or "::1" or "[::1]"
        || (System.Net.IPAddress.TryParse(host, out var ip) && System.Net.IPAddress.IsLoopback(ip));

    private static string NormalizeAuthMode(string? mode, string fallback) =>
        string.IsNullOrWhiteSpace(mode)
            ? fallback
            : (string.Equals(mode.Trim(), "oauth", StringComparison.OrdinalIgnoreCase) ? "oauth" : "headers");

    private static string HtmlPage(bool ok, string message, string lang)
    {
        var title = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase) ? "NextChats MCP 授权" : "NextChats MCP Authorization";
        var color = ok ? "#1d7a3c" : "#b42318";
        var encoded = System.Net.WebUtility.HtmlEncode(message);
        return "<!doctype html><html><head><meta charset='utf-8'>" +
               $"<title>{title}</title><style>body{{font-family:system-ui,-apple-system,sans-serif;display:flex;align-items:center;" +
               $"justify-content:center;height:100vh;margin:0;background:#f6f7f9}}div{{padding:24px 32px;border-radius:12px;" +
               $"background:#fff;box-shadow:0 4px 20px rgba(0,0,0,.08);color:{color};font-size:18px}}</style></head>" +
               $"<body><div>{encoded}</div></body></html>";
    }

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
