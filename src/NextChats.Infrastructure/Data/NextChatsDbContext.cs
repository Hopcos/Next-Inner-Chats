using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Storage.ValueConversion;
using NextChats.Core.Entities;

namespace NextChats.Infrastructure.Data;

public sealed class NextChatsDbContext(DbContextOptions<NextChatsDbContext> options) : DbContext(options)
{
    public DbSet<AppUser> Users => Set<AppUser>();
    public DbSet<AppRole> Roles => Set<AppRole>();
    public DbSet<LlmProvider> LlmProviders => Set<LlmProvider>();
    public DbSet<LlmModel> LlmModels => Set<LlmModel>();
    public DbSet<McpServer> McpServers => Set<McpServer>();
    public DbSet<McpCatalogItem> McpCatalogItems => Set<McpCatalogItem>();
    public DbSet<Prompt> Prompts => Set<Prompt>();
    public DbSet<Skill> Skills => Set<Skill>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<ToolApproval> ToolApprovals => Set<ToolApproval>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<UserSetting> UserSettings => Set<UserSetting>();
    public DbSet<TokenUsageRecord> TokenUsageRecords => Set<TokenUsageRecord>();
    public DbSet<IdempotencyRecord> IdempotencyRecords => Set<IdempotencyRecord>();
    public DbSet<UserFavorite> UserFavorites => Set<UserFavorite>();
    public DbSet<InternalAuthProvider> InternalAuthProviders => Set<InternalAuthProvider>();
    public DbSet<InternalAuthSuccessRule> InternalAuthSuccessRules => Set<InternalAuthSuccessRule>();
    public DbSet<UserRefreshToken> UserRefreshTokens => Set<UserRefreshToken>();
    public DbSet<AppTool> Tools => Set<AppTool>();
    public DbSet<Workspace> Workspaces => Set<Workspace>();
    public DbSet<RoleWorkspaceBinding> RoleWorkspaceBindings => Set<RoleWorkspaceBinding>();

    protected override void OnModelCreating(ModelBuilder b)
    {
        // ---------- SQLite 兼容：DateTimeOffset → ISO-8601 UTC 字符串 ----------
        // SQLite 不支持 DateTimeOffset 参与 ORDER BY 表达式；统一转为固定格式字符串后
        // 字典序 = 时间序，全部排序/区间查询可正常翻译。
        foreach (var entityType in b.Model.GetEntityTypes())
        {
            foreach (var property in entityType.GetProperties().Where(p => p.ClrType == typeof(DateTimeOffset)))
            {
                property.SetValueConverter(new ValueConverter<DateTimeOffset, string>(
                    v => v.ToUniversalTime().ToString("O"),
                    v => DateTimeOffset.Parse(v)));
            }
        }

        // ---------- 用户 / 角色 ----------
        b.Entity<AppUser>(e =>
        {
            // 内部鉴权用户唯一性 = (AuthType, Username)：default 账号与 acs 账号可同名；同类别下用户名唯一
            e.HasIndex(x => new { x.AuthType, x.Username }).IsUnique();
            e.Property(x => x.Username).IsRequired();
        });

        b.Entity<AppRole>(e =>
        {
            e.HasIndex(x => x.Code).IsUnique();
        });

        b.Entity<AppUser>()
            .HasMany(u => u.Roles).WithMany(r => r.Users)
            .UsingEntity("AppUserRoles");

        b.Entity<AppRole>()
            .HasMany(r => r.McpServers).WithMany(m => m.Roles)
            .UsingEntity("RoleMcpBindings");

        b.Entity<AppRole>()
            .HasMany(r => r.Prompts).WithMany(p => p.Roles)
            .UsingEntity("RolePromptBindings");

        b.Entity<AppRole>()
            .HasMany(r => r.Skills).WithMany(s => s.Roles)
            .UsingEntity("RoleSkillBindings");

        // ---------- 角色 ↔ LLM 模型（角色绑定后，该角色的用户才可见/可选这些模型） ----------
        b.Entity<AppRole>()
            .HasMany(r => r.Models).WithMany(m => m.Roles)
            .UsingEntity("RoleModelBindings");

        // ---------- 工作空间（编码会话）：独立绑定表带级别（RoleWorkspaceBinding{ RoleId, WorkspaceId, Level }） ----------
        b.Entity<Workspace>(e => e.HasIndex(x => x.Name));
        b.Entity<RoleWorkspaceBinding>(e =>
        {
            e.HasKey(x => new { x.RoleId, x.WorkspaceId });
            e.HasOne<AppRole>().WithMany().HasForeignKey(x => x.RoleId).OnDelete(DeleteBehavior.Cascade);
            e.HasOne<Workspace>().WithMany().HasForeignKey(x => x.WorkspaceId).OnDelete(DeleteBehavior.Cascade);
        });

        // ---------- 内部鉴权配置（成功判定规则 + 默认角色多选） ----------
        b.Entity<InternalAuthProvider>(e =>
        {
            e.HasIndex(x => x.Name).IsUnique();
            e.HasMany(p => p.SuccessRules).WithOne(r => r.Provider).HasForeignKey(r => r.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });
        b.Entity<InternalAuthProvider>()
            .HasMany(p => p.DefaultRoles).WithMany(r => r.InternalAuthProviders)
            .UsingEntity<Dictionary<string, object>>(
                "InternalAuthProviderRoleBindings",
                j => j.HasOne<AppRole>().WithMany().HasForeignKey("RoleId").OnDelete(DeleteBehavior.Cascade),
                j => j.HasOne<InternalAuthProvider>().WithMany().HasForeignKey("ProviderId").OnDelete(DeleteBehavior.Cascade),
                j => j.HasKey("ProviderId", "RoleId"));

        // ---------- 刷新令牌（双 token：access 短时 + refresh 持久化轮换；用户禁用时整批撤销） ----------
        b.Entity<UserRefreshToken>(e =>
        {
            e.HasIndex(x => x.TokenHash).IsUnique();
            e.HasIndex(x => new { x.UserId, x.ExpiresAt });
            e.HasOne(t => t.User).WithMany().HasForeignKey(t => t.UserId).OnDelete(DeleteBehavior.Cascade);
        });

        // ---------- 沉浸式工具栏（ToolKey 唯一；角色绑定：空 = 仅管理员） ----------
        b.Entity<AppTool>(e =>
        {
            e.ToTable("AppTools"); // 与轻量迁移建的表名对齐
            e.HasIndex(x => x.ToolKey).IsUnique();
        });
        b.Entity<AppTool>()
            .HasMany(t => t.AllowedRoles).WithMany()
            .UsingEntity<Dictionary<string, object>>(
                "AppToolRoleBindings",
                j => j.HasOne<AppRole>().WithMany().HasForeignKey("RoleId").OnDelete(DeleteBehavior.Cascade),
                j => j.HasOne<AppTool>().WithMany().HasForeignKey("ToolId").OnDelete(DeleteBehavior.Cascade),
                j => j.HasKey("ToolId", "RoleId"));

        // ---------- LLM 供应商 / 模型 ----------
        b.Entity<LlmProvider>()
            .HasMany(p => p.Models).WithOne(m => m.Provider).HasForeignKey(m => m.ProviderId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<LlmModel>(e =>
        {
            e.HasIndex(x => new { x.ProviderId, x.Name });
        });

        // ---------- MCP ----------
        b.Entity<McpServer>()
            .HasMany(m => m.Items).WithOne(i => i.Server).HasForeignKey(i => i.McpServerId)
            .OnDelete(DeleteBehavior.Cascade);

        b.Entity<McpCatalogItem>(e =>
        {
            e.HasIndex(x => new { x.McpServerId, x.Kind, x.Name });
        });

        // ---------- 会话 / 消息（按用户隔离索引） ----------
        b.Entity<ChatSession>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.UpdatedAt });
            e.HasMany(s => s.Messages).WithOne(m => m.Session).HasForeignKey(m => m.SessionId)
                .OnDelete(DeleteBehavior.Cascade);
            e.Ignore(x => x.User);
        });

        b.Entity<ChatMessage>(e =>
        {
            e.HasIndex(x => new { x.SessionId, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.ClientMessageId })
                .IsUnique()
                .HasFilter("\"ClientMessageId\" IS NOT NULL");
            e.Property(x => x.TotalTokens).IsConcurrencyToken(false);
        });

        // ---------- 审批 / 审计 / 设置 / 用量 / 幂等 ----------
        b.Entity<ToolApproval>(e =>
        {
            e.HasIndex(x => new { x.Status, x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
        });

        b.Entity<AuditLog>(e =>
        {
            e.HasIndex(x => new { x.CreatedAt });
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            e.HasIndex(x => x.TraceId);
        });

        b.Entity<UserSetting>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Key }).IsUnique();
        });

        b.Entity<TokenUsageRecord>(e =>
        {
            e.HasIndex(x => x.CreatedAt);
            e.HasIndex(x => x.UserId);
            e.HasIndex(x => x.TraceId);
        });

        b.Entity<IdempotencyRecord>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.Key }).IsUnique();
        });

        // ---------- 用户收藏（按用户隔离 + 问题消息去重） ----------
        b.Entity<UserFavorite>(e =>
        {
            e.HasIndex(x => new { x.UserId, x.CreatedAt });
            // 同一用户对同一问题消息只能收藏一次（防重复收藏）
            e.HasIndex(x => new { x.UserId, x.QuestionMessageId })
                .IsUnique()
                .HasFilter("\"QuestionMessageId\" IS NOT NULL");
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });
    }
}
