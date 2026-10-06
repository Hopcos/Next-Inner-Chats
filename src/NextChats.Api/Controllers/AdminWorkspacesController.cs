using Microsoft.AspNetCore.Mvc;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;
using NextChats.Core.Entities;
using NextChats.Core.Services;

namespace NextChats.Api.Controllers;

/// <summary>
/// 管理端：工作空间（编码会话的服务器目录）注册 / 角色授权（级别绑定）/ 服务器目录浏览与探测。
/// 写操作由 AdminReadonlyGuard 自动保护（只读管理员 403）。
/// </summary>
[Route("api/admin/workspaces")]
public sealed class AdminWorkspacesController(IAdminStore store, IConfigStore config, IAuditLogger audit) : AdminControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List()
    {
        var workspaces = await config.GetAllWorkspacesAsync(HttpContext.RequestAborted);
        var roles = await store.ListRolesAsync();
        var roleName = roles.ToDictionary(r => r.Id, r => r.Name);
        var result = new List<object>();
        foreach (var ws in workspaces)
        {
            var bindings = await config.GetWorkspaceBindingsAsync(ws.Id, HttpContext.RequestAborted);
            result.Add(new
            {
                ws.Id, ws.Name, ws.RootPath, ws.Description, ws.Enabled, ws.CreatedAt, ws.UpdatedAt,
                bindings = bindings.Select(b => new { roleId = b.RoleId, roleName = roleName.GetValueOrDefault(b.RoleId, "?"), level = (int)b.Level }).ToList(),
            });
        }
        return Ok(result);
    }

    public sealed record CreateWorkspaceRequest(string Name, string RootPath, string? Description, bool? Enabled);

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] CreateWorkspaceRequest req)
    {
        var root = WorkspacePaths.NormalizeRoot(req.RootPath);
        if (root is null) return BadRequest(Err("WS_INVALID_PATH"));
        var name = string.IsNullOrWhiteSpace(req.Name) ? Path.GetFileName(root) : req.Name.Trim();
        if (name.Length > 128) name = name[..128];

        var all = await config.GetAllWorkspacesAsync(HttpContext.RequestAborted);
        if (all.Any(w => string.Equals(w.RootPath, root, StringComparison.OrdinalIgnoreCase)))
            return Conflict(Err("WS_PATH_EXISTS"));

        var ws = new Workspace { Name = name, RootPath = root, Description = req.Description, Enabled = req.Enabled ?? true };
        await config.SaveWorkspaceAsync(ws, HttpContext.RequestAborted);
        await audit.RecordAsync(AuditCategory.Config, "WS.CREATE", $"trc_{Guid.NewGuid():N}"[..24], UserId,
            ws.Id.ToString(), new { name, root });
        return Ok(new { ws.Id, ws.Name, ws.RootPath, ws.Description, ws.Enabled });
    }

    public sealed record UpdateWorkspaceRequest(string? Name, string? RootPath, string? Description, bool? Enabled);

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] UpdateWorkspaceRequest req)
    {
        var ws = await config.GetWorkspaceAsync(id, HttpContext.RequestAborted);
        if (ws is null) return NotFound(Err("WS_NOT_FOUND"));

        string? newRoot = ws.RootPath;
        if (!string.IsNullOrWhiteSpace(req.RootPath))
        {
            var normalized = WorkspacePaths.NormalizeRoot(req.RootPath);
            if (normalized is null) return BadRequest(Err("WS_INVALID_PATH"));
            var all = await config.GetAllWorkspacesAsync(HttpContext.RequestAborted);
            if (all.Any(w => w.Id != id && string.Equals(w.RootPath, normalized, StringComparison.OrdinalIgnoreCase)))
                return Conflict(Err("WS_PATH_EXISTS"));
            newRoot = normalized;
        }

        ws.Name = string.IsNullOrWhiteSpace(req.Name) ? ws.Name : req.Name.Trim()[..Math.Min(128, req.Name.Trim().Length)];
        ws.RootPath = newRoot;
        if (req.Description is not null) ws.Description = req.Description;
        if (req.Enabled is { } enabled) ws.Enabled = enabled;
        await config.SaveWorkspaceAsync(ws, HttpContext.RequestAborted);
        await audit.RecordAsync(AuditCategory.Config, "WS.UPDATE", $"trc_{Guid.NewGuid():N}"[..24], UserId,
            ws.Id.ToString(), new { root = newRoot, enabled = ws.Enabled });
        return Ok(new { ws.Id, ws.Name, ws.RootPath, ws.Description, ws.Enabled });
    }

    [HttpDelete("{id:guid}")]
    public async Task<IActionResult> Delete(Guid id)
    {
        var ws = await config.GetWorkspaceAsync(id, HttpContext.RequestAborted);
        if (ws is null) return NotFound(Err("WS_NOT_FOUND"));
        await config.DeleteWorkspaceAsync(id, HttpContext.RequestAborted);
        await audit.RecordAsync(AuditCategory.Config, "WS.DELETE", $"trc_{Guid.NewGuid():N}"[..24], UserId,
            id.ToString(), new { ws.Name, ws.RootPath });
        return NoContent();
    }

    // ---------------- 角色授权（级别绑定） ----------------

    [HttpGet("{id:guid}/bindings")]
    public async Task<IActionResult> Bindings(Guid id)
    {
        var ws = await config.GetWorkspaceAsync(id, HttpContext.RequestAborted);
        if (ws is null) return NotFound(Err("WS_NOT_FOUND"));
        var roles = await store.ListRolesAsync();
        var roleName = roles.ToDictionary(r => r.Id, r => r.Name);
        var bindings = await config.GetWorkspaceBindingsAsync(id, HttpContext.RequestAborted);
        return Ok(bindings.Select(b => new { roleId = b.RoleId, roleName = roleName.GetValueOrDefault(b.RoleId, "?"), level = (int)b.Level }));
    }

    public sealed record BindingInput(Guid RoleId, WorkspaceAccessLevel Level);
    public sealed record SetBindingsRequest(IReadOnlyList<BindingInput>? Bindings);

    [HttpPut("{id:guid}/bindings")]
    public async Task<IActionResult> SetBindings(Guid id, [FromBody] SetBindingsRequest req)
    {
        var ws = await config.GetWorkspaceAsync(id, HttpContext.RequestAborted);
        if (ws is null) return NotFound(Err("WS_NOT_FOUND"));
        var roles = await store.ListRolesAsync();
        var validRoleIds = roles.Select(r => r.Id).ToHashSet();
        var input = (req.Bindings ?? []).Where(b => validRoleIds.Contains(b.RoleId))
            .Select(b => (b.RoleId, b.Level)).Distinct().ToList();
        await config.SetWorkspaceRoleBindingsAsync(id, input, HttpContext.RequestAborted);
        await audit.RecordAsync(AuditCategory.Config, "WS.BIND", $"trc_{Guid.NewGuid():N}"[..24], UserId,
            id.ToString(), new { bindings = input.Select(b => new { b.RoleId, level = (int)b.Level }) });
        return NoContent();
    }

    // ---------------- 探测 ----------------

    /// <summary>检查工作空间根目录在服务器上的存在/读写状态（注册时用）</summary>
    [HttpPost("{id:guid}/probe")]
    public async Task<IActionResult> Probe(Guid id)
    {
        var ws = await config.GetWorkspaceAsync(id, HttpContext.RequestAborted);
        if (ws is null) return NotFound(Err("WS_NOT_FOUND"));
        var root = ws.RootPath;
        var exists = Directory.Exists(root);
        var readable = false;
        var writable = false;
        if (exists)
        {
            try
            {
                readable = Directory.EnumerateFileSystemEntries(root).Take(1).Any() || true; // 空目录也算可读
                var probeFile = Path.Combine(root, $".ws-probe-{Guid.NewGuid():N}.tmp");
                try
                {
                    System.IO.File.WriteAllText(probeFile, "ok");
                    System.IO.File.Delete(probeFile);
                    writable = true;
                }
                catch
                {
                    writable = false;
                }
            }
            catch
            {
                readable = false;
            }
        }
        return Ok(new { root, exists, isDir = exists, readable, writable });
    }

    // ---------------- 服务器目录浏览（注册时选路径用；管理员完全信任） ----------------

    [Route("browse")]
    [HttpGet]
    public IActionResult BrowseServerDir([FromQuery] string? path)
    {
        var target = WorkspaceBrowsePaths.NormalizeBrowse(path ?? "");
        if (target is null)
        {
            // 未指定或非法：返回本机所有盘符根目录
            return Ok(new { path = "", parent = "", entries = DriveInfo.GetDrives()
                .Where(d => d.IsReady)
                .Select(d => new { name = d.RootDirectory.FullName, type = "dir", size = 0L }).ToList() });
        }
        if (!Directory.Exists(target)) return Ok(new { path = target, parent = WorkspaceBrowsePaths.NormalizeBrowse(Path.GetDirectoryName(target)), entries = Array.Empty<object>() });
        var entries = new List<object>();
        try
        {
            foreach (var d in Directory.EnumerateDirectories(target).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
                entries.Add(new { name = Path.GetFileName(d), type = "dir", size = 0L });
            foreach (var f in Directory.EnumerateFiles(target).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            {
                try
                {
                    var fi = new FileInfo(f);
                    entries.Add(new { name = fi.Name, type = "file", size = fi.Length });
                }
                catch { /* 不可读项目跳过 */ }
            }
        }
        catch (UnauthorizedAccessException)
        {
            return Ok(new { path = target, parent = WorkspaceBrowsePaths.NormalizeBrowse(Path.GetDirectoryName(target)), entries = Array.Empty<object>(), error = "access denied" });
        }
        return Ok(new { path = target, parent = WorkspaceBrowsePaths.NormalizeBrowse(Path.GetDirectoryName(target)), entries });
    }
}

/// <summary>浏览路径规范化：绝对路径、拒绝 UNC 与非法字符。与注册策略不同，允许盘符根（浏览需要能进入 C:\、D:\ 等）。</summary>
public static class WorkspaceBrowsePaths
{
    public static string? NormalizeBrowse(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return null;   // 拒绝 UNC
        if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        if (!Path.IsPathRooted(p)) return null;
        return Path.GetFullPath(p);
    }
}

/// <summary>工作空间根路径规范化（注册入口共用）：绝对路径、非盘符根、拒绝 UNC；返回规范化路径或 null</summary>
public static class WorkspacePaths
{
    public static string? NormalizeRoot(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim();
        if (p.StartsWith(@"\\", StringComparison.Ordinal)) return null;   // 拒绝 UNC
        if (p.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        if (!Path.IsPathRooted(p)) return null;
        var full = Path.GetFullPath(p);
        var rootOnly = Path.GetPathRoot(full);
        if (rootOnly is not null && string.Equals(rootOnly, full, StringComparison.OrdinalIgnoreCase)) return null; // 拒绝盘符根
        return full;
    }
}