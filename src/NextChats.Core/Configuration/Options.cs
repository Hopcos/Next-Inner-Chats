namespace NextChats.Core.Configuration;

/// <summary>策略引擎配置（危险操作拦截）</summary>
public sealed class PolicyOptions
{
    /// <summary>危险工具名匹配模式（命中 → 需审批或拦截）</summary>
    public string[] DangerousToolPatterns { get; set; } =
    [
        ".*(delete|drop|remove|truncate|purge|destroy).*",
        ".*(exec|shell|command|run_script|bash|powershell).*",
        ".*(send_|transfer|payment|charge|approve|publish|release|deploy).*",
        ".*(update_password|reset_password|revoke|ban|unban).*",
    ];

    /// <summary>豁免（危险名单但属于系统内置白名单工具）</summary>
    public string[] AllowToolPatterns { get; set; } = [];

    /// <summary>直接拦截名单（命中 → Deny，不走审批）</summary>
    public string[] DenyToolPatterns { get; set; } = [];

    /// <summary>审批超时（秒），超时自动 Expired</summary>
    public int ApprovalTimeoutSeconds { get; set; } = 120;

    /// <summary>ReAct 最大迭代轮数（防止死循环烧 Token；触顶时引擎会下发提示事件）</summary>
    public int MaxReActSteps { get; set; } = 50;

    /// <summary>工具调用最大重试次数（错误进循环 + 重试策略，而不是打印一行就结束）</summary>
    public int MaxToolRetries { get; set; } = 2;

    /// <summary>工具重试退避毫秒</summary>
    public int ToolRetryDelayMs { get; set; } = 300;
}

/// <summary>上下文管理（压缩/截断策略）配置</summary>
public sealed class ContextOptions
{
    /// <summary>粗估 token：字符数/token 的比例分母</summary>
    public int CharsPerToken { get; set; } = 4;

    /// <summary>上下文水位线：达到 contextWindow * 0.8 即触发压缩</summary>
    public double CompressThreshold { get; set; } = 0.8;

    /// <summary>压缩后保留的目标水位（占 contextWindow 比例）</summary>
    public double CompressTarget { get; set; } = 0.5;

    /// <summary>硬截断下限：至少保留的消息条数</summary>
    public int MinMessagesAfterTruncate { get; set; } = 4;

    /// <summary>溢出时按“丢弃最旧的 user/assistant 对”进行截断</summary>
    public int TruncateChunkMessages { get; set; } = 4;
}

/// <summary>已用工具集按需裁剪：每轮只向 LLM 发送"实际用到的工具"的定义，避免多轮重复计费大体积工具 schema</summary>
public sealed class ToolTrimOptions
{
    /// <summary>总开关</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>稳定判定：连续 N 轮调用集合未超出"历史 seen 集合"时才允许裁剪（默认 2 轮）</summary>
    public int StableRounds { get; set; } = 2;

    /// <summary>预算判定：第 N 轮起无论如何开始收窄到 seen 集合（默认第 3 轮）</summary>
    public int AfterRounds { get; set; } = 3;

    /// <summary>工具总数大于该值才裁剪；子代理白名单很小（≤3）则跳过</summary>
    public int MinTools { get; set; } = 4;

    /// <summary>每轮补发上限：检测到模型调用"未声明工具"时补入该工具并重决策的次数（防御未严格约束的工具调用）</summary>
    public int RefillLimitPerRound { get; set; } = 1;
}

/// <summary>内置工具（http_fetch 等）配置 —— HTTP 抓取默认仅放行白名单域名，防 SSRF</summary>
public sealed class BuiltinToolOptions
{
    /// <summary>http_fetch 允许访问的域名（精确或子域后缀匹配；默认 GitHub 系，可自行增删）</summary>
    public string[] HttpFetchAllowHosts { get; set; } = ["github.com", "raw.githubusercontent.com"];

    /// <summary>响应体最大字节数（超出报“内容过大”，默认 2 MB）</summary>
    public int HttpFetchMaxBytes { get; set; } = 2_000_000;

    /// <summary>返回给模型的文本最大字符数（超出截断；http_fetch 与 mcp_read_resource 共用）</summary>
    public int HttpFetchMaxChars { get; set; } = 40_000;

    /// <summary>单次抓取超时（秒）</summary>
    public int HttpFetchTimeoutSeconds { get; set; } = 12;
}

/// <summary>安全配置</summary>
public sealed class SecurityOptions
{
    /// <summary>AES-GCM 主密钥（Base64，32 字节；生产环境务必通过环境变量/密钥管理注入）</summary>
    public string EncryptionKey { get; set; } = string.Empty;

    /// <summary>JWT 签名密钥（生产环境通过环境变量注入）</summary>
    public string JwtKey { get; set; } = string.Empty;

    public string JwtIssuer { get; set; } = "next-chats";

    public string JwtAudience { get; set; } = "next-chats-web";

    /// <summary>access token 有效期（分钟）。方案 B 双令牌：短时 access，到期由 refresh token 静默续期</summary>
    public int JwtExpireMinutes { get; set; } = 30;

    /// <summary>refresh token 有效期（天）；每次使用轮换（旧令牌撤销），空闲超过该时长需重新登录</summary>
    public int JwtRefreshExpireDays { get; set; } = 7;

    /// <summary>注入检测告警后是否仍放行（False = 拦截并提示，True = 放行但审计标记）</summary>
    public bool ProceedOnInjection { get; set; } = true;
}
