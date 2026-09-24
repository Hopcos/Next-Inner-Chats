using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;
using NextChats.Core.Entities;
using NextChats.Core.Localization;

namespace NextChats.Core.Services;

/// <summary>
/// MCP 驱动引擎实现（基于 ModelContextProtocol SDK 2.2.0）：
///  - 传输工厂：Streamable HTTP（默认）/ STDIO（预留），见 <see cref="CreateTransport"/>；
///  - 懒连接 + 复用 + 超时 + 失败重连；
///  - 自动带出 tools/prompts/resources，映射为 <see cref="McpCatalogItem"/>；
///  - 错误隔离：单服务器错误以结构化结果返回，不中断会话。
/// </summary>
public sealed class McpDriver : IMcpDriver
{
    private const string ClientName = "next-chats";
    private const string ClientVersion = "1.0.0";

    private readonly IHttpClientProvider _http;
    private readonly ISecurityService _security;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger _logger;
    private readonly ConcurrentDictionary<Guid, Lazy<Task<McpClient>>> _connections = new();
    private readonly ConcurrentDictionary<Guid, SemaphoreSlim> _locks = new();

    public McpDriver(IHttpClientProvider http, ISecurityService security, ILoggerFactory loggerFactory)
    {
        _http = http;
        _security = security;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<McpDriver>();
    }

    // ---------------- 连接管理 ----------------

    private Task<McpClient> GetConnectionAsync(McpServer server, CancellationToken ct)
    {
        if (_connections.TryGetValue(server.Id, out var existing))
        {
            return existing.Value;
        }

        var semaphore = _locks.GetOrAdd(server.Id, _ => new SemaphoreSlim(1, 1));
        return ConnectUnderLockAsync(server, semaphore, ct);
    }

    private async Task<McpClient> ConnectUnderLockAsync(McpServer server, SemaphoreSlim semaphore, CancellationToken ct)
    {
        if (_connections.TryGetValue(server.Id, out var existing))
        {
            return await existing.Value.ConfigureAwait(false);
        }

        await semaphore.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (_connections.TryGetValue(server.Id, out existing))
            {
                return await existing.Value.ConfigureAwait(false);
            }

            var lazy = new Lazy<Task<McpClient>>(() => CreateClientAsync(server, ct));
            _connections[server.Id] = lazy;
            try
            {
                return await lazy.Value.ConfigureAwait(false);
            }
            catch
            {
                _connections.TryRemove(server.Id, out _);
                throw;
            }
        }
        finally
        {
            semaphore.Release();
            _locks.TryRemove(server.Id, out _);
        }
    }

    private async Task<McpClient> CreateClientAsync(McpServer server, CancellationToken ct)
    {
        var transport = CreateTransport(server);
        var options = new McpClientOptions
        {
            ClientInfo = new Implementation { Name = ClientName, Version = ClientVersion },
        };
        _logger.LogInformation("MCP 连接建立 server={Server} transport={Transport} endpoint={Endpoint}",
            server.Name, server.Transport, MaskUri(server.Endpoint));
        return await McpClient.CreateAsync(transport!, options, _loggerFactory, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 传输工厂：当前采用 Streamable HTTP（最新 MCP 规范），后续扩展 STDIO
    /// （McpServer.Transport == Stdio 时创建子进程传输）。
    /// </summary>
    private IClientTransport? CreateTransport(McpServer server)
    {
        if (server.Transport == McpTransportType.Stdio)
        {
            var arguments = string.IsNullOrWhiteSpace(server.StdioArgsJson)
                ? []
                : JsonSerializer.Deserialize<string[]>(server.StdioArgsJson) ?? [];
            _logger.LogInformation("MCP Stdio 传输已创建 server={Server} command={Command}", server.Name, server.StdioCommand);
            return new StdioClientTransport(new StdioClientTransportOptions
            {
                Name = server.Name,
                Command = server.StdioCommand ?? "dotnet",
                Arguments = arguments,
                InheritEnvironmentVariables = true,
            }, _loggerFactory);
        }

        if (string.IsNullOrWhiteSpace(server.Endpoint))
        {
            throw new InvalidOperationException($"MCP 服务器 {server.Name} 未配置 Endpoint");
        }

        var headers = DecodeHeaders(server);
        var options = new HttpClientTransportOptions
        {
            Endpoint = new Uri(server.Endpoint),
            TransportMode = HttpTransportMode.StreamableHttp,
            AdditionalHeaders = headers,
            ConnectionTimeout = TimeSpan.FromSeconds(Math.Max(5, server.TimeoutSeconds)),
        };
        return new HttpClientTransport(options, _http.Create("mcp"), _loggerFactory, ownsHttpClient: false);
    }

    /// <summary>解析并解密请求头（落库时整体加密）</summary>
    private IDictionary<string, string> DecodeHeaders(McpServer server)
    {
        var headers = new Dictionary<string, string>();
        if (string.IsNullOrWhiteSpace(server.HeadersJson)) return headers;
        try
        {
            var raw = server.IsHeadersEncrypted ? _security.DecryptSecret(server.HeadersJson) : server.HeadersJson;
            var node = JsonNode.Parse(raw);
            if (node is JsonObject obj)
            {
                foreach (var kv in obj)
                {
                    if (kv.Value is not null)
                    {
                        headers[kv.Key] = kv.Value.ToString();
                    }
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "MCP 请求头解析失败 server={Server}（已忽略自定义头）", server.Name);
        }
        return headers;
    }

    // ---------------- 工具目录 ----------------

    public IReadOnlyList<UnifiedTool> GetEnabledTools(McpServer server)
    {
        var list = new List<UnifiedTool>();
        foreach (var item in server.Items)
        {
            if (item.Kind != McpItemKind.Tool || !item.Enabled) continue;
            var destructive = item.Name.Contains("delete", StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains("drop", StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains("remove", StringComparison.OrdinalIgnoreCase)
                || item.Name.Contains("truncate", StringComparison.OrdinalIgnoreCase);
            list.Add(new UnifiedTool(server.Name, item.Name, item.Description ?? "", item.SchemaJson, false, destructive));
        }
        return list;
    }

    // ---------------- 调用 ----------------

    public async Task<McpToolResult> CallToolAsync(McpServer server, string toolName, string? argumentsJson, string traceId, string? lang = null, CancellationToken ct = default)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var attempts = 0;
        Exception? lastEx = null;

        for (var attempt = 1; attempt <= 2; attempt++) // 第一层：连接级重连一次
        {
            attempts = attempt;
            try
            {
                McpClient client;
                try
                {
                    client = await GetConnectionAsync(server, ct).ConfigureAwait(false);
                }
                catch (Exception ex) when (attempt == 1)
                {
                    // 连接失败：清掉坏连接，下一轮重连
                    lastEx = ex;
                    _connections.TryRemove(server.Id, out _);
                    continue;
                }

                var arguments = ParseArgs(argumentsJson);
                using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                timeoutCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, server.TimeoutSeconds)));

                // 先刷新 SDK 内部工具缓存：消除 "Tool '{x}' not found in cache" 警告，并让 Mcp-Param-* 头正常带出
                try
                {
                    await client.ListToolsAsync((RequestOptions?)null, timeoutCts.Token).ConfigureAwait(false);
                }
                catch
                {
                    // 列表失败不阻断调用（缓存未命中仅是头部提示问题）
                }

                var result = await client.CallToolAsync(toolName, arguments, null, null, timeoutCts.Token).ConfigureAwait(false);
                var text = RenderContent(result.Content);
                sw.Stop();

                if (result.IsError == true)
                {
                    // 业务错误：透传服务端返回内容；并附加结构化错误详情（部分服务器把真实错误放 structuredContent）
                    var structured = result.StructuredContent is null
                        ? string.Empty
                        : $" | structured={TruncateJson(result.StructuredContent, 800)}";
                    var detail = string.IsNullOrWhiteSpace(text) || text == Texts.Get("MCP_NO_CONTENT", "en")
                        ? $"MCP server returned isError=true without content (tool '{toolName}'){structured}"
                        : $"{text}{structured}";
                    return new McpToolResult(false, "", detail, "MCP_TOOL_ERROR", (int)sw.ElapsedMilliseconds, attempts, Retryable: false);
                }
                return new McpToolResult(true, text, null, null, (int)sw.ElapsedMilliseconds, attempts, Retryable: false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 工具自身超时（非用户取消）
                lastEx = new TimeoutException(Texts.Get("MCP_TIMEOUT", lang ?? "en"));
                _connections.TryRemove(server.Id, out _);
                continue;
            }
            catch (OperationCanceledException)
            {
                throw; // 用户/会话取消
            }
            catch (Exception ex)
            {
                lastEx = ex;
                _connections.TryRemove(server.Id, out _);
                _logger.LogWarning(ex, "MCP 调用失败 trace={Trace} server={Server} tool={Tool}", traceId, server.Name, toolName);
            }
        }

        sw.Stop();
        return new McpToolResult(false, "", MaskMcpError(lastEx, lang ?? "en"), "MCP_ERROR", (int)sw.ElapsedMilliseconds, attempts, Retryable: true);
    }

    /// <summary>
    /// 对模型/用户：不暴露堆栈/请求头，但保留实际异常消息链（最多 3 层、截断），便于排查工具失败根因。
    /// </summary>
    private static string MaskMcpError(Exception? ex, string lang) => ex switch
    {
        null => Texts.Get("MCP_UNKNOWN_ERROR", lang),
        TimeoutException => Compose(Texts.Get("MCP_TIMEOUT", lang), ex),
        HttpRequestException => Compose(Texts.Get("MCP_NETWORK_ERROR", lang), ex),
        McpException => Compose(Texts.Get("MCP_PROTOCOL_ERROR", lang), ex),
        _ => Compose(Texts.Get("MCP_GENERIC_ERROR", lang), ex),
    };

    private static string Compose(string friendly, Exception ex)
    {
        var brief = Brief(ex);
        return string.IsNullOrWhiteSpace(brief) ? friendly : $"{friendly}: {brief}";
    }

    /// <summary>structuredContent 的 JSON 渲染（截断，防止把超长结构体塞回对话）</summary>
    private static string TruncateJson(JsonElement? node, int maxLen)
    {
        if (node is not { } value || value.ValueKind == JsonValueKind.Undefined || value.ValueKind == JsonValueKind.Null)
        {
            return "(no structured detail)";
        }
        string json;
        try
        {
            json = value.GetRawText();
        }
        catch
        {
            return "(structured content unreadable)";
        }
        return json.Length > maxLen ? json[..maxLen] + "…" : json;
    }

    private static string Brief(Exception ex)
    {
        const int maxLen = 500;
        var sb = new System.Text.StringBuilder();
        Exception? cur = ex;
        var depth = 0;
        while (cur is not null && depth < 3)
        {
            var msg = cur.Message?.Trim();
            if (!string.IsNullOrWhiteSpace(msg)
                && sb.ToString().IndexOf(msg, StringComparison.Ordinal) < 0) // 去重（外层常含内层摘要）
            {
                if (sb.Length > 0) sb.Append(" → ");
                sb.Append(msg);
                if (sb.Length >= maxLen) break;
            }
            cur = cur.InnerException;
            depth++;
        }
        var result = sb.ToString();
        return result.Length > maxLen ? result[..maxLen] + "…" : result;
    }

    private static string RenderContent(IList<ContentBlock> blocks)
    {
        if (blocks.Count == 0) return Texts.Get("MCP_NO_CONTENT", "en");
        var sb = new System.Text.StringBuilder();
        foreach (var block in blocks)
        {
            switch (block)
            {
                case TextContentBlock text:
                    sb.AppendLine(text.Text);
                    break;
                case ImageContentBlock:
                    sb.AppendLine(Texts.Get("MCP_IMAGE_CONTENT", "en"));
                    break;
                case ToolResultContentBlock toolResult:
                    sb.AppendLine(toolResult.Content?.ToString() ?? Texts.Get("MCP_EMPTY", "en"));
                    break;
                case ResourceLinkBlock:
                    sb.AppendLine(Texts.Get("MCP_RESOURCE_REF", "en"));
                    break;
                default:
                    sb.AppendLine($"[{block.GetType().Name}]");
                    break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    // ---------------- 自动带出 ----------------

    public async Task<McpDiscoverResult> DiscoverAsync(McpServer server, CancellationToken ct)
    {
        var client = await GetConnectionAsync(server, ct).ConfigureAwait(false);
        var items = new List<McpCatalogItem>();

        // 服务器级 Instructions（MCP 协议：initialize 结果携带；作为系统级使用指南注入 LLM system）
        var instructions = TryGetInstructionsAsync(client);

        // 工具列表是发现的基石：连不上/拿不到 → 整体失败（控制器友好报错）
        var tools = await client.ListToolsAsync((RequestOptions?)null, ct).ConfigureAwait(false);
        foreach (var tool in tools)
        {
            items.Add(new McpCatalogItem
            {
                McpServerId = server.Id,
                Kind = McpItemKind.Tool,
                Name = tool.Name,
                Description = tool.Description ?? tool.Title,
                SchemaJson = tool.JsonSchema.ValueKind == JsonValueKind.Undefined ? null : tool.JsonSchema.GetRawText(),
                Enabled = true,
            });
        }

        // prompts / resources / templates 属于增强能力：服务端未实现时降级为空，不阻断发现
        var prompts = await TryListAsync(() => client.ListPromptsAsync((RequestOptions?)null, ct))
            .ConfigureAwait(false);
        foreach (var prompt in prompts)
        {
            var args = prompt.ProtocolPrompt.Arguments;
            var schema = new JsonObject
            {
                ["arguments"] = new JsonArray(args.Select(a => new JsonObject
                {
                    ["name"] = a.Name,
                    ["required"] = a.Required == true,
                }).ToArray()),
            };
            items.Add(new McpCatalogItem
            {
                McpServerId = server.Id,
                Kind = McpItemKind.Prompt,
                Name = prompt.Name,
                Description = prompt.Description ?? prompt.Title,
                SchemaJson = schema.ToJsonString(),
                Enabled = true,
            });
        }

        var resources = await TryListAsync(() => client.ListResourcesAsync((RequestOptions?)null, ct))
            .ConfigureAwait(false);
        foreach (var resource in resources)
        {
            items.Add(new McpCatalogItem
            {
                McpServerId = server.Id,
                Kind = McpItemKind.Resource,
                Name = resource.Name ?? resource.Uri,
                Description = resource.Description ?? resource.Title ?? resource.Uri,
                SchemaJson = new JsonObject { ["uri"] = resource.Uri, ["mimeType"] = resource.MimeType ?? "" }.ToJsonString(),
                Enabled = true,
            });
        }

        var templates = await TryListAsync(() => client.ListResourceTemplatesAsync((RequestOptions?)null, ct))
            .ConfigureAwait(false);
        foreach (var tpl in templates)
        {
            items.Add(new McpCatalogItem
            {
                McpServerId = server.Id,
                Kind = McpItemKind.Resource,
                Name = tpl.Name ?? tpl.UriTemplate,
                Description = tpl.Description ?? tpl.Title ?? tpl.UriTemplate,
                SchemaJson = new JsonObject { ["uriTemplate"] = tpl.UriTemplate }.ToJsonString(),
                Enabled = true,
            });
        }

        return new McpDiscoverResult(server.Description, instructions, items);
    }

    /// <summary>读取服务器 Instructions（initialize 结果，SDK 暴露为 McpClient.ServerInstructions）</summary>
    private static string? TryGetInstructionsAsync(McpClient client)
    {
        try
        {
            return client.ServerInstructions;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>MCP 协议方法不可用（如 prompts/list 未实现）→ 返回空列表；其它异常向上抛</summary>
    private static async Task<IList<T>> TryListAsync<T>(Func<ValueTask<IList<T>>> list)
    {
        try
        {
            return await list().ConfigureAwait(false);
        }
        catch (McpException) // 支持能力缺失：-32601 Method not found
        {
            return [];
        }
        catch (HttpRequestException) // 808 等网关层“方法不存在”
        {
            return [];
        }
    }

    public async Task<string?> GetPromptAsync(McpServer server, string promptName, string? argumentsJson, CancellationToken ct)
    {
        var client = await GetConnectionAsync(server, ct).ConfigureAwait(false);
        var arguments = ParseArgs(argumentsJson);
        var result = await client.GetPromptAsync(promptName, arguments, null, ct).ConfigureAwait(false);
        var sb = new System.Text.StringBuilder();
        foreach (var pm in result.Messages)
        {
            var text = pm.Content switch
            {
                TextContentBlock t => t.Text,
                ImageContentBlock => Texts.Get("MCP_IMAGE_CONTENT", "en"),
                AudioContentBlock => Texts.Get("MCP_AUDIO_CONTENT", "en"),
                _ => Texts.Get("MCP_CONTENT_PLACEHOLDER", "en"),
            };
            sb.AppendLine($"{pm.Role}: {text}");
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<string> ListResourcesAsync(McpServer server, CancellationToken ct)
    {
        var client = await GetConnectionAsync(server, ct).ConfigureAwait(false);
        var sb = new System.Text.StringBuilder();
        var resources = await TryListAsync(() => client.ListResourcesAsync((RequestOptions?)null, ct)).ConfigureAwait(false);
        foreach (var r in resources)
        {
            sb.AppendLine($"- uri: {r.Uri} | name: {r.Name ?? "-"} | type: {r.MimeType ?? "-"} | desc: {r.Description ?? r.Title ?? "-"}");
        }
        var templates = await TryListAsync(() => client.ListResourceTemplatesAsync((RequestOptions?)null, ct)).ConfigureAwait(false);
        foreach (var tpl in templates)
        {
            sb.AppendLine($"- uriTemplate: {tpl.UriTemplate} | name: {tpl.Name ?? "-"} | desc: {tpl.Description ?? tpl.Title ?? "-"}");
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<string?> ReadResourceAsync(McpServer server, string uri, CancellationToken ct)
    {
        var client = await GetConnectionAsync(server, ct).ConfigureAwait(false);
        var result = await client.ReadResourceAsync(uri, (RequestOptions?)null, ct).ConfigureAwait(false);
        var sb = new System.Text.StringBuilder();
        foreach (var content in result.Contents)
        {
            switch (content)
            {
                case TextResourceContents text when !string.IsNullOrEmpty(text.Text):
                    sb.AppendLine(text.Text);
                    break;
                case BlobResourceContents blob:
                    var mime = blob.MimeType ?? "application/octet-stream";
                    if (mime.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                        || mime is "application/json" or "application/xml" or "application/javascript")
                    {
                        sb.AppendLine(System.Text.Encoding.UTF8.GetString(blob.Blob.Span));
                    }
                    else
                    {
                        sb.AppendLine(Texts.Get("MCP_BLOB_CONTENT", "en"));
                    }
                    break;
                default:
                    sb.AppendLine(Texts.Get("MCP_BLOB_CONTENT", "en"));
                    break;
            }
        }
        return sb.ToString().TrimEnd();
    }

    public async Task<(bool Ok, string? Error, int LatencyMs)> PingAsync(McpServer server, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        try
        {
            var client = await GetConnectionAsync(server, ct).ConfigureAwait(false);
            await client.PingAsync((RequestOptions?)null, ct).ConfigureAwait(false);
            sw.Stop();
            return (true, null, (int)sw.ElapsedMilliseconds);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            sw.Stop();
            _connections.TryRemove(server.Id, out _);
            return (false, Texts.Get("MCP_TIMEOUT", "en"), (int)sw.ElapsedMilliseconds);
        }
        catch (Exception ex)
        {
            sw.Stop();
            _connections.TryRemove(server.Id, out _);
            return (false, MaskMcpError(ex, "en"), (int)sw.ElapsedMilliseconds);
        }
    }

    public Task InvalidateAsync(Guid serverId)
    {
        if (_connections.TryRemove(serverId, out var lazy) && lazy.IsValueCreated)
        {
            _ = Task.Run(async () =>
            {
                try
                {
                    var client = await lazy.Value.ConfigureAwait(false);
                    await client.DisposeAsync().ConfigureAwait(false);
                }
                catch
                {
                    // 忽略释放异常
                }
            });
        }
        return Task.CompletedTask;
    }

    // ---------------- 工具方法 ----------------

    private static IReadOnlyDictionary<string, object> ParseArgs(string? argumentsJson)
    {
        if (string.IsNullOrWhiteSpace(argumentsJson)) return new Dictionary<string, object>();
        try
        {
            using var doc = JsonDocument.Parse(argumentsJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
            {
                return new Dictionary<string, object>();
            }
            var dict = new Dictionary<string, object>();
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                dict[prop.Name] = prop.Value.Clone();
            }
            return dict;
        }
        catch (JsonException)
        {
            return new Dictionary<string, object>();
        }
    }

    private static string MaskUri(string? uri)
    {
        if (string.IsNullOrWhiteSpace(uri)) return Texts.Get("MCP_NOT_CONFIGURED", "en");
        try
        {
            var u = new Uri(uri);
            return $"{u.Scheme}://{u.Host}{(u.Port > 0 ? ":" + u.Port : "")}{u.AbsolutePath}";
        }
        catch (UriFormatException)
        {
            return Texts.Get("MCP_INVALID_URI", "en");
        }
    }
}
