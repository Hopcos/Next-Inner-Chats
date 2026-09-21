using System.Net;
using System.Text;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NextChats.Core.Abstractions;

namespace NextChats.Api.Controllers;

/// <summary>
/// 工具外部 API 代理路由：前端经同源 /api/ext/tool/{{toolKey}}/{{**path}} 调用外部服务，
/// 后端按管理端为该工具配置的 AppTool.BaseUrl 转发（GET only，原样回传 JSON）。
/// 解决两个问题：1) 浏览器跨域直连外部端点（无 CORS）被拦；2) 内网端点地址不出库、不出网。
/// 权限：登录用户 + 该工具对其角色可见（与 /api/me/tools 同口径）。
/// </summary>
[Route("api/ext/tool/{toolKey}")]
public sealed class ToolEndpointProxyController(IAdminStore store, IHttpClientFactory httpClientFactory) : ApiControllerBase
{
    [HttpGet("{**path}")]
    public async Task<IActionResult> Get(string toolKey, string path, CancellationToken ct)
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

        using var client = httpClientFactory.CreateClient("tool-endpoint-proxy");
        client.Timeout = TimeSpan.FromSeconds(60);
        HttpResponseMessage upstream;
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, target);
            upstream = await client.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            return StatusCode(StatusCodes.Status502BadGateway, Err("TOOL_ENDPOINT_UNREACHABLE"));
        }

        var bytes = await upstream.Content.ReadAsByteArrayAsync(ct);
        var contentType = upstream.Content.Headers.ContentType?.ToString() ?? "application/json";
        return new ContentResult
        {
            Content = Encoding.UTF8.GetString(bytes),
            ContentType = contentType,
            StatusCode = (int)upstream.StatusCode,
        };
    }
}
