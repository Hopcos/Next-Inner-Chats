using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NextChats.Core.Abstractions;
using NextChats.Core.Agents;
using NextChats.Core.Clients;
using NextChats.Core.Configuration;
using NextChats.Core.Domain;
using NextChats.Core.Entities;
using NextChats.Core.Localization;

namespace NextChats.Core.Services;

/// <summary>
/// 编排层（Orchestration）：
///  有效交集工具收集（角色绑定 ∩ 用户启用 ∩ Server 启用 ∩ 项级启用）
///  → 匹配激活 Skills（懒加载元工具）
///  → 构建 Prompt（模板引擎渲染 + 注入上下文）
///  → 执行推理循环（ReAct）
///  → 持久化消息/用量/审计（trace_id 贯穿）。
/// </summary>
public sealed class ChatOrchestrator : IChatOrchestrator
{
    private const string SettingProvider = "chat.providerId";
    private const string SettingModel = "chat.modelId";
    private const string SettingPrompt = "chat.promptId";
    private const string SettingMcpServers = "chat.mcpServers";
    private const string SettingSkills = "chat.skills";

    private static readonly decimal PricePer1KInput = 0.0005m;   // 默认 $0.5/M（可通过 provider.ExtraJson 覆盖）
    private static readonly decimal PricePer1KOutput = 0.0015m;

    /// <summary>内置 http_fetch 工具声明（无需 MCP 服务器，白名单域名 GET 抓取文本）</summary>
    private const string HttpFetchSchemaJson =
        """{"type":"object","properties":{"url":{"type":"string","description":"Absolute http(s) URL to fetch (allowlisted hosts only, e.g. https://raw.githubusercontent.com/owner/repo/main/README.md)"}},"required":["url"]}""";

    /// <summary>内置 mcp_prompt：从 MCP 服务器取 Prompt 模板（渲染为 role: text 文本）</summary>
    private const string McpPromptSchemaJson =
        """{"type":"object","properties":{"name":{"type":"string","description":"Prompt name exposed by the MCP server (see the admin catalog)"},"server":{"type":"string","description":"Optional MCP server name; omit to search all bound servers"},"arguments":{"type":"object","description":"Optional template arguments as a JSON object"}},"required":["name"]}""";

    /// <summary>内置 mcp_resources：列出绑定服务器的可用资源（静态资源 + 模板）</summary>
    private const string McpResourcesSchemaJson =
        """{"type":"object","properties":{"server":{"type":"string","description":"Optional MCP server name; omit to list from all bound servers"}},"required":[]}""";

    /// <summary>内置 mcp_read_resource：读取资源内容为文本</summary>
    private const string McpReadResourceSchemaJson =
        """{"type":"object","properties":{"uri":{"type":"string","description":"Resource URI (from the mcp_resources listing)"},"server":{"type":"string","description":"Optional MCP server name; omit to try all bound servers"}},"required":["uri"]}""";

    private const string BuiltinToolServer = "system";
    private const string HttpFetchToolName = "http_fetch";
    private const string McpPromptToolName = "mcp_prompt";
    private const string McpResourcesToolName = "mcp_resources";
    private const string McpReadResourceToolName = "mcp_read_resource";
    private const string DelegateTaskToolName = "delegate_task";

    private const string SettingDelegationEnabled = "agent.delegationEnabled";
    private const string SettingSubAgentModel = "chat.subAgentModelId";

    /// <summary>主-从委派子任务 schema：task 为自包含任务描述（子代理无本会话历史）；tools 为可选工具白名单（省略 = 全部工具）</summary>
    private const string DelegateTaskSchemaJson =
        """{"type":"object","properties":{"task":{"type":"string","description":"Self-contained goal for the sub-agent, including all context it needs (it has no access to this conversation's history)."},"tools":{"type":"array","items":{"type":"string"},"description":"Optional allowlist of tool names this sub-agent may use (e.g. [\"jira_search\",\"jira_get_issue\"]); omit to allow all parent tools."}},"required":["task"]}""";

    /// <summary>全局子 Agent 并发上限（同时最多 3 个并行子任务，其余排队）</summary>
    private static readonly System.Threading.SemaphoreSlim SubAgentGate = new(3);

    private readonly IConfigStore _config;
    private readonly IChatStore _chat;
    private readonly IMcpDriver _mcp;
    private readonly ISkillExecutionEngine _skills;
    private readonly IAgentLoopEngine _loop;
    private readonly IPromptTemplateEngine _templates;
    private readonly IAuditLogger _audit;
    private readonly ISecurityService _security;
    private readonly ISessionCancellationRegistry _cancellations;
    private readonly ICacheService _cache;
    private readonly IAdminStore _admin;
    private readonly IOptions<SecurityOptions> _securityOptions;
    private readonly IOptions<BuiltinToolOptions> _builtinOptions;
    private readonly ILlmRouter _router;
    private readonly ILogger _logger;

    /// <summary>http_fetch 专用 HttpClient（禁用自动重定向 —— 手动跟随并逐跳校验白名单，防 SSRF）</summary>
    private static readonly HttpClient FetchHttp = new(new SocketsHttpHandler { AllowAutoRedirect = false, ConnectTimeout = TimeSpan.FromSeconds(8) })
    {
        Timeout = TimeSpan.FromSeconds(30),
    };

    public ChatOrchestrator(
        IConfigStore config,
        IChatStore chat,
        IMcpDriver mcp,
        ISkillExecutionEngine skills,
        IAgentLoopEngine loop,
        IPromptTemplateEngine templates,
        IAuditLogger audit,
        ISecurityService security,
        ISessionCancellationRegistry cancellations,
        ICacheService cache,
        IAdminStore admin,
        IOptions<SecurityOptions> securityOptions,
        IOptions<BuiltinToolOptions> builtinOptions,
        ILlmRouter router,
        ILogger<ChatOrchestrator> logger)
    {
        _config = config;
        _chat = chat;
        _mcp = mcp;
        _skills = skills;
        _loop = loop;
        _templates = templates;
        _audit = audit;
        _security = security;
        _cancellations = cancellations;
        _cache = cache;
        _admin = admin;
        _securityOptions = securityOptions;
        _builtinOptions = builtinOptions;
        _router = router;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgentEvent> StreamAsync(ChatStreamRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var trace = $"trc_{Guid.NewGuid():N}"[..24];
        var lang = request.Lang ?? "en";
        _logger.LogInformation("[Orchestrator] start trace={Trace} user={User} session={Session}", trace, request.UserId, request.SessionId);
        var session = await _chat.GetSessionAsync(request.UserId, request.SessionId, ct);
        if (session is null)
        {
            yield return AgentEvent.Error("SESSION_NOT_FOUND", Texts.Get("SESSION_NOT_FOUND", lang), trace);
            yield break;
        }

        // ---------- 写操作幂等 ----------
        if (!string.IsNullOrWhiteSpace(request.ClientMessageId))
        {
            var key = IdempotencyKey(request.UserId, request.ClientMessageId);
            var cached = await _chat.GetIdempotencyAsync(request.UserId, key, ct);
            if (cached is not null)
            {
                yield return new AgentEvent { Kind = "duplicate", TraceId = trace, MessageId = ExtractString(cached.ResponseJson, "messageId") };
                yield return AgentEvent.Done(new JsonUsage(), 0, 0, 0, trace);
                yield break;
            }
        }

        // ---------- 输入 Injection 检测及过滤 ----------
        // 话题级重新生成：以历史中该条 user 提问作为本轮输入（不追加新 user 消息）
        var regenMessage = request.RegenerateFromMessageId is { } regenId
            ? (await _chat.ListMessagesAsync(request.UserId, session.Id, ct))
                .FirstOrDefault(m => m.Id == regenId && m.Role == ChatRole.User)
            : null;
        if (request.RegenerateFromMessageId is not null && regenMessage is null)
        {
            yield return AgentEvent.Error("MESSAGE_NOT_FOUND", Texts.Get("MESSAGE_NOT_FOUND", lang), trace);
            yield break;
        }
        var rawInput = regenMessage?.Content ?? request.UserInput;
        var (sanitized, flagged, hints) = _security.SanitizeUserInput(rawInput);
        await _audit.RecordAsync(
            flagged ? AuditCategory.Security : AuditCategory.Chat,
            flagged ? "INPUT_INJECTION_FLAGGED" : "CHAT.START",
            trace, request.UserId, session.Id.ToString(),
            flagged ? new { hints, originalLength = request.UserInput.Length } : null,
            isSuspicious: flagged, ct: ct);

        if (flagged && !_securityOptions.Value.ProceedOnInjection)
        {
            yield return AgentEvent.Error("INPUT_FLAGGED", Texts.Get("INPUT_FLAGGED", lang), trace);
            yield break;
        }
        var userInput = sanitized;

        // ---------- 图片附件（标准 base64，命名约定 image_source；多张逐个识别为文本） ----------
        // 识别通过 MCP 视觉工具完成（参数名 image_source = 标准 base64）；识别结果以 context 事件展示。
        // 无论识别成功与否，都不把 base64 原文拼进发给模型的文本（模型无法直接“看图”，避免浪费上下文并误导模型去解析 base64）。
        const int MaxImages = 6;
        const int MaxImageBase64Chars = 5_000_000;
        if (request.Images is { Count: > 0 } images)
        {
            if (images.Count > MaxImages)
            {
                yield return AgentEvent.Error("IMAGE_TOO_MANY", Texts.Get("IMAGE_TOO_MANY", lang, MaxImages), trace);
                yield break;
            }
            foreach (var img in images)
            {
                if (string.IsNullOrWhiteSpace(img.Base64) || img.Base64.Length > MaxImageBase64Chars || !IsStandardBase64(img.Base64))
                {
                    yield return AgentEvent.Error("IMAGE_INVALID", Texts.Get("IMAGE_INVALID", lang), trace);
                    yield break;
                }
            }
        }

        _logger.LogInformation("[Orchestrator] sanitized trace={Trace} flagged={Flagged}", trace, flagged);

        // ---------- 持久化用户消息（重新生成模式：该条提问已在库中，不再追加） ----------
        var userMessage = regenMessage;
        if (userMessage is null)
        {
            userMessage = await _chat.AppendMessageAsync(new ChatMessage
            {
                SessionId = session.Id,
                UserId = request.UserId,
                Role = ChatRole.User,
                Content = userInput,
                Status = MessageStatus.Complete,
                TraceId = trace,
                ClientMessageId = request.ClientMessageId,
            }, ct);
        }

        // ---------- 有效交集工具收集 ----------
        var (roleMcpIds, rolePromptIds, roleSkillIds, roleModelIds) = await _config.GetRoleBindingsAsync(request.UserId, ct);
        var settings = await _config.GetUserSettingsAsync(request.UserId, ct);

        var preferredProviderId = request.ProviderId ?? ParseGuid(GetSetting(settings, SettingProvider));
        var preferredModelId = request.ModelId ?? ParseGuid(GetSetting(settings, SettingModel));
        var requestedMcp = request.McpServerIds ?? ParseGuids(GetSetting(settings, SettingMcpServers));
        var requestedSkills = request.SkillIds ?? ParseGuids(GetSetting(settings, SettingSkills));
        // 主-从委派开关（用户设置；默认关闭）与 Sub-Agent 模型选择（默认跟随主模型）
        var delegationEnabled = string.Equals(GetSetting(settings, SettingDelegationEnabled), "true", StringComparison.OrdinalIgnoreCase);
        var subAgentModelId = ParseGuid(GetSetting(settings, SettingSubAgentModel));

        // ---------- LLM 模型角色绑定：服务端强制校验（未授权模型直接拒绝，管理员豁免；未绑定角色=全量可见） ----------
        var isAdmin = await _config.IsAdminAsync(request.UserId, ct);
        if (preferredModelId.HasValue && preferredModelId.Value != Guid.Empty
            && !isAdmin && roleModelIds.Length > 0 && !roleModelIds.Contains(preferredModelId.Value))
        {
            yield return AgentEvent.Error("MODEL_NOT_AUTHORIZED", Texts.Get("MODEL_NOT_AUTHORIZED", lang), trace);
            yield break;
        }

        var servers = (await _config.GetEnabledMcpServersAsync(ct))
            .Where(s => roleMcpIds.Contains(s.Id) && (requestedMcp.Count == 0 || requestedMcp.Contains(s.Id)))
            .ToList();
        var enabledSkills = (await _config.GetEnabledSkillsAsync(ct))
            .Where(s => roleSkillIds.Contains(s.Id) && (requestedSkills.Count == 0 || requestedSkills.Contains(s.Id)))
            .ToList();

        var unifiedTools = new List<UnifiedTool>();
        _logger.LogInformation("[Orchestrator] ready trace={Trace} servers={Servers} skills={Skills}", trace, servers.Count, enabledSkills.Count);
        foreach (var server in servers)
        {
            try
            {
                unifiedTools.AddRange(_mcp.GetEnabledTools(server));
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "收集 MCP 工具失败 trace={Trace} server={Server}", trace, server.Name);
            }
        }
        foreach (var skill in enabledSkills)
        {
            unifiedTools.Add(new UnifiedTool("skill", skill.MetaToolName, skill.Description ?? skill.Name, null, IsSkill: true));
        }
        var skillByName = enabledSkills.ToDictionary(s => s.MetaToolName, StringComparer.OrdinalIgnoreCase);

        // ---------- 内置工具：http_fetch（白名单域名 GET 抓取） ----------
        unifiedTools.Add(new UnifiedTool(
            BuiltinToolServer, HttpFetchToolName,
            "Fetch a web page or raw text file over HTTP(S) GET and return its text content (size-limited). " +
            "Use it to read web pages, README.md, raw files such as https://raw.githubusercontent.com/owner/repo/main/README.md. " +
            "Only allowlisted hosts are reachable (github.com / raw.githubusercontent.com by default).",
            HttpFetchSchemaJson, IsSkill: false));

        // ---------- 内置工具：mcp_prompt / mcp_resources / mcp_read_resource（MCP Prompt 与 Resource 按需取用） ----------
        unifiedTools.Add(new UnifiedTool(
            BuiltinToolServer, McpPromptToolName,
            "Retrieve a prompt template from an MCP server, rendered into role/text content. " +
            "Use when an MCP server exposes reusable prompts (see the admin catalog) and you need their template/instructions to act on them.",
            McpPromptSchemaJson, IsSkill: false));
        unifiedTools.Add(new UnifiedTool(
            BuiltinToolServer, McpResourcesToolName,
            "List resources (static resources and templates) exposed by the bound MCP servers, with their URIs. " +
            "Call this first to discover what can be read, then use mcp_read_resource to fetch content.",
            McpResourcesSchemaJson, IsSkill: false));
        unifiedTools.Add(new UnifiedTool(
            BuiltinToolServer, McpReadResourceToolName,
            "Read an MCP resource by URI and return its text content (size-limited). " +
            "Use a URI obtained from mcp_resources. Binary/image resources return a placeholder.",
            McpReadResourceSchemaJson, IsSkill: false));

        // ---------- 内置工具：delegate_task（主-从委派：并行 Sub-Agent；由用户设置开关控制，默认关闭） ----------
        if (delegationEnabled)
        {
            unifiedTools.Add(new UnifiedTool(
                BuiltinToolServer, DelegateTaskToolName,
                "Delegate a self-contained research/retrieval task to a parallel sub-agent. " +
                "Use it to fan out independent investigations — e.g. analyse multiple JIRA issues, several documents, or " +
                "multiple evidence sources for one incident — which run concurrently instead of one-by-one. " +
                "The sub-agent shares your tools (except delegate_task itself), gets a fresh independent context and a small " +
                "step budget, so the task text must be fully self-contained. It returns a concise conclusion with its usage summary.",
                DelegateTaskSchemaJson, IsSkill: false));
        }

        // ---------- MCP 视觉：多张逐个识别为文本（工具参数名 image_source = 标准 base64） ----------
        var visionLines = new List<string>();
        if (request.Images is { Count: > 0 })
        {
            foreach (var server in servers.Where(s => s.IsVision))
            {
                // 视觉工具选择：精确名优先（describe_image / vision / recognize_image / read_image_text），
                // 再按命名约定兜底，但必须排除批量/元数据/切换类工具
                // （batch_describe_images 参数为 wrapperArguments 批量结构、compare_images 多图、get_image_info 只读元数据、switch_vision_backend 切换后端），
                // 否则会向单张 image_source 校验失败的工具传参。
                var allTools = _mcp.GetEnabledTools(server).ToList();
                var visionTool =
                    allTools.FirstOrDefault(t => t.Name is "vision" or "recognize_image") ??
                    allTools.FirstOrDefault(t => t.Name.Equals("describe_image", StringComparison.OrdinalIgnoreCase)) ??
                    allTools.FirstOrDefault(t => t.Name.Equals("read_image_text", StringComparison.OrdinalIgnoreCase)) ??
                    allTools.FirstOrDefault(t =>
                        t.Name.Contains("image", StringComparison.OrdinalIgnoreCase)
                        && !t.Name.StartsWith("batch_", StringComparison.OrdinalIgnoreCase)
                        && !t.Name.StartsWith("compare_", StringComparison.OrdinalIgnoreCase)
                        && !t.Name.StartsWith("switch_", StringComparison.OrdinalIgnoreCase)
                        && !t.Name.StartsWith("get_", StringComparison.OrdinalIgnoreCase)
                        && !t.Name.StartsWith("list_", StringComparison.OrdinalIgnoreCase));
                if (visionTool is null) continue;
                for (var i = 0; i < request.Images.Count; i++)
                {
                    var img = request.Images[i];
                    var args = JsonSerializer.Serialize(new { image_source = img.Base64 });
                    try
                    {
                        var r = await _mcp.CallToolAsync(server, visionTool.Name, args, trace + $"_img{i}", lang, ct);
                        if (!r.Success)
                        {
                            _logger.LogWarning("MCP 视觉识别失败 trace={Trace} server={Server} tool={Tool} error={Error}",
                                trace, server.Name, visionTool.Name, r.ErrorMessage);
                        }
                        visionLines.Add(r.Success
                            ? $"[image {i + 1}] {truncate(r.ResultText, 500)}"
                            : $"[image {i + 1}] {Texts.Get("IMAGE_RECOGNITION_FAILED", lang)}");
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "MCP 视觉识别失败 trace={Trace} server={Server}", trace, server.Name);
                        visionLines.Add($"[image {i + 1}] {Texts.Get("IMAGE_RECOGNITION_FAILED", lang)}");
                    }
                }
            }
        }
        if (visionLines.Count > 0)
        {
            // 视觉识别结果为文本上下文，前端以 context 事件展示（多张逐个识别）
            yield return AgentEvent.ContextEvent("vision", string.Join("\n", visionLines), trace);
        }
        else if (request.Images is { Count: > 0 })
        {
            // 有图片但未产生任何识别结果：当前绑定集合中没有可用视觉工具（或全部 fallback），明确提示
            yield return AgentEvent.ContextEvent("vision", Texts.Get("IMAGE_NO_VISION_TOOL", lang), trace);
        }

        // ---------- 构建 Prompt（模板渲染） ----------
        var selectedPrompts = new List<Prompt>();
        if (request.PromptId is { } pid && rolePromptIds.Contains(pid))
        {
            var p = await _config.GetPromptAsync(pid, ct);
            if (p is { Enabled: true }) selectedPrompts.Add(p);
        }
        if (selectedPrompts.Count == 0)
        {
            var defaults = (await _config.GetEnabledPromptsAsync(ct)).Where(p => rolePromptIds.Contains(p.Id)).Take(2).ToList();
            selectedPrompts.AddRange(defaults);
        }

        var toolDescs = string.Join("\n", unifiedTools.Select(t => $"- {t.Name}: {truncate(t.Description, 120)}"));
        var skillDescs = string.Join("\n", enabledSkills.Select(s => $"- {s.MetaToolName}: {truncate(s.Description ?? s.Name, 120)}{Texts.Get("SKILL_LAZY_HINT", lang)}"));
        var templateVars = new Dictionary<string, object?>
        {
            ["user"] = new { name = session.User?.DisplayName ?? Texts.Get("USER_DISPLAY_FALLBACK", lang), id = request.UserId },
            ["session_id"] = session.Id,
            ["time"] = DateTimeOffset.Now,
            ["tools"] = string.IsNullOrEmpty(toolDescs) ? Texts.Get("TOOLS_NONE", lang) : toolDescs,
            ["skills"] = string.IsNullOrEmpty(skillDescs) ? Texts.Get("SKILLS_NONE", lang) : skillDescs,
            ["trace_id"] = trace,
        };

        var systemBlocks = new List<string>();
        foreach (var p in selectedPrompts)
        {
            try
            {
                var rendered = _templates.Render(p.Content, templateVars);
                if (!string.IsNullOrWhiteSpace(rendered)) systemBlocks.Add(rendered);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Prompt 渲染失败 trace={Trace} prompt={Prompt}", trace, p.Name);
            }
        }
        if (systemBlocks.Count == 0)
        {
            systemBlocks.Add(Texts.Get("DEFAULT_SYSTEM", lang));
        }

        // ---------- MCP 服务器系统级使用指南（Instructions）注入 ----------
        var mcpGuides = servers
            .Where(s => s.Enabled && !string.IsNullOrWhiteSpace(s.Instructions))
            .Select(s => $"- {s.Name}: {s.Instructions!.Trim()}")
            .ToList();
        if (mcpGuides.Count > 0)
        {
            systemBlocks.Add(Texts.Get("MCP_INSTRUCTIONS_HEADER", lang) + "\n" + string.Join("\n", mcpGuides));
        }
        var systemPrompt = string.Join("\n\n---\n\n", systemBlocks);

        // ---------- 会话历史（按用户隔离） ----------
        var history = await _chat.ListMessagesAsync(request.UserId, session.Id, ct);
        var llmHistory = history
            .Where(m => m.Id != userMessage.Id && m.Role is ChatRole.User or ChatRole.Assistant && !string.IsNullOrWhiteSpace(m.Content))
            .TakeLast(40)
            .Select(m => m.Role == ChatRole.User
                ? LlmChatMessage.User(m.Content!)
                : LlmChatMessage.Assistant(m.Content!, reasoning: m.Reasoning)) // 思考链随历史回传（携带 tools 时官方要求，否则 400）
            .ToList();

        var initialMessages = new List<LlmChatMessage> { LlmChatMessage.System(systemPrompt) };
        initialMessages.AddRange(llmHistory);
        if (visionLines.Count > 0)
        {
            initialMessages.Add(LlmChatMessage.Assistant(string.Join("\n", visionLines)));
        }
        initialMessages.Add(LlmChatMessage.User(userInput));

        // ---------- 推理循环（ReAct） ----------
        var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var registryToken = _cancellations.Register(request.UserId, session.Id);
        using var registryLink = CancellationTokenSource.CreateLinkedTokenSource(registryToken);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, registryToken);

        var finalText = new System.Text.StringBuilder();
        var finalReasoning = new System.Text.StringBuilder();
        var toolTrace = new List<JsonObject>();
        var assistantStatus = MessageStatus.Complete;
        string? model = null;
        LlmUsage? totalUsage = null;
        var failureCode = (string?)null;
        var failureMessage = (string?)null;
        int doneTtftMs = 0, doneTotalMs = 0, doneRounds = 0;
        int doneSubCount = 0, doneSubIn = 0, doneSubOut = 0;
        int donePlannerIn = 0, donePlannerOut = 0;
        decimal doneCost = 0m;

        // 费用：按首选模型配置的输入/输出单价估算（未配置用默认单价；实际 fallback 到其他模型属低频，忽略价差）
        decimal priceIn = PricePer1KInput, priceOut = PricePer1KOutput;
        if (request.ModelId.HasValue && request.ModelId.Value != Guid.Empty)
        {
            var pricingModel = await _admin.GetModelAsync(request.ModelId.Value, ct);
            if (pricingModel is not null)
            {
                if (pricingModel.PriceInPer1K > 0) priceIn = pricingModel.PriceInPer1K;
                if (pricingModel.PriceOutPer1K > 0) priceOut = pricingModel.PriceOutPer1K;
            }
        }

        AgentRunRequest request2 = null!;
        request2 = new AgentRunRequest
        {
            TraceId = trace,
            UserId = request.UserId,
            SessionId = session.Id,
            InitialMessages = initialMessages,
            Tools = unifiedTools,
            PreferredProviderId = preferredProviderId,
            PreferredModelId = request.ModelId,
            AllowedModelIds = isAdmin ? null : roleModelIds,
            ContextWindow = await GetContextWindowAsync(preferredProviderId, request.ModelId, ct),
            Lang = lang,
            // 思考模式：前端全局开关（默认启用）+ 强度（默认 high）；映射在客户端统一执行
            ThinkingEnabled = request.ThinkingEnabled ?? true,
            ThinkingEffort = ParseEffort(request.ThinkingEffort) ?? NextChats.Core.Domain.LlmThinkingEffort.High,
            ToolExecutor = (tool, args, t, tct) => ExecuteToolAsync(tool, args, t, tct, request2, servers, skillByName, lang, subAgentModelId),
        };

        // ---------- 主-从委派启动前拆解（Planner）：可拆时首轮自动并行 delegate_task，不依赖模型自觉 ----------
        LlmUsage? plannerUsage = null;
        if (delegationEnabled)
        {
            try
            {
                var planned = await PlanSubTasksAsync(request2, userInput, unifiedTools, lang, ct);
                if (planned is not null)
                {
                    // Planner 的 LLM 消耗无论拆与否都计入面板
                    plannerUsage = planned.Usage;
                    if (planned.Calls is { Count: > 0 })
                    {
                        request2 = request2 with { PrecomputedToolCalls = planned.Calls };
                        _logger.LogInformation("[Orchestrator] planner decomposed {Count} sub-tasks, tools={Tools} trace={Trace}",
                            planned.Calls.Count,
                            string.Join(" | ", planned.Calls.Select(c => c.Arguments?.ToJsonString() ?? "")),
                            trace);
                    }
                    else
                    {
                        _logger.LogInformation("[Orchestrator] planner decided no decomposition, usage={In}/{Out} trace={Trace}",
                            planned.Usage?.PromptTokens ?? 0, planned.Usage?.CompletionTokens ?? 0, trace);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // Planner 失败不阻断主流程：退回模型自主决策
                _logger.LogWarning(ex, "[Orchestrator] planner failed trace={Trace}", trace);
            }
        }

        try
        {
            await foreach (var ev in _loop.RunAsync(request2, linked.Token))
            {
                var outEv = ev;
                switch (ev.Kind)
                {
                    case "text_delta":
                        finalText.Append(ev.Text);
                        break;
                    case "thinking_delta":
                        finalReasoning.Append(ev.Text);
                        if (finalReasoning.Length > 40_000) finalReasoning.Length = 40_000;
                        break;
                    case "tool_start":
                        toolTrace.Add(new JsonObject
                        {
                            ["server"] = ev.ServerName,
                            ["tool"] = ev.ToolName,
                            ["args"] = ev.ArgumentsJson is null ? null : JsonNode.Parse(ev.ArgumentsJson),
                            ["approvalId"] = ev.ApprovalId?.ToString(),
                            ["approvalStatus"] = ev.ApprovalStatus,
                        });
                        break;
                    case "tool_result":
                    case "tool_error":
                    {
                        var last = toolTrace.LastOrDefault(t => t["tool"]?.GetValue<string>() == ev.ToolName);
                        if (last is not null)
                        {
                            last["success"] = ev.Success;
                            last["durationMs"] = ev.DurationMs;
                            last["preview"] = ev.ResultPreview is null ? null : truncate(ev.ResultPreview, 400);
                            last["errorCode"] = ev.ErrorCode;
                        }
                        break;
                    }
                    case "error":
                        failureCode ??= ev.Code;
                        failureMessage ??= ev.Message;
                        assistantStatus = ev.Code == "INTERRUPTED" ? MessageStatus.Stopped : MessageStatus.Failed;
                        break;
                    case "done":
                        // 推理 token：优先用模型上报的 reasoning_tokens；网关未上报时按思考链文本长度估算（chars/4，与首轮兜底估算一致）
                        var reasoningTokens = ev.Usage?.ReasoningTokens ?? 0;
                        if (reasoningTokens <= 0 && finalReasoning.Length > 0)
                        {
                            reasoningTokens = Math.Max(1, finalReasoning.Length / 4);
                        }
                        totalUsage = ev.Usage is null ? null : new LlmUsage(ev.Usage.PromptTokens, ev.Usage.CompletionTokens, reasoningTokens);
                        model ??= ev.Model;
                        doneTtftMs = ev.TtftMs ?? 0;
                        doneTotalMs = ev.TotalMs ?? 0;
                        doneRounds = ev.Usage?.Rounds ?? 0;
                        doneSubCount = ev.Usage?.SubAgentCount ?? 0;
                        doneSubIn = ev.Usage?.SubAgentInputTokens ?? 0;
                        doneSubOut = ev.Usage?.SubAgentOutputTokens ?? 0;
                        donePlannerIn = plannerUsage?.PromptTokens ?? 0;
                        donePlannerOut = plannerUsage?.CompletionTokens ?? 0;
                        doneCost = EstimateCost(totalUsage, priceIn, priceOut)
                            + ((decimal)doneSubIn * priceIn + (decimal)doneSubOut * priceOut) / 1000m
                            + ((decimal)donePlannerIn * priceIn + (decimal)donePlannerOut * priceOut) / 1000m; // 子 Agent + Task 拆解消耗均计入费用
                        // 在透传前重写 done：补齐模型名 + 估算推理 token + 按模型单价估算的费用
                        if (ev.Usage is not null)
                        {
                            outEv = AgentEvent.Done(new JsonUsage
                            {
                                PromptTokens = ev.Usage.PromptTokens,
                                CompletionTokens = ev.Usage.CompletionTokens,
                                ReasoningTokens = reasoningTokens,
                                TotalTokens = ev.Usage.TotalTokens,
                                Rounds = ev.Usage.Rounds,
                                ToolCalls = ev.Usage.ToolCalls,
                                ToolErrors = ev.Usage.ToolErrors,
                                Approvals = ev.Usage.Approvals,
                                SubAgentCount = ev.Usage.SubAgentCount,
                                SubAgentInputTokens = ev.Usage.SubAgentInputTokens,
                                SubAgentOutputTokens = ev.Usage.SubAgentOutputTokens,
                                PlannerInputTokens = donePlannerIn,
                                PlannerOutputTokens = donePlannerOut,
                            }, doneCost, doneTtftMs, doneTotalMs, trace, model);
                        }
                        break;
                    default:
                        outEv = ev;
                        break;
                }
                yield return outEv;
            }
        }
        finally
        {
            _cancellations.Unregister(request.UserId, session.Id, registryToken);
        }

        // ---------- 持久化助手消息 + 用量 + 幂等 ----------
        var assistantMessage = await _chat.AppendMessageAsync(new ChatMessage
        {
            SessionId = session.Id,
            UserId = request.UserId,
            Role = ChatRole.Assistant,
            Content = finalText.Length > 0 ? finalText.ToString() : null,
            Reasoning = finalReasoning.Length > 0 ? finalReasoning.ToString() : null,
            ToolCallsJson = toolTrace.Count > 0 ? JsonSerializer.Serialize(toolTrace) : null,
            Status = assistantStatus,
            Model = model,
            PromptTokens = totalUsage?.PromptTokens ?? 0,
            CompletionTokens = totalUsage?.CompletionTokens ?? 0,
            TotalTokens = (totalUsage?.PromptTokens ?? 0) + (totalUsage?.CompletionTokens ?? 0),
            ReasoningTokens = totalUsage?.ReasoningTokens ?? 0,
            TtftMs = doneTtftMs,
            TotalMs = doneTotalMs,
            Rounds = doneRounds,
            ToolCalls = toolTrace.Count,
            Cost = doneCost,
            SubAgentCount = doneSubCount,
            SubAgentInputTokens = doneSubIn,
            SubAgentOutputTokens = doneSubOut,
            PlannerInputTokens = donePlannerIn,
            PlannerOutputTokens = donePlannerOut,
            TraceId = trace,
        }, ct);

        // 老数据默认标题兼容：空串/旧"新会话"都按未命名处理（前端展示 fallback 文案）
        session.Title = string.IsNullOrWhiteSpace(session.Title) || session.Title == "新会话"
            ? (userInput.Length > 20 ? userInput[..20] + "…" : userInput)
            : session.Title;
        session.LastMessageAt = DateTimeOffset.UtcNow;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _chat.UpdateSessionAsync(session, ct);

        await _chat.RecordUsageAsync(new TokenUsageRecord
        {
            TraceId = trace,
            UserId = request.UserId,
            SessionId = session.Id,
            ProviderName = model ?? "unknown",
            Model = model,
            PromptTokens = totalUsage?.PromptTokens ?? 0,
            CompletionTokens = totalUsage?.CompletionTokens ?? 0,
            TotalTokens = totalUsage?.TotalTokens ?? 0,
            Cost = EstimateCost(totalUsage, priceIn, priceOut)
                + ((decimal)doneSubIn * priceIn + (decimal)doneSubOut * priceOut) / 1000m,
            TtftMs = doneTtftMs,
            TotalMs = doneTotalMs,
            ToolCalls = toolTrace.Count,
            ToolErrorCount = toolTrace.Count(t => t.TryGetPropertyValue("errorCode", out var e) && e is not null),
            ApprovalCount = toolTrace.Count(t => t.TryGetPropertyValue("approvalId", out var a) && a is not null),
            Rounds = doneRounds,
        }, ct);

        await _chat.StoreIdempotencyAsync(request.UserId,
            IdempotencyKey(request.UserId, request.ClientMessageId ?? Guid.NewGuid().ToString("N")),
            JsonSerializer.Serialize(new { messageId = assistantMessage.Id, sessionId = session.Id }), ct);

        await _audit.RecordAsync(
            assistantStatus == MessageStatus.Failed ? AuditCategory.Security : AuditCategory.Chat,
            assistantStatus == MessageStatus.Failed ? "CHAT.FAILED" : "CHAT.COMPLETE",
            trace, request.UserId, session.Id.ToString(),
            new { status = assistantStatus.ToString(), model, promptTokens = totalUsage?.PromptTokens ?? 0, completionTokens = totalUsage?.CompletionTokens ?? 0, failureCode, failureMessage },
            ct: ct);
    }

    /// <summary>统一工具执行器：Skill 元工具 → SkillExecutionEngine；MCP 工具 → IMcpDriver；内置工具 → 本地执行器</summary>
    private async Task<McpToolResult> ExecuteToolAsync(UnifiedTool tool, string? args, string traceId, CancellationToken ct,
        AgentRunRequest request, IReadOnlyList<McpServer> servers, Dictionary<string, Skill> skillByName, string lang, Guid? subAgentModelId)
    {
        if (tool.IsSkill)
        {
            if (!skillByName.TryGetValue(tool.Name, out var skill))
            {
                return new McpToolResult(false, "", Texts.Get("SKILL_NOT_FOUND", lang), "SKILL_NOT_FOUND", 0, 1);
            }
            var input = ExtractString(args ?? "{}", "input") ?? args ?? "";
            var (ok, result, error) = await _skills.ExecuteAsync(skill, input, traceId, ct);
            return new McpToolResult(ok, result, error, ok ? null : "SKILL_ERROR", 0, 1);
        }

        if (tool.ServerName == BuiltinToolServer)
        {
            return tool.Name switch
            {
                HttpFetchToolName => await ExecuteHttpFetchAsync(args, traceId, lang, ct),
                McpPromptToolName => await ExecuteMcpPromptAsync(args, servers, traceId, lang, ct),
                McpResourcesToolName => await ExecuteMcpResourcesAsync(args, servers, traceId, lang, ct),
                McpReadResourceToolName => await ExecuteMcpReadResourceAsync(args, servers, traceId, lang, ct),
                DelegateTaskToolName => await ExecuteDelegateTaskAsync(args, request, subAgentModelId, traceId, lang, ct),
                _ => new McpToolResult(false, "", Texts.Get("TOOL_NOT_FOUND", lang, tool.Name), "TOOL_NOT_FOUND", 0, 1),
            };
        }

        var server = servers.FirstOrDefault(s => s.Name == tool.ServerName);
        if (server is null)
        {
            return new McpToolResult(false, "", Texts.Get("MCP_SERVER_NOT_FOUND", lang), "SERVER_NOT_FOUND", 0, 1);
        }
        return await _mcp.CallToolAsync(server, tool.Name, args, traceId, lang, ct);
    }

    // ================= 主-从委派（delegate_task → 并行 Sub-Agent） =================

    /// <summary>
    /// Planner 结果：Calls 非空 = 决定拆解（首轮预置并行 delegate_task）；Calls 为空 = 决定不拆（回退模型自主）。
    /// Usage 只要 Planner 的 LLM 调用成功即返回（无论拆或不拆，消耗都要计入 Token 面板）。
    /// </summary>
    private sealed record PlanOutcome(IReadOnlyList<LlmToolCall>? Calls, LlmUsage? Usage);

    /// <summary>
    /// 启动前智能拆解：用轻量 LLM 判定用户请求是否可拆分为 2–3 个相互独立的子任务；
    /// 可拆则把子任务预置为 delegate_task 调用（首轮并行执行），否则 Calls 为空走模型自主决策。
    /// 只要 LLM 调用成功（无论拆与否），就返回其 Usage 以便计入 Token 面板；仅在调用本身失败时返回 null。
    /// </summary>
    private async Task<PlanOutcome?> PlanSubTasksAsync(AgentRunRequest parent, string userInput, IReadOnlyList<UnifiedTool> tools, string lang, CancellationToken ct)
    {
        var client = await _router.SelectClientAsync(parent.PreferredProviderId, parent.PreferredModelId, lang, ct, parent.AllowedModelIds?.ToArray());
        // 工具目录：name — 简介（截断防 prompt 膨胀）；planner 据此给每个子任务选定白名单工具
        var toolCatalog = string.Join("\n", tools.Take(40)
            .Select(t => $"- {t.Name} — {truncate(t.Description.Replace('\n', ' '), 100)}"));
        var prompt =
            "You are a task-decomposition planner for a coordinator agent. " +
            "Decide whether the user's request contains 2 or 3 mutually independent investigation sub-tasks that can be executed in parallel " +
            "(e.g. analysing several separate JIRA issues, reading multiple documents, or querying different data sources for one incident). " +
            "Only decompose when the sub-tasks are truly independent and each has a concrete, self-contained, answerable goal; otherwise do not decompose. " +
            "For every sub-task you MUST also pick the 1-5 tools from the catalog below that it needs; pick ONLY names present in the catalog, omit tools the sub-task does not need. " +
            "Example (tool names are placeholders; use real catalog names): for \"get the core flow of Draw AND check Prod Draw data for the last day\" decompose as " +
            "{\"task\": \"Find and summarize the core flow of Draw in code/Jira\", \"tools\": [\"real_tool_a\"]}, " +
            "{\"task\": \"Query last-day Prod logs for Draw and summarize\", \"tools\": [\"real_tool_b\"]}. " +
            "STRONG GUIDANCE: a request that investigates/retrieves/analyses TWO OR MORE distinct subjects, files, issues, or data sources " +
            "MUST be decomposed into parallel sub-tasks, even simple ones. Only single-subject requests should set \"decompose\": false. " +
            $"Tool catalog:\n{toolCatalog}\n\n" +
            "Reply ONLY with JSON, no prose, no markdown:\n" +
            "{\"decompose\": false}\n" +
            "or\n" +
            "{\"decompose\": true, \"tasks\": [{\"task\": \"...\", \"tools\": [\"tool_name_1\", \"tool_name_2\"]}, {\"task\": \"...\", \"tools\": [\"tool_name_3\"]}]}\n" +
            "Rules: 2-3 tasks maximum; every task text must be fully self-contained (each sub-task has no access to your decision or the original request); " +
            "if a sub-task needs no tool, set \"tools\": []; no filler tasks.";

        var result = await client.CompleteAsync(new LlmRequest
        {
            Messages =
            [
                LlmChatMessage.System(prompt),
                LlmChatMessage.User(truncate(userInput, 3000)),
            ],
            Stream = false,
            EnableReasoning = false,
            ThinkingEnabled = false,
            MaxTokens = 600,
        }, ct);

        var content = result.Message.Content;
        if (string.IsNullOrWhiteSpace(content))
        {
            return new PlanOutcome(null, result.Usage);
        }

        IReadOnlyList<LlmToolCall>? calls = null;
        try
        {
            using var doc = System.Text.Json.JsonDocument.Parse(content);
            var root = doc.RootElement;
            if (root.TryGetProperty("decompose", out var dec) && dec.ValueKind == System.Text.Json.JsonValueKind.True && root.TryGetProperty("tasks", out var tasksEl))
            {
                var knownNames = new HashSet<string>(tools.Select(t => t.Name), StringComparer.OrdinalIgnoreCase);
                var planned = new List<(string Task, List<string> Tools)>();
                foreach (var item in tasksEl.EnumerateArray())
                {
                    var task = item.TryGetProperty("task", out var tv) ? tv.GetString() : null;
                    if (string.IsNullOrWhiteSpace(task)) continue;
                    task = task.Trim();
                    if (task.Length > 1200) task = task[..1200];

                    // 工具白名单：只保留目录里真实存在的名字（防幻觉/注入不存在的工具）
                    var allow = new List<string>();
                    if (item.TryGetProperty("tools", out var toolsEl) && toolsEl.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        foreach (var n in toolsEl.EnumerateArray())
                        {
                            var name = n.GetString();
                            if (!string.IsNullOrWhiteSpace(name) && knownNames.Contains(name) && allow.Count < 6)
                                allow.Add(name);
                        }
                    }
                    planned.Add((task, allow));
                    if (planned.Count >= 3) break;
                }

                if (planned.Count is >= 2 and <= 3)
                {
                    calls = planned
                        .Select((p, i) => new LlmToolCall($"call_sub_{i}", DelegateTaskToolName,
                            JsonNode.Parse(JsonSerializer.Serialize(new { task = p.Task, tools = p.Tools })) as JsonObject))
                        .ToList();
                }
            }
        }
        catch (System.Text.Json.JsonException)
        {
            // 输出非法 JSON：按"不拆"处理，但 LLM 消耗照记
        }
        return new PlanOutcome(calls, result.Usage);
    }

    /// <summary>
    /// 把子任务交给一个独立的 AgentLoopEngine.RunAsync 实例（独立上下文/轮次预算/工具链，仅去掉 delegate_task 防递归），
    /// 事件不外发到 SSE，只收集正文结论 + 用量摘要，以结构化文本回灌主 Agent。
    /// </summary>
    private async Task<McpToolResult> ExecuteDelegateTaskAsync(string? args, AgentRunRequest parentRequest, Guid? subAgentModelId, string traceId, string lang, CancellationToken ct)
    {
        var task = ExtractString(args ?? "{}", "task") ?? (string.IsNullOrWhiteSpace(args) ? null : args!.Trim());
        if (string.IsNullOrWhiteSpace(task))
        {
            return new McpToolResult(false, "", Texts.Get("SUB_AGENT_NEED_TASK", lang), "SUB_AGENT_NEED_TASK", 0, 1);
        }

        // 可选工具白名单（tools 数组）：子代理每轮只携带这些工具的定义，省掉无关工具 schema 的重复开销
        var allowedTools = ParseToolAllowlist(args);

        if (!await SubAgentGate.WaitAsync(TimeSpan.FromSeconds(60), ct))
        {
            return new McpToolResult(false, "", Texts.Get("SUB_AGENT_BUSY", lang), "SUB_AGENT_BUSY", 0, 1);
        }
        try
        {
            var sw = System.Diagnostics.Stopwatch.StartNew();
            var subTrace = $"trc_{Guid.NewGuid():N}"[..24];

            // 子代理工具集：父工具扣除 delegate_task（防递归），再按白名单裁剪
            // 未提供白名单 = 全部；显式空数组 = 无工具（纯 LLM 任务，省掉工具定义开销）；白名单全部无效 = 保守回退全部
            var parentTools = parentRequest.Tools?.Where(t => t.Name != DelegateTaskToolName).ToList() ?? [];
            var subTools = allowedTools is null ? parentTools
                : parentTools.Where(t => allowedTools.Contains(t.Name, StringComparer.OrdinalIgnoreCase)).ToList();
            if (allowedTools is { Count: > 0 } && subTools.Count == 0)
            {
                subTools = parentTools; // 白名单里没有真实工具时保守回退，避免子代理失去能力
            }

            var subRequest = new AgentRunRequest
            {
                TraceId = subTrace,
                UserId = parentRequest.UserId,
                SessionId = parentRequest.SessionId,
                InitialMessages =
                [
                    LlmChatMessage.System(
                        "You are a sub-agent working for a main coordinator agent. " +
                        "Complete the assigned task using the available tools, then reply with ONLY a concise, factual conclusion " +
                        "(no preamble, no planning outline, no markdown headers). Make reasonable assumptions instead of asking questions. " +
                        "If the task cannot be fully completed, state what was found and what is missing."),
                    LlmChatMessage.User(task),
                ],
                Tools = subTools,
                PreferredProviderId = parentRequest.PreferredProviderId,
                PreferredModelId = subAgentModelId ?? parentRequest.PreferredModelId, // 默认跟随主 Agent 模型
                AllowedModelIds = subAgentModelId is null ? parentRequest.AllowedModelIds : null,
                ContextWindow = parentRequest.ContextWindow,
                Lang = lang,
                ThinkingEnabled = parentRequest.ThinkingEnabled,
                ThinkingEffort = parentRequest.ThinkingEffort,
                MaxSteps = 4, // 子任务小轮次预算，防止跑飞
                ToolExecutor = parentRequest.ToolExecutor,
            };

            var text = new System.Text.StringBuilder();
            var reasoning = new System.Text.StringBuilder();
            var toolCount = 0;
            var rounds = 0;
            var inTokens = 0;
            var outTokens = 0;
            var reTokens = 0;
            try
            {
                await foreach (var ev in _loop.RunAsync(subRequest, ct))
                {
                    switch (ev.Kind)
                    {
                        case "text_delta": text.Append(ev.Text); break;
                        case "thinking_delta": reasoning.Append(ev.Text); break;
                        case "tool_start": toolCount++; break;
                        case "round_start": rounds++; break;
                        case "done":
                            inTokens = ev.PromptTokens ?? 0;
                            outTokens = ev.CompletionTokens ?? 0;
                            reTokens = ev.ReasoningTokens ?? 0;
                            break;
                    }
                }
            }
            catch (OperationCanceledException)
            {
                throw; // 主循环中断 → 子任务随之取消
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Sub-agent failed trace={Trace} subTrace={SubTrace}", traceId, subTrace);
                return new McpToolResult(false, "", Texts.Get("SUB_AGENT_ERROR", lang), "SUB_AGENT_ERROR", 0, 1);
            }
            sw.Stop();

            var conclusion = text.Length > 0 ? text.ToString()
                : reasoning.Length > 0 ? "(子代理仅产出思考，无正文结论)"
                : "(子代理未产出结论)";
            var summary =
                $"[Sub-Agent result] tools={toolCount}, rounds={rounds}, {sw.ElapsedMilliseconds}ms, " +
                $"in={inTokens}/out={outTokens}/reasoning={reTokens}\n{truncate(conclusion, 4000)}";
            return new McpToolResult(true, summary, null, null, (int)sw.ElapsedMilliseconds, 1,
                SubAgentCount: 1, SubAgentInputTokens: inTokens, SubAgentOutputTokens: outTokens);
        }
        finally
        {
            SubAgentGate.Release();
        }
    }

    // ================= 内置 MCP Prompt / Resource 工具 =================

    /// <summary>按 server 参数选取候选服务器：缺省 → 全部绑定服务器（按序尝试）；指定 → 全名精确匹配（忽略大小写）</summary>
    private static (IReadOnlyList<McpServer> Candidates, string? ErrorKey) PickMcpServers(
        IReadOnlyList<McpServer> servers, string? serverName, string lang)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return (servers, null);
        var hit = servers.FirstOrDefault(s => s.Name.Equals(serverName.Trim(), StringComparison.OrdinalIgnoreCase));
        if (hit is null)
        {
            return ([], Texts.Get("MCP_SERVER_NOT_FOUND", lang));
        }
        return ([hit], null);
    }

    /// <summary>解析内置工具参数 JSON（name / server / uri / arguments）</summary>
    private static (string? Name, string? Server, string? Uri, string? ArgumentsJson) ParseMcpToolArgs(string? args)
    {
        string? name = null, server = null, uri = null, argumentsJson = null;
        try
        {
            using var doc = JsonDocument.Parse(args ?? "{}");
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return (null, null, null, null);
            if (root.TryGetProperty("name", out var n)) name = n.GetString();
            if (root.TryGetProperty("server", out var s)) server = s.GetString();
            if (root.TryGetProperty("uri", out var u)) uri = u.GetString();
            if (root.TryGetProperty("arguments", out var a) && a.ValueKind == JsonValueKind.Object) argumentsJson = a.GetRawText();
        }
        catch (JsonException)
        {
            // 参数解析失败 → 按缺失处理，由各执行器给出友好错误
        }
        return (name, server, uri, argumentsJson);
    }

    private async Task<McpToolResult> ExecuteMcpPromptAsync(string? args, IReadOnlyList<McpServer> servers, string traceId, string lang, CancellationToken ct)
    {
        var (name, serverName, _, argumentsJson) = ParseMcpToolArgs(args);
        if (string.IsNullOrWhiteSpace(name))
        {
            return new McpToolResult(false, "", Texts.Get("MCP_PROMPT_NEED_NAME", lang), "MCP_PROMPT_NEED_NAME", 0, 1);
        }
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (candidates, errKey) = PickMcpServers(servers, serverName, lang);
        if (errKey is not null) return new McpToolResult(false, "", errKey, "MCP_SERVER_NOT_FOUND", 0, 1);
        foreach (var s in candidates)
        {
            try
            {
                var text = await _mcp.GetPromptAsync(s, name, argumentsJson, ct).ConfigureAwait(false);
                if (text is not null)
                {
                    sw.Stop();
                    return new McpToolResult(true, $"[{s.Name}]\n{text}", null, null, (int)sw.ElapsedMilliseconds, 1);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mcp_prompt 失败 trace={Trace} server={Server} prompt={Prompt}", traceId, s.Name, name);
            }
        }
        sw.Stop();
        var target = candidates.Count == 1 ? candidates[0].Name : string.Join("/", candidates.Select(c => c.Name));
        return new McpToolResult(false, "", Texts.Get("MCP_PROMPT_NOT_FOUND", lang, name, target), "MCP_PROMPT_NOT_FOUND", (int)sw.ElapsedMilliseconds, 1);
    }

    private async Task<McpToolResult> ExecuteMcpResourcesAsync(string? args, IReadOnlyList<McpServer> servers, string traceId, string lang, CancellationToken ct)
    {
        var (_, serverName, _, _) = ParseMcpToolArgs(args);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (candidates, errKey) = PickMcpServers(servers, serverName, lang);
        if (errKey is not null) return new McpToolResult(false, "", errKey, "MCP_SERVER_NOT_FOUND", 0, 1);
        var sb = new System.Text.StringBuilder();
        foreach (var s in candidates)
        {
            try
            {
                var listing = await _mcp.ListResourcesAsync(s, ct).ConfigureAwait(false);
                sb.AppendLine($"## {s.Name}");
                sb.AppendLine(string.IsNullOrWhiteSpace(listing) ? Texts.Get("MCP_RESOURCES_NONE", lang) : listing);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "mcp_resources 失败 trace={Trace} server={Server}", traceId, s.Name);
                sb.AppendLine($"## {s.Name}\n(error: {ex.Message})");
            }
        }
        sw.Stop();
        return new McpToolResult(true, sb.ToString().TrimEnd(), null, null, (int)sw.ElapsedMilliseconds, 1);
    }

    private async Task<McpToolResult> ExecuteMcpReadResourceAsync(string? args, IReadOnlyList<McpServer> servers, string traceId, string lang, CancellationToken ct)
    {
        var (_, serverName, uri, _) = ParseMcpToolArgs(args);
        if (string.IsNullOrWhiteSpace(uri))
        {
            return new McpToolResult(false, "", Texts.Get("MCP_RESOURCE_NEED_URI", lang), "MCP_RESOURCE_NEED_URI", 0, 1);
        }
        var maxChars = Math.Max(2_000, _builtinOptions.Value.HttpFetchMaxChars);
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var (candidates, errKey) = PickMcpServers(servers, serverName, lang);
        if (errKey is not null) return new McpToolResult(false, "", errKey, "MCP_SERVER_NOT_FOUND", 0, 1);
        string? lastError = null;
        foreach (var s in candidates)
        {
            try
            {
                var text = await _mcp.ReadResourceAsync(s, uri, ct).ConfigureAwait(false);
                if (text is not null)
                {
                    sw.Stop();
                    if (text.Length > maxChars) text = text[..maxChars];
                    return new McpToolResult(true, $"[{s.Name}]\n{text}", null, null, (int)sw.ElapsedMilliseconds, 1);
                }
            }
            catch (Exception ex)
            {
                lastError = ex.Message;
                _logger.LogWarning(ex, "mcp_read_resource 失败 trace={Trace} server={Server} uri={Uri}", traceId, s.Name, uri);
            }
        }
        sw.Stop();
        var target = candidates.Count == 1 ? candidates[0].Name : string.Join("/", candidates.Select(c => c.Name));
        return new McpToolResult(false, lastError ?? "", Texts.Get("MCP_RESOURCE_READ_FAILED", lang, uri, target), "MCP_RESOURCE_READ_FAILED", (int)sw.ElapsedMilliseconds, 1);
    }

    // ================= 内置 http_fetch =================

    private async Task<McpToolResult> ExecuteHttpFetchAsync(string? args, string traceId, string lang, CancellationToken ct)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        string? url = null;
        try
        {
            using var doc = JsonDocument.Parse(args ?? "{}");
            if (doc.RootElement.ValueKind == JsonValueKind.Object && doc.RootElement.TryGetProperty("url", out var u))
            {
                url = u.GetString();
            }
        }
        catch (JsonException) { /* 参数解析失败 → 按无效 URL 处理 */ }

        var opts = _builtinOptions.Value;
        if (string.IsNullOrWhiteSpace(url)
            || !Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri)
            || (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return new McpToolResult(false, "", Texts.Get("HTTP_FETCH_BAD_URL", lang), "HTTP_FETCH_BAD_URL", 0, 1);
        }
        if (!HostAllowed(uri.Host, opts))
        {
            _logger.LogWarning("http_fetch 域名不在白名单 trace={Trace} host={Host}", traceId, uri.Host);
            return new McpToolResult(false, "", Texts.Get("HTTP_FETCH_DENIED", lang, uri.Host), "HTTP_FETCH_DENIED", 0, 1);
        }

        try
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, opts.HttpFetchTimeoutSeconds)));

            var resp = await FetchHttp.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            // 手动跟随重定向（最多 3 跳，逐跳校验白名单，防 SSRF 跳转内网）
            for (var hop = 0; hop < 3 && (int)resp.StatusCode is >= 300 and < 400; hop++)
            {
                var loc = resp.Headers.Location;
                resp.Dispose();
                if (loc is null) break;
                var next = new Uri(uri, loc);
                if (!HostAllowed(next.Host, opts))
                {
                    return new McpToolResult(false, "", Texts.Get("HTTP_FETCH_DENIED", lang, next.Host), "HTTP_FETCH_DENIED", 0, 1);
                }
                uri = next;
                resp = await FetchHttp.GetAsync(next, HttpCompletionOption.ResponseHeadersRead, cts.Token);
            }

            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                resp.Dispose();
                sw.Stop();
                return new McpToolResult(false, "", Texts.Get("HTTP_FETCH_HTTP", lang, code), "HTTP_FETCH_HTTP", (int)sw.ElapsedMilliseconds, 1);
            }

            var mediaType = resp.Content.Headers.ContentType?.MediaType ?? "";
            using var ms = new MemoryStream();
            await resp.Content.CopyToAsync(ms, cts.Token);
            resp.Dispose();
            sw.Stop();
            var latency = (int)sw.ElapsedMilliseconds;

            if (ms.Length > opts.HttpFetchMaxBytes)
            {
                return new McpToolResult(false, "", Texts.Get("HTTP_FETCH_TOO_LARGE", lang, ms.Length / 1024 / 1024), "HTTP_FETCH_TOO_LARGE", latency, 1);
            }

            var text = DecodeFetchText(ms.ToArray(), mediaType);
            if (text.Length > opts.HttpFetchMaxChars)
            {
                text = text[..opts.HttpFetchMaxChars];
            }
            return new McpToolResult(true, text, null, null, latency, 1);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return new McpToolResult(false, "", Texts.Get("MCP_TIMEOUT", lang), "HTTP_FETCH_TIMEOUT", 0, 2);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogWarning(ex, "http_fetch 网络失败 trace={Trace} host={Host}", traceId, uri.Host);
            return new McpToolResult(false, "", Texts.Get("HTTP_FETCH_NETWORK", lang), "HTTP_FETCH_NETWORK", 0, 2);
        }
    }

    /// <summary>白名单校验：host 精确等于白名单项，或以 . 前缀作为子域</summary>
    private static bool HostAllowed(string host, BuiltinToolOptions opts)
    {
        foreach (var item in opts.HttpFetchAllowHosts)
        {
            var allow = item.Trim().TrimStart('*', '.').Trim().ToLowerInvariant();
            if (string.IsNullOrEmpty(allow)) continue;
            if (host.Equals(allow, StringComparison.OrdinalIgnoreCase)
                || host.EndsWith("." + allow, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>按 Content-Type 解码并去 HTML 标签：raw 文本原样返回，HTML 抽取可见文本</summary>
    private static string DecodeFetchText(byte[] bytes, string? mediaType)
    {
        var isHtml = mediaType?.Contains("html", StringComparison.OrdinalIgnoreCase) == true;
        string text;
        try
        {
            text = System.Text.Encoding.UTF8.GetString(bytes);
        }
        catch
        {
            text = "";
        }
        if (!isHtml) return CleanControl(text);
        // 去 script/style/注释 → 去标签 → 实体还原 → 压缩空白
        text = System.Text.RegularExpressions.Regex.Replace(text,
            "<(script|style|noscript)[\\s\\S]*?</\\1>", " ", System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        text = System.Text.RegularExpressions.Regex.Replace(text, "<!--[\\s\\S]*?-->", " ");
        text = System.Text.RegularExpressions.Regex.Replace(text, "<[^>]+>", " ");
        text = System.Net.WebUtility.HtmlDecode(text);
        return CleanControl(text);
    }

    private static string CleanControl(string text)
    {
        var sb = new System.Text.StringBuilder(text.Length);
        foreach (var c in text)
        {
            if (c == '\n' || c == '\r' || c == '\t' || c >= ' ')
            {
                sb.Append(c);
            }
        }
        return System.Text.RegularExpressions.Regex.Replace(sb.ToString(), "\\s{3,}", "\n\n");
    }

    private async Task<int> GetContextWindowAsync(Guid? preferredProviderId, Guid? preferredModelId, CancellationToken ct)
    {
        var server = (await _config.GetActiveProvidersAsync(ct))
            .Where(p => p.Enabled && p.IsHealthy)
            .OrderBy(p => p.Priority)
            .FirstOrDefault(p => p.Id == (preferredProviderId ?? Guid.Empty));
        if (server is null)
        {
            server = (await _config.GetActiveProvidersAsync(ct)).Where(p => p.Enabled && p.IsHealthy).OrderBy(p => p.Priority).FirstOrDefault();
        }
        var model = server?.Models.Where(m => m.Enabled).OrderBy(m => m.Priority)
            .FirstOrDefault(m => m.Id == (preferredModelId ?? Guid.Empty))
            ?? server?.Models.Where(m => m.Enabled).OrderBy(m => m.Priority).FirstOrDefault();
        return model?.ContextWindow ?? 128_000;
    }

    private static bool IsStandardBase64(string base64)
    {
        try
        {
            _ = Convert.FromBase64String(base64);
            return true;
        }
        catch (FormatException)
        {
            return false;
        }
    }

    private static string IdempotencyKey(Guid userId, string clientMessageId) => $"{userId}:{clientMessageId}";

    /// <summary>解析 delegate_task 可选工具白名单。返回 null = 未提供（不限工具）；空列表 = 明确"无工具"；非空 = 白名单</summary>
    private static List<string>? ParseToolAllowlist(string? args)
    {
        if (string.IsNullOrWhiteSpace(args)) return null;
        try
        {
            using var doc = JsonDocument.Parse(args);
            if (!doc.RootElement.TryGetProperty("tools", out var arr) || arr.ValueKind != JsonValueKind.Array)
            {
                return null;
            }
            var names = new List<string>();
            foreach (var n in arr.EnumerateArray())
            {
                if (n.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(n.GetString()))
                {
                    names.Add(n.GetString()!);
                }
            }
            return names.Distinct(StringComparer.Ordinal).ToList(); // 空数组 → 空列表（明确无工具）
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? ExtractString(string json, string prop)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String)
            {
                return v.GetString();
            }
        }
        catch (JsonException)
        {
            // 忽略
        }
        return null;
    }

    private static decimal EstimateCost(LlmUsage? usage, decimal? priceIn = null, decimal? priceOut = null)
    {
        if (usage is null) return 0;
        return usage.PromptTokens * (priceIn ?? PricePer1KInput) / 1000
             + usage.CompletionTokens * (priceOut ?? PricePer1KOutput) / 1000;
    }

    private static string? GetSetting(IDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) ? value : null;

    private static Guid? ParseGuid(string? s) => Guid.TryParse(s, out var g) ? g : null;

    private static NextChats.Core.Domain.LlmThinkingEffort? ParseEffort(string? v) =>
        Enum.TryParse<NextChats.Core.Domain.LlmThinkingEffort>(v, true, out var e) ? e : null;

    private static List<Guid> ParseGuids(string? s)
    {
        if (string.IsNullOrWhiteSpace(s)) return [];
        try
        {
            var arr = JsonSerializer.Deserialize<List<string>>(s);
            return arr?.Select(Guid.Parse).ToList() ?? [];
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string truncate(string? s, int max) =>
        string.IsNullOrEmpty(s) ? Texts.Get("NO_DESCRIPTION", "en") : (s.Length <= max ? s : s[..max] + "…");
}
