using Microsoft.AspNetCore.Mvc;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;

namespace NextChats.Api.Controllers;

/// <summary>管理端：沉浸式工具栏维护（唯一标识/图标/名称/描述/启用 + 角色绑定）</summary>
[Route("api/admin/tools")]
public sealed class AdminToolController(IAdminStore store, IAuditLogger audit) : AdminControllerBase
{
    public sealed record ToolInput(string ToolKey, string Name, string? Icon, string? Description, string? BaseUrl, bool Enabled, Guid[]? RoleIds);

    [HttpGet]
    public async Task<IActionResult> List()
    {
        var tools = await store.ListToolsAsync();
        return Ok(tools.Select(x => new
        {
            x.Id, x.ToolKey, x.Name, x.Icon, x.Description, x.BaseUrl, x.Enabled, x.CreatedAt, x.UpdatedAt,
            roleIds = x.AllowedRoles.Select(r => r.Id).ToList(),
            roleNames = x.AllowedRoles.Select(r => r.Name).ToList(),
        }));
    }

    [HttpPost]
    public async Task<IActionResult> Create([FromBody] ToolInput input)
    {
        var error = await ValidateAsync(input, currentId: null);
        if (error is not null) return BadRequest(Err(error));
        var saved = await store.SaveToolAsync(
            Guid.Empty, input.ToolKey.Trim(), input.Name.Trim(), (input.Icon ?? "puzzle").Trim(),
            string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
            string.IsNullOrWhiteSpace(input.BaseUrl) ? null : input.BaseUrl.Trim(),
            input.Enabled, input.RoleIds ?? [], HttpContext.RequestAborted);
        await audit.RecordAsync(AuditCategory.Admin, "TOOL.CREATE", $"trc_{Guid.NewGuid():N}"[..24], UserId,
            detail: new { toolKey = saved.ToolKey, name = saved.Name });
        return Ok(saved.Id);
    }

    [HttpPut("{id:guid}")]
    public async Task<IActionResult> Update(Guid id, [FromBody] ToolInput input)
    {
        var error = await ValidateAsync(input, currentId: id);
        if (error is not null) return BadRequest(Err(error));
        try
        {
            await store.SaveToolAsync(
                id, input.ToolKey.Trim(), input.Name.Trim(), (input.Icon ?? "puzzle").Trim(),
                string.IsNullOrWhiteSpace(input.Description) ? null : input.Description.Trim(),
                string.IsNullOrWhiteSpace(input.BaseUrl) ? null : input.BaseUrl.Trim(),
                input.Enabled, input.RoleIds ?? [], HttpContext.RequestAborted);
            await audit.RecordAsync(AuditCategory.Admin, "TOOL.UPDATE", $"trc_{Guid.NewGuid():N}"[..24], UserId,
                detail: new { toolKey = input.ToolKey.Trim() });
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

    /// <summary>必填校验 + 唯一标识查重（排除自身）</summary>
    private async Task<string?> ValidateAsync(ToolInput input, Guid? currentId)
    {
        if (string.IsNullOrWhiteSpace(input.ToolKey) || string.IsNullOrWhiteSpace(input.Name)) return "TOOL_FIELDS_REQUIRED";
        var existing = await store.GetToolByKeyAsync(input.ToolKey.Trim(), HttpContext.RequestAborted);
        if (existing is not null && existing.Id != currentId) return "TOOL_KEY_EXISTS";
        return null;
    }
}
