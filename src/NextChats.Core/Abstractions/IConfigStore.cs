using NextChats.Core.Domain;
using NextChats.Core.Entities;

namespace NextChats.Core.Abstractions;

/// <summary>
/// 配置/权限读取存储（引擎只依赖接口；实现位于 Infrastructure，带缓存）。
/// 有效交集规则：角色绑定 ∩ 用户启用 ∩ Server 级启用 ∩ 项级启用。
/// </summary>
public interface IConfigStore
{
    // ---------- LLM Provider ----------
    Task<IReadOnlyList<LlmProvider>> GetActiveProvidersAsync(CancellationToken ct = default);

    Task<LlmProvider?> GetProviderAsync(Guid id, CancellationToken ct = default);

    // ---------- MCP ----------
    Task<IReadOnlyList<McpServer>> GetEnabledMcpServersAsync(CancellationToken ct = default);

    Task<McpServer?> GetMcpServerAsync(Guid id, CancellationToken ct = default);

    // ---------- Skills / Prompts ----------
    Task<IReadOnlyList<Skill>> GetEnabledSkillsAsync(CancellationToken ct = default);

    Task<Skill?> GetSkillAsync(Guid id, CancellationToken ct = default);

    Task<Prompt?> GetPromptAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<Prompt>> GetEnabledPromptsAsync(CancellationToken ct = default);

    // ---------- RBAC ----------
    Task<bool> IsAdminAsync(Guid userId, CancellationToken ct = default);

    /// <summary>用户在角色层可用的配置集合（缓存 + 失效）</summary>
    Task<(Guid[] McpServerIds, Guid[] PromptIds, Guid[] SkillIds, Guid[] ModelIds)> GetRoleBindingsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>用户角色绑定授权的 LLM 模型集合（用于模型可见性过滤）</summary>
    Task<Guid[]> GetRoleModelIdsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>某个 MCP Server 是否对该用户角色开放</summary>
    Task<bool> CanAccessMcpAsync(Guid userId, Guid mcpServerId, CancellationToken ct = default);

    Task<bool> CanAccessPromptAsync(Guid userId, Guid promptId, CancellationToken ct = default);

    Task<bool> CanAccessSkillAsync(Guid userId, Guid skillId, CancellationToken ct = default);

    // ---------- 工作空间（编码会话） ----------
    /// <summary>全部工作空间（含禁用；管理端用）</summary>
    Task<IReadOnlyList<Workspace>> GetAllWorkspacesAsync(CancellationToken ct = default);

    /// <summary>启用中的工作空间</summary>
    Task<IReadOnlyList<Workspace>> GetEnabledWorkspacesAsync(CancellationToken ct = default);

    Task<Workspace?> GetWorkspaceAsync(Guid id, CancellationToken ct = default);

    /// <summary>用户（经角色绑定）可用的工作空间及级别（<see cref="WorkspaceAccessLevel"/>；管理员豁免 = 全部 Enabled 工作空间 + FullAccess）</summary>
    Task<IReadOnlyList<(Guid WorkspaceId, WorkspaceAccessLevel Level)>> GetRoleWorkspaceBindingsAsync(Guid userId, CancellationToken ct = default);

    /// <summary>某工作空间的角色绑定明细（管理端）</summary>
    Task<IReadOnlyList<RoleWorkspaceBinding>> GetWorkspaceBindingsAsync(Guid workspaceId, CancellationToken ct = default);

    /// <summary>新增/更新工作空间</summary>
    Task SaveWorkspaceAsync(Workspace workspace, CancellationToken ct = default);

    /// <summary>删除工作空间（级联清理角色绑定；不删除磁盘目录）</summary>
    Task DeleteWorkspaceAsync(Guid id, CancellationToken ct = default);

    /// <summary>全量替换某工作空间的角色绑定（roles 中未出现的角色绑定被移除）</summary>
    Task SetWorkspaceRoleBindingsAsync(Guid workspaceId, IReadOnlyList<(Guid RoleId, WorkspaceAccessLevel Level)> bindings, CancellationToken ct = default);

    // ---------- 用户设置 ----------
    Task<IDictionary<string, string>> GetUserSettingsAsync(Guid userId, CancellationToken ct = default);

    Task SetUserSettingAsync(Guid userId, string key, string valueJson, CancellationToken ct = default);

    /// <summary>配置变更后的缓存失效通知（由管理端调用）</summary>
    Task InvalidateConfigCacheAsync(CancellationToken ct = default);
}
