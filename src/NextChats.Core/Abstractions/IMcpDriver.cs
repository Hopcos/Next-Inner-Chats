using NextChats.Core.Entities;

namespace NextChats.Core.Abstractions;

/// <summary>暴露给模型的统一工具视图（MCP 工具 + Skill 元工具）</summary>
public sealed record UnifiedTool(
    string ServerName,   // MCP server 名或 "skill"
    string Name,         // 工具名（MCP 原名 / skill.MetaToolName）
    string Description,
    string? SchemaJson,
    bool IsSkill,
    bool DestructiveHint = false);

/// <summary>工具调用结果（“错误进循环 + 重试”）：无论成败都返回结构化结果</summary>
public sealed record McpToolResult(
    bool Success,
    string ResultText,       // 成功时的文本结果
    string? ErrorMessage,    // 失败时的错误信息（脱敏）
    string? ErrorCode,       // 友好错误码
    int DurationMs,
    int Attempts,
    bool Retryable = false,  // 是否可重试（连接/超时类瞬时错误 true；业务错误 false）
    int SubAgentCount = 0,   // 本次工具调用派生的子 Agent 数量（delegate_task = 1；其余 0）
    int SubAgentInputTokens = 0,
    int SubAgentOutputTokens = 0);

/// <summary>MCP 自动带出元数据（description / instructions / tools / prompts / resources）</summary>
public sealed record McpDiscoverResult(
    string? Description,
    string? Instructions,
    IReadOnlyList<McpCatalogItem> Items);

/// <summary>
/// MCP 驱动引擎：遵循最新 MCP 规范（ModelContextProtocol 2.x SDK，Streamable HTTP Transport 为主，
/// Stdio Transport 已预留）；懒连接 + 连接复用 + 超时 + 重试 + 错误隔离（单服务器错误不影响会话）。
/// </summary>
public interface IMcpDriver
{
    /// <summary>自动带出 description / tools / prompts / resources</summary>
    Task<McpDiscoverResult> DiscoverAsync(McpServer server, CancellationToken ct);

    /// <summary>获取服务器当前启用的工具（来自自动带出的目录，已剔除被禁用的项）</summary>
    IReadOnlyList<UnifiedTool> GetEnabledTools(McpServer server);

    /// <summary>调用工具（失败返回 ErrorMessage 回灌给模型，不中断会话）；lang 影响错误文案</summary>
    Task<McpToolResult> CallToolAsync(McpServer server, string toolName, string? argumentsJson, string traceId, string? lang = null, CancellationToken ct = default);

    /// <summary>获取 Prompt（供个人设置查看能力摘要）</summary>
    Task<string?> GetPromptAsync(McpServer server, string promptName, string? argumentsJson, CancellationToken ct);

    /// <summary>列出服务器可用资源（静态资源 + 模板摘要；mcp_resources 内置工具）</summary>
    Task<string> ListResourcesAsync(McpServer server, CancellationToken ct);

    /// <summary>读取资源内容为文本（mcp_read_resource 内置工具）；失败返回 null</summary>
    Task<string?> ReadResourceAsync(McpServer server, string uri, CancellationToken ct);

    Task<(bool Ok, string? Error, int LatencyMs)> PingAsync(McpServer server, CancellationToken ct);

    /// <summary>配置变更后清理连接缓存</summary>
    Task InvalidateAsync(Guid serverId);
}
