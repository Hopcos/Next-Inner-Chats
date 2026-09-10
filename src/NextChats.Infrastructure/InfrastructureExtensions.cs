using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using NextChats.Core.Abstractions;
using NextChats.Core.Services;
using NextChats.Infrastructure.Data;
using NextChats.Infrastructure.Services;

namespace NextChats.Infrastructure;

public static class InfrastructureExtensions
{
    /// <summary>注册基础设施：SQLite + EF Core 池化、缓存、安全、审计、数据存储、引擎</summary>
    public static IServiceCollection AddNextChatsInfrastructure(this IServiceCollection services, IConfiguration configuration, string? connectionString = null)
    {
        var conn = connectionString
            ?? configuration.GetConnectionString("Default")
            ?? "Data Source=data/nextchats.db";

        // ---------- 缓存 / 安全 / 审计 / 中断 ----------
        services.AddMemoryCache();
        services.AddSingleton<ICacheService, MemoryCacheService>();
        services.AddSingleton<ISecurityService, SecurityService>();
        services.AddSingleton<IAuditLogger, AuditLogger>();
        services.AddSingleton<ISessionCancellationRegistry, SessionCancellationRegistry>();

        // ---------- EF Core（SQLite，可切换 MySQL8） ----------
        services.AddDbContextFactory<NextChatsDbContext>(options =>
            options.UseSqlite(conn, sqlite => sqlite.MigrationsAssembly("NextChats.Infrastructure")));

        // ---------- 数据存储（单例 + DbContext 池化） ----------
        services.AddSingleton<NextChatsStore>();
        services.AddSingleton<IConfigStore>(sp => sp.GetRequiredService<NextChatsStore>());
        services.AddSingleton<IChatStore>(sp => sp.GetRequiredService<NextChatsStore>());
        services.AddSingleton<IAdminStore>(sp => sp.GetRequiredService<NextChatsStore>());

        // ---------- HTTP ----------
        services.AddHttpClient();
        // llm 客户端不设总超时：长思考（分钟级推理）+ 长流式输出可能远超 180s，
        // 总超时会在推理中途掐断连接。首 token 等待上限由供应商 TimeoutSeconds 在客户端内控制。
        services.AddHttpClient("llm", client => client.Timeout = System.Threading.Timeout.InfiniteTimeSpan);
        services.AddHttpClient("mcp", client => client.Timeout = TimeSpan.FromSeconds(120));
        services.AddSingleton<IHttpClientProvider, HttpClientProvider>();

        // ---------- 引擎 ----------
        services.AddSingleton<IPromptTemplateEngine, PromptTemplateEngine>();
        services.AddSingleton<IPolicyEngine, PolicyEngine>();
        services.AddSingleton<IMcpDriver, McpDriver>();
        services.AddSingleton<IApprovalCoordinator, ApprovalCoordinator>();
        services.AddSingleton<ILlmRouter, LlmRouter>();
        services.AddSingleton<IContextManager, ContextManager>();
        services.AddSingleton<ISkillExecutionEngine, SkillExecutionEngine>();
        services.AddSingleton<IAgentLoopEngine, AgentLoopEngine>();
        services.AddSingleton<IChatOrchestrator, ChatOrchestrator>();

        return services;
    }

    /// <summary>建库（EnsureCreated；生产可切换 EF Migrations）</summary>
    public static async Task InitializeDatabaseAsync(this IServiceProvider services)
    {
        await using var scope = services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<IDbContextFactory<NextChatsDbContext>>();
        await using var ctx = await db.CreateDbContextAsync();
        await ctx.Database.EnsureCreatedAsync();
        await EnsureCompatibleSchemaAsync(ctx);
    }

    /// <summary>
    /// 轻量兼容迁移：EnsureCreated 对已存在数据库不会追加新列，这里为增量字段补列。
    /// 生产建议切换 EF Migrations 后移除。
    /// </summary>
    private static async Task EnsureCompatibleSchemaAsync(NextChatsDbContext ctx)
    {
        var conn = ctx.Database.GetDbConnection();
        var opened = false;
        if (conn.State != System.Data.ConnectionState.Open)
        {
            await conn.OpenAsync();
            opened = true;
        }
        try
        {
            await AddColumnIfMissingAsync(conn, "LlmModels", "ThinkingEffort", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, "LlmProviders", "ThinkingParam", "TEXT NOT NULL DEFAULT 'None'");
            await AddColumnIfMissingAsync(conn, "McpServers", "Instructions", "TEXT");
            // ChatMessages 用量明细列（历史对话也可查看 Token 指标；decimal 同 TokenUsageRecords.Cost 存 TEXT）
            await AddColumnIfMissingAsync(conn, "ChatMessages", "ReasoningTokens", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, "ChatMessages", "TtftMs", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, "ChatMessages", "TotalMs", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, "ChatMessages", "Rounds", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, "ChatMessages", "ToolCalls", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, "ChatMessages", "Cost", "TEXT NOT NULL DEFAULT '0'");
            await AddColumnIfMissingAsync(conn, "ChatMessages", "SubAgentCount", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, "ChatMessages", "SubAgentInputTokens", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, "ChatMessages", "SubAgentOutputTokens", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, "ChatMessages", "PlannerInputTokens", "INTEGER NOT NULL DEFAULT 0");
            await AddColumnIfMissingAsync(conn, "ChatMessages", "PlannerOutputTokens", "INTEGER NOT NULL DEFAULT 0");
            await AddTableIfMissingAsync(conn, "UserFavorites", """
                CREATE TABLE "UserFavorites" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_UserFavorites" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "Title" TEXT NOT NULL,
                    "QuestionText" TEXT,
                    "AnswerText" TEXT,
                    "QuestionMessageId" TEXT,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL,
                    CONSTRAINT "FK_UserFavorites_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX "IX_UserFavorites_UserId_CreatedAt" ON "UserFavorites" ("UserId", "CreatedAt");
                CREATE INDEX "IX_UserFavorites_UserId_QuestionMessageId" ON "UserFavorites" ("UserId", "QuestionMessageId") WHERE "QuestionMessageId" IS NOT NULL;
                """);
            // 角色 ↔ LLM 模型 多对多（RoleModelBindings）
            // 注意：连接表列名由 EF 按导航属性生成 —— AppRole.Models ↔ LlmModel.Roles → "ModelsId"/"RolesId"
            await FixJoinTableColumnsAsync(conn, "RoleModelBindings", "ModelsId");
            await AddTableIfMissingAsync(conn, "RoleModelBindings", """
                CREATE TABLE "RoleModelBindings" (
                    "RolesId" TEXT NOT NULL,
                    "ModelsId" TEXT NOT NULL,
                    CONSTRAINT "PK_RoleModelBindings" PRIMARY KEY ("RolesId", "ModelsId"),
                    CONSTRAINT "FK_RoleModelBindings_Roles_RolesId" FOREIGN KEY ("RolesId") REFERENCES "Roles" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_RoleModelBindings_LlmModels_ModelsId" FOREIGN KEY ("ModelsId") REFERENCES "LlmModels" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX "IX_RoleModelBindings_ModelsId" ON "RoleModelBindings" ("ModelsId");
                """);
            // ---------- 内部鉴权：账号类型列 + (AuthType, Username) 组合唯一索引 ----------
            await AddColumnIfMissingAsync(conn, "Users", "AuthType", "TEXT NOT NULL DEFAULT 'default'");
            // 只读模式（默认 false）：管理员可被标记为只读，仅可查看后台
            await AddColumnIfMissingAsync(conn, "Users", "IsReadonly", "INTEGER NOT NULL DEFAULT 0");
            await RebuildUserUniqueIndexAsync(conn);
            await AddTableIfMissingAsync(conn, "InternalAuthProviders", """
                CREATE TABLE "InternalAuthProviders" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_InternalAuthProviders" PRIMARY KEY,
                    "Name" TEXT NOT NULL,
                    "Api" TEXT NOT NULL,
                    "HttpMethod" TEXT NOT NULL DEFAULT 'POST',
                    "RequestFormat" TEXT NOT NULL DEFAULT 'BodyJson',
                    "UsernameField" TEXT NOT NULL DEFAULT 'username',
                    "PasswordField" TEXT NOT NULL DEFAULT 'password',
                    "Enabled" INTEGER NOT NULL DEFAULT 1,
                    "TimeoutSeconds" INTEGER NOT NULL DEFAULT 15,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX "IX_InternalAuthProviders_Name" ON "InternalAuthProviders" ("Name");
                """);
            await AddTableIfMissingAsync(conn, "InternalAuthSuccessRules", """
                CREATE TABLE "InternalAuthSuccessRules" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_InternalAuthSuccessRules" PRIMARY KEY,
                    "ProviderId" TEXT NOT NULL,
                    "Field" TEXT NOT NULL,
                    "Operator" INTEGER NOT NULL,
                    "ExpectedValue" TEXT,
                    CONSTRAINT "FK_InternalAuthSuccessRules_InternalAuthProviders_ProviderId"
                        FOREIGN KEY ("ProviderId") REFERENCES "InternalAuthProviders" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX "IX_InternalAuthSuccessRules_ProviderId" ON "InternalAuthSuccessRules" ("ProviderId");
                """);
            // 内部鉴权 ↔ 默认角色 多对多（列名与 EF UsingEntity 显式配置一致：ProviderId / RoleId）
            await AddTableIfMissingAsync(conn, "InternalAuthProviderRoleBindings", """
                CREATE TABLE "InternalAuthProviderRoleBindings" (
                    "ProviderId" TEXT NOT NULL,
                    "RoleId" TEXT NOT NULL,
                    CONSTRAINT "PK_InternalAuthProviderRoleBindings" PRIMARY KEY ("ProviderId", "RoleId"),
                    CONSTRAINT "FK_InternalAuthProviderRoleBindings_InternalAuthProviders_ProviderId"
                        FOREIGN KEY ("ProviderId") REFERENCES "InternalAuthProviders" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_InternalAuthProviderRoleBindings_Roles_RoleId"
                        FOREIGN KEY ("RoleId") REFERENCES "Roles" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX "IX_InternalAuthProviderRoleBindings_RoleId" ON "InternalAuthProviderRoleBindings" ("RoleId");
                """);
            // ---------- 刷新令牌（refresh token：存哈希、轮换、随用户删除级联） ----------
            await AddTableIfMissingAsync(conn, "UserRefreshTokens", """
                CREATE TABLE "UserRefreshTokens" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_UserRefreshTokens" PRIMARY KEY,
                    "UserId" TEXT NOT NULL,
                    "TokenHash" TEXT NOT NULL,
                    "ExpiresAt" TEXT NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "RevokedAt" TEXT,
                    "ReplacedByTokenHash" TEXT,
                    CONSTRAINT "FK_UserRefreshTokens_Users_UserId" FOREIGN KEY ("UserId") REFERENCES "Users" ("Id") ON DELETE CASCADE
                );
                CREATE UNIQUE INDEX "IX_UserRefreshTokens_TokenHash" ON "UserRefreshTokens" ("TokenHash");
                CREATE INDEX "IX_UserRefreshTokens_UserId_ExpiresAt" ON "UserRefreshTokens" ("UserId", "ExpiresAt");
                """);
            // ---------- 沉浸式工具栏（工具注册 + 角色绑定） ----------
            await AddTableIfMissingAsync(conn, "AppTools", """
                CREATE TABLE "AppTools" (
                    "Id" TEXT NOT NULL CONSTRAINT "PK_AppTools" PRIMARY KEY,
                    "ToolKey" TEXT NOT NULL,
                    "Name" TEXT NOT NULL,
                    "Icon" TEXT NOT NULL,
                    "Description" TEXT,
                    "Enabled" INTEGER NOT NULL,
                    "CreatedAt" TEXT NOT NULL,
                    "UpdatedAt" TEXT NOT NULL
                );
                CREATE UNIQUE INDEX "IX_AppTools_ToolKey" ON "AppTools" ("ToolKey");
                """);
            await AddTableIfMissingAsync(conn, "AppToolRoleBindings", """
                CREATE TABLE "AppToolRoleBindings" (
                    "ToolId" TEXT NOT NULL,
                    "RoleId" TEXT NOT NULL,
                    CONSTRAINT "PK_AppToolRoleBindings" PRIMARY KEY ("ToolId", "RoleId"),
                    CONSTRAINT "FK_AppToolRoleBindings_AppTools_ToolId" FOREIGN KEY ("ToolId") REFERENCES "AppTools" ("Id") ON DELETE CASCADE,
                    CONSTRAINT "FK_AppToolRoleBindings_Roles_RoleId" FOREIGN KEY ("RoleId") REFERENCES "Roles" ("Id") ON DELETE CASCADE
                );
                CREATE INDEX "IX_AppToolRoleBindings_RoleId" ON "AppToolRoleBindings" ("RoleId");
                """);
        }
        finally
        {
            if (opened) await conn.CloseAsync();
        }
    }

    private static async Task AddColumnIfMissingAsync(System.Data.Common.DbConnection conn, string table, string column, string definition)
    {
        await using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{column}'";
        var exists = Convert.ToInt32(await check.ExecuteScalarAsync()) > 0;
        if (exists) return;
        await using var alter = conn.CreateCommand();
        alter.CommandText = $"ALTER TABLE \"{table}\" ADD COLUMN \"{column}\" {definition}";
        await alter.ExecuteNonQueryAsync();
    }

    /// <summary>轻量建表：EnsureCreated 对已存在的库不会追加新表，这里幂等补建（表已存在则跳过）</summary>
    private static async Task AddTableIfMissingAsync(System.Data.Common.DbConnection conn, string table, string createSql)
    {
        await using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = '{table}'";
        var exists = Convert.ToInt32(await check.ExecuteScalarAsync()) > 0;
        if (exists) return;
        await using var create = conn.CreateCommand();
        create.CommandText = createSql;
        await create.ExecuteNonQueryAsync();
    }

    /// <summary>修复早期以错误列名建成的多对多表：表存在但缺指定列 → 丢弃后由 AddTableIfMissingAsync 重建</summary>
    private static async Task FixJoinTableColumnsAsync(System.Data.Common.DbConnection conn, string table, string requiredColumn)
    {
        await using var check = conn.CreateCommand();
        check.CommandText = $"SELECT COUNT(*) FROM sqlite_master WHERE type='table' AND name = '{table}'";
        var exists = Convert.ToInt32(await check.ExecuteScalarAsync()) > 0;
        if (!exists) return;
        await using var colCheck = conn.CreateCommand();
        colCheck.CommandText = $"SELECT COUNT(*) FROM pragma_table_info('{table}') WHERE name = '{requiredColumn}'";
        var hasColumn = Convert.ToInt32(await colCheck.ExecuteScalarAsync()) > 0;
        if (hasColumn) return;
        await using var drop = conn.CreateCommand();
        drop.CommandText = $"DROP TABLE \"{table}\"";
        await drop.ExecuteNonQueryAsync();
    }

    /// <summary>
    /// 用户唯一性从「用户名」升级为「(AuthType, Username)」：
    /// 删除旧的单列唯一索引，重建组合唯一索引（幂等；全新库由 EnsureCreated 直接建组合索引，此处跳过）。
    /// </summary>
    private static async Task RebuildUserUniqueIndexAsync(System.Data.Common.DbConnection conn)
    {
        await using var check = conn.CreateCommand();
        check.CommandText = "SELECT COUNT(*) FROM sqlite_master WHERE type='index' AND name = 'IX_Users_AuthType_Username'";
        var exists = Convert.ToInt32(await check.ExecuteScalarAsync()) > 0;
        if (exists) return;
        await using var drop = conn.CreateCommand();
        drop.CommandText = "DROP INDEX IF EXISTS \"IX_Users_Username\"";
        await drop.ExecuteNonQueryAsync();
        await using var create = conn.CreateCommand();
        create.CommandText = "CREATE UNIQUE INDEX \"IX_Users_AuthType_Username\" ON \"Users\" (\"AuthType\", \"Username\")";
        await create.ExecuteNonQueryAsync();
    }
}
