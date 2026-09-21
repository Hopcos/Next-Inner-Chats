using NextChats.Core.Domain;
using NextChats.Core.Entities;

namespace NextChats.Core.Abstractions;

/// <summary>管理端 CRUD 存储</summary>
public interface IAdminStore
{
    // ---------- 用户 / 角色 ----------
    Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken ct = default);

    Task<AppUser?> GetUserAsync(Guid id, bool includeRoles = false, CancellationToken ct = default);

    Task<AppUser?> GetUserByNameAsync(string username, CancellationToken ct = default);

    /// <summary>按 (AuthType, Username) 组合唯一查询用户（内部鉴权用户与 default 用户可同名）</summary>
    Task<AppUser?> GetUserAsync(string authType, string username, bool includeRoles = true, CancellationToken ct = default);

    Task<AppUser> CreateUserAsync(AppUser user, IEnumerable<Guid> roleIds, CancellationToken ct = default);

    Task UpdateUserAsync(AppUser user, CancellationToken ct = default);

    Task SetUserRolesAsync(Guid userId, IEnumerable<Guid> roleIds, CancellationToken ct = default);

    Task DeleteUserAsync(Guid id, CancellationToken ct = default);

    Task<IReadOnlyList<AppRole>> ListRolesAsync(bool includeBindings = false, CancellationToken ct = default);

    Task<AppRole?> GetRoleAsync(Guid id, CancellationToken ct = default);

    Task<AppRole> CreateRoleAsync(AppRole role, CancellationToken ct = default);

    Task UpdateRoleAsync(AppRole role, CancellationToken ct = default);

    Task DeleteRoleAsync(Guid id, CancellationToken ct = default);

    /// <summary>角色 ↔ 资源绑定（Mcp/Prompt/Skill/LLM模型）</summary>
    Task SetRoleBindingsAsync(Guid roleId, Guid[] mcpIds, Guid[] promptIds, Guid[] skillIds, Guid[] modelIds, CancellationToken ct = default);

    // ---------- LLM Provider ----------
    Task<IReadOnlyList<LlmProvider>> ListProvidersAsync(CancellationToken ct = default);

    Task<LlmProvider?> GetProviderAsync(Guid id, CancellationToken ct = default);

    Task<LlmProvider> CreateProviderAsync(LlmProvider provider, CancellationToken ct = default);

    Task UpdateProviderAsync(LlmProvider provider, CancellationToken ct = default);

    Task DeleteProviderAsync(Guid id, CancellationToken ct = default);

    // ---------- LLM Model（供应商下的模型，每个模型独立配置） ----------
    Task<LlmModel?> GetModelAsync(Guid modelId, CancellationToken ct = default);

    Task<LlmModel> AddModelAsync(LlmModel model, CancellationToken ct = default);

    Task UpdateModelAsync(LlmModel model, CancellationToken ct = default);

    Task DeleteModelAsync(Guid modelId, CancellationToken ct = default);

    // ---------- MCP Server ----------
    Task<IReadOnlyList<McpServer>> ListMcpServersAsync(bool includeItems = false, CancellationToken ct = default);

    Task<McpServer?> GetMcpServerAsync(Guid id, bool includeItems = false, CancellationToken ct = default);

    Task<McpServer> CreateMcpServerAsync(McpServer server, CancellationToken ct = default);

    Task UpdateMcpServerAsync(McpServer server, CancellationToken ct = default);

    Task DeleteMcpServerAsync(Guid id, CancellationToken ct = default);

    /// <summary>替换自动带出的能力项（tools/prompts/resources），保留用户禁用状态</summary>
    Task SyncMcpCatalogAsync(Guid serverId, IReadOnlyList<McpCatalogItem> discovered, CancellationToken ct = default);

    Task SetMcpItemEnabledAsync(Guid itemId, bool enabled, CancellationToken ct = default);

    // ---------- Prompt ----------
    Task<IReadOnlyList<Prompt>> ListPromptsAsync(CancellationToken ct = default);

    Task<Prompt?> GetPromptAsync(Guid id, CancellationToken ct = default);

    Task<Prompt> CreatePromptAsync(Prompt prompt, CancellationToken ct = default);

    Task UpdatePromptAsync(Prompt prompt, CancellationToken ct = default);

    Task DeletePromptAsync(Guid id, CancellationToken ct = default);

    // ---------- Skill ----------
    Task<IReadOnlyList<Skill>> ListSkillsAsync(CancellationToken ct = default);

    Task<Skill?> GetSkillAsync(Guid id, CancellationToken ct = default);

    Task<Skill> CreateSkillAsync(Skill skill, CancellationToken ct = default);

    Task UpdateSkillAsync(Skill skill, CancellationToken ct = default);

    Task DeleteSkillAsync(Guid id, CancellationToken ct = default);

    // ---------- 内部鉴权（acs / ucs…） ----------
    Task<InternalAuthProvider?> GetInternalAuthProviderByNameAsync(string name, CancellationToken ct = default);

    Task<IReadOnlyList<InternalAuthProvider>> ListInternalAuthProvidersAsync(CancellationToken ct = default);

    /// <summary>新建（id=Guid.Empty）或整体更新一条内部鉴权配置（成功判定规则与默认角色全量替换）</summary>
    Task<InternalAuthProvider> SaveInternalAuthProviderAsync(
        Guid id, string name, string api, string httpMethod, string requestFormat,
        string usernameField, string passwordField, bool enabled, int timeoutSeconds,
        IReadOnlyList<(string Field, SuccessRuleOperator Operator, string? ExpectedValue)> successRules,
        Guid[] roleIds, CancellationToken ct = default);

    Task DeleteInternalAuthProviderAsync(Guid id, CancellationToken ct = default);

    // ---------- 刷新令牌（refresh token：存哈希 / 轮换 / 禁用撤销） ----------
    /// <summary>登记新刷新令牌（创建前顺手清理该用户已过期的令牌）</summary>
    Task CreateRefreshTokenAsync(Guid userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct = default);

    /// <summary>按 SHA-256 哈希取刷新令牌（含用户与其当前角色，供重签 access）</summary>
    Task<UserRefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken ct = default);

    /// <summary>撤销某刷新令牌（轮换时调用；replacedByTokenHash=替换它的新令牌哈希）</summary>
    Task RevokeRefreshTokenAsync(string tokenHash, string? replacedByTokenHash, CancellationToken ct = default);

    /// <summary>撤销某用户的全部未撤销刷新令牌（用户被禁用时调用）</summary>
    Task<int> RevokeRefreshTokensForUserAsync(Guid userId, CancellationToken ct = default);

    // ---------- 沉浸式工具栏（管理端维护 + 用户可用列表） ----------
    Task<IReadOnlyList<AppTool>> ListToolsAsync(CancellationToken ct = default);

    /// <summary>按唯一标识取工具（管理端查重）</summary>
    Task<AppTool?> GetToolByKeyAsync(string toolKey, CancellationToken ct = default);

    /// <summary>新建（id=Guid.Empty）或整体更新工具（角色绑定全量替换）</summary>
    Task<AppTool> SaveToolAsync(Guid id, string toolKey, string name, string icon, string? description, string? baseUrl, bool enabled, Guid[] roleIds, CancellationToken ct = default);

    Task DeleteToolAsync(Guid id, CancellationToken ct = default);

    /// <summary>当前用户可见的启用工具：admin 全量；普通用户=启用且绑定角色命中（未绑定角色的工具仅 admin 可见）</summary>
    Task<IReadOnlyList<AppTool>> ListToolsForUserAsync(Guid[] roleIds, bool isAdmin, CancellationToken ct = default);

    // ---------- 审计 ----------
    Task<IReadOnlyList<AuditLog>> QueryAuditLogsAsync(Guid? userId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct = default);
}
