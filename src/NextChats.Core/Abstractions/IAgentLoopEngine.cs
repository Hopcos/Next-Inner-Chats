using NextChats.Core.Agents;
using NextChats.Core.Domain;

namespace NextChats.Core.Abstractions;

/// <summary>Agent Loop（ReAct 循环引擎）：思考 → 行动 → 观察，直到收敛或达到轮次上限</summary>
public interface IAgentLoopEngine
{
    /// <summary>
    /// 执行 ReAct 循环，产出 Agent 事件流。
    /// 约定：InitialMessages 已含 system 首条；引擎内部负责追加 user 轮、工具结果、策略审批、上下文压缩/截断。
    /// 工具错误以 tool result 回灌模型（错误进循环 + 重试策略），不中断会话。
    /// </summary>
    IAsyncEnumerable<AgentEvent> RunAsync(AgentRunRequest request, CancellationToken ct);
}

public sealed record AgentRunRequest
{
    public required string TraceId { get; init; }

    public required Guid UserId { get; init; }

    public required Guid SessionId { get; init; }

    public required IReadOnlyList<NextChats.Core.Clients.LlmChatMessage> InitialMessages { get; init; }

    /// <summary>有效交集工具（MCP 工具 + Skill 元工具），Orchestrator 已收敛</summary>
    public IReadOnlyList<UnifiedTool>? Tools { get; init; }

    /// <summary>统一工具执行器（Orchestrator 注入：MCP → IMcpDriver，Skill → ISkillExecutionEngine）</summary>
    public required Func<UnifiedTool, string?, string, CancellationToken, Task<McpToolResult>> ToolExecutor { get; init; }

    /// <summary>首选 Provider（用户聊天设置），null = 路由默认</summary>
    public Guid? PreferredProviderId { get; init; }

    /// <summary>首选模型（用户聊天设置，供应商下带出的模型），null = 供应商内优先级最高的启用模型</summary>
    public Guid? PreferredModelId { get; init; }

    /// <summary>LLM 模型白名单（用户角色绑定；null/空 = 不限制）。未授权模型被路由层排除，管理员传 null</summary>
    public IReadOnlyList<Guid>? AllowedModelIds { get; init; }

    public string? ModelOverride { get; init; }

    /// <summary>ReAct 轮次上限；0 = 使用配置 Policy:MaxReActSteps（默认 20）</summary>
    public int MaxSteps { get; init; } = 0;

    /// <summary>思考模式开关（聊天窗口全局开关，默认启用）</summary>
    public bool ThinkingEnabled { get; init; } = true;

    /// <summary>思考强度（UI 档；发送侧统一映射 low/high/high/max）</summary>
    public LlmThinkingEffort ThinkingEffort { get; init; } = LlmThinkingEffort.High;

    public int ContextWindow { get; init; } = 128_000;

    /// <summary>界面语言（zh 前缀 = 中文，其他 = 英文；影响工具回灌 / 事件文案 / Mock 输出的本地化）</summary>
    public string? Lang { get; init; }

    /// <summary>
    /// 启动前预置工具调用（主-从委派 Planner 的拆解产物，仅允许 delegate_task）。
    /// 首轮直接进入决策/执行阶段而不等待模型决策；其余轮次照常由模型决定。
    /// </summary>
    public IReadOnlyList<NextChats.Core.Clients.LlmToolCall>? PrecomputedToolCalls { get; init; }
}
