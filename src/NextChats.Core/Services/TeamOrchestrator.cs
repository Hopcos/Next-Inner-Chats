using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using NextChats.Core.Abstractions;
using NextChats.Core.Agents;
using NextChats.Core.Clients;
using NextChats.Core.Domain;
using NextChats.Core.Entities;
using NextChats.Core.Localization;

namespace NextChats.Core.Services;

/// <summary>
/// 团队协作编排（Team Review）：
/// 用户提问 → 责任工程师产出方案 → 协助工程师【并行】独立建议（同轮互相隔离）→
/// 责任工程师评估吸收/驳斥并迭代，直到责任工程师声明无争议（[CONSENSUS]）或达到
/// 最大建议轮次（maxRounds）→ 责任工程师输出最终结果（[FINAL]）。
///
/// 纯 LLM 调用（不带工具），不触碰现有普通聊天（/api/chat/stream → ChatOrchestrator）路径。
/// </summary>
public sealed class TeamOrchestrator : ITeamOrchestrator
{
    private const int MaxRoundsLimit = 10;
    private const int DefaultMaxRounds = 2;
    private const int DefaultMaxParallel = 4;
    private const int ContextChunkChars = 8000; // 上下文回放单条文本上限（防轮次膨胀超长）

    private readonly IChatStore _chat;
    private readonly IConfigStore _config;
    private readonly IAdminStore _admin;
    private readonly ITeamStore _team;
    private readonly ILlmRouter _router;
    private readonly IAuditLogger _audit;
    private readonly ISessionCancellationRegistry _cancellations;
    private readonly TeamToolExecutor _tools;
    private readonly ILogger<TeamOrchestrator> _logger;

    public TeamOrchestrator(
        IChatStore chat,
        IConfigStore config,
        IAdminStore admin,
        ITeamStore team,
        ILlmRouter router,
        IAuditLogger audit,
        ISessionCancellationRegistry cancellations,
        TeamToolExecutor tools,
        ILogger<TeamOrchestrator> logger)
    {
        _chat = chat;
        _config = config;
        _admin = admin;
        _team = team;
        _router = router;
        _audit = audit;
        _cancellations = cancellations;
        _tools = tools;
        _logger = logger;
    }

    // ================= 配置 =================

    public async Task<TeamConfigDto?> GetConfigAsync(Guid userId, Guid sessionId, CancellationToken ct = default)
    {
        var session = await _chat.GetSessionAsync(userId, sessionId, ct);
        if (session is null) return null;
        var engineers = await _team.ListTeamEngineersAsync(sessionId, ct);
        var cfg = ParseConfig(session.TeamConfigJson);
        return new TeamConfigDto(session.TeamMode, cfg.MaxRounds, cfg.Parallel, cfg.StopOnConsensus, cfg.MaxParallel, engineers.Select(ToDto).ToList());
    }

    public async Task<TeamConfigDto> SaveConfigAsync(Guid userId, Guid sessionId, TeamConfigUpsert cfg, CancellationToken ct = default)
    {
        var session = await _chat.GetSessionAsync(userId, sessionId, ct)
            ?? throw new KeyNotFoundException("session");
        var engineers = cfg.Engineers ?? [];
        if (cfg.Enabled)
        {
            // 启用团队模式：严格校验工程师完整性 + 模型可用/角色绑定
            if (engineers.Count == 0) throw new ArgumentException("TEAM_NEED_ENGINEERS");
            if (engineers.Count(e => e.Role == (int)TeamEngineerRole.Responsible && e.Enabled) != 1)
                throw new ArgumentException("TEAM_NEED_ONE_RESPONSIBLE");
            if (engineers.Count(e => e.Role == (int)TeamEngineerRole.Assistant && e.Enabled) == 0)
                throw new ArgumentException("TEAM_NEED_ASSISTANT");
            var names = engineers.Where(e => e.Enabled).Select(e => e.Name.Trim()).ToList();
            if (names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("TEAM_NAME_REQUIRED");
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count) throw new ArgumentException("TEAM_NAME_DUPLICATE");

            var isAdmin = await _config.IsAdminAsync(userId, ct);
            var roleModelIds = await _config.GetRoleModelIdsAsync(userId, ct);
            var providers = await _config.GetActiveProvidersAsync(ct);

            foreach (var e in engineers)
            {
                var provider = providers.FirstOrDefault(p => p.Id == e.ProviderId && p.Enabled);
                if (provider is null) throw new ArgumentException("TEAM_PROVIDER_INVALID");
                var model = provider.Models.FirstOrDefault(m => m.Id == e.ModelId && m.Enabled);
                if (model is null) throw new ArgumentException("TEAM_MODEL_INVALID");
                if (!isAdmin && !roleModelIds.Contains(e.ModelId)) throw new ArgumentException("TEAM_MODEL_FORBIDDEN");
            }
        }
        else
        {
            // 关闭团队模式：不要求工程师完整性（允许清空列表/暂缺模型），工程师配置保留供下次启用复用；
            // 若仍带了工程师则仅做名称查重这类轻量校验
            var names = engineers.Where(e => e.Enabled).Select(e => e.Name.Trim()).ToList();
            if (names.Any(string.IsNullOrWhiteSpace)) throw new ArgumentException("TEAM_NAME_REQUIRED");
            if (names.Distinct(StringComparer.OrdinalIgnoreCase).Count() != names.Count) throw new ArgumentException("TEAM_NAME_DUPLICATE");
        }

        var maxRounds = Math.Clamp(cfg.MaxRounds, 1, MaxRoundsLimit);
        var maxParallel = Math.Clamp(cfg.MaxParallel, 1, 8);
        var json = JsonSerializer.Serialize(new { maxRounds, parallel = cfg.Parallel, stopOnConsensus = cfg.StopOnConsensus, maxParallel });

        session.TeamMode = cfg.Enabled;
        session.TeamConfigJson = json;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _chat.UpdateSessionAsync(session, ct);

        var entities = engineers.Select((e, i) => new TeamEngineer
        {
            SessionId = sessionId,
            Name = e.Name.Trim(),
            Role = (TeamEngineerRole)e.Role,
            ProviderId = e.ProviderId,
            ModelId = e.ModelId,
            DisplayOrder = e.DisplayOrder != 0 ? e.DisplayOrder : i,
            Enabled = e.Enabled,
            UpdatedAt = DateTimeOffset.UtcNow,
        }).ToList();
        await _team.SaveTeamEngineersAsync(sessionId, entities, ct);

        await _audit.RecordAsync(AuditCategory.Chat, "TEAM.CONFIG", $"trc_{Guid.NewGuid():N}"[..24], userId, sessionId.ToString(),
            new { enabled = cfg.Enabled, maxRounds, parallel = cfg.Parallel, engineers = entities.Count }, ct: ct);

        return new TeamConfigDto(cfg.Enabled, maxRounds, cfg.Parallel, cfg.StopOnConsensus, maxParallel, entities.Select(ToDto).ToList());
    }

    // ================= 执行 =================

    public async IAsyncEnumerable<AgentEvent> StreamAsync(TeamStreamRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // 生产（后台任务）与消费（迭代器 yield）解耦：生产者把事件写入 Channel，
        // 迭代器逐个 yield 给 SSE——规避 C# “yield 不能出现在 try/catch 内”的限制，
        // 同时让每段 team_delta 以流式（而非整段缓冲）推给前端。
        var channel = Channel.CreateUnbounded<AgentEvent>();
        var producer = Task.Run(
            async () =>
            {
                try
                {
                    await RunAsync(request, channel.Writer, ct);
                }
                catch (OperationCanceledException)
                {
                    channel.Writer.TryWrite(AgentEvent.Error("STREAM_CANCELLED", "cancelled", ""));
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "Team stream producer 异常");
                    channel.Writer.TryWrite(AgentEvent.Error("TEAM_STREAM_ERROR", ex.Message, ""));
                }
                finally
                {
                    channel.Writer.TryComplete();
                }
            },
            CancellationToken.None);

        await foreach (var ev in channel.Reader.ReadAllAsync(ct))
        {
            yield return ev;
        }
        try { await producer; } catch { /* 异常已转为事件 */ }
    }

    private async Task RunAsync(TeamStreamRequest request, ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        var session = await _chat.GetSessionAsync(request.UserId, request.SessionId, ct);
        if (session is null)
        {
            writer.TryWrite(AgentEvent.Error("SESSION_NOT_FOUND", "session not found", ""));
            return;
        }
        var lang = request.Lang ?? "en";

        // 幂等：同一 clientMessageId 重复请求直接返回成功（不重复消耗）
        var idemKey = IdempotencyKey(request.UserId, request.ClientMessageId ?? Guid.NewGuid().ToString("N"));
        var existing = await _chat.GetIdempotencyAsync(request.UserId, idemKey, ct);
        if (existing is not null)
        {
            writer.TryWrite(AgentEvent.Done(new JsonUsage(), 0, 0, 0, ""));
            return;
        }

        var engineers = (await _team.ListTeamEngineersAsync(request.SessionId, ct))
            .Where(e => e.Enabled).OrderBy(e => e.DisplayOrder).ToList();
        var responsible = engineers.FirstOrDefault(e => e.Role == TeamEngineerRole.Responsible);
        var assistants = engineers.Where(e => e.Role == TeamEngineerRole.Assistant).ToList();

        if (!session.TeamMode || responsible is null || assistants.Count == 0)
        {
            writer.TryWrite(AgentEvent.Error("TEAM_NOT_CONFIGURED", "team not configured for this session", ""));
            return;
        }

        var cfg = ParseConfig(session.TeamConfigJson);
        var maxRounds = Math.Clamp(cfg.MaxRounds, 1, MaxRoundsLimit);
        var trace = $"trc_{Guid.NewGuid():N}"[..24];

        // 用户消息入库（与普通聊天一致，历史/话题/搜索兼容）
        await _chat.AppendMessageAsync(new ChatMessage
        {
            SessionId = session.Id,
            UserId = request.UserId,
            Role = ChatRole.User,
            Content = request.UserInput,
            Status = MessageStatus.Complete,
            TraceId = trace,
        }, ct);

        await _audit.RecordAsync(AuditCategory.Chat, "TEAM.START", trace, request.UserId, session.Id.ToString(),
            new { prompt = request.UserInput, rounds = maxRounds, assistants = assistants.Count, parallel = cfg.Parallel }, ct: ct);

        var registryToken = _cancellations.Register(request.UserId, session.Id);
        var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, registryToken);

        // 工具上下文（与普通聊天同口径收集：设置勾选 MCP/技能 + 角色绑定 + 内置 + 会话工作空间）
        var toolCtx = await _tools.CollectAsync(request.UserId, session.WorkspaceId, linked.Token);

        var roundsLog = new List<object>();
        LlmUsage totalUsage = new(0, 0);
        int totalTtft = 0, totalMs = 0, doneRounds = 0;
        decimal totalCost = 0m;
        string? modelName = null;
        string? finalPersist = null;
        var status = MessageStatus.Complete;
        string? failureCode = null, failureMessage = null;
        var prices = new Dictionary<Guid, (decimal In, decimal Out)>();

        try
        {
            var task = request.UserInput;

            // 上下文回放缓存：工程师 id → 自己前几轮的建议（隔离用：同轮不见他人建议）
            var assistantHistory = new Dictionary<Guid, List<string>>();
            string solution = task;
            writer.TryWrite(AgentEvent.TeamStart(maxRounds, responsible.Name, assistants.Count, cfg.Parallel, trace));

            for (var round = 1; round <= maxRounds; round++)
            {
                ct.ThrowIfCancellationRequested();
                writer.TryWrite(AgentEvent.TeamRoundStart(round, trace));

                // ---- 1. 责任工程师产出当前方案（流式：team_text 开段 + team_delta 增量；可调用工具；失败自动重试一次） ----
                var solCalls = await CallEngineerWithRetryAsync(responsible,
                    BuildResponsibleSolutionPrompt(responsible.Name, round, maxRounds, task), writer, round, "solution", trace, lang, linked.Token, toolCtx, request.UserId);
                var sol = solCalls[^1];
                foreach (var c in solCalls)
                {
                    AddUsed(c, ref totalUsage, ref modelName, ref totalTtft, ref totalMs);
                    if (c.Usage is not null) totalCost += await EstimateCostAsync(responsible.ModelId, c.Usage, prices, linked.Token);
                }
                if (sol.Err is not null || string.IsNullOrWhiteSpace(sol.Text))
                {
                    status = MessageStatus.Failed; failureCode = "TEAM_RESPONSIBLE_FAIL"; failureMessage = sol.Err ?? "empty solution";
                    writer.TryWrite(AgentEvent.Error(failureCode, failureMessage ?? "", trace));
                    break;
                }
                solution = sol.Text;
                roundsLog.Add(MakeRound(round, "solution", responsible, sol.Text, sol.Thinking, sol.Tools));

                // ---- 2. 协助工程师并行/串行独立建议（同轮互相隔离；各自流式写入同一 Channel） ----
                var suggestionTexts = new Dictionary<Guid, string>();
                IReadOnlyList<IReadOnlyList<EngineerCall>> results;
                if (cfg.Parallel)
                {
                    var sem = new SemaphoreSlim(Math.Max(1, Math.Min(cfg.MaxParallel, assistants.Count)));
                    var tasks = assistants.Select(async a =>
                    {
                        await sem.WaitAsync(linked.Token);
                        try
                        {
                            var ownHistory = assistantHistory.TryGetValue(a.Id, out var h) ? string.Join("\n", h) : "";
                            return await CallEngineerWithRetryAsync(a,
                                BuildAssistantSuggestionPrompt(a.Name, round, maxRounds, task, solution, ownHistory),
                                writer, round, "suggestion", trace, lang, linked.Token, toolCtx, request.UserId);
                        }
                        finally { sem.Release(); }
                    }).ToList();
                    results = await Task.WhenAll(tasks);
                }
                else
                {
                    var seq = new List<IReadOnlyList<EngineerCall>>();
                    foreach (var a in assistants)
                    {
                        var ownHistory = assistantHistory.TryGetValue(a.Id, out var h) ? string.Join("\n", h) : "";
                        seq.Add(await CallEngineerWithRetryAsync(a,
                            BuildAssistantSuggestionPrompt(a.Name, round, maxRounds, task, solution, ownHistory),
                            writer, round, "suggestion", trace, lang, linked.Token, toolCtx, request.UserId));
                    }
                    results = seq;
                }

                for (var i = 0; i < assistants.Count; i++)
                {
                    var eng = assistants[i];
                    var calls = results[i];
                    var r = calls[^1];
                    foreach (var c in calls)
                    {
                        AddUsed(c, ref totalUsage, ref modelName, ref totalTtft, ref totalMs);
                        if (c.Usage is not null) totalCost += await EstimateCostAsync(eng.ModelId, c.Usage, prices, linked.Token);
                    }
                    var sug = !string.IsNullOrWhiteSpace(r.Text) ? r.Text : "(该工程师本轮未能给出建议)";
                    suggestionTexts[eng.Id] = sug;
                    if (!assistantHistory.TryGetValue(eng.Id, out var h)) { h = []; assistantHistory[eng.Id] = h; }
                    h.Add(sug);
                    roundsLog.Add(MakeRound(round, "suggestion", eng, sug, r.Thinking, r.Tools));
                }

                // ---- 3. 评估 / 终轮 ----
                var suggestionsBlock = string.Join("\n\n", assistants
                    .Where(a => suggestionTexts.ContainsKey(a.Id))
                    .Select(a => $"--- {a.Name} ---\n{Truncate(suggestionTexts[a.Id], ContextChunkChars)}"));

                if (round == maxRounds)
                {
                    var fCalls = await CallEngineerWithRetryAsync(responsible,
                        BuildFinalPrompt(responsible.Name, round, maxRounds, task, solution, suggestionsBlock), writer, round, "final", trace, lang, linked.Token, toolCtx, request.UserId);
                    var f = fCalls[^1];
                    foreach (var c in fCalls)
                    {
                        AddUsed(c, ref totalUsage, ref modelName, ref totalTtft, ref totalMs);
                        if (c.Usage is not null) totalCost += await EstimateCostAsync(responsible.ModelId, c.Usage, prices, linked.Token);
                    }
                    if (f.Err is not null || string.IsNullOrWhiteSpace(f.Text))
                    {
                        status = MessageStatus.Failed; failureCode = "TEAM_FINAL_FAIL"; failureMessage = f.Err ?? "empty final";
                        writer.TryWrite(AgentEvent.Error(failureCode, failureMessage ?? "", trace));
                        break;
                    }
                    finalPersist = f.Text; // 发往前端的增量已剥离 [FINAL] 标记；此处同样存剥离后内容
                    doneRounds = round;
                    roundsLog.Add(MakeRound(round, "final", responsible, f.Text, f.Thinking, f.Tools));
                    writer.TryWrite(AgentEvent.TeamEnd(finalPersist, totalCost, trace));
                    break;
                }
                else
                {
                    var evCalls = await CallEngineerWithRetryAsync(responsible,
                        BuildEvalPrompt(responsible.Name, round, maxRounds, task, solution, suggestionsBlock), writer, round, "eval", trace, lang, linked.Token, toolCtx, request.UserId);
                    var ev = evCalls[^1];
                    foreach (var c in evCalls)
                    {
                        AddUsed(c, ref totalUsage, ref modelName, ref totalTtft, ref totalMs);
                        if (c.Usage is not null) totalCost += await EstimateCostAsync(responsible.ModelId, c.Usage, prices, linked.Token);
                    }
                    if (ev.Err is not null || string.IsNullOrWhiteSpace(ev.Text))
                    {
                        status = MessageStatus.Failed; failureCode = "TEAM_EVAL_FAIL"; failureMessage = ev.Err ?? "empty eval";
                        writer.TryWrite(AgentEvent.Error(failureCode, failureMessage ?? "", trace));
                        break;
                    }
                    roundsLog.Add(MakeRound(round, "eval", responsible, ev.Text, ev.Thinking, ev.Tools));
                    solution = ev.Text; // 评估结论成为下一轮“当前方案”

                    if (cfg.StopOnConsensus && ContainsConsensus(ev.Raw))
                    {
                        doneRounds = round;
                        finalPersist = ev.Text;
                        writer.TryWrite(AgentEvent.TeamEnd(finalPersist, totalCost, trace));
                        break;
                    }
                }
            }
        }
        catch (OperationCanceledException)
        {
            status = MessageStatus.Stopped;
            writer.TryWrite(AgentEvent.Error("STREAM_CANCELLED", "cancelled", trace));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Team stream 异常 trace={Trace}", trace);
            status = MessageStatus.Failed; failureCode = "TEAM_STREAM_ERROR"; failureMessage = ex.Message;
            writer.TryWrite(AgentEvent.Error("TEAM_STREAM_ERROR", ex.Message, trace));
        }
        finally
        {
            _cancellations.Unregister(request.UserId, session.Id, registryToken);
        }

        // ---------- 持久化：最终结果 + 团队轮次（RoundsJson） + 用量 ----------
        var roundsJson = roundsLog.Count > 0 ? JsonSerializer.Serialize(roundsLog) : null;
        // 失败且无正文时，把失败原因作为占位内容持久化（避免刷新后出现无法解释的空白消息，造成"会话中断"错觉）
        var finalContent = finalPersist;
        if (string.IsNullOrWhiteSpace(finalContent) && status == MessageStatus.Failed)
        {
            finalContent = $"[{failureCode ?? "TEAM_FAILED"}] {failureMessage ?? "team generation failed"}";
        }
        var assistantMessage = await _chat.AppendMessageAsync(new ChatMessage
        {
            SessionId = session.Id,
            UserId = request.UserId,
            Role = ChatRole.Assistant,
            Content = string.IsNullOrWhiteSpace(finalContent) ? null : finalContent,
            Reasoning = null,
            RoundsJson = roundsJson,
            Status = status,
            Model = modelName,
            PromptTokens = totalUsage.PromptTokens,
            CompletionTokens = totalUsage.CompletionTokens,
            TotalTokens = totalUsage.PromptTokens + totalUsage.CompletionTokens,
            ReasoningTokens = totalUsage.ReasoningTokens,
            CacheTokens = totalUsage.CacheTokens,
            TtftMs = totalTtft,
            TotalMs = totalMs,
            Rounds = doneRounds,
            Cost = totalCost,
            TraceId = trace,
        }, ct);

        session.Title = string.IsNullOrWhiteSpace(session.Title) || session.Title == "新会话"
            ? (request.UserInput.Length > 20 ? request.UserInput[..20] + "…" : request.UserInput)
            : session.Title;
        session.LastMessageAt = DateTimeOffset.UtcNow;
        session.UpdatedAt = DateTimeOffset.UtcNow;
        await _chat.UpdateSessionAsync(session, ct);

        await _chat.RecordUsageAsync(new TokenUsageRecord
        {
            TraceId = trace,
            UserId = request.UserId,
            SessionId = session.Id,
            ProviderName = modelName ?? "unknown",
            Model = modelName,
            PromptTokens = totalUsage.PromptTokens,
            CompletionTokens = totalUsage.CompletionTokens,
            TotalTokens = totalUsage.PromptTokens + totalUsage.CompletionTokens,
            CacheTokens = totalUsage.CacheTokens,
            Cost = totalCost,
            TtftMs = totalTtft,
            TotalMs = totalMs,
            Rounds = doneRounds,
        }, ct);

        await _chat.StoreIdempotencyAsync(request.UserId, idemKey,
            JsonSerializer.Serialize(new { messageId = assistantMessage.Id, sessionId = session.Id }), ct);

        await _audit.RecordAsync(
            status == MessageStatus.Failed ? AuditCategory.Security : AuditCategory.Chat,
            status == MessageStatus.Failed ? "TEAM.FAILED" : "TEAM.COMPLETE",
            trace, request.UserId, session.Id.ToString(),
            new { status = status.ToString(), model = modelName, rounds = doneRounds, failureCode, failureMessage }, ct: ct);

        return;
    }

    private static object MakeRound(int round, string phase, TeamEngineer eng, string text, string? thinking = null, IReadOnlyList<object>? tools = null)
        => new { round, phase, engineerId = eng.Id, engineer = eng.Name, text, thinking, tools };

    // ================= 工程师流式调用 =================

    /// <summary>
    /// 工程师调用 + 失败/空输出自动重试一次：网关瞬断或模型空回复（偶发）不致命，
    /// 重试成功即继续（此前会导致整轮 TEAM_*_FAIL 并中断会话体验）。两次调用的 token 消耗都需计入。
    /// </summary>
    private async Task<IReadOnlyList<EngineerCall>> CallEngineerWithRetryAsync(
        TeamEngineer eng, string systemPrompt, ChannelWriter<AgentEvent> writer,
        int round, string phase, string trace, string lang, CancellationToken ct,
        TeamToolExecutor.TeamToolContext toolCtx, Guid userId)
    {
        var first = await CallEngineerStreamAsync(eng, systemPrompt, writer, round, phase, trace, lang, ct, toolCtx, userId);
        if (first.Err is null && !string.IsNullOrWhiteSpace(first.Text)) return [first];
        var reason = first.Err ?? "empty reply";
        writer.TryWrite(AgentEvent.ContextEvent("team_retry",
            $"{eng.Name} ({phase}) retrying once after: {Truncate(reason, 120)}", trace));
        var second = await CallEngineerStreamAsync(eng, systemPrompt, writer, round, phase, trace, lang, ct, toolCtx, userId);
        return [first, second];
    }

    private const int MaxToolSteps = 8; // 单工程师一次调用内最大工具决策步数（防死循环）
    private const int ToolResultMaxChars = 8000; // 工具结果回灌给 LLM 的最大字符数
    private const int ToolPreviewMaxChars = 2000; // 工具结果展示（工具卡“结果”）最大字符数

    /// <summary>单工程师一次调用的结果：Raw=原始全文（含 [FINAL]/[CONSENSUS] 标记）；Text=剥离标记后的展示/持久化文本；Thinking=完整推理过程；Tools=工具调用轨迹（持久化用）</summary>
    private sealed record EngineerCall(string? Raw, string? Text, string? Thinking, IReadOnlyList<object> Tools,
        LlmUsage? Usage, string? Model, int Ms, int TtftMs, string? Err);

    /// <summary>
    /// 工程师流式调用 + 工具循环（与普通聊天一致：模型可调用 MCP/技能/工作空间/内置工具，工具结果回灌后继续决策，
    /// 直到模型直接输出正文；思考跨步累积展示，正文仅最终步下发）。团队场景无审批，工具直接执行。
    /// </summary>
    private async Task<EngineerCall> CallEngineerStreamAsync(
        TeamEngineer eng, string systemPrompt, ChannelWriter<AgentEvent> writer,
        int round, string phase, string trace, string lang, CancellationToken ct,
        TeamToolExecutor.TeamToolContext toolCtx, Guid userId)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var messages = new List<LlmChatMessage> { LlmChatMessage.System(systemPrompt), LlmChatMessage.User("Proceed.") };
        var toolDefs = toolCtx.Tools.Select(t => new LlmToolDef(t.Name, t.Description, ToSchema(t.SchemaJson))).ToList();
        var toolTraces = new List<object>();
        var thinking = new StringBuilder();
        var finalText = new StringBuilder();
        int thinkShown = 0, finalShown = 0;
        int ttft = -1;
        LlmUsage? usage = null;
        string? model = null;

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(360));
            var client = await _router.GetClientAsync(eng.ProviderId, eng.ModelId, timeout.Token);

            // 开段：前端据此创建（阶段/工程师）卡片；随后 team_think_delta / team_tool_* / team_delta 持续追加
            writer.TryWrite(AgentEvent.TeamText(round, phase, eng.Id, eng.Name, "", trace));

            for (var step = 0; step < MaxToolSteps; step++)
            {
                var stepCalls = new List<LlmToolCall>();
                await foreach (var chunk in client.StreamAsync(new LlmRequest
                {
                    Messages = messages,
                    Stream = true,
                    // 思考模式：与普通聊天一致开启；推理增量以 team_think_delta 下发并持久化。
                    // 强度取 Medium：团队单轮多次调用（方案/建议/评估），xhigh 会显著放大延迟与成本
                    ThinkingEnabled = true,
                    EnableReasoning = true,
                    ThinkingEffort = LlmThinkingEffort.Medium,
                    MaxTokens = 16_000,
                    Tools = toolDefs,
                }, timeout.Token))
                {
                    switch (chunk)
                    {
                        case LlmChunk.ReasoningDelta rd:
                            if (ttft < 0) ttft = (int)sw.ElapsedMilliseconds;
                            thinking.Append(rd.Text);
                            FlushThinkDelta(thinking, ref thinkShown, round, phase, eng, trace, writer);
                            break;
                        case LlmChunk.TextDelta d:
                            if (ttft < 0) ttft = (int)sw.ElapsedMilliseconds;
                            finalText.Append(d.Text);
                            FlushTeamsDelta(finalText, ref finalShown, thinking, ref thinkShown, round, phase, eng, trace, writer);
                            break;
                        case LlmChunk.ToolUse tu:
                            stepCalls.Add(tu.Call);
                            break;
                        case LlmChunk.Done done:
                            usage ??= done.Usage;
                            model ??= done.Model;
                            if (finalText.Length == 0) finalText.Append(done.Content ?? "");
                            break;
                    }
                }

                if (stepCalls.Count > 0)
                {
                    // 工具决策步：执行工具并把结果回灌 messages，随后进入下一决策步
                    await RunToolStepAsync(eng, stepCalls, messages, toolCtx, userId, round, phase, trace, lang, writer, timeout.Token, toolTraces);
                    continue;
                }

                // 模型直接输出正文 → 本工程师调用完成
                break;
            }

            // 工具上限内模型始终未直接输出正文（持续调工具，常见于深度推理模型）：去掉工具定义收尾一次，
            // 强制模型基于已有工具结果直接给出最终回复 —— 避免“空方案”（此前直接判空失败）
            if (finalText.Length == 0)
            {
                messages.Add(LlmChatMessage.User(
                    "Tool-call budget exhausted. Stop calling tools now. Provide your final answer directly as plain text, based on the conversation and all tool results above."));
                await foreach (var chunk in client.StreamAsync(new LlmRequest
                {
                    Messages = messages,
                    Stream = true,
                    ThinkingEnabled = true,
                    EnableReasoning = true,
                    ThinkingEffort = LlmThinkingEffort.Medium,
                    MaxTokens = 16_000,
                    // Tools 不传：本步不允许再调工具
                }, timeout.Token))
                {
                    switch (chunk)
                    {
                        case LlmChunk.ReasoningDelta rd:
                            if (ttft < 0) ttft = (int)sw.ElapsedMilliseconds;
                            thinking.Append(rd.Text);
                            FlushThinkDelta(thinking, ref thinkShown, round, phase, eng, trace, writer);
                            break;
                        case LlmChunk.TextDelta d:
                            if (ttft < 0) ttft = (int)sw.ElapsedMilliseconds;
                            finalText.Append(d.Text);
                            FlushTeamsDelta(finalText, ref finalShown, thinking, ref thinkShown, round, phase, eng, trace, writer);
                            break;
                        case LlmChunk.Done done:
                            usage ??= done.Usage;
                            model ??= done.Model;
                            if (finalText.Length == 0) finalText.Append(done.Content ?? "");
                            break;
                    }
                }
            }

            FlushTeamsDelta(finalText, ref finalShown, thinking, ref thinkShown, round, phase, eng, trace, writer); // 尾部残余

            sw.Stop();
            var fullRaw = finalText.ToString();
            return new EngineerCall(fullRaw, CleanMarkers(fullRaw), thinking.ToString(), toolTraces,
                usage, model, (int)sw.ElapsedMilliseconds, ttft, null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            sw.Stop();
            _logger.LogWarning(ex, "Team engineer stream failed engineer={Engineer}", eng.Name);
            return new EngineerCall(null, null, null, toolTraces, usage, model, (int)sw.ElapsedMilliseconds, ttft, ex.Message);
        }
    }

    /// <summary>执行一批工具调用：team_tool_start → 执行 → team_tool_result → ToolResult 回灌 messages（串行，保持事件顺序清晰）</summary>
    private async Task RunToolStepAsync(TeamEngineer eng, IReadOnlyList<LlmToolCall> calls,
        List<LlmChatMessage> messages, TeamToolExecutor.TeamToolContext toolCtx, Guid userId,
        int round, string phase, string trace, string lang, ChannelWriter<AgentEvent> writer,
        CancellationToken ct, List<object> toolTraces)
    {
        foreach (var call in calls)
        {
            var tool = toolCtx.Tools.FirstOrDefault(t => string.Equals(t.Name, call.Name, StringComparison.OrdinalIgnoreCase));
            var argsJson = call.Arguments?.ToJsonString() ?? "{}";
            var callId = toolTraces.Count;

            if (tool is null)
            {
                var notFoundMsg = Texts.Get("TOOL_NOT_FOUND", lang, call.Name);
                writer.TryWrite(AgentEvent.TeamToolStart(round, phase, eng.Id, eng.Name, "unknown", call.Name, argsJson, trace, callId));
                writer.TryWrite(AgentEvent.TeamToolResult(round, phase, eng.Id, eng.Name, "unknown", call.Name, false, notFoundMsg, "TOOL_NOT_FOUND", 0, trace, callId));
                messages.Add(LlmChatMessage.ToolResult(call.Id, notFoundMsg));
                toolTraces.Add(new { server = "unknown", tool = call.Name, args = argsJson, success = false, preview = notFoundMsg, errorCode = "TOOL_NOT_FOUND", durationMs = 0 });
                continue;
            }

            writer.TryWrite(AgentEvent.TeamToolStart(round, phase, eng.Id, eng.Name, tool.ServerName, tool.Name, argsJson, trace, callId));
            McpToolResult result;
            try
            {
                result = await _tools.ExecuteAsync(tool, argsJson, userId, toolCtx.WsCtx, toolCtx.Servers, toolCtx.SkillsByName, trace, lang, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "团队工具执行异常 trace={Trace} tool={Server}.{Tool}", trace, tool.ServerName, tool.Name);
                result = new McpToolResult(false, "", Texts.Get("TOOL_EXECUTE_ERROR", lang), "TOOL_EXECUTE_ERROR", 0, 1);
            }

            var preview = Truncate(result.ResultText, ToolPreviewMaxChars);
            writer.TryWrite(AgentEvent.TeamToolResult(round, phase, eng.Id, eng.Name, tool.ServerName, tool.Name,
                result.Success, preview, result.ErrorCode, result.DurationMs, trace, callId));
            var resultText = result.Success ? result.ResultText : (result.ErrorMessage ?? Texts.Get("TOOL_EXECUTE_ERROR", lang));
            messages.Add(LlmChatMessage.ToolResult(call.Id, Truncate(resultText, ToolResultMaxChars)));
            toolTraces.Add(new { server = tool.ServerName, tool = tool.Name, args = argsJson,
                success = result.Success, preview, errorCode = result.ErrorCode, durationMs = result.DurationMs });
        }
    }

    /// <summary>schema JSON 字符串 → JsonObject（工具定义入 LLM 请求）</summary>
    private static JsonObject? ToSchema(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson)) return null;
        try
        {
            return JsonNode.Parse(schemaJson) as JsonObject;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>把 StringBuilder 中尚未下发的文本/思考增量作为事件写出（攒够阈值再发，避免每 token 一帧）；
    /// 正文增量前先冲净未发完的思考残余，保证事件顺序为 思考→正文</summary>
    private static void FlushTeamsDelta(StringBuilder raw, ref int shown, StringBuilder thinking, ref int thinkShown,
        int round, string phase, TeamEngineer eng, string trace, ChannelWriter<AgentEvent> writer)
    {
        FlushThinkDelta(thinking, ref thinkShown, round, phase, eng, trace, writer);
        if (raw.Length - shown < 28) return;
        var clean = CleanMarkers(raw.ToString());
        if (clean.Length > shown)
        {
            writer.TryWrite(AgentEvent.TeamDelta(round, phase, eng.Id, eng.Name, clean[shown..], trace));
            shown = clean.Length;
        }
    }

    /// <summary>思考增量（原始内容，不做任何清洗/剥离）</summary>
    private static void FlushThinkDelta(StringBuilder sb, ref int shown, int round, string phase,
        TeamEngineer eng, string trace, ChannelWriter<AgentEvent> writer)
    {
        if (sb.Length - shown < 28) return;
        var text = sb.ToString();
        writer.TryWrite(AgentEvent.TeamThinkDelta(round, phase, eng.Id, eng.Name, text[shown..], trace));
        shown = text.Length;
    }

    /// <summary>剥离控制标记（[FINAL]/[CONSENSUS]，含小写变体）：发往前端与持久化的内容均不带标记</summary>
    private static string CleanMarkers(string text)
    {
        foreach (var marker in new[] { "[FINAL]", "[CONSENSUS]", "[final]", "[consensus]" })
            text = text.Replace(marker, "", StringComparison.Ordinal);
        return text.TrimStart(':', ' ', '\n', '\r', '-', '—').Trim();
    }

    // ================= Prompt 构建（隔离语义内嵌） =================

    private static string BuildResponsibleSolutionPrompt(string name, int round, int maxRounds, string task) => $$"""
        You are "{{name}}", the RESPONSIBLE engineer in a multi-engineer team review (round {{round}}/{{maxRounds}}).
        You own the task and are responsible for the final result.

        User task:
        {{task}}

        Produce the best candidate solution for the task. Output the solution directly, no preamble.
        """;

    private static string BuildAssistantSuggestionPrompt(string name, int round, int maxRounds, string task, string solution, string ownHistory)
    {
        var ownBlock = ownHistory.Length > 0
            ? "Your own previous suggestions:\n" + Truncate(ownHistory, ContextChunkChars)
            : "";
        return $$"""
        You are "{{name}}", an ASSISTANT engineer in a multi-engineer team review (round {{round}}/{{maxRounds}}).

        User task:
        {{task}}

        Current solution from the responsible engineer:
        {{Truncate(solution, ContextChunkChars)}}

        {{ownBlock}}

        Review the CURRENT solution and give concrete, actionable suggestions (issues, risks, improvements).
        Work INDEPENDENTLY: you cannot see other assistant engineers' suggestions this round, and you must not speculate about their opinions.
        Output your suggestions directly, no preamble.
        """;
    }

    private static string BuildEvalPrompt(string name, int round, int maxRounds, string task, string solution, string suggestionsBlock) => $$"""
        You are "{{name}}", the RESPONSIBLE engineer in a multi-engineer team review (round {{round}}/{{maxRounds}}).

        User task:
        {{task}}

        Current solution:
        {{Truncate(solution, ContextChunkChars)}}

        Suggestions from assistant engineers this round:
        {{Truncate(suggestionsBlock, ContextChunkChars * 3)}}

        For each suggestion, accept or reject it with a brief reason, then produce the improved solution / next-iteration plan.
        If you judge that there is no meaningful remaining dispute (no further change needed), say so explicitly (e.g. "no remaining dispute") and end your answer with the marker [CONSENSUS]; do not list further changes.
        """;

    private static string BuildFinalPrompt(string name, int round, int maxRounds, string task, string solution, string suggestionsBlock) => $$"""
        You are "{{name}}", the RESPONSIBLE engineer in a multi-engineer team review. This is the FINAL round ({{round}}/{{maxRounds}}).

        User task:
        {{task}}

        Your current solution:
        {{Truncate(solution, ContextChunkChars)}}

        Suggestions from assistant engineers:
        {{Truncate(suggestionsBlock, ContextChunkChars * 3)}}

        Review all suggestions and produce the definitive FINAL answer to the user's task.
        Begin your answer with the marker [FINAL].
        """;

    /// <summary>
    /// 无争议检测（“无争议提前结束”）：优先 [CONSENSUS] 显式标记；再对输出末尾做自然语言共识兜底
    /// （仅检查末尾区域——评估结论区，避免“部分同意但仍有异议”的中间表述误判）。
    /// </summary>
    private static bool ContainsConsensus(string? text)
    {
        if (string.IsNullOrEmpty(text)) return false;
        if (text.Contains("[CONSENSUS]", StringComparison.OrdinalIgnoreCase)) return true;

        var tail = text.Length > 500 ? text[^500..] : text;
        if (tail.Contains("no remaining dispute", StringComparison.OrdinalIgnoreCase)
            || tail.Contains("no further change needed", StringComparison.OrdinalIgnoreCase)
            || tail.Contains("no remaining disagreement", StringComparison.OrdinalIgnoreCase)
            || tail.Contains("no further disagreement", StringComparison.OrdinalIgnoreCase)
            || tail.Contains("all suggestions have been incorporated", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        // 中文：结论性短语；含“有异议/仍有分歧”等否定时不算达成一致
        if (tail.Contains("达成一致", StringComparison.OrdinalIgnoreCase)
            || tail.Contains("已无分歧", StringComparison.OrdinalIgnoreCase)
            || tail.Contains("无进一步分歧", StringComparison.OrdinalIgnoreCase)
            || tail.Contains("无需再修改", StringComparison.OrdinalIgnoreCase)
            || tail.Contains("已无修改", StringComparison.OrdinalIgnoreCase)
            || (tail.Contains("无异议", StringComparison.OrdinalIgnoreCase) && !tail.Contains("有异议", StringComparison.OrdinalIgnoreCase)))
        {
            return true;
        }
        return false;
    }

    private static string Truncate(string? s, int max)
    {
        if (string.IsNullOrEmpty(s)) return "";
        return s.Length <= max ? s : s[..max] + "\n…(截断)";
    }

    // ================= 用量/费用 =================

    /// <summary>聚合一次工程师调用的用量：token 累加；TTFT 取真实首 token 时延（无则回退整段耗时）；总耗时累加</summary>
    private static void AddUsed(EngineerCall call,
        ref LlmUsage total, ref string? modelName, ref int totalTtft, ref int totalMs)
    {
        var usage = call.Usage;
        if (usage is not null)
        {
            total = new LlmUsage(total.PromptTokens + usage.PromptTokens,
                total.CompletionTokens + usage.CompletionTokens,
                total.ReasoningTokens + usage.ReasoningTokens,
                total.CacheTokens + usage.CacheTokens);
        }
        if (!string.IsNullOrWhiteSpace(call.Model)) modelName ??= call.Model;
        totalTtft += call.TtftMs >= 0 ? call.TtftMs : call.Ms;
        totalMs += call.Ms;
    }

    private async Task<decimal> EstimateCostAsync(Guid modelId, LlmUsage usage,
        Dictionary<Guid, (decimal In, decimal Out)> prices, CancellationToken ct)
    {
        if (!prices.TryGetValue(modelId, out var p))
        {
            var m = await _admin.GetModelAsync(modelId, ct);
            p = m is null || (m.PriceInPer1K <= 0 && m.PriceOutPer1K <= 0)
                ? (0.001m, 0.002m)
                : (m.PriceInPer1K, m.PriceOutPer1K);
            prices[modelId] = p;
        }
        // 推理 token 按输出单价计费（与普通聊天同样的“输出”口径；思考开启后占大头，必须计入）
        return (usage.PromptTokens * p.In + (usage.CompletionTokens + usage.ReasoningTokens) * p.Out) / 1000m;
    }

    private static string IdempotencyKey(Guid userId, string clientMessageId) => $"team:{userId:N}:{clientMessageId}";

    private static (int MaxRounds, bool Parallel, bool StopOnConsensus, int MaxParallel) ParseConfig(string? json)
    {
        int maxRounds = DefaultMaxRounds; bool parallel = true; bool stopOnConsensus = true; int maxParallel = DefaultMaxParallel;
        if (!string.IsNullOrWhiteSpace(json))
        {
            try
            {
                using var doc = JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("maxRounds", out var r) && r.TryGetInt32(out var ri)) maxRounds = Math.Clamp(ri, 1, MaxRoundsLimit);
                if (doc.RootElement.TryGetProperty("parallel", out var p)) parallel = p.GetBoolean();
                if (doc.RootElement.TryGetProperty("stopOnConsensus", out var s)) stopOnConsensus = s.GetBoolean();
                if (doc.RootElement.TryGetProperty("maxParallel", out var m) && m.TryGetInt32(out var mi)) maxParallel = Math.Clamp(mi, 1, 8);
            }
            catch (JsonException) { /* 破损配置 → 默认值 */ }
        }
        return (maxRounds, parallel, stopOnConsensus, maxParallel);
    }

    private static TeamEngineerDto ToDto(TeamEngineer e) => new(e.Id, e.Name, (int)e.Role, e.ProviderId, e.ModelId, e.DisplayOrder, e.Enabled);
}
