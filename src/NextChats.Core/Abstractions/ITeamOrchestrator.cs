using NextChats.Core.Agents;

namespace NextChats.Core.Abstractions;

/// <summary>团队工程师 DTO（会话级配置）</summary>
public sealed record TeamEngineerDto(
    Guid? Id,
    string Name,
    int Role,          // TeamEngineerRole: 1=Responsible, 2=Assistant
    Guid ProviderId,
    Guid ModelId,
    int DisplayOrder,
    bool Enabled);

/// <summary>团队配置（GET/PUT /api/chat/sessions/{id}/team）</summary>
public sealed record TeamConfigDto(
    bool Enabled,
    int MaxRounds,
    bool Parallel,
    bool StopOnConsensus,
    int MaxParallel,
    IReadOnlyList<TeamEngineerDto> Engineers);

/// <summary>保存团队配置请求</summary>
public sealed record TeamConfigUpsert(
    bool Enabled,
    int MaxRounds,
    bool Parallel,
    bool StopOnConsensus,
    int MaxParallel,
    IReadOnlyList<TeamEngineerDto>? Engineers);

/// <summary>一次团队协作请求的编排输入</summary>
public sealed record TeamStreamRequest
{
    public required Guid UserId { get; init; }

    public required Guid SessionId { get; init; }

    public required string UserInput { get; init; }

    /// <summary>客户端消息 ID（幂等）</summary>
    public string? ClientMessageId { get; init; }

    /// <summary>界面语言（zh 前缀 = 中文；影响审计/错误文案）</summary>
    public string? Lang { get; init; }
}

/// <summary>
/// 团队协作编排：责任工程师执行 → 协助工程师建议（并行、同轮互相隔离）→
/// 责任工程师评估迭代，直到无争议或达到最大建议轮次，输出最终结果。
/// 纯 LLM 调用（不携带工具），完全独立于现有普通聊天编排，互不影响。
/// </summary>
public interface ITeamOrchestrator
{
    /// <summary>读取会话团队配置（工程师 + 参数；会话不存在返回 null）</summary>
    Task<TeamConfigDto?> GetConfigAsync(Guid userId, Guid sessionId, CancellationToken ct = default);

    /// <summary>保存会话团队配置（整体替换工程师；校验模型可用性/角色绑定）</summary>
    Task<TeamConfigDto> SaveConfigAsync(Guid userId, Guid sessionId, TeamConfigUpsert cfg, CancellationToken ct = default);

    /// <summary>流式执行一次团队协作（SSE 事件流；不触碰普通聊天路径）</summary>
    IAsyncEnumerable<AgentEvent> StreamAsync(TeamStreamRequest request, CancellationToken ct);
}
