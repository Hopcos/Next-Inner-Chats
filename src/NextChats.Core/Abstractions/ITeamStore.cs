using NextChats.Core.Entities;

namespace NextChats.Core.Abstractions;

/// <summary>团队协作存储：会话级工程师配置（服务端按用户隔离由调用方保证——先经 ChatStore 校验会话归属）</summary>
public interface ITeamStore
{
    /// <summary>列出会话的团队工程师（全量，按 DisplayOrder 排序）</summary>
    Task<IReadOnlyList<TeamEngineer>> ListTeamEngineersAsync(Guid sessionId, CancellationToken ct = default);

    /// <summary>整体替换会话的工程师集合（先删后插；sessionId 归属校验由调用方完成）</summary>
    Task SaveTeamEngineersAsync(Guid sessionId, IReadOnlyList<TeamEngineer> engineers, CancellationToken ct = default);
}
