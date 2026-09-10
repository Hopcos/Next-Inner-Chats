using NextChats.Core.Clients;

namespace NextChats.Core.Agents;

/// <summary>
/// Agent 事件（SSE 双向协议：前端据此渲染流式文本 / 可折叠思考 / 工具卡片 / 审批弹窗）。
/// 单一类 + Kind 判别字段，便于 JSON 序列化与前端分发。
/// </summary>
public sealed class AgentEvent
{
    public required string Kind { get; init; }

    public string? TraceId { get; init; }

    public string? Text { get; init; }

    public string? Reason { get; init; }

    public string? ServerName { get; init; }

    public string? ToolName { get; init; }

    public string? ArgumentsJson { get; init; }

    public string? ResultPreview { get; init; }

    public bool? Success { get; init; }

    public int? DurationMs { get; init; }

    public int? Attempt { get; init; }

    public string? ErrorCode { get; init; }

    public Guid? ApprovalId { get; init; }

    public string? ApprovalStatus { get; init; }

    public int? Round { get; init; }

    public string? MessageId { get; init; }

    public string? Code { get; init; }

    public string? Message { get; init; }

    public int? PromptTokens { get; init; }

    public int? CompletionTokens { get; init; }

    public int? ReasoningTokens { get; init; }

    public int? TotalTokens { get; init; }

    public int? Rounds { get; init; }

    public int? ToolCalls { get; init; }

    public decimal? Cost { get; init; }

    public int? TtftMs { get; init; }

    public int? TotalMs { get; init; }

    public string? Model { get; init; }

    public JsonUsage? Usage { get; init; }

    public static AgentEvent ThinkingStart(string traceId) => new() { Kind = "thinking_start", TraceId = traceId };

    public static AgentEvent ThinkingDelta(string text, string traceId) => new() { Kind = "thinking_delta", Text = text, TraceId = traceId };

    public static AgentEvent ThinkingEnd(string? reason, string traceId) => new() { Kind = "thinking_end", Reason = reason, TraceId = traceId };

    public static AgentEvent ToolStart(string server, string tool, string? args, bool requiresApproval, Guid? approvalId, string traceId) => new()
    {
        Kind = "tool_start", ServerName = server, ToolName = tool, ArgumentsJson = args, TraceId = traceId,
        ApprovalStatus = requiresApproval ? "pending" : null, ApprovalId = approvalId,
    };

    public static AgentEvent ToolResult(string server, string tool, bool success, string? preview, string? errorCode, int durationMs, string traceId) => new()
    {
        Kind = success ? "tool_result" : "tool_error", ServerName = server, ToolName = tool,
        Success = success, ResultPreview = preview, ErrorCode = errorCode, DurationMs = durationMs, TraceId = traceId,
    };

    public static AgentEvent ApprovalUpdated(Guid approvalId, string status, string traceId) => new()
    {
        Kind = "approval_updated", ApprovalId = approvalId, ApprovalStatus = status, TraceId = traceId,
    };

    public static AgentEvent RoundStart(int round, string traceId) => new() { Kind = "round_start", Round = round, TraceId = traceId };

    public static AgentEvent ContextEvent(string action, string? detail, string traceId) => new() { Kind = "context", Reason = action, Text = detail, TraceId = traceId };

    public static AgentEvent MessageDone(string messageId, string? model, int promptTokens, int completionTokens, decimal cost, string traceId) => new()
    {
        Kind = "message_done", MessageId = messageId, Model = model, PromptTokens = promptTokens,
        CompletionTokens = completionTokens, Cost = cost, TraceId = traceId,
    };

    public static AgentEvent Error(string code, string message, string traceId) => new() { Kind = "error", Code = code, Message = message, TraceId = traceId };

    public static AgentEvent Done(JsonUsage usage, decimal cost, int ttftMs, int totalMs, string traceId, string? model = null) => new()
    {
        Kind = "done", Usage = usage, Cost = cost, TtftMs = ttftMs, TotalMs = totalMs, TraceId = traceId,
        PromptTokens = usage.PromptTokens, CompletionTokens = usage.CompletionTokens, TotalTokens = usage.TotalTokens,
        ReasoningTokens = usage.ReasoningTokens, Rounds = usage.Rounds, ToolCalls = usage.ToolCalls,
        Model = model,
    };
}

/// <summary>前端的用量结构</summary>
public sealed class JsonUsage
{
    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public int ReasoningTokens { get; set; }

    public int TotalTokens { get; set; }

    public int Rounds { get; set; }

    public int ToolCalls { get; set; }

    public int ToolErrors { get; set; }

    public int Approvals { get; set; }
}
