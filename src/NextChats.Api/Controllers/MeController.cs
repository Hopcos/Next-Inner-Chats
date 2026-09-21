using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NextChats.Core.Abstractions;

namespace NextChats.Api.Controllers;

/// <summary>个人设置：基础信息 / 查看 Prompt·MCP·SKILL（只读能力摘要）/ 聊天偏好设置</summary>
[ApiController]
[Authorize]
[Route("api/me")]
public sealed class MeController(
    IAdminStore admin,
    IConfigStore config) : ApiControllerBase
{
    [HttpGet]
    public async Task<IActionResult> Profile()
    {
        var user = await admin.GetUserAsync(UserId, includeRoles: true);
        if (user is null) return NotFound(Err("USER_NOT_FOUND"));
        var roles = user.Roles.Select(r => r.Code).ToArray();
        return Ok(new
        {
            id = user.Id,
            username = user.Username,
            displayName = user.DisplayName ?? user.Username,
            email = user.Email,
            roles,
            authType = user.AuthType,
            isAdmin = roles.Contains("admin"),
            isReadonly = user.IsReadonly,
        });
    }

    /// <summary>个人设置 → 仅可查看：名称 / 描述 / 能力摘要（服务端按角色过滤）</summary>
    [HttpGet("catalog")]
    public async Task<IActionResult> Catalog()
    {
        var (mcpIds, promptIds, skillIds, modelIds) = await config.GetRoleBindingsAsync(UserId);
        // 管理员不受模型角色绑定限制（管理端需全量可见）；绑定为空（未配置）时保持默认全量
        var isAdmin = await config.IsAdminAsync(UserId);
        var modelRestricted = !isAdmin && modelIds.Length > 0;

        var prompts = (await config.GetEnabledPromptsAsync())
            .Where(p => promptIds.Contains(p.Id))
            .Select(p => new { p.Id, p.Name, p.Description, p.Summary })
            .ToList();

        var mcps = (await config.GetEnabledMcpServersAsync())
            .Where(m => mcpIds.Contains(m.Id))
            .Select(m => new
            {
                m.Id,
                m.Name,
                m.Description,
                m.Transport,
                endpoint = Core.Services.LogSanitizer.MaskUri(m.Endpoint),
                items = m.Items.Where(i => i.Enabled).Select(i => new { i.Id, kind = i.Kind.ToString(), i.Name, i.Description }).ToList(),
            })
            .ToList();

        var skills = (await config.GetEnabledSkillsAsync())
            .Where(s => skillIds.Contains(s.Id))
            .Select(s => new { s.Id, s.Name, s.Description, s.Summary, s.MetaToolName })
            .ToList();

        // 聊天设置需要选择“供应商 » 模型”（仅非敏感字段）
        // 角色模型绑定：绑定了 LLM 模型的角色，其用户只能看到并选择绑定内的模型；
        // 未绑定任何模型（modelIds 为空）时保持默认，全部启用模型可见。
        var providers = (await config.GetActiveProvidersAsync())
            .Select(p => new
            {
                p.Id,
                p.Name,
                kind = p.Kind.ToString(),
                isHealthy = p.IsHealthy,
                models = p.Models
                    .Where(m => m.Enabled && (!modelRestricted || modelIds.Contains(m.Id)))
                    .OrderBy(m => m.Priority)
                    .Select(m => new { m.Id, m.Name, m.IsVision, m.ContextWindow, m.PriceInPer1K, m.PriceOutPer1K })
                    .ToList(),
            })
            .Where(p => p.models.Count > 0)
            .ToList();

        return Ok(new { prompts, mcps, skills, providers });
    }

    /// <summary>沉浸式工具栏：当前用户可用的启用工具（admin 全量；普通用户按角色绑定过滤）</summary>
    [HttpGet("tools")]
    public async Task<IActionResult> Tools()
    {
        var user = await admin.GetUserAsync(UserId, includeRoles: true);
        if (user is null) return NotFound(Err("USER_NOT_FOUND"));
        var roleIds = user.Roles.Select(r => r.Id).ToArray();
        var isAdmin = user.Roles.Any(r => r.Code == "admin");
        var tools = await admin.ListToolsForUserAsync(roleIds, isAdmin);
        return Ok(tools.Select(t => new { t.Id, key = t.ToolKey, t.Name, t.Icon, t.Description, t.BaseUrl }));
    }

    /// <summary>读取个人设置（JSON 值原样返回，前端解析）</summary>
    [HttpGet("settings")]
    public async Task<IActionResult> GetSettings()
    {
        var settings = await config.GetUserSettingsAsync(UserId);
        return Ok(settings);
    }

    /// <summary>保存聊天偏好：provider/prompt/mcp servers/skills/主题/3D 等（下次自动记住 → 服务端持久化）</summary>
    [HttpPut("settings")]
    public async Task<IActionResult> PutSettings([FromBody] Dictionary<string, JsonElement> settings)
    {
        foreach (var (key, value) in settings)
        {
            await config.SetUserSettingAsync(UserId, key, value.ValueKind == JsonValueKind.String ? value.GetString()! : value.GetRawText());
        }
        return Ok(await config.GetUserSettingsAsync(UserId));
    }
}
