using Microsoft.AspNetCore.Mvc;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;
using NextChats.Core.Services;

namespace NextChats.Api.Controllers;

/// <summary>
/// 用户端：可用工作空间列表 / 工作空间内目录浏览（限定根内）/ 会话绑定与解除工作空间。
/// 权限按角色在后端强制（GetRoleWorkspaceBindingsAsync），未授权工作空间一律 403/404。
/// </summary>
[Route("api/chat")]
public sealed class WorkspaceController(IConfigStore config, IChatStore chat, IWorkspaceSandbox sandbox, IAuditLogger audit) : ApiControllerBase
{
    /// <summary>当前用户可用的工作空间（角色绑定派生级别；管理员豁免 = 全部启用 + FullAccess）</summary>
    [HttpGet("workspaces")]
    public async Task<IActionResult> ListAvailable()
    {
        var bindings = (await config.GetRoleWorkspaceBindingsAsync(UserId, HttpContext.RequestAborted)).ToList();
        if (bindings.Count == 0) return Ok(Array.Empty<object>());
        var all = await config.GetEnabledWorkspacesAsync(HttpContext.RequestAborted);
        var map = all.ToDictionary(w => w.Id);
        return Ok(bindings
            .Where(b => map.ContainsKey(b.WorkspaceId))
            .Select(b =>
            {
                var w = map[b.WorkspaceId];
                return new { w.Id, w.Name, w.RootPath, w.Description, level = (int)b.Level };
            }));
    }

    /// <summary>工作空间内目录浏览（相对路径，限定根内；供用户端选择器展示服务器目录树）</summary>
    [HttpGet("workspaces/{workspaceId:guid}/browse")]
    public async Task<IActionResult> Browse(Guid workspaceId, [FromQuery] string? path)
    {
        var bindings = (await config.GetRoleWorkspaceBindingsAsync(UserId, HttpContext.RequestAborted)).ToList();
        var binding = bindings.FirstOrDefault(b => b.WorkspaceId == workspaceId);
        if (binding.WorkspaceId == Guid.Empty) return NotFound(Err("WS_NOT_FOUND"));
        var ws = await config.GetWorkspaceAsync(workspaceId, HttpContext.RequestAborted);
        if (ws is null || !ws.Enabled) return NotFound(Err("WS_DISABLED"));

        var ctx = new WsContext(ws.Id, ws.Name, Path.GetFullPath(ws.RootPath), binding.Level);
        string target;
        try
        {
            target = sandbox.ResolveInside(ctx, path);
        }
        catch (WsPathEscapedException)
        {
            return BadRequest(Err("WS_PATH_ESCAPE"));
        }
        if (!Directory.Exists(target)) return Ok(new { root = ws.RootPath, path = Path.GetRelativePath(ws.RootPath, target), entries = Array.Empty<object>() });

        var entries = new List<object>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(target).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                entries.Add(new { name = Path.GetFileName(d), type = "dir", size = 0L });
            foreach (var f in Directory.EnumerateFiles(target).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try { var fi = new FileInfo(f); entries.Add(new { name = fi.Name, type = "file", size = fi.Length }); }
                catch { /* 跳过不可读项 */ }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return BadRequest(Err("WS_ACCESS_DENIED"));
        }
        return Ok(new { root = ws.RootPath, path = Path.GetRelativePath(ws.RootPath, target), entries });
    }

    public sealed record SetWorkspaceRequest(Guid? WorkspaceId);

    /// <summary>绑定/解除会话工作空间（null = 解除 → 普通聊天）；未授权工作空间 403</summary>
    [HttpPut("sessions/{sessionId:guid}/workspace")]
    public async Task<IActionResult> SetWorkspace(Guid sessionId, [FromBody] SetWorkspaceRequest req)
    {
        if (req.WorkspaceId is { } wsId)
        {
            var bindings = await config.GetRoleWorkspaceBindingsAsync(UserId, HttpContext.RequestAborted);
            if (!bindings.Any(b => b.WorkspaceId == wsId)) return Forbid();
            var ws = await config.GetWorkspaceAsync(wsId, HttpContext.RequestAborted);
            if (ws is null || !ws.Enabled) return NotFound(Err("WS_DISABLED"));
        }
        var ok = await chat.SetSessionWorkspaceAsync(UserId, sessionId, req.WorkspaceId, HttpContext.RequestAborted);
        if (!ok) return NotFound(Err("SESSION_NOT_FOUND"));
        await audit.RecordAsync(AuditCategory.Chat, req.WorkspaceId is null ? "SESSION.WS_UNBIND" : "SESSION.WS_BIND",
            $"trc_{Guid.NewGuid():N}"[..24], UserId, sessionId.ToString(),
            req.WorkspaceId is null ? null : new { workspaceId = req.WorkspaceId });
        return Ok(new { workspaceId = req.WorkspaceId });
    }
}