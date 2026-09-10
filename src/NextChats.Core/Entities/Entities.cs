using System.ComponentModel.DataAnnotations;
using NextChats.Core.Domain;

namespace NextChats.Core.Entities;

/// <summary>用户（按用户做数据隔离的基础）</summary>
public class AppUser
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(64)] public string Username { get; set; } = null!;

    /// <summary>
    /// 账号类型：default=系统本地账号（密码登录）；其他值（如 acs）=内部鉴权账号（由鉴权中心验证，无密码）。
    /// 用户唯一性 = (AuthType, Username) 组合唯一。
    /// </summary>
    [MaxLength(64)] public string AuthType { get; set; } = "default";

    [MaxLength(64)] public string? DisplayName { get; set; }

    [MaxLength(128)] public string? Email { get; set; }

    /// <summary>PBKDF2 哈希（永不存明文）</summary>
    [Required, MaxLength(512)] public string PasswordHash { get; set; } = null!;

    [Required, MaxLength(128)] public string PasswordSalt { get; set; } = null!;

    public UserStatus Status { get; set; } = UserStatus.Active;

    /// <summary>
    /// 只读模式（默认否）：即使拥有 admin 角色，也只能查看后台内容，
    /// 无法对管理端做任何新增/编辑/删除等写操作（由 AdminReadonlyGuard 强制拦截）。
    /// 用户自己的会话/收藏等个人数据操作不受影响。
    /// </summary>
    public bool IsReadonly { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastLoginAt { get; set; }

    public List<AppRole> Roles { get; set; } = [];
}

/// <summary>
/// 刷新令牌（Refresh Token）：access token 过期后用于静默续期。
/// 每次使用即轮换（旧令牌撤销、记录替换者）；用户被禁用时该用户全部刷新令牌一并撤销。
/// 仅存 SHA-256 哈希（TokenHash），明文不落库。
/// </summary>
public class UserRefreshToken
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    public AppUser? User { get; set; }

    /// <summary>刷新令牌明文的 SHA-256 十六进制哈希（不存明文）</summary>
    [Required, MaxLength(64)] public string TokenHash { get; set; } = null!;

    public DateTimeOffset ExpiresAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>撤销时间（null = 有效）；轮换时旧令牌被撤销</summary>
    public DateTimeOffset? RevokedAt { get; set; }

    /// <summary>轮换后替换它的新令牌哈希（用于重放审计）</summary>
    [MaxLength(64)] public string? ReplacedByTokenHash { get; set; }
}

/// <summary>角色（RBAC）</summary>
public class AppRole
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(64)] public string Name { get; set; } = null!;

    [Required, MaxLength(64)] public string Code { get; set; } = null!;

    [MaxLength(256)] public string? Description { get; set; }

    /// <summary>内置角色（不可删除）</summary>
    public bool IsSystem { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<AppUser> Users { get; set; } = [];

    /// <summary>角色可用的 MCP 服务</summary>
    public List<McpServer> McpServers { get; set; } = [];

    public List<Prompt> Prompts { get; set; } = [];

    public List<Skill> Skills { get; set; } = [];

    /// <summary>角色可用的 LLM 模型（角色绑定后，拥有该角色的用户才能看到并选择这些模型）</summary>
    public List<LlmModel> Models { get; set; } = [];

    /// <summary>内部鉴权配置（作为该鉴权账号登录时的默认角色）</summary>
    public List<InternalAuthProvider> InternalAuthProviders { get; set; } = [];
}

/// <summary>内部鉴权配置（如 acs / ucs）：调用鉴权中心验证账号密码，通过后自动建号并以 JWT 登录</summary>
public class InternalAuthProvider
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>鉴权名称（acs / ucs…），唯一；也是登录方式标识与内部鉴权用户的账号类型</summary>
    [Required, MaxLength(64)] public string Name { get; set; } = null!;

    /// <summary>鉴权中心地址（如 http://acs2-stage.demo.local/login）</summary>
    [Required, MaxLength(512)] public string Api { get; set; } = null!;

    /// <summary>HTTP 方法（POST / GET / PUT…）</summary>
    [Required, MaxLength(16)] public string HttpMethod { get; set; } = "POST";

    /// <summary>请求体格式：BodyJson = body(application/json)</summary>
    [Required, MaxLength(32)] public string RequestFormat { get; set; } = "BodyJson";

    /// <summary>鉴权中心请求体中用户名对应的字段名（值来源为登录时输入的账号）</summary>
    [Required, MaxLength(64)] public string UsernameField { get; set; } = "username";

    /// <summary>鉴权中心请求体中密码对应的字段名（值来源为登录时输入的密码）</summary>
    [Required, MaxLength(64)] public string PasswordField { get; set; } = "password";

    public bool Enabled { get; set; } = true;

    /// <summary>调用鉴权中心的超时（秒）</summary>
    public int TimeoutSeconds { get; set; } = 15;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>鉴权成功判定规则（AND 语义：全部满足才算成功）</summary>
    public List<InternalAuthSuccessRule> SuccessRules { get; set; } = [];

    /// <summary>基于此鉴权类别登录的内部用户默认绑定的角色（多选）</summary>
    public List<AppRole> DefaultRoles { get; set; } = [];
}

/// <summary>内部鉴权成功判定规则：定义响应中的某个/多个字段不为空，或等于固定值</summary>
public class InternalAuthSuccessRule
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProviderId { get; set; }

    /// <summary>响应 JSON 字段（支持点路径，如 sessionID / data.sessionID）</summary>
    [Required, MaxLength(128)] public string Field { get; set; } = null!;

    public SuccessRuleOperator Operator { get; set; } = SuccessRuleOperator.NotEmpty;

    /// <summary>Operator=Equals 时用于比较的固定值</summary>
    [MaxLength(512)] public string? ExpectedValue { get; set; }

    public InternalAuthProvider? Provider { get; set; }
}

/// <summary>沉浸式工具栏条目：ToolKey 对应前端注册的 Cordis 工具插件（唯一标识从已注册插件中选择，非手工输入）；
/// 权限通过 AllowedRoles 绑定（未绑定 = 仅管理员可用）</summary>
public class AppTool
{
    public Guid Id { get; set; } = Guid.NewGuid();

    /// <summary>唯一标识（对应前端 Cordis 工具插件 key，如 ai-translate），唯一索引</summary>
    [Required, MaxLength(64)] public string ToolKey { get; set; } = null!;

    /// <summary>显示名称（管理端可自定义）</summary>
    [Required, MaxLength(64)] public string Name { get; set; } = null!;

    /// <summary>图标：从系统内置图标库中选择（图标 key）</summary>
    [Required, MaxLength(32)] public string Icon { get; set; } = "puzzle";

    /// <summary>缩略描述（工具主页卡片展示）</summary>
    [MaxLength(256)] public string? Description { get; set; }

    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>可使用此工具的角色（空 = 仅管理员）</summary>
    public List<AppRole> AllowedRoles { get; set; } = [];
}

/// <summary>LLM 供应商配置（Server 端统一管理，多供应商 + 基础信息 + 模型子表）</summary>
public class LlmProvider
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(64)] public string Name { get; set; } = null!;

    public LlmProviderKind Kind { get; set; } = LlmProviderKind.OpenAiCompatible;

    /// <summary>服务地址（OpenAI 兼容 /v1/chat/completions 或 /models）</summary>
    [MaxLength(512)] public string? BaseUrl { get; set; }

    /// <summary>API Key（AES-GCM 加密存储，展示时脱敏）</summary>
    [MaxLength(2048)] public string? ApiKeyEncrypted { get; set; }

    public int TimeoutSeconds { get; set; } = 120;

    public bool Enabled { get; set; }

    /// <summary>供应商级优先级（数字越小越优先，用于 LLM Router 规则引擎）</summary>
    public int Priority { get; set; } = 100;

    /// <summary>
    /// 思考参数模式（前端聊天「思考模式」开关开启时如何下发参数）：
    /// None（默认）= 不发送任何思考参数，交由网关默认行为（llm-cs 等网关 Coding 默认开启思考，且不认 thinking 参数）；
    /// DeepSeek = 官方协议 thinking:{type:enabled, reasoning_effort}；Qwen = enable_thinking；OpenAIEffort = 仅 reasoning_effort。
    /// </summary>
    [MaxLength(32)] public string ThinkingParam { get; set; } = "None";

    /// <summary>健康状态（路由时跳过不健康实例）</summary>
    public bool IsHealthy { get; set; } = true;

    [MaxLength(256)] public string? LastError { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>该供应商下带出的模型（每个模型可分别配置 视觉/上下文/成本/启用）</summary>
    public List<LlmModel> Models { get; set; } = [];
}

/// <summary>供应商下的模型（“获取模型”自动带出；可移除；逐模型配置）</summary>
public class LlmModel
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid ProviderId { get; set; }

    [Required, MaxLength(128)] public string Name { get; set; } = null!;

    /// <summary>是否启用（路由时只选启用模型）</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>是否支持视觉（图片识别）：聊天中启用图片上传/粘贴的前提之一</summary>
    public bool IsVision { get; set; }

    /// <summary>上下文窗口大小（基于此做压缩、截断）</summary>
    public int ContextWindow { get; set; } = 128_000;

    /// <summary>输入成本（每 1K token，美元）</summary>
    public decimal PriceInPer1K { get; set; }

    /// <summary>输出成本（每 1K token，美元）</summary>
    public decimal PriceOutPer1K { get; set; }

    /// <summary>供应商内优先级（数字越小越优先）</summary>
    public int Priority { get; set; } = 100;

    /// <summary>思考力度（已废弃：管理页不再暴露，由聊天全局开关/强度 + 供应商思考参数模式驱动；列保留兼容旧库）</summary>
    public NextChats.Core.Domain.LlmThinkingEffort ThinkingEffort { get; set; } = NextChats.Core.Domain.LlmThinkingEffort.Off;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public LlmProvider? Provider { get; set; }

    /// <summary>绑定了该模型的角色（角色绑定后，只有这些角色的用户可见/可选该模型）</summary>
    public List<AppRole> Roles { get; set; } = [];
}

/// <summary>MCP Server 配置（多 MCP、启用开关；支持手工填写 + 自动带出元数据）</summary>
public class McpServer
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(64)] public string Name { get; set; } = null!;

    public McpTransportType Transport { get; set; } = McpTransportType.Http;

    /// <summary>Streamable HTTP 端点；Stdio 时可为空</summary>
    [MaxLength(512)] public string? Endpoint { get; set; }

    /// <summary>HTTP 请求头（JSON 对象，如 { "Authorization": "Bearer xxx" }；存储时敏感值加密/脱敏）</summary>
    [MaxLength(4096)] public string? HeadersJson { get; set; }

    /// <summary>HeadersJson 是否已整体加密（AES-GCM 落库，使用时解密）</summary>
    public bool IsHeadersEncrypted { get; set; }

    /// <summary>Stdio 可执行文件（后续扩展）</summary>
    [MaxLength(512)] public string? StdioCommand { get; set; }

    [MaxLength(2048)] public string? StdioArgsJson { get; set; }

    public bool Enabled { get; set; }

    /// <summary>是否支持视觉（图片识别）：聊天中 MCP 侧识别图片来源之一</summary>
    public bool IsVision { get; set; }

    public int TimeoutSeconds { get; set; } = 60;

    /// <summary>自动带出的服务器描述（获取后回填）</summary>
    [MaxLength(2048)] public string? Description { get; set; }

    /// <summary>服务器提供的系统级使用指南（MCP 协议 instructions；“获取”后自动回填，可手工编辑；注入 LLM 系统提示供模型遵循）</summary>
    [MaxLength(8192)] public string? Instructions { get; set; }

    /// <summary>自动带出的元数据缓存（JsonObject：capabilities / protocolVersion / tools/prompts/resources 摘要）</summary>
    public string? MetadataJson { get; set; }

    public string? LastError { get; set; }

    public DateTimeOffset? LastFetchedAt { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>服务端可禁用具体 tool/prompt/resource</summary>
    public List<McpCatalogItem> Items { get; set; } = [];

    public List<AppRole> Roles { get; set; } = [];
}

/// <summary>MCP 服务自动带出的能力项（工具/提示/资源），可单独禁用</summary>
public class McpCatalogItem
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid McpServerId { get; set; }

    public McpItemKind Kind { get; set; }

    [Required, MaxLength(256)] public string Name { get; set; } = null!;

    [MaxLength(2048)] public string? Description { get; set; }

    /// <summary>InputSchema（Tool）/参数（Prompt）等原始 JSON</summary>
    public string? SchemaJson { get; set; }

    /// <summary>是否启用（可禁用不需要的工具/提示/资源）</summary>
    public bool Enabled { get; set; } = true;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public McpServer? Server { get; set; }
}

/// <summary>Prompt 配置（Server 端统一，多 Prompt + 启用开关）</summary>
public class Prompt
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(64)] public string Name { get; set; } = null!;

    [MaxLength(512)] public string? Description { get; set; }

    /// <summary>能力摘要（个人设置里只展示名称/描述/能力摘要）</summary>
    [MaxLength(512)] public string? Summary { get; set; }

    /// <summary>模板内容（支持 {{var}} / #if / #each 等占位，由 Prompt 模板引擎渲染）</summary>
    public string Content { get; set; } = null!;

    public bool Enabled { get; set; }

    [MaxLength(1024)] public string? TagsJson { get; set; }

    public int Version { get; set; } = 1;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<AppRole> Roles { get; set; } = [];
}

/// <summary>Skill 配置（懒加载、暴露为元工具由模型决定调用）</summary>
public class Skill
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(64)] public string Name { get; set; } = null!;

    [MaxLength(512)] public string? Description { get; set; }

    [MaxLength(512)] public string? Summary { get; set; }

    /// <summary>元工具名（暴露给模型，如 skill_code_review）</summary>
    [Required, MaxLength(128)] public string MetaToolName { get; set; } = null!;

    /// <summary>指令模板（懒加载：仅当模型调用该元工具时才注入完整指令，防止 Token 爆炸）</summary>
    public string Instruction { get; set; } = null!;

    /// <summary>示例输入（联调用）</summary>
    [MaxLength(2048)] public string? ExampleInput { get; set; }

    /// <summary>示例输出</summary>
    [MaxLength(4096)] public string? ExampleOutput { get; set; }

    public bool Enabled { get; set; }

    /// <summary>执行 Skill 时若需嵌套 LLM 调用可覆盖模型</summary>
    [MaxLength(128)] public string? ModelOverride { get; set; }

    public int MaxNestedSteps { get; set; } = 4;

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public List<AppRole> Roles { get; set; } = [];
}

/// <summary>对话会话</summary>
public class ChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    [Required, MaxLength(128)] public string Title { get; set; } = "";

    public SessionStatus Status { get; set; } = SessionStatus.Active;

    public Guid? LlmProviderId { get; set; }

    /// <summary>压缩后的上下文摘要（供恢复/续聊）</summary>
    public string? ContextJson { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? LastMessageAt { get; set; }

    public AppUser? User { get; set; }

    public List<ChatMessage> Messages { get; set; } = [];
}

/// <summary>聊天消息</summary>
public class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid SessionId { get; set; }

    public Guid UserId { get; set; }

    public ChatRole Role { get; set; }

    /// <summary>内容（脱敏后的完整上下文）</summary>
    public string? Content { get; set; }

    /// <summary>思考过程（可折叠展示）</summary>
    public string? Reasoning { get; set; }

    /// <summary>工具调用/结果 JSON（前端可折叠卡片展示）</summary>
    public string? ToolCallsJson { get; set; }

    public MessageStatus Status { get; set; } = MessageStatus.Complete;

    [MaxLength(128)] public string? Model { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    /// <summary>汇总（冗余存储，供聚合查询免计算）</summary>
    public int TotalTokens { get; set; }

    /// <summary>推理（思考链）token 数</summary>
    public int ReasoningTokens { get; set; }

    /// <summary>首字响应耗时（ms）</summary>
    public int TtftMs { get; set; }

    /// <summary>本次回答总耗时（ms）</summary>
    public int TotalMs { get; set; }

    /// <summary>ReAct 推理轮数</summary>
    public int Rounds { get; set; }

    /// <summary>工具调用次数</summary>
    public int ToolCalls { get; set; }

    /// <summary>估算费用（美元）</summary>
    public decimal Cost { get; set; }

    /// <summary>子 Agent 数量（delegate_task 派生）</summary>
    public int SubAgentCount { get; set; }

    /// <summary>子 Agent 输入 token 汇总</summary>
    public int SubAgentInputTokens { get; set; }

    /// <summary>子 Agent 输出 token 汇总</summary>
    public int SubAgentOutputTokens { get; set; }

    /// <summary>Task 拆解（Planner）输入 token</summary>
    public int PlannerInputTokens { get; set; }

    /// <summary>Task 拆解（Planner）输出 token</summary>
    public int PlannerOutputTokens { get; set; }

    [MaxLength(64)] public string? TraceId { get; set; }

    /// <summary>客户端消息 ID（写操作幂等）</summary>
    [MaxLength(64)] public string? ClientMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public ChatSession? Session { get; set; }
}

/// <summary>工具审批（dangerous op 拦截：pending / approved / rejected / expired）</summary>
public class ToolApproval
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(64)] public string TraceId { get; set; } = null!;

    public Guid UserId { get; set; }

    public Guid SessionId { get; set; }

    [Required, MaxLength(64)] public string McpServerName { get; set; } = null!;

    [Required, MaxLength(128)] public string ToolName { get; set; } = null!;

    public string? ArgumentsJson { get; set; }

    public ApprovalStatus Status { get; set; } = ApprovalStatus.Pending;

    [MaxLength(512)] public string? Reason { get; set; }

    [MaxLength(64)] public string? DecidedBy { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset? DecidedAt { get; set; }

    public DateTimeOffset ExpiresAt { get; set; }
}

/// <summary>审计日志（角色/用户隔离 + 完整上下文）</summary>
public class AuditLog
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(64)] public string TraceId { get; set; } = null!;

    public Guid? UserId { get; set; }

    public AuditCategory Category { get; set; }

    [Required, MaxLength(64)] public string Action { get; set; } = null!;

    [MaxLength(256)] public string? Target { get; set; }

    /// <summary>脱敏后的细节 JSON</summary>
    public string? DetailJson { get; set; }

    [MaxLength(64)] public string? Ip { get; set; }

    [MaxLength(256)] public string? UserAgent { get; set; }

    public bool IsSuspicious { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>用户设置（启动的 MCP/SKILL/主题等，JSON 值）</summary>
public class UserSetting
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    [Required, MaxLength(64)] public string Key { get; set; } = null!;

    public string ValueJson { get; set; } = "{}";

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>用户收藏的对话（按用户隔离；一对提问+回答；支持重命名、删除、去重）</summary>
public class UserFavorite
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public Guid UserId { get; set; }

    /// <summary>收藏标题：默认取问题摘要（前 N 字符），可手工重命名</summary>
    [Required, MaxLength(128)] public string Title { get; set; } = "";

    /// <summary>收藏的提问（用户消息全文）</summary>
    public string? QuestionText { get; set; }

    /// <summary>收藏的回答（助手消息全文，含思考过程与正文）</summary>
    public string? AnswerText { get; set; }

    /// <summary>来源问题消息 Id（用于去重：同一问题只收藏一次）</summary>
    public Guid? QuestionMessageId { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAt { get; set; } = DateTimeOffset.UtcNow;

    public AppUser? User { get; set; }
}

/// <summary>Token 用量/成本/时延（可观测性与成本）</summary>
public class TokenUsageRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [MaxLength(64)] public string TraceId { get; set; } = null!;

    public Guid? UserId { get; set; }

    public Guid? SessionId { get; set; }

    [MaxLength(128)] public string? ProviderName { get; set; }

    [MaxLength(128)] public string? Model { get; set; }

    public int PromptTokens { get; set; }

    public int CompletionTokens { get; set; }

    public int TotalTokens { get; set; }

    public decimal Cost { get; set; }

    /// <summary>首 Token 时延</summary>
    public int TtftMs { get; set; }

    public int TotalMs { get; set; }

    public int ToolCalls { get; set; }

    public int ToolErrorCount { get; set; }

    public int ApprovalCount { get; set; }

    public int Rounds { get; set; }

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}

/// <summary>写操作幂等记录</summary>
public class IdempotencyRecord
{
    public Guid Id { get; set; } = Guid.NewGuid();

    [Required, MaxLength(128)] public string Key { get; set; } = null!;

    public Guid UserId { get; set; }

    public string ResponseJson { get; set; } = "{}";

    public DateTimeOffset CreatedAt { get; set; } = DateTimeOffset.UtcNow;
}
