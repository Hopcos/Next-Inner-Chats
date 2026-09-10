namespace NextChats.Core.Localization;

/// <summary>
/// 全局用户可见文案字典（en 默认 / zh）。
/// 约定：代码中不得硬编码用户可见文案，一律通过 <see cref="Texts.Get"/> 按 code 取；
/// 语言由请求级 lang（通常来自 X-Lang / Accept-Language）决定，zh 前缀命中中文，其余英文。
/// 支持 {0} {1} 位置参数（string.Format 风格）。
/// </summary>
public static class Texts
{
    private static readonly Dictionary<string, (string En, string Zh)> Table = new(StringComparer.OrdinalIgnoreCase)
    {
        // ---------- 认证 / 通用 ----------
        ["AUTH_INVALID_CREDENTIALS"] = ("Invalid username or password", "用户名或密码错误"),
        ["AUTH_USER_NOT_FOUND"] = ("User not found", "用户不存在"),
        ["AUTH_PROVIDER_NOT_FOUND"] = ("This sign-in method is not configured or has been disabled", "该鉴权方式未配置或已禁用"),
        ["AUTH_PROVIDER_ERROR"] = ("Sign-in service is unavailable, please try again later", "鉴权服务暂时不可用，请稍后重试"),
        ["AUTH_EXPIRED"] = ("Session expired, please sign in again", "登录已过期，请重新登录"),
        ["REFRESH_TOKEN_INVALID"] = ("Invalid or unknown refresh token, please sign in again", "登录凭证已失效，请重新登录"),
        ["REFRESH_TOKEN_EXPIRED"] = ("Your session has expired (inactive over 7 days), please sign in again", "登录已过期（长时间未活动），请重新登录"),
        ["REFRESH_TOKEN_REVOKED"] = ("This session has been signed out elsewhere, please sign in again", "登录凭证已被注销（可能已在其他设备重新登录），请重新登录"),
        ["AUTH_USER_DISABLED"] = ("This account has been disabled, please contact the administrator", "账号已被禁用，请联系管理员"),
        ["SESSION_NOT_FOUND"] = ("Session not found or deleted", "会话不存在或已被删除"),
        ["MESSAGE_NOT_FOUND"] = ("Message not found or already deleted", "消息不存在或已被删除"),
        ["EMPTY_FAVORITE"] = ("Favorite content cannot be empty", "收藏内容不能为空"),
        ["FAVORITE_NOT_FOUND"] = ("Favorite not found or already deleted", "收藏不存在或已被删除"),
        ["FAVORITE_DUPLICATED"] = ("This conversation is already in your favorites", "该对话已收藏，无需重复收藏"),
        ["MODEL_NOT_AUTHORIZED"] = ("You are not authorized to use the selected model. Please choose a model bound to your role.", "您没有使用所选模型的权限，请选择角色已绑定的模型"),
        ["MODEL_NOT_AVAILABLE"] = ("The selected model does not exist or is disabled", "所选模型不存在或已禁用"),
        ["INPUT_EMPTY"] = ("Input cannot be empty", "输入内容不能为空"),
        ["INPUT_TOO_LARGE"] = ("Input is too large (limit {0} characters)", "输入内容过长（上限 {0} 字符）"),
        ["TOOL_FIELDS_REQUIRED"] = ("Tool key and name are required", "工具唯一标识与名称不能为空"),
        ["TOOL_KEY_EXISTS"] = ("This tool key is already registered by another entry", "该工具唯一标识已被其他条目使用"),
        ["TOOL_CONFIG_NOT_FOUND"] = ("Tool entry not found or already deleted", "工具条目不存在或已被删除"),
        ["EMPTY_MESSAGE"] = ("Message cannot be empty", "消息内容不能为空"),
        ["STREAM_ERROR"] = ("Stream request failed, please retry later", "流式对话出现异常，请稍后重试"),
        ["INPUT_FLAGGED"] = ("Suspected prompt injection detected and blocked. Please rephrase your input.", "检测到疑似注入内容，已拦截。请规范输入后重试。"),
        ["IMAGE_TOO_MANY"] = ("Too many images (max {0}). Please reduce and retry.", "图片数量过多（最多 {0} 张），请减少后重试"),
        ["IMAGE_INVALID"] = ("One or more images are invalid (not standard base64 or too large). Please re-upload.", "图片数据无效（非标准 base64 或超出大小限制），请重新上传"),
        ["IMAGE_RECOGNITION_FAILED"] = ("Vision recognition failed for this image", "该图片视觉识别失败"),
        ["IMAGE_NO_VISION_TOOL"] = ("No vision-capable MCP is bound; the image was not recognized. Bind a vision MCP in Chat Settings to enable image recognition.", "当前未绑定支持视觉识别的 MCP 工具，图片未识别；请在「聊天设置」中绑定 Vision 类 MCP 以启用图片识别"),
        ["INTERRUPTED"] = ("Stopped", "已中断"),
        ["AGENT_MAX_STEPS"] = ("⚠ Reached the maximum of {0} tool rounds, so the answer may be incomplete. Try splitting the question into smaller steps.", "⚠ 已达最大工具轮次（{0}），回答可能不完整。建议把问题拆分成更小的步骤后重试。"),
        ["LLM_ERROR"] = ("Model call failed. Please retry or switch the model.", "模型调用出现异常，请稍后重试或更换模型"),
        ["LLM_ERROR_HTTP"] = ("Model service temporarily unavailable (HTTP {0}). Already retried automatically; please try again later or switch the model.", "模型服务暂时不可用（HTTP {0}），已自动重试仍失败，请稍后重试或更换模型"),
        ["LLM_UNAVAILABLE"] = ("No LLM provider is available. Please check provider configuration.", "暂无可用 LLM 供应商，请联系管理员配置并启用"),
        ["LLM_FALLBACK"] = ("Requested provider is unavailable (disabled / unhealthy / call failed). Falling back to {0} / {1}.", "所选供应商不可用（禁用 / 不健康 / 调用失败），已自动切换到 {0} / {1}。"),
        ["LLM_MODEL_FALLBACK"] = ("Requested model is unavailable, using {0} / {1} instead.", "所选模型不可用，已改用 {0} / {1}。"),
        ["MCP_DISABLED"] = ("Please enable the MCP server first", "请先启用该 MCP 服务器"),
        ["INVALID_ENCODING"] = ("Name/arguments contain invalid encoding characters (garbled text). Please submit UTF-8.", "名称/参数包含无效编码字符（乱码），请使用 UTF-8 提交"),
        ["MCP_CONNECT_FAILED"] = ("Cannot reach the MCP server. Check Endpoint / Headers / network (details hidden).", "无法连接 MCP 服务，请检查 Endpoint / Header / 网络（错误已隐藏细节）"),
        ["MCP_NETWORK_ERROR"] = ("Cannot reach the MCP server (network or auth issue). Check the configuration.", "无法连接 MCP 服务（网络或鉴权问题），请检查配置"),
        ["MCP_FETCH_FAILED"] = ("Fetch failed", "获取失败"),
        ["MCP_UNREACHABLE"] = ("MCP server unreachable", "MCP 服务器不可达"),
        ["MCP_TIMEOUT"] = ("MCP service timed out, retry later", "MCP 服务响应超时，请稍后重试"),
        ["MCP_UNKNOWN_ERROR"] = ("MCP service call failed (unknown error)", "MCP 服务调用失败（未知错误）"),
        ["MCP_PROTOCOL_ERROR"] = ("MCP protocol error, check the server configuration", "MCP 协议错误，请检查服务端配置"),
        ["MCP_GENERIC_ERROR"] = ("MCP tool call failed, retry or use another approach", "MCP 工具调用失败，请重试或改用其它方式"),
        ["MCP_NO_CONTENT"] = ("(no content)", "(无内容)"),
        ["MCP_EMPTY"] = ("(empty)", "(空)"),
        ["MCP_IMAGE_CONTENT"] = ("[image content]", "[图片内容]"),
        ["MCP_AUDIO_CONTENT"] = ("[audio content]", "[音频内容]"),
        ["MCP_RESOURCE_REF"] = ("[resource reference]", "[资源引用]"),
        ["MCP_CONTENT_PLACEHOLDER"] = ("[content]", "[内容]"),
        ["MCP_NOT_CONFIGURED"] = ("(not configured)", "(未配置)"),
        ["MCP_INVALID_URI"] = ("(invalid URI)", "(非法 URI)"),
        ["NOT_FOUND"] = ("Requested resource was not found", "请求的资源不存在"),
        ["FORBIDDEN"] = ("Access denied", "没有权限执行该操作"),
        ["BAD_REQUEST"] = ("Invalid request parameters", "请求参数不合法"),
        ["INTERNAL_ERROR"] = ("Internal server error, please retry later", "服务内部错误，请稍后重试"),

        // ---------- 管理实体 ----------
        ["PROVIDER_NOT_FOUND"] = ("LLM provider not found", "供应商不存在"),
        ["MODEL_NOT_FOUND"] = ("Model not found", "模型不存在"),
        ["PROVIDER_DISABLED"] = ("The provider is disabled, please enable it first", "供应商已禁用，请先启用"),
        ["LLM_FETCH_MODELS_FAILED"] = ("Failed to fetch models: {0}", "获取模型失败：{0}"),
        ["LLM_UNREACHABLE"] = ("Provider unreachable: {0}", "供应商不可用：{0}"),
        ["LLM_AUTH_FAILED"] = ("Authentication failed: {0}. Please check the API Key of this provider.", "鉴权失败：{0}。请检查该供应商的 API Key 是否正确。"),
        ["MCP_SERVER_NOT_FOUND"] = ("MCP server not found", "MCP 服务器不存在"),
        ["PROMPT_NOT_FOUND"] = ("Prompt not found", "Prompt 不存在"),
        ["SKILL_NOT_FOUND"] = ("Skill not found", "Skill 不存在"),
        ["USER_NOT_FOUND"] = ("User not found", "用户不存在"),
        ["ROLE_NOT_FOUND"] = ("Role not found", "角色不存在"),
        ["APPROVAL_NOT_FOUND"] = ("Approval record not found", "审批记录不存在"),
        ["APPROVAL_NOT_PENDING"] = ("This approval has already been processed or expired", "该审批已处理或已过期"),
        ["PASSWORD_REQUIRED"] = ("Initial password is required", "初始密码必填"),
        ["CANNOT_DELETE_SELF"] = ("You cannot delete the currently signed-in account", "不能删除当前登录账号"),
        ["USER_READONLY"] = ("Read-only mode: you can only view the admin panel. To make changes, contact an administrator who can edit.", "只读模式：你仅可查看后台内容，无法进行新增、编辑、删除等操作，如需改动请联系可编辑的管理员。"),
        ["CANNOT_READONLY_SELF"] = ("You cannot set the currently signed-in account to read-only, otherwise no one would be able to manage the backend", "不能将当前登录账号设为只读，否则将无人可继续管理后台"),
        ["INTERNAL_AUTH_NOT_FOUND"] = ("Authentication method not found", "鉴权配置不存在"),
        ["INTERNAL_AUTH_FIELDS_REQUIRED"] = ("Authentication name and API endpoint are required", "鉴权名称与鉴权中心地址必填"),
        ["INTERNAL_AUTH_NAME_EXISTS"] = ("An authentication method with this name already exists", "已存在同名鉴权配置"),
        ["SUCCESS_RULE_REQUIRED"] = ("At least one success-response rule is required", "至少需要配置一条鉴权成功判定规则"),
        ["SUCCESS_RULE_FIELD_REQUIRED"] = ("The success-response rule field cannot be empty", "鉴权成功判定规则的字段不能为空"),
        ["SUCCESS_RULE_OPERATOR_INVALID"] = ("Invalid success-response rule operator", "鉴权成功判定规则的操作符不合法"),

        // ---------- Agent 循环（工具回灌给模型 / 事件文案，用户可见） ----------
        ["TOOL_NOT_FOUND"] = ("Tool {0} does not exist or is disabled. Tell the user and switch to an available tool.", "工具 {0} 不存在或未启用。请向用户说明并改用可用工具。"),
        ["OP_DENIED"] = ("This operation was blocked by the system policy (dangerous action). Inform the user and offer an alternative.", "该操作已被系统策略拦截（危险操作），不允许执行。请告知用户并给出替代方案。"),
        ["APPROVAL_TIMEOUT_TOOL"] = ("The tool approval timed out and was not granted. You can retry or use another approach.", "工具调用审批超时未获批准，本次未执行。可重新发起或改用其它方式。"),
        ["APPROVAL_REJECTED_TOOL"] = ("The user rejected this tool call. Stop the operation and explain.", "用户已拒绝本次工具调用，请停止该操作并说明。"),
        ["APPROVAL_EXPIRED_REASON"] = ("Approval timed out and auto-expired", "审批超时未决策，自动过期"),
        ["TOOL_EXECUTE_FAILED"] = ("Tool execution failed", "工具执行失败"),
        ["TOOL_EXECUTE_ERROR"] = ("Tool execution failed. Retry or use another approach.", "工具执行异常，请重试或换一种方式。"),
        ["CONTEXT_COMPRESSED"] = ("Context was near the limit ({0} → {1} messages) and was auto-compressed", "上下文接近上限（{0} → {1} 条），已自动压缩历史"),
        ["LLM_LENGTH_TRUNCATED"] = ("The model reached its output token limit and the reply was cut off. It may be incomplete — ask the model to continue, or regenerate.", "模型输出达到长度上限被截断，回复可能不完整。可让模型继续回答，或重新生成。"),
        ["LLM_WAITING"] = ("Waiting for the model to respond (upstream queued or slow)…", "正在等待模型响应（上游排队或较慢）……"),
        ["SUB_AGENT_NEED_TASK"] = ("delegate_task requires a non-empty \"task\".", "delegate_task 需要非空的 task 任务描述。"),
        ["SUB_AGENT_BUSY"] = ("Too many sub-agents are running concurrently; try again shortly.", "并行子 Agent 已达上限，请稍后重试。"),
        ["SUB_AGENT_ERROR"] = ("The sub-agent failed unexpectedly.", "子 Agent 执行异常。"),

        // ---------- 上下文管理 ----------
        ["CONTEXT_SUMMARY_MARKER"] = ("[Conversation summary] (compressed by the system, kept as background)", "【历史对话摘要】（由系统压缩，仅作背景保留）"),
        ["CONTEXT_COMPRESS_PROMPT"] = ("You are a conversation compressor. Compress the conversation below into a concise summary keeping key facts, user intent, and executed tool results, at most 200 words. Output only the summary.", "你是一个对话压缩器。请用简洁的中文把下面的历史对话压缩成保留关键事实、用户意图、已执行工具结果的摘要，不超过 600 字。只输出摘要。"),
        ["TRUNCATED_SUFFIX"] = ("(truncated)", "（已截断）"),

        // ---------- 默认系统提示 / 模板变量 ----------
        ["DEFAULT_SYSTEM"] = ("You are a helpful AI assistant. Use the context and available tools to answer the user's questions as best you can.", "你是一个乐于助人的 AI 助手。请基于上下文与可用的工具，尽你所能回答用户的问题。"),
        ["MCP_INSTRUCTIONS_HEADER"] = ("System-level usage guides provided by the connected MCP servers (follow them when using these servers' tools):", "以下为已连接 MCP 服务器提供的系统级使用指南（使用对应服务器工具时请遵循）："),
        ["HTTP_FETCH_BAD_URL"] = ("http_fetch: invalid or unsupported URL (http/https required)", "http_fetch：URL 无效或不支持（仅允许 http/https）"),
        ["HTTP_FETCH_DENIED"] = ("http_fetch: host '{0}' is not in the allowed list (github.com / raw.githubusercontent.com by default). Try an allowed host or ask the user to add it to configuration.", "http_fetch：域名 {0} 不在白名单（默认仅 github.com / raw.githubusercontent.com），请使用允许的域名，或联系管理员在配置中放行"),
        ["HTTP_FETCH_HTTP"] = ("http_fetch: the server responded HTTP {0}", "http_fetch：服务器返回 HTTP {0}"),
        ["HTTP_FETCH_TOO_LARGE"] = ("http_fetch: response too large (over ~{0} MB), try a smaller/raw endpoint", "http_fetch：响应体过大（超过约 {0} MB），请尝试更小或 raw 格式的地址"),
        ["HTTP_FETCH_NETWORK"] = ("http_fetch: network error while fetching the URL", "http_fetch：抓取该地址时发生网络错误"),
        ["HTTP_FETCH_TIMEOUT"] = ("http_fetch: fetch timed out", "http_fetch：抓取超时"),
        ["USER_DISPLAY_FALLBACK"] = ("User", "用户"),
        ["TOOLS_NONE"] = ("(no tools available)", "（当前无可用工具）"),
        ["SKILLS_NONE"] = ("(none)", "（无）"),
        ["SKILL_LAZY_HINT"] = ("(skill instructions load lazily on demand)", "（技能指令按需懒加载）"),
        ["NO_DESCRIPTION"] = ("(no description)", "(无描述)"),
        ["SKILL_INPUT_DESC"] = ("The concrete content/material to hand to this skill", "传递给该技能的具体内容/材料"),
        ["SKILL_TARGET_DESC"] = ("Optional: action target (file/function/link, etc.)", "可选：作用目标（文件/函数/链接等）"),
        ["SKILL_DESC_FALLBACK"] = ("{0} skill", "{0} 技能"),
        ["SKILL_NO_OUTPUT"] = ("(no output)", "(无输出)"),
        ["SKILL_LLM_UNAVAILABLE"] = ("LLM temporarily unavailable, skill execution failed", "LLM 暂不可用，技能执行失败"),
        ["MCP_IMAGE_CONTENT"] = ("[image content]", "[图片内容]"),
        ["MCP_AUDIO_CONTENT"] = ("[audio content]", "[音频内容]"),
        ["MCP_BLOB_CONTENT"] = ("[binary content]", "[二进制内容]"),
        ["MCP_CONTENT_PLACEHOLDER"] = ("[content]", "[内容]"),
        ["MCP_RESOURCES_NONE"] = ("(this server exposes no resources)", "（该服务器未暴露资源）"),
        ["MCP_RESOURCE_READ_FAILED"] = ("Failed to read resource '{0}' from MCP server '{1}'", "从 MCP 服务器「{1}」读取资源「{0}」失败"),
        ["MCP_PROMPT_NOT_FOUND"] = ("Prompt '{0}' not found on server '{1}' (or the server rejected the arguments)", "服务器「{1}」上找不到 Prompt「{0}」（或参数被拒绝）"),
        ["MCP_PROMPT_NEED_NAME"] = ("mcp_prompt: 'name' is required", "mcp_prompt：必须提供 name 参数"),
        ["MCP_RESOURCE_NEED_URI"] = ("mcp_read_resource: 'uri' is required", "mcp_read_resource：必须提供 uri 参数"),

        // ---------- Mock 演示模型输出（用户可见正文） ----------
        ["MOCK_TITLE"] = ("(Mock streaming demo · no external model called)", "（Mock 流式演示 · 未调用外部模型）"),
        ["MOCK_TOOL_CONTEXT_INTRO"] = ("I received the tool result and here is my understanding of it:", "我已经收到了工具返回结果，并在下面展示了对它的理解："),
        ["MOCK_CONTINUE"] = ("--- conversation continues ---", "--- 对话继续 ---"),
        ["MOCK_RECEIVED"] = ("Received: {0}", "收到：{0}"),
        ["MOCK_TIME"] = ("Current server time: {0} UTC", "当前服务器时间：{0} UTC"),
        ["MOCK_PROVIDER"] = ("Provider: {0} / {1} (offline Mock — good for testing streaming, collapsible thinking and interruption)", "本次供应商：{0} / {1}（离线 Mock，可用于联调流式输出、思考过程折叠与中断）"),
        ["MOCK_PRODUCTION_HINT"] = ("In production, configure an OpenAI-compatible LLM provider.", "在生产环境请配置 OpenAI 兼容的 LLM Provider。"),
        ["MOCK_REASONING_LOW"] = ("(simulated reasoning, low effort) Quick plan: answer concisely, one pass, minimal verification.", "（模拟推理·低力度）快速规划：简洁作答，单次完成，少量校验。"),
        ["MOCK_REASONING_MEDIUM"] = ("(simulated reasoning, medium effort) Plan: understand the intent; outline the answer; verify key points once before replying.", "（模拟推理·中力度）规划：理解意图；拟定回答要点；回复前核对关键点一次。"),
        ["MOCK_REASONING_HIGH"] = ("(simulated reasoning, high effort) Deep thinking: decompose the question, explore multiple angles, cross-check facts and edge cases, then compose a thorough, well-structured answer with a final self-review.", "（模拟推理·高力度）深度思考：拆解问题；多角度推演；交叉核对事实与边界情况；最后组织完整、结构清晰的回答，并做最终自查。"),
        ["MOCK_REASONING_MAX"] = ("(simulated reasoning, max effort) Exhaustive reasoning: enumerate all branches, simulate each path, verify assumptions and edge cases multiply, then produce the most complete structured answer with a rigorous final audit.", "（模拟推理·最大力度）穷尽推理：枚举所有分支；逐路径推演；多重校验假设与边界情况；最终产出最完整的结构化回答，并做严格终审。"),
        ["MOCK_REASONING_TOOL"] = ("The user may want to use tools. I will call the available mock tools to fetch live info, then compose the reply.", "用户可能希望使用工具。我先调用可用的 mock 工具获取即时信息，再组织回复。"),
        ["MOCK_REASONING_DANGER"] = ("This tool name matches the dangerous-operation policy, so it needs user approval before it actually runs. I will request the tool call now.", "该工具名称命中危险操作策略，需要用户审批后才会真正执行。我先发起工具调用请求。"),
        ["MOCK_REASONING_CALL"] = ("I will call the tool {0} to complete the task, then summarize the result for the user.", "我将调用工具 {0} 完成任务，然后把结果整理给用户。"),
        ["MOCK_IMAGES_RECEIVED"] = ("Received {0} image(s) via image_source (standard base64). They are recognized one by one as text by the vision MCP tool (see results above).", "已收到 {0} 张图片（image_source 标准 base64），已由视觉 MCP 工具逐个识别为文本（见上方识别结果）。"),
    };

    /// <summary>按语言取文案（{0}/{1} 参数用 string.Format 语法）。未知 code 返回 code 本身以便快速发现缺失项。</summary>
    public static string Get(string code, string lang, params object?[] args)
    {
        if (!Table.TryGetValue(code, out var pair)) return code;
        var text = lang?.StartsWith("zh", StringComparison.OrdinalIgnoreCase) == true ? pair.Zh : pair.En;
        return args.Length == 0 ? text : string.Format(text, args);
    }

    /// <summary>判断 code 是否已注册（供调试/测试断言）</summary>
    public static bool Has(string code) => Table.ContainsKey(code);
}
