namespace NextChats.Core.Domain;

/// <summary>LLM 供应商类型（可拔插，新增类型只需注册对应 Client）</summary>
public enum LlmProviderKind
{
    /// <summary>OpenAI 兼容 Chat Completions 协议</summary>
    OpenAiCompatible = 0,

    /// <summary>内置离线演示模型（无需 Key），用于联调流式/中断链路</summary>
    Mock = 1,
}

/// <summary>模型级思考力度（发送 reasoning_effort；Mock 端控制模拟推理内容长度）</summary>
public enum LlmThinkingEffort
{
    /// <summary>关闭思考（默认；不发送 reasoning_effort 参数，由网关默认行为决定）</summary>
    Off = 0,

    /// <summary>低力度（快速低耗）</summary>
    Low = 1,

    /// <summary>中力度</summary>
    Medium = 2,

    /// <summary>高力度（深度推理）</summary>
    High = 3,

    /// <summary>最大力度（复杂/长链 Agent 任务；DeepSeek-V4 家族映射到 reasoning_effort=max）</summary>
    Max = 4,
}

/// <summary>MCP 传输类型（当前支持 Streamable HTTP，后续扩展 STDIO 等）</summary>
public enum McpTransportType
{
    /// <summary>Streamable HTTP（默认，遵循最新 MCP 规范）</summary>
    Http = 0,

    /// <summary>标准输入输出传输（本地子进程）</summary>
    Stdio = 1,
}

public enum McpItemKind
{
    Tool = 0,
    Prompt = 1,
    Resource = 2,
}

public enum ChatRole
{
    System = 0,
    User = 1,
    Assistant = 2,
    Tool = 3,
}

public enum MessageStatus
{
    Sending = 0,
    Complete = 1,
    Stopped = 2,
    Failed = 3,
}

public enum SessionStatus
{
    Active = 0,
    Archived = 1,
}

public enum UserStatus
{
    Active = 0,
    Disabled = 1,
}

public enum ApprovalStatus
{
    Pending = 0,
    Approved = 1,
    Rejected = 2,
    Expired = 3,
    Cancelled = 4,
}

public enum AuditCategory
{
    Auth = 0,
    Chat = 1,
    Tool = 2,
    Config = 3,
    Security = 4,
    Admin = 5,
    Approval = 6,
}

/// <summary>工作空间访问级别（角色绑定取值；工具按最小级别放行：只读 10 / 写 20 / 完全 30 / 自主 40）</summary>
public enum WorkspaceAccessLevel
{
    /// <summary>只读：查看/搜索/差异/浏览（ws_info ws_list ws_read ws_grep ws_diff ws_stat ws_browse）</summary>
    ReadOnly = 10,

    /// <summary>工作空间写：可修改工作空间根目录内文件（+ ws_write ws_edit ws_mkdir ws_delete；ws_exec 仅限工作空间内 cwd 且命令黑名单）</summary>
    WorkspaceWrite = 20,

    /// <summary>完全访问：额外解锁任意路径读写与任意命令执行（等同以服务用户身份开终端，危险，需管理员显式授权）</summary>
    FullAccess = 30,

    /// <summary>自主模式：在完全访问基础上跳过所有人工确认（审批自动放行）。管理员默认仍为完全访问，不自动获得自主模式。</summary>
    Autonomous = 40,
}

/// <summary>审批动作</summary>
public enum ApprovalDecision
{
    Approved = 1,
    Rejected = 2,
}

/// <summary>策略评估结果</summary>
public enum PolicyVerdict
{
    /// <summary>放行</summary>
    Allow = 0,

    /// <summary>禁止（拦截，危险操作）</summary>
    Deny = 1,

    /// <summary>需人工审批</summary>
    RequireApproval = 2,
}

/// <summary>内部鉴权成功判定操作符</summary>
public enum SuccessRuleOperator
{
    /// <summary>字段存在且不为空（null / 空字符串 / 空数组 / 空对象 视为空）</summary>
    NotEmpty = 0,

    /// <summary>字段值等于固定值（字符串比较）</summary>
    Equals = 1,
}
