using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;
using NextChats.Core.Entities;
using NextChats.Infrastructure.Data;

namespace NextChats.Infrastructure.Data;

/// <summary>
/// 统一数据存储（单例）：基于 IDbContextFactory（EF Core 池化），内存缓存高频配置；
/// 后续可平滑迁移到 Redis 缓存 + MySQL 实例（同一接口）。
/// </summary>
public sealed class NextChatsStore : IConfigStore, IChatStore, IAdminStore, ITeamStore
{
    private readonly IDbContextFactory<NextChatsDbContext> _db;
    private readonly ICacheService _cache;

    public NextChatsStore(IDbContextFactory<NextChatsDbContext> db, ICacheService cache)
    {
        _db = db;
        _cache = cache;
    }

    // ================= IConfigStore =================

    public async Task<IReadOnlyList<LlmProvider>> GetActiveProvidersAsync(CancellationToken ct = default)
    {
        return await _cache.GetOrAddAsync("cfg:providers", async token =>
        {
            await using var db = await _db.CreateDbContextAsync(token);
            return await db.LlmProviders.AsNoTracking()
                .Include(p => p.Models)
                .Where(p => p.Enabled)
                .ToListAsync(token);
        }, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<LlmProvider?> GetProviderAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.LlmProviders.AsNoTracking().Include(p => p.Models).FirstOrDefaultAsync(p => p.Id == id, ct);
    }

    public async Task<IReadOnlyList<McpServer>> GetEnabledMcpServersAsync(CancellationToken ct = default)
    {
        return await _cache.GetOrAddAsync("cfg:mcps", async token =>
        {
            await using var db = await _db.CreateDbContextAsync(token);
            return await db.McpServers.AsNoTracking()
                .Include(m => m.Items)
                .Where(m => m.Enabled)
                .ToListAsync(token);
        }, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<McpServer?> GetMcpServerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.McpServers.AsNoTracking().Include(m => m.Items).FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<IReadOnlyList<Skill>> GetEnabledSkillsAsync(CancellationToken ct = default)
    {
        return await _cache.GetOrAddAsync("cfg:skills", async token =>
        {
            await using var db = await _db.CreateDbContextAsync(token);
            return await db.Skills.AsNoTracking().Where(s => s.Enabled).ToListAsync(token);
        }, TimeSpan.FromSeconds(30), ct);
    }

    public Task<Skill?> GetSkillAsync(Guid id, CancellationToken ct = default)
    {
        return GetAsync(db => db.Skills.AsNoTracking().FirstOrDefaultAsync(s => s.Id == id, ct));
    }

    public Task<Prompt?> GetPromptAsync(Guid id, CancellationToken ct = default)
    {
        return GetAsync(db => db.Prompts.AsNoTracking().FirstOrDefaultAsync(p => p.Id == id, ct));
    }

    public async Task<IReadOnlyList<Prompt>> GetEnabledPromptsAsync(CancellationToken ct = default)
    {
        return await _cache.GetOrAddAsync("cfg:prompts", async token =>
        {
            await using var db = await _db.CreateDbContextAsync(token);
            return await db.Prompts.AsNoTracking().Where(p => p.Enabled).ToListAsync(token);
        }, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<(Guid[] McpServerIds, Guid[] PromptIds, Guid[] SkillIds, Guid[] ModelIds)> GetRoleBindingsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _cache.GetOrAddAsync($"rbac:{userId}", async token => await LoadBindingsAsync(userId, token),
            TimeSpan.FromMinutes(2), ct);
    }

    private async Task<(Guid[] McpServerIds, Guid[] PromptIds, Guid[] SkillIds, Guid[] ModelIds)> LoadBindingsAsync(Guid userId, CancellationToken ct)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var roleIds = await db.Users.AsNoTracking()
            .Where(u => u.Id == userId)
            .SelectMany(u => u.Roles.Select(r => r.Id))
            .ToListAsync(ct);

        var mcpIds = await db.Set<Dictionary<string, object>>("RoleMcpBindings")
            .Where(x => roleIds.Contains((Guid)x["RolesId"]))
            .Select(x => (Guid)x["McpServersId"])
            .ToListAsync(ct);
        var promptIds = await db.Set<Dictionary<string, object>>("RolePromptBindings")
            .Where(x => roleIds.Contains((Guid)x["RolesId"]))
            .Select(x => (Guid)x["PromptsId"])
            .ToListAsync(ct);
        var skillIds = await db.Set<Dictionary<string, object>>("RoleSkillBindings")
            .Where(x => roleIds.Contains((Guid)x["RolesId"]))
            .Select(x => (Guid)x["SkillsId"])
            .ToListAsync(ct);
        var modelIds = await db.Set<Dictionary<string, object>>("RoleModelBindings")
            .Where(x => roleIds.Contains((Guid)x["RolesId"]))
            .Select(x => (Guid)x["ModelsId"])
            .ToListAsync(ct);

        return (mcpIds.Distinct().ToArray(), promptIds.Distinct().ToArray(), skillIds.Distinct().ToArray(), modelIds.Distinct().ToArray());
    }

    /// <summary>用户被角色绑定授权的 LLM 模型集合（用于前端模型可见性过滤）</summary>
    public async Task<Guid[]> GetRoleModelIdsAsync(Guid userId, CancellationToken ct = default)
    {
        var (_, _, _, modelIds) = await GetRoleBindingsAsync(userId, ct);
        return modelIds;
    }

    public async Task<bool> IsAdminAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking()
            .AnyAsync(u => u.Id == userId && u.Roles.Any(r => r.Code == "admin"), ct);
    }

    public async Task<bool> CanAccessMcpAsync(Guid userId, Guid mcpServerId, CancellationToken ct = default)
    {
        var (ids, _, _, _) = await GetRoleBindingsAsync(userId, ct);
        return ids.Contains(mcpServerId);
    }

    public async Task<bool> CanAccessPromptAsync(Guid userId, Guid promptId, CancellationToken ct = default)
    {
        var (_, ids, _, _) = await GetRoleBindingsAsync(userId, ct);
        return ids.Contains(promptId);
    }

    public async Task<bool> CanAccessSkillAsync(Guid userId, Guid skillId, CancellationToken ct = default)
    {
        var (_, _, ids, _) = await GetRoleBindingsAsync(userId, ct);
        return ids.Contains(skillId);
    }

    // ================= 工作空间（编码会话） =================

    public async Task<IReadOnlyList<Workspace>> GetAllWorkspacesAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.Workspaces.AsNoTracking().OrderBy(w => w.Name).ToListAsync(ct);
    }

    public async Task<IReadOnlyList<Workspace>> GetEnabledWorkspacesAsync(CancellationToken ct = default)
    {
        return await _cache.GetOrAddAsync("cfg:workspaces", async token =>
        {
            await using var db = await _db.CreateDbContextAsync(token);
            return (IReadOnlyList<Workspace>)await db.Workspaces.AsNoTracking()
                .Where(w => w.Enabled).OrderBy(w => w.Name).ToListAsync(token);
        }, TimeSpan.FromSeconds(30), ct);
    }

    public async Task<Workspace?> GetWorkspaceAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.Workspaces.AsNoTracking().FirstOrDefaultAsync(w => w.Id == id, ct);
    }

    /// <summary>用户经角色绑定可用的工作空间 + 级别；管理员默认豁免 = 全部启用工作空间 + FullAccess(30)（便于管理员以最高级别进入任何工作空间），但显式角色绑定取更高级别（如自主模式 40 生效）</summary>
    public async Task<IReadOnlyList<(Guid WorkspaceId, WorkspaceAccessLevel Level)>> GetRoleWorkspaceBindingsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _cache.GetOrAddAsync($"rbac:ws:{userId}", async token =>
        {
            await using var db = await _db.CreateDbContextAsync(token);
            var roleIds = await db.Users.AsNoTracking()
                .Where(u => u.Id == userId)
                .SelectMany(u => u.Roles.Select(r => r.Id))
                .ToListAsync(token);
            if (roleIds.Count == 0) return new List<(Guid, WorkspaceAccessLevel)>();

            var enabledIds = await db.Workspaces.AsNoTracking().Where(w => w.Enabled).Select(w => w.Id).ToListAsync(token);
            var rowList = await db.RoleWorkspaceBindings.AsNoTracking()
                .Where(x => roleIds.Contains(x.RoleId) && enabledIds.Contains(x.WorkspaceId))
                .Select(x => new { x.WorkspaceId, x.Level })
                .ToListAsync(token);
            // 同一用户多角色重复绑定时取最高级别
            var explicitMax = rowList
                .GroupBy(x => x.WorkspaceId)
                .Select(g => (WorkspaceId: g.Key, Level: g.Max(x => x.Level)))
                .ToDictionary(x => x.WorkspaceId, x => x.Level);

            var isAdmin = await db.Users.AsNoTracking()
                .AnyAsync(u => u.Id == userId && u.Roles.Any(r => r.Code == "admin"), token);
            if (isAdmin)
            {
                // 管理员默认 FullAccess(30)；显式绑定了更高级别（如自主模式 40）则取之 —— 管理员"默认仍为完全访问"，但尊重显式配置
                return enabledIds.Select(id =>
                {
                    var level = explicitMax.TryGetValue(id, out var lv) ? lv : WorkspaceAccessLevel.FullAccess;
                    if ((int)level < (int)WorkspaceAccessLevel.FullAccess) level = WorkspaceAccessLevel.FullAccess;
                    return (id, level);
                }).ToList();
            }

            return explicitMax
                .Select(kv => (kv.Key, kv.Value))
                .ToList();
        }, TimeSpan.FromMinutes(2), ct);
    }

    public async Task<IReadOnlyList<RoleWorkspaceBinding>> GetWorkspaceBindingsAsync(Guid workspaceId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.RoleWorkspaceBindings.AsNoTracking()
            .Where(x => x.WorkspaceId == workspaceId)
            .ToListAsync(ct);
    }

    public async Task SaveWorkspaceAsync(Workspace workspace, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var existing = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == workspace.Id, ct);
        if (existing is null)
        {
            workspace.CreatedAt = DateTimeOffset.UtcNow;
            workspace.UpdatedAt = DateTimeOffset.UtcNow;
            db.Workspaces.Add(workspace);
        }
        else
        {
            existing.Name = workspace.Name;
            existing.RootPath = workspace.RootPath;
            existing.Description = workspace.Description;
            existing.Enabled = workspace.Enabled;
            existing.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
        await _cache.RemoveByPrefixAsync("rbac:", ct);
    }

    public async Task DeleteWorkspaceAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var ws = await db.Workspaces.FirstOrDefaultAsync(w => w.Id == id, ct);
        if (ws is null) return;
        db.Workspaces.Remove(ws);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
        await _cache.RemoveByPrefixAsync("rbac:", ct);
    }

    public async Task SetWorkspaceRoleBindingsAsync(Guid workspaceId, IReadOnlyList<(Guid RoleId, WorkspaceAccessLevel Level)> bindings, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var old = await db.RoleWorkspaceBindings.Where(x => x.WorkspaceId == workspaceId).ToListAsync(ct);
        db.RoleWorkspaceBindings.RemoveRange(old);
        foreach (var (roleId, level) in bindings)
        {
            db.RoleWorkspaceBindings.Add(new RoleWorkspaceBinding { RoleId = roleId, WorkspaceId = workspaceId, Level = level });
        }
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("rbac:", ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    public async Task<IDictionary<string, string>> GetUserSettingsAsync(Guid userId, CancellationToken ct = default)
    {
        return await _cache.GetOrAddAsync($"settings:{userId}", async token =>
        {
            await using var db = await _db.CreateDbContextAsync(token);
            return await db.UserSettings.AsNoTracking()
                .Where(s => s.UserId == userId)
                .ToDictionaryAsync(s => s.Key, s => s.ValueJson, token);
        }, TimeSpan.FromSeconds(30), ct);
    }

    public async Task SetUserSettingAsync(Guid userId, string key, string valueJson, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.UserSettings.FirstOrDefaultAsync(s => s.UserId == userId && s.Key == key, ct);
        if (row is null)
        {
            db.UserSettings.Add(new UserSetting { UserId = userId, Key = key, ValueJson = valueJson });
        }
        else
        {
            row.ValueJson = valueJson;
            row.UpdatedAt = DateTimeOffset.UtcNow;
        }
        await db.SaveChangesAsync(ct);
        await _cache.RemoveAsync($"settings:{userId}", ct);
    }

    public Task InvalidateConfigCacheAsync(CancellationToken ct = default)
    {
        // 配置变更低频 → 全量缓存失效（Redis 版本可精确删除前缀）
        return _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    // ================= IChatStore =================

    public async Task<ChatSession> CreateSessionAsync(Guid userId, string title, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var session = new ChatSession { UserId = userId, Title = title };
        db.ChatSessions.Add(session);
        await db.SaveChangesAsync(ct);
        return session;
    }

    public async Task<ChatSession?> GetSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.ChatSessions.AsNoTracking()
            .FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct);
    }

    public async Task<IReadOnlyList<ChatSession>> ListSessionsAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        // SQLite 不支持含 COALESCE（??）的 DateTimeOffset 参与 ORDER BY 表达式 → 取回内存排序。
        // 排序：置顶会话优先（置顶区内按置顶时间倒序）→ 其余按最近消息/创建时间倒序
        var rows = await db.ChatSessions.AsNoTracking()
            .Where(s => s.UserId == userId)
            .ToListAsync(ct);
        return rows
            .OrderByDescending(s => s.IsPinned)
            .ThenByDescending(s => s.PinnedAt ?? s.UpdatedAt)
            .ThenByDescending(s => s.LastMessageAt ?? s.CreatedAt)
            .ToList();
    }

    public async Task UpdateSessionAsync(ChatSession session, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == session.Id && s.UserId == session.UserId, ct);
        if (row is null) return;
        row.Title = session.Title;
        row.Status = session.Status;
        row.ContextJson = session.ContextJson;
        row.UpdatedAt = session.UpdatedAt;
        row.LastMessageAt = session.LastMessageAt;
        row.LlmProviderId = session.LlmProviderId;
        row.IsPinned = session.IsPinned;
        row.PinnedAt = session.PinnedAt;
        row.WorkspaceId = session.WorkspaceId;
        row.TeamMode = session.TeamMode;
        row.TeamConfigJson = session.TeamConfigJson;
        await db.SaveChangesAsync(ct);
    }

    public async Task<bool> SetSessionPinnedAsync(Guid userId, Guid sessionId, bool pinned, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct);
        if (row is null) return false;
        row.IsPinned = pinned;
        row.PinnedAt = pinned ? DateTimeOffset.UtcNow : null;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> SetSessionWorkspaceAsync(Guid userId, Guid sessionId, Guid? workspaceId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct);
        if (row is null) return false;
        row.WorkspaceId = workspaceId;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> RenameSessionAsync(Guid userId, Guid sessionId, string title, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct);
        if (row is null) return false;
        row.Title = title.Trim().Length > 0 ? title.Trim() : row.Title;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task DeleteSessionAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var session = await db.ChatSessions.FirstOrDefaultAsync(s => s.Id == sessionId && s.UserId == userId, ct);
        if (session is null) return;
        await db.TeamEngineers.Where(e => e.SessionId == sessionId).ExecuteDeleteAsync(ct);
        db.ChatSessions.Remove(session);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<ChatMessage>> ListMessagesAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.UserId == userId)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChatTopicBrief>> ListTopicsAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.UserId == userId && m.Role == ChatRole.User)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => new ChatTopicBrief(m.Id, m.Content ?? string.Empty))
            .ToListAsync(ct);
    }

    public async Task<IReadOnlyList<ChatMessage>> ListMessagesWindowAsync(Guid userId, Guid sessionId, int limit, Guid? beforeId = null, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var baseQuery = db.ChatMessages.AsNoTracking().Where(m => m.SessionId == sessionId && m.UserId == userId);

        DateTimeOffset beforeTs = DateTimeOffset.MaxValue;
        if (beforeId.HasValue)
        {
            var anchor = await baseQuery.Where(m => m.Id == beforeId.Value)
                .Select(m => new { m.CreatedAt }).FirstOrDefaultAsync(ct);
            if (anchor is null) return [];
            beforeTs = anchor.CreatedAt;
        }

        var rows = await baseQuery
            .Where(m => beforeId.HasValue
                ? m.CreatedAt < beforeTs || (m.CreatedAt == beforeTs && m.Id < beforeId.Value)
                : true)
            .OrderByDescending(m => m.CreatedAt).ThenByDescending(m => m.Id)
            .Take(limit)
            .ToListAsync(ct);
        rows.Reverse();
        return rows;
    }

    public async Task<ChatMessage> AppendMessageAsync(ChatMessage message, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);

        // 幂等：同一 (UserId, ClientMessageId) 只落一条
        if (!string.IsNullOrWhiteSpace(message.ClientMessageId))
        {
            var existing = await db.ChatMessages
                .FirstOrDefaultAsync(m => m.UserId == message.UserId && m.ClientMessageId == message.ClientMessageId, ct);
            if (existing is not null) return existing;
        }

        db.ChatMessages.Add(message);
        await db.SaveChangesAsync(ct);
        return message;
    }

    public async Task<bool> TruncateFromMessageAsync(Guid userId, Guid sessionId, Guid messageId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        // 目标消息必须属于该会话，且按 DB 顺序位于其后（含自身）的消息一并删除
        var target = await db.ChatMessages
            .FirstOrDefaultAsync(m => m.Id == messageId && m.SessionId == sessionId && m.UserId == userId, ct);
        if (target is null) return false;

        var ordered = await db.ChatMessages.AsNoTracking()
            .Where(m => m.SessionId == sessionId && m.UserId == userId)
            .OrderBy(m => m.CreatedAt).ThenBy(m => m.Id)
            .Select(m => m.Id)
            .ToListAsync(ct);
        var index = ordered.IndexOf(messageId);
        if (index < 0) return false;

        var toRemove = ordered.Skip(index).ToArray();
        var entities = await db.ChatMessages
            .Where(m => toRemove.Contains(m.Id))
            .ToListAsync(ct);
        db.ChatMessages.RemoveRange(entities);
        await db.SaveChangesAsync(ct);
        return true;
    }

    // ==================== 用户收藏（按用户隔离） ====================

    public async Task<IReadOnlyList<UserFavorite>> ListFavoritesAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.UserFavorites.AsNoTracking()
            .Where(f => f.UserId == userId)
            .OrderByDescending(f => f.CreatedAt)
            .ToListAsync(ct);
    }

    /// <summary>按来源问题消息查收藏（去重判断：同一问题只收藏一次）</summary>
    public async Task<UserFavorite?> FindFavoriteByQuestionAsync(Guid userId, Guid? questionMessageId, CancellationToken ct = default)
    {
        if (questionMessageId is null) return null;
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.UserFavorites.AsNoTracking()
            .FirstOrDefaultAsync(f => f.UserId == userId && f.QuestionMessageId == questionMessageId, ct);
    }

    public async Task<UserFavorite> AddFavoriteAsync(UserFavorite favorite, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        db.UserFavorites.Add(favorite);
        await db.SaveChangesAsync(ct);
        return favorite;
    }

    public async Task<bool> RenameFavoriteAsync(Guid userId, Guid id, string title, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.UserFavorites.FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId, ct);
        if (row is null) return false;
        row.Title = title.Trim().Length > 0 ? title.Trim() : row.Title;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<bool> DeleteFavoriteAsync(Guid userId, Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.UserFavorites.FirstOrDefaultAsync(f => f.Id == id && f.UserId == userId, ct);
        if (row is null) return false;
        db.UserFavorites.Remove(row);
        await db.SaveChangesAsync(ct);
        return true;
    }

    public async Task<ToolApproval> CreateApprovalAsync(ToolApproval approval, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        db.ToolApprovals.Add(approval);
        await db.SaveChangesAsync(ct);
        return approval;
    }

    public async Task<ToolApproval?> GetApprovalAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.ToolApprovals.AsNoTracking().FirstOrDefaultAsync(a => a.Id == id, ct);
    }

    public async Task<IReadOnlyList<ToolApproval>> ListApprovalsAsync(Guid? userId, ApprovalStatus? status, int take, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var query = db.ToolApprovals.AsNoTracking().AsQueryable();
        if (userId.HasValue) query = query.Where(a => a.UserId == userId.Value);
        if (status.HasValue) query = query.Where(a => a.Status == status.Value);
        return await query.OrderByDescending(a => a.CreatedAt).Take(take).ToListAsync(ct);
    }

    public async Task<ToolApproval> UpdateApprovalAsync(ToolApproval approval, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        db.ToolApprovals.Update(approval);
        await db.SaveChangesAsync(ct);
        return approval;
    }

    public async Task RecordUsageAsync(TokenUsageRecord record, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        db.TokenUsageRecords.Add(record);
        await db.SaveChangesAsync(ct);
    }

    public async Task<IReadOnlyList<TokenUsageRecord>> QueryUsageAsync(Guid? userId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var query = db.TokenUsageRecords.AsNoTracking().Where(r => r.CreatedAt >= from && r.CreatedAt < to);
        if (userId.HasValue) query = query.Where(r => r.UserId == userId.Value);
        return await query.OrderByDescending(r => r.CreatedAt).Take(take).ToListAsync(ct);
    }

    public async Task<IdempotencyRecord?> GetIdempotencyAsync(Guid userId, string key, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.IdempotencyRecords.AsNoTracking().FirstOrDefaultAsync(r => r.UserId == userId && r.Key == key, ct);
    }

    public async Task StoreIdempotencyAsync(Guid userId, string key, string responseJson, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var exists = await db.IdempotencyRecords.AnyAsync(r => r.UserId == userId && r.Key == key, ct);
        if (!exists)
        {
            db.IdempotencyRecords.Add(new IdempotencyRecord { UserId = userId, Key = key, ResponseJson = responseJson });
            await db.SaveChangesAsync(ct);
        }
    }

    // ================= IAdminStore =================

    public async Task<IReadOnlyList<AppUser>> ListUsersAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.Users.AsNoTracking().Include(u => u.Roles).ToListAsync(ct);
    }

    public async Task<AppUser?> GetUserAsync(Guid id, bool includeRoles = false, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var query = db.Users.AsNoTracking().AsQueryable();
        if (includeRoles) query = query.Include(u => u.Roles);
        return await query.FirstOrDefaultAsync(u => u.Id == id, ct);
    }

    public Task<AppUser?> GetUserByNameAsync(string username, CancellationToken ct = default)
    {
        return GetAsync(db => db.Users.AsNoTracking().Include(u => u.Roles)
            .FirstOrDefaultAsync(u => u.Username == username, ct));
    }

    public Task<AppUser?> GetUserAsync(string authType, string username, bool includeRoles = true, CancellationToken ct = default)
    {
        return GetAsync(async db =>
        {
            var query = db.Users.AsNoTracking().AsQueryable();
            if (includeRoles) query = query.Include(u => u.Roles);
            return await query.FirstOrDefaultAsync(u => u.AuthType == authType && u.Username == username, ct);
        });
    }

    public async Task<AppUser> CreateUserAsync(AppUser user, IEnumerable<Guid> roleIds, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var roles = await db.Roles.Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);
        user.Roles = roles;
        db.Users.Add(user);
        await db.SaveChangesAsync(ct);
        return user;
    }

    public async Task UpdateUserAsync(AppUser user, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.Users.FirstOrDefaultAsync(u => u.Id == user.Id, ct);
        if (row is null) return;
        row.DisplayName = user.DisplayName;
        row.Email = user.Email;
        row.Status = user.Status;
        row.IsReadonly = user.IsReadonly;
        if (!string.IsNullOrWhiteSpace(user.PasswordHash) && !string.IsNullOrWhiteSpace(user.PasswordSalt))
        {
            row.PasswordHash = user.PasswordHash;
            row.PasswordSalt = user.PasswordSalt;
        }
        row.LastLoginAt = user.LastLoginAt ?? row.LastLoginAt;
        await db.SaveChangesAsync(ct);
        await _cache.RemoveAsync($"rbac:{user.Id}", ct);
    }

    public async Task SetUserRolesAsync(Guid userId, IEnumerable<Guid> roleIds, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var user = await db.Users.Include(u => u.Roles).FirstOrDefaultAsync(u => u.Id == userId, ct);
        if (user is null) return;
        user.Roles.Clear();
        var roles = await db.Roles.Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);
        foreach (var role in roles) user.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveAsync($"rbac:{userId}", ct);
        await _cache.RemoveByPrefixAsync("rbac:", ct);
    }

    public async Task DeleteUserAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var user = await db.Users.FirstOrDefaultAsync(u => u.Id == id, ct);
        if (user is null) return;
        db.Users.Remove(user);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("rbac:", ct);
    }

    public async Task<IReadOnlyList<AppRole>> ListRolesAsync(bool includeBindings = false, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var query = db.Roles.AsNoTracking().AsQueryable();
        if (includeBindings)
        {
            query = query.Include(r => r.McpServers).Include(r => r.Prompts).Include(r => r.Skills).Include(r => r.Models);
        }
        return await query.ToListAsync(ct);
    }

    public async Task<AppRole?> GetRoleAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.Roles.AsNoTracking().Include(r => r.McpServers).Include(r => r.Prompts).Include(r => r.Skills)
            .FirstOrDefaultAsync(r => r.Id == id, ct);
    }

    public async Task<AppRole> CreateRoleAsync(AppRole role, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        db.Roles.Add(role);
        await db.SaveChangesAsync(ct);
        return role;
    }

    public async Task UpdateRoleAsync(AppRole role, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.Roles.FirstOrDefaultAsync(r => r.Id == role.Id, ct);
        if (row is null) return;
        row.Name = role.Name;
        row.Description = role.Description;
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("rbac:", ct);
    }

    public async Task DeleteRoleAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var role = await db.Roles.FirstOrDefaultAsync(r => r.Id == id, ct);
        if (role is null) return;
        if (role.IsSystem) throw new InvalidOperationException("内置角色不可删除");
        db.Roles.Remove(role);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("rbac:", ct);
    }

    public async Task SetRoleBindingsAsync(Guid roleId, Guid[] mcpIds, Guid[] promptIds, Guid[] skillIds, Guid[] modelIds, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var role = await db.Roles
            .Include(r => r.McpServers).Include(r => r.Prompts).Include(r => r.Skills).Include(r => r.Models)
            .FirstOrDefaultAsync(r => r.Id == roleId, ct);
        if (role is null) return;

        role.McpServers.Clear();
        role.Prompts.Clear();
        role.Skills.Clear();
        role.Models.Clear();
        await db.SaveChangesAsync(ct); // 先清除关联

        role.McpServers = await db.McpServers.Where(m => mcpIds.Contains(m.Id)).ToListAsync(ct);
        role.Prompts = await db.Prompts.Where(p => promptIds.Contains(p.Id)).ToListAsync(ct);
        role.Skills = await db.Skills.Where(s => skillIds.Contains(s.Id)).ToListAsync(ct);
        role.Models = await db.LlmModels.Where(m => modelIds.Contains(m.Id)).ToListAsync(ct);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("rbac:", ct);
    }

    public async Task<IReadOnlyList<LlmProvider>> ListProvidersAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.LlmProviders.AsNoTracking().Include(p => p.Models).OrderBy(p => p.Priority).ToListAsync(ct);
    }

    public async Task<LlmProvider> CreateProviderAsync(LlmProvider provider, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        db.LlmProviders.Add(provider);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
        return provider;
    }

    public async Task UpdateProviderAsync(LlmProvider provider, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.LlmProviders.FirstOrDefaultAsync(p => p.Id == provider.Id, ct);
        if (row is null) return;
        row.Name = provider.Name;
        row.Kind = provider.Kind;
        row.BaseUrl = provider.BaseUrl;
        if (!string.IsNullOrWhiteSpace(provider.ApiKeyEncrypted)) row.ApiKeyEncrypted = provider.ApiKeyEncrypted;
        row.TimeoutSeconds = provider.TimeoutSeconds;
        row.Enabled = provider.Enabled;
        row.Priority = provider.Priority;
        row.ThinkingParam = provider.ThinkingParam;
        row.IsHealthy = provider.IsHealthy;
        row.LastError = provider.LastError;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    // ================= LLM Model（供应商下的模型） =================

    public async Task<LlmModel?> GetModelAsync(Guid modelId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.LlmModels.AsNoTracking().FirstOrDefaultAsync(m => m.Id == modelId, ct);
    }

    public async Task<LlmModel> AddModelAsync(LlmModel model, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        db.LlmModels.Add(model);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
        return model;
    }

    public async Task UpdateModelAsync(LlmModel model, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.LlmModels.FirstOrDefaultAsync(m => m.Id == model.Id, ct);
        if (row is null) return;
        row.Name = model.Name;
        row.Enabled = model.Enabled;
        row.IsVision = model.IsVision;
        row.ContextWindow = model.ContextWindow;
        row.PriceInPer1K = model.PriceInPer1K;
        row.PriceOutPer1K = model.PriceOutPer1K;
        row.Priority = model.Priority;
        row.ThinkingEffort = model.ThinkingEffort;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    public async Task DeleteModelAsync(Guid modelId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.LlmModels.FirstOrDefaultAsync(m => m.Id == modelId, ct);
        if (row is null) return;
        db.LlmModels.Remove(row);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    public async Task DeleteProviderAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.LlmProviders.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null) return;
        db.LlmProviders.Remove(row);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    public async Task<IReadOnlyList<McpServer>> ListMcpServersAsync(bool includeItems = false, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var query = db.McpServers.AsNoTracking().AsQueryable();
        if (includeItems) query = query.Include(m => m.Items);
        return await query.OrderBy(m => m.Name).ToListAsync(ct);
    }

    public async Task<McpServer?> GetMcpServerAsync(Guid id, bool includeItems = false, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var query = db.McpServers.AsNoTracking().AsQueryable();
        if (includeItems) query = query.Include(m => m.Items);
        return await query.FirstOrDefaultAsync(m => m.Id == id, ct);
    }

    public async Task<McpServer> CreateMcpServerAsync(McpServer server, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        db.McpServers.Add(server);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
        return server;
    }

    public async Task UpdateMcpServerAsync(McpServer server, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.McpServers.FirstOrDefaultAsync(m => m.Id == server.Id, ct);
        if (row is null) return;
        row.Name = server.Name;
        row.Transport = server.Transport;
        row.Endpoint = server.Endpoint;
        if (!string.IsNullOrWhiteSpace(server.HeadersJson)) { row.HeadersJson = server.HeadersJson; row.IsHeadersEncrypted = server.IsHeadersEncrypted; }
        row.StdioCommand = server.StdioCommand;
        row.StdioArgsJson = server.StdioArgsJson;
        row.Enabled = server.Enabled;
        row.IsVision = server.IsVision;
        row.TimeoutSeconds = server.TimeoutSeconds;
        row.Description = server.Description;
        row.Instructions = server.Instructions;
        row.MetadataJson = server.MetadataJson;
        row.LastError = server.LastError;
        row.LastFetchedAt = server.LastFetchedAt;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    public async Task DeleteMcpServerAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.McpServers.FirstOrDefaultAsync(m => m.Id == id, ct);
        if (row is null) return;
        db.McpServers.Remove(row);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    public async Task SyncMcpCatalogAsync(Guid serverId, IReadOnlyList<McpCatalogItem> discovered, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var server = await db.McpServers.Include(m => m.Items).FirstOrDefaultAsync(m => m.Id == serverId, ct);
        if (server is null) return;

        var existing = server.Items.ToDictionary(i => (i.Kind, i.Name));
        var keep = new HashSet<Guid>();
        foreach (var item in discovered)
        {
            if (existing.TryGetValue((item.Kind, item.Name), out var old))
            {
                old.Description = item.Description;
                old.SchemaJson = item.SchemaJson;
                keep.Add(old.Id);
            }
            else
            {
                item.McpServerId = serverId;
                db.McpCatalogItems.Add(item);
                keep.Add(item.Id);
            }
        }
        foreach (var remove in server.Items.Where(i => !keep.Contains(i.Id)).ToList())
        {
            db.McpCatalogItems.Remove(remove);
        }
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    public async Task SetMcpItemEnabledAsync(Guid itemId, bool enabled, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var item = await db.McpCatalogItems.FirstOrDefaultAsync(i => i.Id == itemId, ct);
        if (item is null) return;
        item.Enabled = enabled;
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    public async Task<IReadOnlyList<Prompt>> ListPromptsAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.Prompts.AsNoTracking().OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<Prompt> CreatePromptAsync(Prompt prompt, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        db.Prompts.Add(prompt);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
        return prompt;
    }

    public async Task UpdatePromptAsync(Prompt prompt, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.Prompts.FirstOrDefaultAsync(p => p.Id == prompt.Id, ct);
        if (row is null) return;
        row.Name = prompt.Name;
        row.Description = prompt.Description;
        row.Summary = prompt.Summary;
        row.Content = prompt.Content;
        row.Enabled = prompt.Enabled;
        row.TagsJson = prompt.TagsJson;
        row.Version = prompt.Version + 1;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    public async Task DeletePromptAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.Prompts.FirstOrDefaultAsync(p => p.Id == id, ct);
        if (row is null) return;
        db.Prompts.Remove(row);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    public async Task<IReadOnlyList<Skill>> ListSkillsAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.Skills.AsNoTracking().OrderBy(s => s.Name).ToListAsync(ct);
    }

    public async Task<Skill> CreateSkillAsync(Skill skill, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        db.Skills.Add(skill);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
        return skill;
    }

    public async Task UpdateSkillAsync(Skill skill, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.Skills.FirstOrDefaultAsync(s => s.Id == skill.Id, ct);
        if (row is null) return;
        row.Name = skill.Name;
        row.Description = skill.Description;
        row.Summary = skill.Summary;
        row.MetaToolName = skill.MetaToolName;
        row.Instruction = skill.Instruction;
        row.ExampleInput = skill.ExampleInput;
        row.ExampleOutput = skill.ExampleOutput;
        row.Enabled = skill.Enabled;
        row.ModelOverride = skill.ModelOverride;
        row.MaxNestedSteps = skill.MaxNestedSteps;
        row.UpdatedAt = DateTimeOffset.UtcNow;
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    public async Task DeleteSkillAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var row = await db.Skills.FirstOrDefaultAsync(s => s.Id == id, ct);
        if (row is null) return;
        db.Skills.Remove(row);
        await db.SaveChangesAsync(ct);
        await _cache.RemoveByPrefixAsync("cfg:", ct);
    }

    public async Task<IReadOnlyList<AuditLog>> QueryAuditLogsAsync(Guid? userId, DateTimeOffset from, DateTimeOffset to, int take, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var query = db.AuditLogs.AsNoTracking().Where(a => a.CreatedAt >= from && a.CreatedAt < to);
        if (userId.HasValue) query = query.Where(a => a.UserId == userId.Value);
        return await query.OrderByDescending(a => a.CreatedAt).Take(take).ToListAsync(ct);
    }

    // ================= 内部鉴权（IAdminStore） =================

    public Task<InternalAuthProvider?> GetInternalAuthProviderByNameAsync(string name, CancellationToken ct = default)
    {
        return GetAsync(db => db.InternalAuthProviders.AsNoTracking()
            .Include(p => p.SuccessRules).Include(p => p.DefaultRoles)
            .FirstOrDefaultAsync(p => p.Name == name, ct));
    }

    public async Task<IReadOnlyList<InternalAuthProvider>> ListInternalAuthProvidersAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.InternalAuthProviders.AsNoTracking()
            .Include(p => p.SuccessRules).Include(p => p.DefaultRoles)
            .OrderBy(p => p.Name).ToListAsync(ct);
    }

    public async Task<InternalAuthProvider> SaveInternalAuthProviderAsync(
        Guid id, string name, string api, string httpMethod, string requestFormat,
        string usernameField, string passwordField, bool enabled, int timeoutSeconds,
        IReadOnlyList<(string Field, SuccessRuleOperator Operator, string? ExpectedValue)> successRules,
        Guid[] roleIds, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        InternalAuthProvider provider;
        if (id == Guid.Empty)
        {
            provider = new InternalAuthProvider { Name = name };
            db.InternalAuthProviders.Add(provider);
        }
        else
        {
            provider = await db.InternalAuthProviders
                .Include(p => p.SuccessRules).Include(p => p.DefaultRoles)
                .FirstOrDefaultAsync(p => p.Id == id, ct)
                ?? throw new KeyNotFoundException($"InternalAuthProvider {id} not found");
            provider.SuccessRules.Clear();
            provider.DefaultRoles.Clear();
            await db.SaveChangesAsync(ct); // 先清关联，避免整行重插
        }

        provider.Name = name;
        provider.Api = api;
        provider.HttpMethod = httpMethod;
        provider.RequestFormat = requestFormat;
        provider.UsernameField = usernameField;
        provider.PasswordField = passwordField;
        provider.Enabled = enabled;
        provider.TimeoutSeconds = timeoutSeconds;
        provider.UpdatedAt = DateTimeOffset.UtcNow;

        foreach (var (field, op, expected) in successRules)
        {
            provider.SuccessRules.Add(new InternalAuthSuccessRule { Field = field, Operator = op, ExpectedValue = expected });
        }
        if (roleIds.Length > 0)
        {
            provider.DefaultRoles = await db.Roles.Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);
        }
        await db.SaveChangesAsync(ct);
        return provider;
    }

    public async Task DeleteInternalAuthProviderAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var provider = await db.InternalAuthProviders.Include(p => p.SuccessRules)
            .FirstOrDefaultAsync(p => p.Id == id, ct);
        if (provider is null) return;
        db.InternalAuthProviders.Remove(provider);
        await db.SaveChangesAsync(ct);
    }

    // ================= 刷新令牌（refresh token） =================

    public async Task CreateRefreshTokenAsync(Guid userId, string tokenHash, DateTimeOffset expiresAt, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        // 轻量清理：顺带删除已过期的旧令牌，避免表无限膨胀（now 参数化，SQLite 才能翻译比较）
        var now = DateTimeOffset.UtcNow;
        await db.UserRefreshTokens.Where(t => t.ExpiresAt < now).ExecuteDeleteAsync(ct);
        db.UserRefreshTokens.Add(new UserRefreshToken
        {
            UserId = userId,
            TokenHash = tokenHash,
            ExpiresAt = expiresAt,
        });
        await db.SaveChangesAsync(ct);
    }

    public async Task<UserRefreshToken?> GetRefreshTokenAsync(string tokenHash, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.UserRefreshTokens.AsNoTracking()
            .Include(t => t.User).ThenInclude(u => u!.Roles)
            .FirstOrDefaultAsync(t => t.TokenHash == tokenHash, ct);
    }

    public async Task RevokeRefreshTokenAsync(string tokenHash, string? replacedByTokenHash, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        await db.UserRefreshTokens
            .Where(t => t.TokenHash == tokenHash && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s
                .SetProperty(t => t.RevokedAt, now)
                .SetProperty(t => t.ReplacedByTokenHash, replacedByTokenHash), ct);
    }

    public async Task<int> RevokeRefreshTokensForUserAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var now = DateTimeOffset.UtcNow;
        return await db.UserRefreshTokens
            .Where(t => t.UserId == userId && t.RevokedAt == null)
            .ExecuteUpdateAsync(s => s.SetProperty(t => t.RevokedAt, now), ct);
    }

    // ---------------- 沉浸式工具栏 ----------------

    public async Task<IReadOnlyList<AppTool>> ListToolsAsync(CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.Tools.AsNoTracking()
            .Include(t => t.AllowedRoles)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task<AppTool?> GetToolByKeyAsync(string toolKey, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.Tools.AsNoTracking()
            .Include(t => t.AllowedRoles)
            .FirstOrDefaultAsync(t => t.ToolKey == toolKey, ct);
    }

    public async Task<AppTool> SaveToolAsync(Guid id, string toolKey, string name, string icon, string? description, string? baseUrl, bool enabled, Guid[] roleIds, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        AppTool tool;
        if (id == Guid.Empty)
        {
            tool = new AppTool { ToolKey = toolKey };
            db.Tools.Add(tool);
        }
        else
        {
            tool = await db.Tools
                .Include(t => t.AllowedRoles)
                .FirstOrDefaultAsync(t => t.Id == id, ct)
                ?? throw new KeyNotFoundException($"AppTool {id} not found");
            tool.AllowedRoles.Clear();
            await db.SaveChangesAsync(ct); // 先清绑定，避免唯一键冲突
        }

        tool.ToolKey = toolKey;
        tool.Name = name;
        tool.Icon = icon;
        tool.Description = description;
        tool.BaseUrl = baseUrl;
        tool.Enabled = enabled;
        tool.UpdatedAt = DateTimeOffset.UtcNow;

        if (roleIds.Length > 0)
        {
            var roles = await db.Roles.Where(r => roleIds.Contains(r.Id)).ToListAsync(ct);
            tool.AllowedRoles.AddRange(roles);
        }
        await db.SaveChangesAsync(ct);
        return tool;
    }

    public async Task DeleteToolAsync(Guid id, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        await db.Tools.Where(t => t.Id == id).ExecuteDeleteAsync(ct);
    }

    public async Task<IReadOnlyList<AppTool>> ListToolsForUserAsync(Guid[] roleIds, bool isAdmin, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        var all = await db.Tools.AsNoTracking()
            .Include(t => t.AllowedRoles)
            .Where(t => t.Enabled)
            .OrderBy(t => t.CreatedAt)
            .ToListAsync(ct);
        if (isAdmin) return all;
        // 普通用户：仅显示绑定了其任一角色的工具（未绑定 = 不可见）
        return all.Where(t => t.AllowedRoles.Any(r => roleIds.Contains(r.Id))).ToList();
    }

    // ---------------- 团队协作（会话级工程师） ----------------

    public async Task<IReadOnlyList<TeamEngineer>> ListTeamEngineersAsync(Guid sessionId, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        return await db.TeamEngineers.AsNoTracking()
            .Where(e => e.SessionId == sessionId)
            .OrderBy(e => e.DisplayOrder)
            .ThenBy(e => e.CreatedAt)
            .ToListAsync(ct);
    }

    public async Task SaveTeamEngineersAsync(Guid sessionId, IReadOnlyList<TeamEngineer> engineers, CancellationToken ct = default)
    {
        await using var db = await _db.CreateDbContextAsync(ct);
        await db.TeamEngineers.Where(e => e.SessionId == sessionId).ExecuteDeleteAsync(ct);
        if (engineers.Count > 0)
        {
            db.TeamEngineers.AddRange(engineers);
            await db.SaveChangesAsync(ct);
        }
    }

    // ---------------- 辅助 ----------------

    private async Task<T?> GetAsync<T>(Func<NextChatsDbContext, Task<T?>> query) where T : class
    {
        await using var db = await _db.CreateDbContextAsync();
        return await query(db);
    }
}
