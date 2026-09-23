using NextChats.Core.Agents;

namespace NextChats.Core.Abstractions;

/// <summary>一次聊天请求中的图片附件（标准 base64，命名约定 image_source）</summary>
public sealed record ImageAttachment
{
    public string? FileName { get; init; }

    public string? MimeType { get; init; }

    /// <summary>标准 base64 编码的图像数据（不含 data: URI 前缀）</summary>
    public required string Base64 { get; init; }
}

/// <summary>用户发起一次聊天请求的编排输入</summary>
public sealed record ChatStreamRequest
{
    public required Guid UserId { get; init; }

    public required Guid SessionId { get; init; }

    public required string UserInput { get; init; }

    /// <summary>图片附件（多张逐个识别为文本；MCP 视觉工具参数名为 image_source）</summary>
    public IReadOnlyList<ImageAttachment>? Images { get; init; }

    /// <summary>客户端消息 ID（幂等）</summary>
    public string? ClientMessageId { get; init; }

    /// <summary>聊天设置（前端传入；服务端用角色绑定 ∩ 启用开关做交集收敛）</summary>
    public Guid? ProviderId { get; init; }

    /// <summary>首选模型（供应商下带出的模型；null = 供应商内自动选择）</summary>
    public Guid? ModelId { get; init; }

    public Guid? PromptId { get; init; }

    public IReadOnlyList<Guid>? McpServerIds { get; init; }

    public IReadOnlyList<Guid>? SkillIds { get; init; }

    /// <summary>思考模式开关（聊天窗口全局开关；null = 默认启用）</summary>
    public bool? ThinkingEnabled { get; init; }

    /// <summary>思考强度（UI 档字符串：low/medium/high/max；null = 默认 high）</summary>
    public string? ThinkingEffort { get; init; }

    /// <summary>界面语言（zh 前缀 = 中文，其他 = 英文；影响默认 system / Agent 事件 / Mock 输出的本地化）</summary>
    public string? Lang { get; init; }

    /// <summary>话题级重新生成：传入要重跑的 user 消息 ID（后端不追加新 user 消息，直接以该条提问重新生成）</summary>
    public Guid? RegenerateFromMessageId { get; init; }

    /// <summary>
    /// 图片附件持久化结果 JSON：[{ "fileName": string, "mimeType": string, "url": "/api/chat/images/yyyyMM/xxx.png" }]。
    /// Api 层已把 base64 落盘（uploads/chat），随用户消息持久化，前端刷新后据此恢复图片。null = 无图。
    /// </summary>
    public string? AttachmentsJson { get; init; }
}

/// <summary>
/// 编排层（Orchestration）：有效交集工具收集 → 匹配激活 Skills → 构建 Prompt → 执行推理循环。
/// </summary>
public interface IChatOrchestrator
{
    /// <summary>流式执行一次对话（SSE 事件流）</summary>
    IAsyncEnumerable<AgentEvent> StreamAsync(ChatStreamRequest request, CancellationToken ct);
}
