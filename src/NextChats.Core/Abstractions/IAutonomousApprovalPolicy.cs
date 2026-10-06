namespace NextChats.Core.Abstractions;

/// <summary>
/// 自主模式判定：当前会话绑定的工作空间授予用户的最高级别为 Autonomous(40)
/// 时，该会话内所有需人工审批的工具调用自动放行（跳过确认）。
/// 管理员豁免仍是 FullAccess(30)，不会自动获得自主模式。
/// </summary>
public interface IAutonomousApprovalPolicy
{
    Task<bool> IsAutonomousAsync(Guid userId, Guid sessionId, CancellationToken ct = default);
}
