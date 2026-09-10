using System.Text.Json.Nodes;
using NextChats.Core.Domain;

namespace NextChats.Core.Clients;

public enum LlmFinishReason
{
    Stop,
    ToolCalls,
    Length,
    ContentFilter,
    Error,
}

/// <summary>发送给 LLM 的消息</summary>
public sealed record LlmChatMessage(string Role, string? Content)
{
    public IReadOnlyList<LlmToolCall>? ToolCalls { get; init; }

    /// <summary>tool 结果消息关联的 tool_call_id</summary>
    public string? ToolCallId { get; init; }

    /// <summary>assistant 消息的思考内容（DeepSeek 官方协议：携带 tools 的对话轮必须回传 reasoning_content，否则 400）</summary>
    public string? Reasoning { get; init; }

    public static LlmChatMessage System(string content) => new("system", content);

    public static LlmChatMessage User(string content) => new("user", content);

    public static LlmChatMessage Assistant(string? content, IReadOnlyList<LlmToolCall>? toolCalls = null, string? reasoning = null)
        => new("assistant", content) { ToolCalls = toolCalls, Reasoning = reasoning };

    public static LlmChatMessage ToolResult(string toolCallId, string content) => new("tool", content) { ToolCallId = toolCallId };
}

public sealed record LlmToolCall(string Id, string Name, JsonObject? Arguments);

/// <summary>暴露给模型的工具定义</summary>
public sealed record LlmToolDef(string Name, string Description, JsonObject? InputSchema);

public sealed record LlmUsage(int PromptTokens, int CompletionTokens, int ReasoningTokens = 0)
{
    public int TotalTokens => PromptTokens + CompletionTokens;
}

/// <summary>完整回复</summary>
public sealed record LlmResult(
    LlmChatMessage Message,
    LlmUsage Usage,
    LlmFinishReason FinishReason,
    string Model,
    int TtftMs,
    int TotalMs);

/// <summary>流式块</summary>
public abstract record LlmChunk(string Kind)
{
    /// <summary>文本增量</summary>
    public sealed record TextDelta(string Text) : LlmChunk("text_delta");

    /// <summary>思考增量（若有 reasoning 能力）</summary>
    public sealed record ReasoningDelta(string Text) : LlmChunk("reasoning_delta");

    /// <summary>模型发起工具调用</summary>
    public sealed record ToolUse(LlmToolCall Call) : LlmChunk("tool_use");

    /// <summary>流结束（含用量汇总）</summary>
    public sealed record Done(LlmUsage Usage, LlmFinishReason FinishReason, string Model, string? Content, string? Reasoning) : LlmChunk("done");
}

public sealed record LlmRequest
{
    public required IReadOnlyList<LlmChatMessage> Messages { get; init; }

    public IReadOnlyList<LlmToolDef>? Tools { get; init; }

    public string? Model { get; init; }

    public double? Temperature { get; init; }

    public int? MaxTokens { get; init; }

    public bool Stream { get; init; } = true;

    /// <summary>是否启用思考模式（前端聊天全局开关；Ping/上下文压缩等默认关闭）</summary>
    public bool ThinkingEnabled { get; init; }

    /// <summary>思考强度（UI 档：Low/Medium/High/Max；发送侧统一映射 low/high/high/max）</summary>
    public LlmThinkingEffort ThinkingEffort { get; init; } = LlmThinkingEffort.High;

    /// <summary>是否启用思考（reasoning）</summary>
    public bool EnableReasoning { get; init; }
}

/// <summary>LLM 客户端（可拔插：OpenAI 兼容 / Mock / 后续 Anthropic 等）</summary>
public interface ILlmClient
{
    string ProviderName { get; }

    string Model { get; }

    /// <summary>流式对话（首 token 时延 TTFT 由实现上报到事件流）</summary>
    IAsyncEnumerable<LlmChunk> StreamAsync(LlmRequest request, CancellationToken ct);

    Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct);
}
