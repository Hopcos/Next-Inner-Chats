using Microsoft.AspNetCore.Mvc;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;

namespace NextChats.Api.Controllers;

/// <summary>管理端：工具审批（tool_approvals：pending / approved / rejected / expired）</summary>
[Route("api/admin/approvals")]
public sealed class AdminApprovalController(IApprovalCoordinator coordinator, IChatStore chat, IAuditLogger audit) : AdminControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] string? status, [FromQuery] Guid? userId, [FromQuery] int take = 100)
    {
        var s = Enum.TryParse<ApprovalStatus>(status, true, out var st) ? st : (ApprovalStatus?)null;
        var approvals = await chat.ListApprovalsAsync(userId, s, Math.Min(take, 500));
        return Ok(approvals);
    }

    /// <summary>用户侧决策入口（本人会话的审批）也可复用：/api/me/approvals</summary>
    [HttpPost("{id:guid}/decide")]
    public async Task<IActionResult> Decide(Guid id, [FromBody] DecideInput input)
    {
        var approval = await chat.GetApprovalAsync(id);
        if (approval is null) return NotFound(Err("APPROVAL_NOT_FOUND"));

        var decision = input.Approved ? ApprovalDecision.Approved : ApprovalDecision.Rejected;
        var ok = await coordinator.NotifyDecisionAsync(id, decision, UserId.ToString(), input.Reason, HttpContext.RequestAborted);
        if (!ok)
        {
            return BadRequest(Err("APPROVAL_NOT_PENDING"));
        }
        await audit.RecordAsync(AuditCategory.Approval, input.Approved ? "APPROVAL.APPROVED" : "APPROVAL.REJECTED",
            $"trc_{Guid.NewGuid():N}"[..24], UserId, approval.ToolName,
            detail: new { approvalId = id, tool = $"{approval.McpServerName}.{approval.ToolName}", reason = input.Reason });
        return Ok(new { ok = true });
    }

    public sealed record DecideInput(bool Approved, string? Reason);

    /// <summary>用户可查看/审批自己的 pending 审批</summary>
    [HttpGet("mine")]
    public async Task<IActionResult> Mine()
    {
        var approvals = await chat.ListApprovalsAsync(UserId, null, 100);
        return Ok(approvals);
    }
}

/// <summary>管理端：审计日志（完整上下文、脱敏 Detail）</summary>
[Route("api/admin/audit")]
public sealed class AdminAuditController(IAdminStore store) : AdminControllerBase
{
    [HttpGet]
    public async Task<IActionResult> List([FromQuery] Guid? userId, [FromQuery] long from = 0, [FromQuery] long to = 0, [FromQuery] int take = 200)
    {
        var fromTime = from > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(from) : DateTimeOffset.UtcNow.AddDays(-7);
        var toTime = to > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(to) : DateTimeOffset.UtcNow.AddDays(1);
        var logs = await store.QueryAuditLogsAsync(userId, fromTime, toTime, Math.Min(take, 1000));
        return Ok(logs);
    }
}

/// <summary>管理端：可观测性与成本（Token / TTFT / 成本 / 工具时延 / 审批数）</summary>
[Route("api/admin/metrics")]
public sealed class AdminMetricsController(IChatStore chat, IAdminStore admin) : AdminControllerBase
{
    [HttpGet("usage")]
    public async Task<IActionResult> Usage([FromQuery] Guid? userId, [FromQuery] long from = 0, [FromQuery] long to = 0, [FromQuery] int take = 500)
    {
        var fromTime = from > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(from) : DateTimeOffset.UtcNow.AddDays(-7);
        var toTime = to > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(to) : DateTimeOffset.UtcNow.AddDays(1);
        var records = await chat.QueryUsageAsync(userId, fromTime, toTime, Math.Min(take, 2000));

        var totals = new
        {
            promptTokens = records.Sum(r => (long)r.PromptTokens),
            completionTokens = records.Sum(r => (long)r.CompletionTokens),
            totalTokens = records.Sum(r => (long)r.TotalTokens),
            cost = records.Sum(r => r.Cost),
            requests = records.Count,
            avgTtftMs = records.Count > 0 ? (int)records.Average(r => r.TtftMs) : 0,
            avgTotalMs = records.Count > 0 ? (int)records.Average(r => r.TotalMs) : 0,
            toolCalls = records.Sum(r => r.ToolCalls),
            toolErrors = records.Sum(r => r.ToolErrorCount),
            approvals = records.Sum(r => r.ApprovalCount),
            rounds = records.Sum(r => r.Rounds),
        };

        var byDay = records
            .GroupBy(r => r.CreatedAt.ToString("yyyy-MM-dd"))
            .Select(g => new { day = g.Key, tokens = g.Sum(r => (long)r.TotalTokens), cost = g.Sum(r => r.Cost), requests = g.Count() })
            .OrderBy(x => x.day)
            .ToList();

        return Ok(new { totals, byDay, records });
    }

    /// <summary>各用户用量统计：用户名 / 请求数 / Tokens / 成本 / 最后请求时间 / 最后登录时间</summary>
    [HttpGet("usage/users")]
    public async Task<IActionResult> UsageUsers([FromQuery] long from = 0, [FromQuery] long to = 0, [FromQuery] int take = 500)
    {
        var fromTime = from > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(from) : DateTimeOffset.UtcNow.AddDays(-7);
        var toTime = to > 0 ? DateTimeOffset.FromUnixTimeMilliseconds(to) : DateTimeOffset.UtcNow.AddDays(1);
        var records = await chat.QueryUsageAsync(null, fromTime, toTime, Math.Min(take, 2000), HttpContext.RequestAborted);
        var users = await admin.ListUsersAsync(HttpContext.RequestAborted);
        var userMap = users.ToDictionary(u => u.Id);

        var rows = records
            .Where(r => r.UserId.HasValue)
            .GroupBy(r => r.UserId!.Value)
            .Select(g =>
            {
                userMap.TryGetValue(g.Key, out var u);
                var name = u is null
                    ? $"@{g.Key.ToString("N")[..8]}"
                    : string.IsNullOrWhiteSpace(u.DisplayName) ? u.Username : u.DisplayName;
                return new
                {
                    userId = g.Key,
                    userName = name,
                    requests = g.Count(),
                    tokens = g.Sum(r => (long)r.TotalTokens),
                    cost = g.Sum(r => r.Cost),
                    lastRequestAt = g.Max(r => r.CreatedAt),
                    lastLoginAt = u?.LastLoginAt,
                };
            })
            .OrderByDescending(x => x.cost)
            .ToList();

        return Ok(rows);
    }
}
