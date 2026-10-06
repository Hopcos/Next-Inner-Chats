using Microsoft.Extensions.Logging;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;

namespace NextChats.Infrastructure.Services;

/// <summary>自主模式策略：会话绑定工作空间且用户（经角色绑定）最高级别 ≥ Autonomous 时视为自主。</summary>
public sealed class AutonomousApprovalPolicy(IChatStore chat, IConfigStore config, ILogger<AutonomousApprovalPolicy> logger) : IAutonomousApprovalPolicy
{
    public async Task<bool> IsAutonomousAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        try
        {
            var session = await chat.GetSessionAsync(userId, sessionId, ct).ConfigureAwait(false);
            if (session is null || session.WorkspaceId is not { } wsId) return false;

            var bindings = await config.GetRoleWorkspaceBindingsAsync(userId, ct).ConfigureAwait(false);
            var hasAutonomous = bindings.Any(b => b.WorkspaceId == wsId && b.Level >= WorkspaceAccessLevel.Autonomous);
            if (hasAutonomous) logger.LogInformation("自主模式放行 u={UserId} s={SessionId} ws={WsId}", userId, sessionId, wsId);
            return hasAutonomous;
        }
        catch (Exception ex)
        {
            // 判定失败时保守回退：仍走人工审批
            logger.LogWarning(ex, "autonomy check failed u={UserId} s={SessionId}", userId, sessionId);
            return false;
        }
    }
}
