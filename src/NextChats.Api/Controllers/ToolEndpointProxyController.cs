using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;
using NextChats.Core.Entities;

namespace NextChats.Api.Controllers;

/// <summary>
/// 工具外部 API 代理路由：前端经同源 /api/ext/tool/{{toolKey}}/{{**path}} 调用外部服务，
/// 后端按管理端为该工具配置的 AppTool.BaseUrl 转发，并附加管理端配置的授权头（HeadersJson，
/// AES-GCM 加密落库，转发时解密；如 K8S Explorer 的 "Authorization: Bearer &lt;token&gt;"）（原样回传 JSON）。
/// 默认只读 GET；POST 仅对 <see cref="PostAllowList"/> 登记的工具+路径开放（如 Kafka Producer 的发送消息），并写审计。
/// 解决三个问题：1) 浏览器跨域直连外部端点（无 CORS）被拦；2) 内网端点地址不出库、不出网；
/// 3) 外部 API 的敏感授权头只存在于后端，不暴露给浏览器。
/// 权限：登录用户 + 该工具对其角色可见（与 /api/me/tools 同口径）。
/// </summary>
[Route("api/ext/tool/{toolKey}")]
public sealed class ToolEndpointProxyController(IAdminStore store, IHttpClientFactory httpClientFactory, ISecurityService security, IAuditLogger audit) : ApiControllerBase
{
    /// <summary>
    /// 允许经代理发起 POST 的写操作白名单：toolKey → 路径正则。
    /// 代理默认只读（GET）；写操作必须在此显式登记，防止代理被滥用为任意 POST 通道。
    /// kafka-producer：仅 POST /api/topics/{topic}/messages（produce_message，向 Kafka 追加一条消息）。
    /// </summary>
    private static readonly Dictionary<string, System.Text.RegularExpressions.Regex> PostAllowList = new(StringComparer.OrdinalIgnoreCase)
    {
        ["kafka-producer"] = new(@"^api/topics/[^/]+/messages$", System.Text.RegularExpressions.RegexOptions.Compiled),
    };

    [HttpGet("{**path}")]
    public Task<IActionResult> Get(string toolKey, string path, CancellationToken ct)
        => ForwardAsync(toolKey, HttpMethod.Get, ct);

    /// <summary>受限 POST 转发（仅白名单工具/路径；请求体原样转发，并写审计）</summary>
    [HttpPost("{**path}")]
    public async Task<IActionResult> Post(string toolKey, string path, CancellationToken ct)
    {
        var routePath = (HttpContext.Request.RouteValues["path"]?.ToString() ?? string.Empty).Trim('/');
        if (!PostAllowList.TryGetValue(toolKey, out var allowed) || !allowed.IsMatch(routePath))
        {
            return StatusCode(StatusCodes.Status405MethodNotAllowed, Err("TOOL_ENDPOINT_METHOD_NOT_ALLOWED"));
        }

        using var reader = new StreamReader(HttpContext.Request.Body, Encoding.UTF8);
        var body = await reader.ReadToEndAsync(ct);
        var result = await ForwardAsync(toolKey, HttpMethod.Post, ct, body);

        // 写操作留痕：只记录目标与结果状态，不记录消息体（可能含业务敏感数据）
        var status = (result as ContentResult)?.StatusCode ?? (result as ObjectResult)?.StatusCode ?? 0;
        await audit.RecordAsync(AuditCategory.Tool, "TOOL.EXT_POST", $"trc_{Guid.NewGuid():N}"[..24], UserId,
            target: $"{toolKey}/{routePath}",
            detail: new { toolKey, path = routePath, query = HttpContext.Request.QueryString.ToUriComponent(), status, bodyBytes = Encoding.UTF8.GetByteCount(body) },
            ip: HttpContext.Connection.RemoteIpAddress?.ToString(),
            ct: CancellationToken.None);
        return result;
    }

    private async Task<IActionResult> ForwardAsync(string toolKey, HttpMethod method, CancellationToken ct, string? requestBody = null)
    {
        var tool = await store.GetToolByKeyAsync(toolKey, ct);
        if (tool is null || string.IsNullOrWhiteSpace(tool.BaseUrl))
        {
            return BadRequest(Err("TOOL_ENDPOINT_NOT_CONFIGURED"));
        }

        // 与 /api/me/tools 同口径：仅当前用户可见的工具可代理
        var user = await store.GetUserAsync(UserId, includeRoles: true, ct: ct);
        if (user is null) return Unauthorized(Err("USER_NOT_FOUND"));
        var roleIds = user.Roles.Select(r => r.Id).ToArray();
        var visible = await store.ListToolsForUserAsync(roleIds, user.Roles.Any(r => r.Code == "admin"), ct);
        if (visible.All(t => t.Id != tool.Id))
        {
            return Forbid();
        }

        if (!Uri.TryCreate(tool.BaseUrl.Trim().TrimEnd('/') + "/", UriKind.Absolute, out var baseUri)
            || (baseUri.Scheme != Uri.UriSchemeHttp && baseUri.Scheme != Uri.UriSchemeHttps))
        {
            return BadRequest(Err("TOOL_ENDPOINT_INVALID"));
        }

        var routePath = HttpContext.Request.RouteValues["path"]?.ToString() ?? string.Empty;
        var pathPart = string.IsNullOrWhiteSpace(routePath) ? string.Empty : $"/{routePath.Trim('/')}";
        var target = new Uri(baseUri, $"{pathPart}{HttpContext.Request.QueryString.ToUriComponent()}");
        var headers = DecodeHeaders(tool);

        using var client = httpClientFactory.CreateClient("tool-endpoint-proxy");
        client.Timeout = TimeSpan.FromSeconds(60);
        HttpResponseMessage upstream;
        try
        {
            using var req = new HttpRequestMessage(method, target);
            foreach (var kv in headers)
            {
                req.Headers.TryAddWithoutValidation(kv.Key, kv.Value);
            }
            if (requestBody is not null)
            {
                req.Content = new StringContent(requestBody, Encoding.UTF8, "application/json");
            }
            upstream = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, Err("TOOL_ENDPOINT_UNREACHABLE"));
        }

        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";

        // 二进制 / 附件响应（如 K8S Explorer 的 files/download）：必须按字节原样流式透传。
        // 下面的 UTF-8 字符串化只适合 JSON / 文本，用于二进制会把非法字节替换成 U+FFFD 导致文件损坏。
        if (IsBinaryResponse(upstream))
        {
            Response.StatusCode = (int)upstream.StatusCode;
            Response.ContentType = contentType;
            if (upstream.Content.Headers.ContentDisposition is { } cd)
            {
                Response.Headers.ContentDisposition = cd.ToString();
            }
            if (upstream.Content.Headers.ContentLength is { } len)
            {
                Response.ContentLength = len;
            }
            await using var upstreamStream = await upstream.Content.ReadAsStreamAsync(ct);
            await upstreamStream.CopyToAsync(Response.Body, ct);
            upstream.Dispose();
            return new EmptyResult();
        }

        var bytes = await upstream.Content.ReadAsByteArrayAsync(ct);
        return new ContentResult
        {
            Content = Encoding.UTF8.GetString(bytes),
            ContentType = contentType,
            StatusCode = (int)upstream.StatusCode,
        };
    }

    /// <summary>
    /// 上游响应是否应按二进制透传：带 attachment 的 Content-Disposition，或 Content-Type 不是 JSON / 文本类。
    /// 无 Content-Type 时按 JSON 处理（保持既有行为）。
    /// </summary>
    private static bool IsBinaryResponse(HttpResponseMessage upstream)
    {
        if (upstream.Content.Headers.ContentDisposition?.DispositionType?.Equals("attachment", StringComparison.OrdinalIgnoreCase) == true)
        {
            return true;
        }
        var mt = upstream.Content.Headers.ContentType?.MediaType;
        if (string.IsNullOrEmpty(mt)) return false;
        return !(mt.StartsWith("text/", StringComparison.OrdinalIgnoreCase)
                 || mt.Contains("json", StringComparison.OrdinalIgnoreCase)
                 || mt.Contains("xml", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>解析并解密该工具配置的授权头（落库时整体加密；解密失败/非法 JSON 返回空）</summary>
    private IDictionary<string, string> DecodeHeaders(AppTool tool)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(tool.HeadersJson)) return headers;
        try
        {
            var raw = tool.IsHeadersEncrypted ? security.DecryptSecret(tool.HeadersJson) : tool.HeadersJson;
            if (JsonNode.Parse(raw) is JsonObject obj)
            {
                foreach (var kv in obj)
                {
                    if (kv.Value is not null) headers[kv.Key] = kv.Value.ToString();
                }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "工具授权头解析失败 toolKey={ToolKey}", tool.ToolKey);
        }
        return headers;
    }
}
