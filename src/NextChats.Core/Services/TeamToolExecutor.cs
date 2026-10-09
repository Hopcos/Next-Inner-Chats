using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NextChats.Core.Abstractions;
using NextChats.Core.Configuration;
using NextChats.Core.Domain;
using NextChats.Core.Entities;
using NextChats.Core.Localization;

namespace NextChats.Core.Services;

/// <summary>
/// 团队协作（Team Review）专用工具执行器：与普通聊天（ChatOrchestrator）同口径收集与执行工具，
/// 使团队工程师也能调用 MCP / 技能 / 工作空间 / 内置工具，展示与普通聊天一致。
///
/// 差异点（团队场景收敛）：
/// - 不注入审批（多工程师并发调用无法弹窗确认）：与 Autonomous 会话一致，直接执行；
/// - 不注入 delegate_task / 视觉审批等会话编排专属能力；
/// - 权限（角色绑定 / 级别）每次执行重新校验，与普通聊天同语义。
/// </summary>
public sealed class TeamToolExecutor
{
    private const string BuiltinToolServer = "system";
    private const string HttpFetchToolName = "http_fetch";
    private const string McpPromptToolName = "mcp_prompt";
    private const string McpResourcesToolName = "mcp_resources";
    private const string McpReadResourceToolName = "mcp_read_resource";

    private const string SettingMcpServers = "chat.mcpServers";
    private const string SettingSkills = "chat.skills";

    // 与普通聊天同声明（http_fetch / mcp_prompt / mcp_resources / mcp_read_resource）
    private const string HttpFetchSchemaJson =
        """{"type":"object","properties":{"url":{"type":"string","description":"Absolute http(s) URL to fetch (allowlisted hosts only, e.g. https://raw.githubusercontent.com/owner/repo/main/README.md)"}},"required":["url"]}""";

    private const string McpPromptSchemaJson =
        """{"type":"object","properties":{"name":{"type":"string","description":"Prompt name exposed by the MCP server (see the admin catalog)"},"server":{"type":"string","description":"Optional MCP server name; omit to search all bound servers"},"arguments":{"type":"object","description":"Optional template arguments as a JSON object"}},"required":["name"]}""";

    private const string McpResourcesSchemaJson =
        """{"type":"object","properties":{"server":{"type":"string","description":"Optional MCP server name; omit to list from all bound servers"}},"required":[]}""";

    private const string McpReadResourceSchemaJson =
        """{"type":"object","properties":{"uri":{"type":"string","description":"Resource URI (from the mcp_resources listing)"},"server":{"type":"string","description":"Optional MCP server name; omit to try all bound servers"}},"required":["uri"]}""";

    /// <summary>http_fetch 专用 HttpClient（与普通聊天同：禁用自动重定向，逐跳校验白名单防 SSRF）</summary>
    private static readonly HttpClient FetchHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    private readonly IConfigStore _config;
    private readonly IMcpDriver _mcp;
    private readonly ISkillExecutionEngine _skills;
    private readonly IWorkspaceSandbox _workspaces;
    private readonly IAuditLogger _audit;
    private readonly IOptions<BuiltinToolOptions> _builtinOptions;
    private readonly ILogger<TeamToolExecutor> _logger;

    public TeamToolExecutor(
        IConfigStore config,
        IMcpDriver mcp,
        ISkillExecutionEngine skills,
        IWorkspaceSandbox workspaces,
        IAuditLogger audit,
        IOptions<BuiltinToolOptions> builtinOptions,
        ILogger<TeamToolExecutor> logger)
    {
        _config = config;
        _mcp = mcp;
        _skills = skills;
        _workspaces = workspaces;
        _audit = audit;
        _builtinOptions = builtinOptions;
        _logger = logger;
    }

    /// <summary>团队上下文（单次 Run 内共享：工具集 + MCP 服务器 + 技能表 + 工作空间上下文）</summary>
    public sealed record TeamToolContext(
        IReadOnlyList<UnifiedTool> Tools,
        IReadOnlyList<McpServer> Servers,
        IReadOnlyDictionary<string, Skill> SkillsByName,
        WsContext? WsCtx);

    /// <summary>收集工具（与普通聊天同口径：设置勾选 MCP/技能 + 角色绑定交集 + 内置工具 + 会话工作空间 ws_*）。不注入返回空集（仅内置）。</summary>
    public async Task<TeamToolContext> CollectAsync(Guid userId, Guid? workspaceId, CancellationToken ct)
    {
        var (roleMcpIds, _, roleSkillIds, _) = await _config.GetRoleBindingsAsync(userId, ct);
        var settings = await _config.GetUserSettingsAsync(userId, ct);
        var requestedMcp = ParseGuids(GetSetting(settings, SettingMcpServers));
        var requestedSkills = ParseGuids(GetSetting(settings, SettingSkills));

        var servers = (await _config.GetEnabledMcpServersAsync(ct))
            .Where(s => requestedMcp.Contains(s.Id) && roleMcpIds.Contains(s.Id))
            .ToList();
        var enabledSkills = (await _config.GetEnabledSkillsAsync(ct))
            .Where(s => requestedSkills.Contains(s.Id) && roleSkillIds.Contains(s.Id))
            .ToList();

        var unifiedTools = new List<UnifiedTool>();
        foreach (var server in servers)
        {
            try { unifiedTools.AddRange(_mcp.GetEnabledTools(server)); }
            catch (Exception ex) { _logger.LogWarning(ex, "团队收集 MCP 工具失败 server={Server}", server.Name); }
        }
        foreach (var skill in enabledSkills)
        {
            unifiedTools.Add(new UnifiedTool("skill", skill.MetaToolName, skill.Description ?? skill.Name, null, IsSkill: true));
        }
        var skillByName = enabledSkills.ToDictionary(s => s.MetaToolName, StringComparer.OrdinalIgnoreCase);

        // 内置工具（与普通聊天一致）
        unifiedTools.Add(new UnifiedTool(BuiltinToolServer, HttpFetchToolName,
            "Fetch a web page or raw text file over HTTP(S) GET and return its text content (size-limited). " +
            "Use it to read web pages, README.md, raw files such as https://raw.githubusercontent.com/owner/repo/main/README.md. " +
            "Only allowlisted hosts are reachable (github.com / raw.githubusercontent.com by default).",
            HttpFetchSchemaJson, IsSkill: false));
        unifiedTools.Add(new UnifiedTool(BuiltinToolServer, McpPromptToolName,
            "Retrieve a prompt template from an MCP server, rendered into role/text content. " +
            "Use when an MCP server exposes reusable prompts (see the admin catalog) and you need their template/instructions to act on them.",
            McpPromptSchemaJson, IsSkill: false));
        unifiedTools.Add(new UnifiedTool(BuiltinToolServer, McpResourcesToolName,
            "List resources (static resources and templates) exposed by the bound MCP servers, with their URIs. " +
            "Call this first to discover what can be read, then use mcp_read_resource to fetch content.",
            McpResourcesSchemaJson, IsSkill: false));
        unifiedTools.Add(new UnifiedTool(BuiltinToolServer, McpReadResourceToolName,
            "Read an MCP resource by URI and return its text content (size-limited). " +
            "Use a URI obtained from mcp_resources. Binary/image resources return a placeholder.",
            McpReadResourceSchemaJson, IsSkill: false));

        // 工作空间工具（会话绑定且角色授权时注入）
        WsContext? wsCtx = null;
        if (workspaceId is { } wsId && wsId != Guid.Empty)
        {
            var wsBindings = (await _config.GetRoleWorkspaceBindingsAsync(userId, ct)).ToList();
            var binding = wsBindings.FirstOrDefault(b => b.WorkspaceId == wsId);
            if (binding.WorkspaceId != Guid.Empty)
            {
                wsCtx = await _workspaces.ResolveAsync(wsId, binding.Level, ct);
                if (wsCtx is not null)
                {
                    var wsTools = WorkspaceToolCatalog.ForLevel(wsCtx.Level, wsCtx.Name).ToArray();
                    unifiedTools.AddRange(wsTools);
                    _logger.LogInformation("[TeamTools] workspace ws={Ws} level={Level} tools={Tools}", wsCtx.Name, wsCtx.Level, wsTools.Length);
                }
            }
        }

        return new TeamToolContext(unifiedTools, servers, skillByName, wsCtx);
    }

    /// <summary>执行统一工具（Skill 元工具 / MCP 工具 / 内置 http_fetch、mcp_*、ws_*）。失败返回结构化结果，不抛异常。</summary>
    public async Task<McpToolResult> ExecuteAsync(
        UnifiedTool tool, string? args, Guid userId, WsContext? wsCtx,
        IReadOnlyList<McpServer> servers, IReadOnlyDictionary<string, Skill> skillByName,
        string traceId, string lang, CancellationToken ct)
    {
        if (tool.IsSkill)
        {
            if (!skillByName.TryGetValue(tool.Name, out var skill))
            {
                return new McpToolResult(false, "", Texts.Get("SKILL_NOT_FOUND", lang), "SKILL_NOT_FOUND", 0, 1);
            }
            var input = ExtractString(args ?? "{}", "input") ?? args ?? "";
            var (ok, result, error) = await _skills.ExecuteAsync(skill, input, traceId, ct);
            return new McpToolResult(ok, result, error, ok ? null : "SKILL_ERROR", 0, 1);
        }

        if (tool.ServerName == BuiltinToolServer)
        {
            return tool.Name switch
            {
                HttpFetchToolName => await ExecuteHttpFetchAsync(args, traceId, lang, ct),
                McpPromptToolName => await ExecuteMcpPromptAsync(args, servers, traceId, lang, ct),
                McpResourcesToolName => await ExecuteMcpResourcesAsync(args, servers, traceId, lang, ct),
                McpReadResourceToolName => await ExecuteMcpReadResourceAsync(args, servers, traceId, lang, ct),
                _ when wsCtx is not null && IsWorkspaceTool(tool.Name)
                    => await ExecuteWorkspaceToolAsync(tool.Name, args, wsCtx, userId, traceId, lang, ct),
                _ => new McpToolResult(false, "", Texts.Get("TOOL_NOT_FOUND", lang, tool.Name), "TOOL_NOT_FOUND", 0, 1),
            };
        }

        var server = servers.FirstOrDefault(s => s.Name == tool.ServerName);
        if (server is null)
        {
            return new McpToolResult(false, "", Texts.Get("MCP_SERVER_NOT_FOUND", lang), "SERVER_NOT_FOUND", 0, 1);
        }
        return await _mcp.CallToolAsync(server, tool.Name, args, traceId, lang, ct);
    }

    private static bool IsWorkspaceTool(string name) =>
        WorkspaceToolCatalog.ReadTools.Contains(name, StringComparer.OrdinalIgnoreCase)
        || WorkspaceToolCatalog.WriteTools.Contains(name, StringComparer.OrdinalIgnoreCase);

    /// <summary>执行 ws_* 工作空间工具（与 ChatOrchestrator 同逻辑：每次执行重验角色绑定与级别）</summary>
    private async Task<McpToolResult> ExecuteWorkspaceToolAsync(string toolName, string? args, WsContext wsCtx,
        Guid userId, string traceId, string lang, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var wsBindings = (await _config.GetRoleWorkspaceBindingsAsync(userId, ct)).ToList();
        var binding = wsBindings.FirstOrDefault(b => b.WorkspaceId == wsCtx.WorkspaceId);
        if (binding.WorkspaceId == Guid.Empty)
        {
            await _audit.RecordAsync(AuditCategory.Security, "WS.DENIED", traceId, userId,
                wsCtx.WorkspaceId.ToString(), new { tool = toolName, reason = "binding removed" }, isSuspicious: true, ct: ct);
            return new McpToolResult(false, "", Texts.Get("WS_NOT_AUTHORIZED", lang), "WS_NOT_AUTHORIZED", (int)sw.ElapsedMilliseconds, 1);
        }
        if (binding.Level != wsCtx.Level) wsCtx = wsCtx with { Level = binding.Level };

        var isWrite = WorkspaceToolCatalog.WriteTools.Contains(toolName, StringComparer.OrdinalIgnoreCase);
        if (isWrite && binding.Level < WorkspaceAccessLevel.WorkspaceWrite)
        {
            await _audit.RecordAsync(AuditCategory.Security, "WS.DENIED", traceId, userId,
                wsCtx.WorkspaceId.ToString(), new { tool = toolName, level = binding.Level.ToString() }, isSuspicious: true, ct: ct);
            return new McpToolResult(false, "", Texts.Get("WS_DENIED_TOOL", lang, toolName, binding.Level), "WS_DENIED", (int)sw.ElapsedMilliseconds, 1);
        }

        var result = _workspaces.Execute(wsCtx, toolName, args);
        var detail = ExtractWsDetail(toolName, args);
        await _audit.RecordAsync(AuditCategory.Tool, $"WS.{toolName}", traceId, userId,
            wsCtx.WorkspaceId.ToString(), result.Ok ? detail : new { baseDetail = detail, error = result.ErrorCode }, ct: ct);

        return new McpToolResult(result.Ok, result.Text, result.Ok ? null : result.Text, result.ErrorCode,
            (int)sw.ElapsedMilliseconds, 1, Retryable: false);
    }

    private static object? ExtractWsDetail(string toolName, string? args)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(args)) return new { tool = toolName };
            var node = JsonNode.Parse(args);
            var obj = node as JsonObject ?? new JsonObject();
            var path = obj["path"]?.ToString() ?? "";
            var command = obj["command"]?.ToString() ?? "";
            return new
            {
                tool = toolName,
                path = Truncate(path, 200),
                command = string.IsNullOrEmpty(command) ? null : Truncate(command, 100),
                contentLen = obj["content"]?.ToString().Length,
            };
        }
        catch (JsonException) { return new { tool = toolName }; }
    }

    // ================= 内置 mcp_prompt / mcp_resources / mcp_read_resource =================

    private static (string? Name, string? Server, string? Uri, string? ArgumentsJson) ParseMcpToolArgs(string? args)
    {
        string? name = null, server = null, uri = null, argumentsJson = null;
        try
        {
            using var doc = JsonDocument.Parse(args ?? "{}");
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, null, null);
            if (root.TryGetProperty("name", out var n)) name = n.GetString();
            if (root.TryGetProperty("server", out var s)) server = s.GetString();
            if (root.TryGetProperty("uri", out var u)) uri = u.GetString();
            if (root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object) argumentsJson = a.GetRawText();
        }
        catch (JsonException) { /* 参数解析失败 → 按缺失处理 */ }
        return (name, server, uri, argumentsJson);
    }

    private static (IReadOnlyList<McpServer> Candidates, string? ErrKey) PickMcpServers(IReadOnlyList<McpServer> servers, string? serverName, string lang)
    {
        if (servers.Count == 0) return (servers, Texts.Get("MCP_SERVER_NOT_FOUND", lang));
        if (string.IsNullOrWhiteSpace(serverName)) return (servers, null);
        var hit = servers.FirstOrDefault(s => s.Name.Equals(serverName.Trim(), StringComparison.OrdinalIgnoreCase));
        return hit is null ? (servers, Texts.Get("MCP_SERVER_NOT_FOUND", lang)) : ([hit], null);
    }

    private async Task<McpToolResult> ExecuteMcpPromptAsync(string? args, IReadOnlyList<McpServer> servers, string traceId, string lang, CancellationToken ct)
    {
        var (name, serverName, _, argumentsJson) = ParseMcpToolArgs(args);
        if (string.IsNullOrWhiteSpace(name))
        {
            return new McpToolResult(false, "", Texts.Get("MCP_PROMPT_NEED_NAME", lang), "MCP_PROMPT_NEED_NAME", 0, 1);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (candidates, errKey) = PickMcpServers(servers, serverName, lang);
        if (errKey is not null) return new McpToolResult(false, "", errKey, "MCP_SERVER_NOT_FOUND", 0, 1);
        foreach (var s in candidates)
        {
            try
            {
                var text = await _mcp.GetPromptAsync(s, name, argumentsJson, ct).ConfigureAwait(false);
                if (text is not null)
                {
                    sw.Stop();
                    return new McpToolResult(true, $"[{s.Name}]\n{text}", null, null, (int)sw.ElapsedMilliseconds, 1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "团队 mcp_prompt 失败 trace={Trace} server={Server} prompt={Prompt}", traceId, s.Name, name);
            }
        }
        sw.Stop();
        var target = candidates.Count == 1 ? candidates[0].Name : string.Join("/", candidates.Select(c => c.Name));
        return new McpToolResult(false, "", Texts.Get("MCP_PROMPT_NOT_FOUND", lang, name, target), "MCP_PROMPT_NOT_FOUND", (int)sw.ElapsedMilliseconds, 1);
    }

    private async Task<McpToolResult> ExecuteMcpResourcesAsync(string? args, IReadOnlyList<McpServer> servers, string traceId, string lang, CancellationToken ct)
    {
        var (_, serverName, _, _) = ParseMcpToolArgs(args);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (candidates, errKey) = PickMcpServers(servers, serverName, lang);
        if (errKey is not null) return new McpToolResult(false, "", errKey, "MCP_SERVER_NOT_FOUND", 0, 1);
        var sb = new System.Text.StringBuilder();
        foreach (var s in candidates)
        {
            try
            {
                var listing = await _mcp.ListResourcesAsync(s, ct).ConfigureAwait(false);
                sb.AppendLine($"## {s.Name}");
                sb.AppendLine(string.IsNullOrWhiteSpace(listing) ? Texts.Get("MCP_RESOURCES_NONE", lang) : listing);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "团队 mcp_resources 失败 trace={Trace} server={Server}", traceId, s.Name);
                sb.AppendLine($"## {s.Name}\n(error: {ex.Message})");
            }
        }
        sw.Stop();
        return new McpToolResult(true, sb.ToString().TrimEnd(), null, null, (int)sw.ElapsedMilliseconds, 1);
    }

    private async Task<McpToolResult> ExecuteMcpReadResourceAsync(string? args, IReadOnlyList<McpServer> servers, string traceId, string lang, CancellationToken ct)
    {
        var (_, serverName, uri, _) = ParseMcpToolArgs(args);
        if (string.IsNullOrWhiteSpace(uri))
        {
            return new McpToolResult(false, "", Texts.Get("MCP_RESOURCE_NEED_URI", lang), "MCP_RESOURCE_NEED_URI", 0, 1);
        }
        var maxChars = Math.Max(2_000, _builtinOptions.Value.HttpFetchMaxChars);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (candidates, errKey) = PickMcpServers(servers, serverName, lang);
        if (errKey is not null) return new McpToolResult(false, "", errKey, "MCP_SERVER_NOT_FOUND", 0, 1);
        string? lastError = null;
        foreach (var s in candidates)
        {
            try
            {
                var text = await _mcp.ReadResourceAsync(s, uri, ct).ConfigureAwait(false);
                if (text is not null)
                {
                    sw.Stop();
                    if (text.Length > maxChars) text = text[..maxChars];
                    return new McpToolResult(true, $"[{s.Name}]\n{text}", null, null, (int)sw.ElapsedMilliseconds, 1);
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _logger.LogWarning(ex, "团队 mcp_read_resource 失败 trace={Trace} server={Server} uri={Uri}", traceId, s.Name, uri);
            }
        }
        sw.Stop();
        var target = candidates.Count == 1 ? candidates[0].Name : string.Join("/", candidates.Select(c => c.Name));
        return new McpToolResult(false, lastError ?? "", Texts.Get("MCP_RESOURCE_READ_FAILED", lang, uri, target), "MCP_RESOURCE_READ_FAILED", (int)sw.ElapsedMilliseconds, 1);
    }

    // ================= 内置 http_fetch =================

    private async Task<McpToolResult> ExecuteHttpFetchAsync(string? args, string traceId, string lang, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? url = null;
        try
        {
            using var doc = JsonDocument.Parse(args ?? "{}");
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("url", out var u))
            {
                url = u.GetString();
            }
        }
        catch (JsonException) { /* 参数解析失败 → 按无效 URL 处理 */ }

        var opts = _builtinOptions.Value;
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new McpToolResult(false, "", Texts.Get("HTTP_FETCH_BAD_URL", lang), "HTTP_FETCH_BAD_URL", 0, 1);
        }
        if (!HostAllowed(uri.Host, opts))
        {
            _logger.LogWarning("http_fetch 域名不在白名单 trace={Trace} host={Host}", traceId, uri.Host);
            return new McpToolResult(false, "", Texts.Get("HTTP_FETCH_DENIED", lang, uri.Host), "HTTP_FETCH_DENIED", 0, 1);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, opts.HttpFetchTimeoutSeconds)));

            var resp = await FetchHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            for (var hop = 0; hop < 3 && (int)resp.StatusCode is >= 300 and < 400; hop++)
            {
                var loc = resp.Headers.Location;
                resp.Dispose();
                if (loc is null) break;
                var next = new Uri(uri, loc);
                if (!HostAllowed(next.Host, opts))
                {
                    return new McpToolResult(false, "", Texts.Get("HTTP_FETCH_DENIED", lang, next.Host), "HTTP_FETCH_DENIED", 0, 1);
                }
                uri = next;
                resp = await FetchHttp.GetAsync(next, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                resp.Dispose();
                sw.Stop();
                return new McpToolResult(false, "", Texts.Get("HTTP_FETCH_HTTP", lang, code), "HTTP_FETCH_HTTP", (int)sw.ElapsedMilliseconds, 1);
            }

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            using var ms = new MemoryStream();
            await resp.Content.CopyToAsync(ms, cts.Token);
            resp.Dispose();
            sw.Stop();
            var latency = (int)sw.ElapsedMilliseconds;

            if (ms.Length > opts.HttpFetchMaxBytes)
            {
                return new McpToolResult(false, "", Texts.Get("HTTP_FETCH_TOO_LARGE", lang, ms.Length / 1024 / 1024), "HTTP_FETCH_TOO_LARGE", latency, 1);
            }

            var text = DecodeFetchText(ms.ToArray(), mediaType);
            if (text.Length > opts.HttpFetchMaxChars)
            {
                text = text[..opts.HttpFetchMaxChars];
            }
            return new McpToolResult(true, text, null, null, latency, 1);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new McpToolResult(false, "", Texts.Get("MCP_TIMEOUT", lang), "HTTP_FETCH_TIMEOUT", 0, 2);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "http_fetch 网络失败 trace={Trace} host={Host}", traceId, uri.Host);
            return new McpToolResult(false, "", Texts.Get("HTTP_FETCH_NETWORK", lang), "HTTP_FETCH_NETWORK", 0, 2);
        }
    }

    private static bool HostAllowed(string host, BuiltinToolOptions opts)
    {
        foreach (var item in opts.HttpFetchAllowHosts)
        {
            var allow = item.Trim().TrimStart('*', '.').Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(allow)) continue;
            if (host.Equals(allow, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + allow, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    private static string DecodeFetchText(byte[] bytes, string? mediaType)
    {
        var isHtml = mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
        string text;
        try { text = System.Text.Encoding.UTF8.GetString(bytes); }
        catch { text = ""; }
        if (!isHtml) return CleanControl(text);
        text = Regex.Replace(text, "<(script|style|noscript)[\\s\\S]*?</\\1>", " ", RegexOptions.IgnoreCase);
        text = Regex.Replace(text, "<!--[\\s\\S]*?-->", " ");
        text = Regex.Replace(text, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return CleanControl(text);
    }

    private static string CleanControl(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch == '\t' || ch == '\n' || ch == '\r' || ch >= ' ') sb.Append(ch);
        }
        var collapsed = Regex.Replace(sb.ToString(), @"[ \t]{2,}", " ");
        return collapsed.Trim();
    }

    // ================= 工具 =================

    private static string? GetSetting(IDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) ? value : null;

    private static List<Guid> ParseGuids(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return [];
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(s);
            return arr?.Select(Guid.Parse).ToList() ?? [];
        }
        catch (Exception) { return []; }
    }

    private static string? ExtractString(string json, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty(prop, out var v)) return v.GetString();
        }
        catch (JsonException) { /* 忽略 */ }
        return null;
    }

    private static string Truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? Texts.Get("NO_DESCRIPTION", "en") : (s.Length <= max ? s : s[..max] + "…");
}
