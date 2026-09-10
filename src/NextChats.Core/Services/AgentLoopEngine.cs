using System.Diagnostics;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NextChats.Core.Abstractions;
using NextChats.Core.Agents;
using NextChats.Core.Clients;
using NextChats.Core.Configuration;
using NextChats.Core.Domain;
using NextChats.Core.Localization;

namespace NextChats.Core.Services;

/// <summary>
/// Agent Loop（ReAct 循环引擎）：
///  思考（LLM 推理 + reasoning 流，Channel 生产/消费保证事件实时下发）
///  → 行动（策略评估 → 审批 → 工具执行，错误进循环 + 重试）
///  → 观察（工具结果回灌）→ 重复直到收敛。
/// </summary>
public sealed class AgentLoopEngine : IAgentLoopEngine
{
    private readonly ILlmRouter _router;
    private readonly IPolicyEngine _policy;
    private readonly IApprovalCoordinator _approvals;
    private readonly IContextManager _context;
    private readonly IOptions<PolicyOptions> _policyOptions;
    private readonly ILogger _logger;

    private sealed class RoundOutcome
    {
        public string Text = "";
        public string Reasoning = "";
        public readonly List<LlmToolCall> ToolCalls = [];
        public LlmUsage? Usage;
        public LlmFinishReason Finish;
        public string? Model;
        public int TtftMs = -1;
        public bool Interrupted;
        public bool LlmUnavailable;
        public bool Failed;
    }

    public AgentLoopEngine(
        ILlmRouter router,
        IPolicyEngine policy,
        IApprovalCoordinator approvals,
        IContextManager context,
        IOptions<PolicyOptions> policyOptions,
        ILogger<AgentLoopEngine> logger)
    {
        _router = router;
        _policy = policy;
        _approvals = approvals;
        _context = context;
        _policyOptions = policyOptions;
        _logger = logger;
    }

    public async IAsyncEnumerable<AgentEvent> RunAsync(AgentRunRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var trace = request.TraceId;
        var lang = request.Lang ?? "en";
        var messages = new List<LlmChatMessage>(request.InitialMessages);
        var toolDefs = request.Tools?.Select(t => new LlmToolDef(t.Name, t.Description, ToSchema(t.SchemaJson))).ToList();
        _logger.LogInformation("[AgentLoop] start trace={Trace} tools={Tools} maxSteps={Max}", trace, toolDefs?.Count ?? 0, request.MaxSteps > 0 ? request.MaxSteps : _policyOptions.Value.MaxReActSteps);

        var usage = new JsonUsage();
        var ttftMs = -1;
        var swAll = Stopwatch.StartNew();
        var interrupted = false;

        for (var round = 1; round <= (request.MaxSteps > 0 ? request.MaxSteps : _policyOptions.Value.MaxReActSteps); round++)
        {
            usage.Rounds = round;
            yield return AgentEvent.RoundStart(round, trace);

            // ---- 上下文保障：先压缩后截断，确保不超出长度限制 ----
            if (_context.NeedsCompression(messages, request.ContextWindow))
            {
                var before = messages.Count;
                try
                {
                    messages = (await _context.CompressAsync(messages, request.ContextWindow, toolDefs, ct)).ToList();
                }
                catch (OperationCanceledException)
                {
                    interrupted = true;
                    break;
                }
                yield return AgentEvent.ContextEvent("compress",
                    Texts.Get("CONTEXT_COMPRESSED", lang, before, messages.Count), trace);
            }

            // ---- 思考：LLM 流式（Channel：生产者流式写入事件，消费者实时转发） ----
            var outcome = new RoundOutcome();
            var channel = Channel.CreateUnbounded<AgentEvent>(new UnboundedChannelOptions
            {
                SingleReader = true,
                SingleWriter = true,
            });
            var producer = Task.Run(async () => await ProduceThinkAsync(request, messages, toolDefs, trace, lang, outcome, channel.Writer, ct), ct);

            try
            {
                // 读取不携带 ct：取消时由生产者写入 INTERRUPTED 事件并完成通道，
                // 保证中断也有事件可收、消息可持久化为 Stopped。
                await foreach (var ev in channel.Reader.ReadAllAsync(CancellationToken.None))
                {
                    yield return ev;
                }
            }
            finally
            {
                await SafeAwait(producer);
            }

            if (outcome.TtftMs > 0 && ttftMs < 0) ttftMs = outcome.TtftMs;
            if (outcome.Usage is not null)
            {
                usage.PromptTokens += outcome.Usage.PromptTokens;
                usage.CompletionTokens += outcome.Usage.CompletionTokens;
                usage.TotalTokens += outcome.Usage.TotalTokens;
            }

            if (interrupted || outcome.Interrupted)
            {
                interrupted = true;
                break;
            }
            if (outcome.LlmUnavailable)
            {
                throw new OperationCanceledException(); // LLM_UNAVAILABLE 事件已下发，终止事件流
            }
            if (outcome.Failed)
            {
                break; // LLM_ERROR 事件已下发；会话继续可用
            }

            // ---- 观察：处理工具调用 ----
            if (outcome.Finish == LlmFinishReason.ToolCalls && outcome.ToolCalls.Count > 0)
            {
                messages.Add(LlmChatMessage.Assistant(outcome.Text, outcome.ToolCalls, outcome.Reasoning));
                foreach (var call in outcome.ToolCalls)
                {
                    if (ct.IsCancellationRequested)
                    {
                        interrupted = true;
                        break;
                    }

                    usage.ToolCalls++;
                    var tool = request.Tools?.FirstOrDefault(t => t.Name == call.Name);
                    var argsJson = call.Arguments?.ToJsonString() ?? "{}";

                    if (tool is null)
                    {
                        usage.ToolErrors++;
                        var unknownMsg = Texts.Get("TOOL_NOT_FOUND", lang, call.Name);
                        messages.Add(LlmChatMessage.ToolResult(call.Id, unknownMsg));
                        yield return AgentEvent.ToolResult("unknown", call.Name, false, null, "TOOL_NOT_FOUND", 0, trace);
                        continue;
                    }

                    var verdict = _policy.Evaluate(tool.ServerName, tool.Name, argsJson);
                    string toolResult;
                    var execOk = true; // 拒绝/超时等策略消息视为非执行失败；只有真实工具执行结果才翻转
                    var swTool = Stopwatch.StartNew();

                    if (verdict == PolicyVerdict.Deny)
                    {
                        usage.ToolErrors++;
                        toolResult = Texts.Get("OP_DENIED", lang);
                        messages.Add(LlmChatMessage.ToolResult(call.Id, toolResult));
                        swTool.Stop();
                        yield return AgentEvent.ToolResult(tool.ServerName, tool.Name, false, toolResult, "OP_DENIED", (int)swTool.ElapsedMilliseconds, trace);
                        continue;
                    }

                    if (verdict == PolicyVerdict.RequireApproval)
                    {
                        usage.Approvals++;
                        var approval = await _approvals.CreateAsync(request.UserId, request.SessionId, trace,
                            tool.ServerName, tool.Name, call.Arguments,
                            TimeSpan.FromSeconds(_policyOptions.Value.ApprovalTimeoutSeconds), ct);
                        yield return AgentEvent.ToolStart(tool.ServerName, tool.Name, argsJson, true, approval.Id, trace);
                        yield return AgentEvent.ApprovalUpdated(approval.Id, "pending", trace);

                        var decision = await _approvals.WaitForDecisionAsync(approval.Id,
                            TimeSpan.FromSeconds(_policyOptions.Value.ApprovalTimeoutSeconds), ct);

                        if (ct.IsCancellationRequested)
                        {
                            interrupted = true;
                            break;
                        }

                        if (decision is null)
                        {
                            yield return AgentEvent.ApprovalUpdated(approval.Id, "expired", trace);
                            toolResult = Texts.Get("APPROVAL_TIMEOUT_TOOL", lang);
                        }
                        else if (decision == ApprovalDecision.Rejected)
                        {
                            yield return AgentEvent.ApprovalUpdated(approval.Id, "rejected", trace);
                            toolResult = Texts.Get("APPROVAL_REJECTED_TOOL", lang);
                        }
                        else
                        {
                            yield return AgentEvent.ApprovalUpdated(approval.Id, "approved", trace);
                            (execOk, toolResult) = await ExecuteToolWithRetryAsync(request, tool, argsJson, trace, lang, ct, usage);
                        }
                    }
                    else
                    {
                        yield return AgentEvent.ToolStart(tool.ServerName, tool.Name, argsJson, false, null, trace);
                        (execOk, toolResult) = await ExecuteToolWithRetryAsync(request, tool, argsJson, trace, lang, ct, usage);
                    }

                    swTool.Stop();
                    messages.Add(LlmChatMessage.ToolResult(call.Id, toolResult));
                    yield return AgentEvent.ToolResult(tool.ServerName, tool.Name,
                        execOk, Truncate(toolResult, 800), null, (int)swTool.ElapsedMilliseconds, trace);
                }

                if (interrupted) break;
                continue; // 进入下一轮 ReAct
            }

            // ---- 模型直接输出正文 ----
            if (outcome.Reasoning.Length > 0)
            {
                yield return AgentEvent.ThinkingEnd(null, trace);
            }
            // 输出被 token 上限截断：明确提示，不静默当作“正常完成”（否则用户会看到“没推理完”却已结束）
            if (outcome.Finish == LlmFinishReason.Length)
            {
                yield return AgentEvent.ContextEvent("truncated", Texts.Get("LLM_LENGTH_TRUNCATED", lang), trace);
            }
            break;
        }

        swAll.Stop();

        // ---- 轮次触顶兜底：最后仍在调用工具（无正文答案）→ 明确提示，而不是“静默断开” ----
        if (!interrupted && messages.Count > 0 && messages[^1].Role == "tool")
        {
            var maxRounds = request.MaxSteps > 0 ? request.MaxSteps : _policyOptions.Value.MaxReActSteps;
            yield return AgentEvent.ContextEvent("max_steps",
                Texts.Get("AGENT_MAX_STEPS", lang, maxRounds), trace);
        }

        if (interrupted)
        {
            yield return AgentEvent.Error("INTERRUPTED", Texts.Get("INTERRUPTED", lang), trace);
        }

        yield return AgentEvent.Done(usage, 0m, ttftMs, (int)swAll.ElapsedMilliseconds, trace);
    }

    /// <summary>思考阶段生产者：LLM 流式事件写入 Channel（内部捕获异常 → 事件化，不中断会话）</summary>
    private async Task ProduceThinkAsync(
        AgentRunRequest request,
        IReadOnlyList<LlmChatMessage> messages,
        IReadOnlyList<LlmToolDef>? toolDefs,
        string trace,
        string lang,
        RoundOutcome outcome,
        ChannelWriter<AgentEvent> writer,
        CancellationToken ct)
    {
        var reasoningStarted = false;
        try
        {
            ILlmClient client;
            try
            {
                client = await _router.SelectClientAsync(request.PreferredProviderId, request.PreferredModelId, lang, ct, request.AllowedModelIds?.ToArray());
                await NotifySelectionFallbackAsync(request, client, lang, trace, writer, ct);
            }
            catch (LlmUnavailableException ex)
            {
                outcome.LlmUnavailable = true;
                _logger.LogError("LLM 不可用 trace={Trace}: {Msg}", trace, ex.Message);
                await writer.WriteAsync(AgentEvent.Error("LLM_UNAVAILABLE", Texts.Get("LLM_UNAVAILABLE", lang), trace), ct);
                return;
            }

            outcome.Model = client.Model;
            var sw = Stopwatch.StartNew();
            var llmRequest = new LlmRequest
            {
                Messages = messages,
                Tools = toolDefs,
                Stream = true,
                EnableReasoning = true,
                Model = request.ModelOverride,
                ThinkingEnabled = request.ThinkingEnabled,
                ThinkingEffort = request.ThinkingEffort,
            };

            // 首块等待提示：上游排队/慢时给出可见反馈（避免用户误以为“没在推理/需要切换窗口才继续”）
            var waitingLock = new object();
            var waitingSent = false;
            using var waitTimer = new System.Threading.Timer(_ =>
            {
                lock (waitingLock)
                {
                    if (waitingSent) return;
                    waitingSent = true;
                }
                writer.TryWrite(AgentEvent.ContextEvent("waiting", Texts.Get("LLM_WAITING", lang), trace));
            }, null, TimeSpan.FromSeconds(8), System.Threading.Timeout.InfiniteTimeSpan);

            try
            {
                await foreach (var chunk in client.StreamAsync(llmRequest, ct))
                {
                    // 首个 chunk（任何类型）到达 → 上游已响应，停止等待提示
                    waitTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
                    switch (chunk)
                    {
                    case LlmChunk.TextDelta td:
                        if (outcome.TtftMs < 0) outcome.TtftMs = (int)sw.ElapsedMilliseconds;
                        outcome.Text += td.Text;
                        await writer.WriteAsync(new AgentEvent { Kind = "text_delta", Text = td.Text, TraceId = trace }, ct);
                        break;

                    case LlmChunk.ReasoningDelta rd:
                        if (outcome.TtftMs < 0) outcome.TtftMs = (int)sw.ElapsedMilliseconds;
                        if (!reasoningStarted)
                        {
                            reasoningStarted = true;
                            await writer.WriteAsync(AgentEvent.ThinkingStart(trace), ct);
                        }
                        outcome.Reasoning += rd.Text;
                        await writer.WriteAsync(AgentEvent.ThinkingDelta(rd.Text, trace), ct);
                        break;

                    case LlmChunk.ToolUse tu:
                        outcome.ToolCalls.Add(tu.Call);
                        break;

                    case LlmChunk.Done done:
                        outcome.Usage = done.Usage;
                        outcome.Finish = done.FinishReason;
                        if (string.IsNullOrEmpty(outcome.Text)) outcome.Text = done.Content ?? "";
                        if (string.IsNullOrEmpty(outcome.Reasoning)) outcome.Reasoning = done.Reasoning ?? "";
                        break;
                }
            }
        }
            finally
            {
                waitTimer.Change(System.Threading.Timeout.Infinite, System.Threading.Timeout.Infinite);
            }
        }
        catch (OperationCanceledException)
        {
            outcome.Interrupted = true;
            _logger.LogWarning("Agent 思考被中断 trace={Trace}", trace);
            try
            {
                await writer.WriteAsync(AgentEvent.Error("INTERRUPTED", Texts.Get("INTERRUPTED", lang), trace), CancellationToken.None);
            }
            catch (Exception)
            {
                // 写入失败忽略
            }
        }
        catch (LlmUnavailableException ex)
        {
            outcome.LlmUnavailable = true;
            _logger.LogError(ex, "LLM 不可用 trace={Trace}", trace);
            try
            {
                await writer.WriteAsync(AgentEvent.Error("LLM_UNAVAILABLE", Texts.Get("LLM_UNAVAILABLE", lang), trace), CancellationToken.None);
            }
            catch (Exception)
            {
                // ignore
            }
        }
        catch (Exception ex)
        {
            outcome.Failed = true;
            _logger.LogError(ex, "LLM 调用异常 trace={Trace}", trace);
            try
            {
                // HTTP 状态类错误把状态码透传给用户（如网关 500：已自动重试仍失败），诊断友好
                var message = ex is NextChats.Core.Clients.LlmHttpException httpEx
                    ? Texts.Get("LLM_ERROR_HTTP", lang, httpEx.StatusCode)
                    : Texts.Get("LLM_ERROR", lang);
                await writer.WriteAsync(AgentEvent.Error("LLM_ERROR", message, trace), CancellationToken.None);
            }
            catch (Exception)
            {
                // ignore
            }
        }
        finally
        {
            // 通道必须收尾，否则消费者会无限等待 → 流永不结束
            writer.TryComplete();
        }
    }

    /// <summary>
    /// 用户选择了供应商/模型，但运行时被降级（不可用/熔断/模型被替换）时，
    /// 向事件流发 context 提示，让“实际用的是什么”对用户透明。
    /// </summary>
    private async Task NotifySelectionFallbackAsync(
        AgentRunRequest request, ILlmClient client, string lang, string trace,
        ChannelWriter<AgentEvent> writer, CancellationToken ct)
    {
        if (!request.PreferredProviderId.HasValue || request.PreferredProviderId.Value == Guid.Empty) return;
        try
        {
            var actual = await _router.SelectAsync(request.PreferredProviderId.Value, ct);
            if (actual is null || actual.Id != request.PreferredProviderId.Value)
            {
                await writer.WriteAsync(AgentEvent.ContextEvent("fallback",
                    Texts.Get("LLM_FALLBACK", lang, actual?.Name ?? "?", client.Model), trace), ct);
                return;
            }
            if (request.PreferredModelId.HasValue && request.PreferredModelId.Value != Guid.Empty)
            {
                var model = actual.Models.FirstOrDefault(m => m.Id == request.PreferredModelId.Value);
                if (model is null || !string.Equals(model.Name.Trim(), client.Model.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    await writer.WriteAsync(AgentEvent.ContextEvent("fallback",
                        Texts.Get("LLM_MODEL_FALLBACK", lang, actual.Name, client.Model), trace), ct);
                }
            }
        }
        catch (Exception)
        {
            // 提示失败不致命：跳过
        }
    }

    /// <summary>工具执行失败前缀（固定符号，非本地化文案；外部工具结果不以该前缀标记失败）</summary>
    private const string ErrorMarker = "❌";

    /// <summary>执行工具：错误进循环 + 重试策略（退避），最终失败以 tool result 回灌给模型</summary>
    private async Task<(bool Success, string Text)> ExecuteToolWithRetryAsync(AgentRunRequest request, UnifiedTool tool, string argsJson, string trace, string lang, CancellationToken ct, JsonUsage usage)
    {
        var maxAttempts = 1 + Math.Max(0, _policyOptions.Value.MaxToolRetries);
        string lastError = Texts.Get("TOOL_EXECUTE_FAILED", lang);
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            try
            {
                var result = await request.ToolExecutor(tool, argsJson, trace, ct);
                if (result.Success)
                {
                    return (true, result.ResultText);
                }
                lastError = result.ErrorMessage ?? lastError;
                // 瞬时错误（连接/超时）才重试；业务错误（MCP_TOOL_ERROR 等）直接回灌
                if (!result.Retryable || attempt >= maxAttempts)
                {
                    usage.ToolErrors++;
                    return (false, $"{ErrorMarker} {result.ErrorCode ?? "TOOL_ERROR"}: {lastError}");
                }
                usage.ToolErrors++;
                await Task.Delay(_policyOptions.Value.ToolRetryDelayMs * attempt, ct);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "工具执行异常 trace={Trace} tool={Server}.{Tool} attempt={Attempt}", trace, tool.ServerName, tool.Name, attempt);
                lastError = Texts.Get("TOOL_EXECUTE_ERROR", lang);
                if (attempt >= maxAttempts) break;
                await Task.Delay(_policyOptions.Value.ToolRetryDelayMs * attempt, ct);
            }
        }
        usage.ToolErrors++;
        return (false, $"{ErrorMarker} {lastError}");
    }

    private static JsonObject? ToSchema(string? schemaJson)
    {
        if (string.IsNullOrWhiteSpace(schemaJson)) return null;
        try
        {
            return JsonNode.Parse(schemaJson) as JsonObject;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static async Task SafeAwait(Task task)
    {
        try
        {
            await task;
        }
        catch (Exception)
        {
            // 生产者异常已事件化，忽略
        }
    }
}
