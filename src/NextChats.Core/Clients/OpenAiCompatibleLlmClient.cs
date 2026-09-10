using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;
using NextChats.Core.Entities;
using NextChats.Core.Services;

namespace NextChats.Core.Clients;

/// <summary>
/// OpenAI 兼容 Chat Completions 客户端（支持流式 + 工具调用 + reasoning_content + usage）。
/// 鉴权：每次请求携带 Bearer API Key（按供应商解密，不入共享 HttpClient 默认头，避免串供应商）。
/// 思考模式：由聊天全局开关/强度（LlmRequest）＋ 供应商思考参数模式（ThinkingParam）统一驱动；
/// 模型级思考力度配置已废弃（管理页不再暴露）。
/// </summary>
public sealed class OpenAiCompatibleLlmClient : ILlmClient
{
    private readonly LlmProvider _provider;
    private readonly string _model;
    private readonly HttpClient _http;
    private readonly ISecurityService _security;
    private readonly ILogger _logger;

    public OpenAiCompatibleLlmClient(LlmProvider provider, string model, HttpClient http, ISecurityService security, ILogger logger)
    {
        _provider = provider;
        _model = model;
        _http = http;
        _security = security;
        _logger = logger;
    }

    public string ProviderName => _provider.Name;

    public string Model => _model;

    /// <summary>附加 Bearer 鉴权（仅当配置了密钥；解密后 Trim 防御粘贴空白）</summary>
    private void ApplyAuth(HttpRequestMessage req)
    {
        if (string.IsNullOrWhiteSpace(_provider.ApiKeyEncrypted)) return;
        var key = _security.DecryptSecret(_provider.ApiKeyEncrypted)?.Trim();
        if (!string.IsNullOrWhiteSpace(key))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
        }
    }

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        using var completeCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        completeCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _provider.TimeoutSeconds)));

        for (var attempt = 1; ; attempt++)
        {
            using var payload = BuildPayload(request, stream: false);
            using var req = new HttpRequestMessage(HttpMethod.Post, BuildChatUrl()) { Content = payload };
            ApplyAuth(req);
            using var resp = await _http.SendAsync(req, completeCts.Token);
            var body = await resp.Content.ReadAsStringAsync(completeCts.Token);
            if (!resp.IsSuccessStatusCode)
            {
                var code = (int)resp.StatusCode;
                // 上游瞬时故障（5xx / 408 / 429）自动重试：总尝试 4 次（初试 + 3 次重试），指数退避 500/1000/2000ms
                var retryable = code == 408 || code == 429 || code >= 500;
                if (attempt < 4 && retryable && !ct.IsCancellationRequested)
                {
                    completeCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _provider.TimeoutSeconds)));
                    var delayMs = 500 * (1 << (attempt - 1));
                    _logger.LogWarning("LLM 上游瞬时故障 HTTP {Code}（{Provider}/{Model}），第 {Attempt} 次重试（{Delay}ms）：{Body}",
                        code, _provider.Name, _model, attempt, delayMs, MaskBody(body));
                    await Task.Delay(delayMs, ct);
                    continue;
                }
                throw new LlmHttpException(code, MaskBody(body));
            }

            using var doc = JsonDocument.Parse(body);
            var root = doc.RootElement;
            var choice = root.GetProperty("choices")[0];
            var msg = choice.TryGetProperty("message", out var m) ? m : default;
            var content = msg.ValueKind == JsonValueKind.Object && msg.TryGetProperty("content", out var c) && c.ValueKind == JsonValueKind.String ? c.GetString() : null;
            var reasoning = msg.ValueKind == JsonValueKind.Object ? GetStringProp(msg, "reasoning_content", "reasoning") : null;
            var toolCalls = ParseToolCalls(msg);

            var usage = ParseUsage(root, request);
            var finish = choice.TryGetProperty("finish_reason", out var fr) ? fr.GetString() : null;
            sw.Stop();

            return new LlmResult(
                LlmChatMessage.Assistant(content, toolCalls, reasoning),
                usage,
                finish == "tool_calls" ? LlmFinishReason.ToolCalls : finish == "length" ? LlmFinishReason.Length : LlmFinishReason.Stop,
                _model,
                (int)sw.ElapsedMilliseconds,
                (int)sw.ElapsedMilliseconds);
        }
    }

    /// <summary>按候选字段名顺序读取字符串属性（兼容 reasoning_content / reasoning 等网关差异），无则返回 null。</summary>
    private static string? GetStringProp(JsonElement obj, params string[] names)
    {
        foreach (var name in names)
        {
            if (obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String)
            {
                var s = v.GetString();
                if (!string.IsNullOrEmpty(s)) return s;
            }
        }
        return null;
    }

    public async IAsyncEnumerable<LlmChunk> StreamAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        // 首 token 等待上限 = 供应商 TimeoutSeconds（默认 120s）；首个内容块到达后计时作废。
        // 不设“总时长”限制：长思考 + 长流式输出可能持续数分钟，中途掐断会导致推理中断。
        using var firstTokenCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var attempts = 0;

        while (true)
        {
            attempts++;
            // 每次（重）请求重新开始首 token 计时
            firstTokenCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _provider.TimeoutSeconds)));

            using var payload = BuildPayload(request, stream: true);
            using var req = new HttpRequestMessage(HttpMethod.Post, BuildChatUrl()) { Content = payload };
            ApplyAuth(req);
            HttpResponseMessage resp;
            try
            {
                resp = await _http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, firstTokenCts.Token);
            }
            catch (OperationCanceledException) when (firstTokenCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new LlmHttpException(408, $"first token timeout after {Math.Max(5, _provider.TimeoutSeconds)}s");
            }
            using (resp)
            {
                if (!resp.IsSuccessStatusCode)
                {
                    var errBody = await resp.Content.ReadAsStringAsync(ct);
                    var code = (int)resp.StatusCode;
                    var msg = $"HTTP {code}: {MaskBody(errBody)}";
                    // 上游瞬时故障（5xx / 408 / 429）自动重试：总尝试 4 次（初试 + 3 次重试），指数退避 500/1000/2000ms。
                    // 仅“首块未产出”前重试（进入 ReadChunksAsync 后不再重试）；网关 500（upstream error）多为瞬时，重试可自愈。
                    var retryable = code == 408 || code == 429 || code >= 500;
                    if (attempts < 4 && retryable && !ct.IsCancellationRequested)
                    {
                        var delayMs = 500 * (1 << (attempts - 1));
                        _logger.LogWarning("LLM 上游瞬时故障 HTTP {Code}（{Provider}/{Model}），第 {Attempt} 次重试（{Delay}ms）：{Body}",
                            code, _provider.Name, _model, attempts, delayMs, MaskBody(errBody));
                        await Task.Delay(delayMs, ct);
                        continue;
                    }
                    throw new LlmHttpException(code, msg);
                }
                await foreach (var chunk in ReadChunksAsync(resp, request, ct)) yield return chunk;
            }
            yield break;
        }
    }

    /// <summary>读取并解析 SSE 响应主体。仅“首块未产出”之前允许上层重试；进入此处后不再重试。</summary>
    private async IAsyncEnumerable<LlmChunk> ReadChunksAsync(
        HttpResponseMessage resp,
        LlmRequest request,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        using var firstChunkCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        firstChunkCts.CancelAfter(TimeSpan.FromSeconds(Math.Max(5, _provider.TimeoutSeconds)));
        var firstChunkSeen = false;

        var sw = Stopwatch.StartNew();
        var ttft = -1;
        var textSb = new StringBuilder();
        var reasoningSb = new StringBuilder();
        var toolAggregators = new Dictionary<int, (string Name, StringBuilder Args)>();
        LlmUsage? usage = null;
        var finish = "stop";

        await using var stream = await resp.Content.ReadAsStreamAsync(ct);
        await foreach (var data in SseParser.ReadDataAsync(stream, ct))
        {
            // 首块迟迟不来（上游吞掉请求不往下发）→ 按首 token 超时处理（非 catch 版，兼容 yield）
            if (!firstChunkSeen && firstChunkCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                throw new LlmHttpException(408, $"first chunk timeout after {Math.Max(5, _provider.TimeoutSeconds)}s");
            }
            using var doc = SseParser.Parse(data);
            if (doc is null) continue;
            var root = doc.RootElement;

            if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
            {
                usage = new LlmUsage(GetInt(u, "prompt_tokens"), GetInt(u, "completion_tokens"));
            }

            if (!root.TryGetProperty("choices", out var choices) || choices.GetArrayLength() == 0)
            {
                if (root.TryGetProperty("error", out var err))
                {
                    throw new LlmHttpException(400, err.ToString());
                }
                continue;
            }

            var choice = choices[0];
            if (choice.TryGetProperty("finish_reason", out var fr) && fr.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(fr.GetString()))
            {
                finish = fr.GetString()!;
            }
            if (!choice.TryGetProperty("delta", out var delta)) continue;

            // 思考内容字段名兼容：OpenAI/DeepSeek 官方用 reasoning_content；llm-cs 网关（vLLM 中转）用 reasoning
            var reasoningText = GetStringProp(delta, "reasoning_content", "reasoning");
            if (!string.IsNullOrEmpty(reasoningText))
            {
                if (!firstChunkSeen)
                {
                    firstChunkSeen = true;
                    firstChunkCts.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
                }
                reasoningSb.Append(reasoningText);
                // 尊重产品开关：关闭思考模式时仍累积（用于 usage 估算/历史回传），但不向客户端产出思考增量
                if (request.ThinkingEnabled)
                {
                    if (ttft < 0) ttft = (int)sw.ElapsedMilliseconds;
                    yield return new LlmChunk.ReasoningDelta(reasoningText);
                }
                continue;
            }

            if (delta.TryGetProperty("content", out var dc) && dc.ValueKind == JsonValueKind.String)
            {
                var t = dc.GetString();
                if (!string.IsNullOrEmpty(t))
                {
                    if (!firstChunkSeen)
                    {
                        firstChunkSeen = true;
                        firstChunkCts.CancelAfter(System.Threading.Timeout.InfiniteTimeSpan);
                    }
                    if (ttft < 0) ttft = (int)sw.ElapsedMilliseconds;
                    textSb.Append(t);
                    yield return new LlmChunk.TextDelta(t);
                    continue;
                }
            }

            if (delta.TryGetProperty("tool_calls", out var tcs))
            {
                foreach (var tc in tcs.EnumerateArray())
                {
                    var idx = tc.TryGetProperty("index", out var ix) ? ix.GetInt32() : 0;
                    if (!toolAggregators.TryGetValue(idx, out var agg))
                    {
                        toolAggregators[idx] = ("", new StringBuilder());
                        agg = toolAggregators[idx];
                    }
                    if (tc.TryGetProperty("function", out var fn))
                    {
                        if (fn.TryGetProperty("name", out var name) && name.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(name.GetString()))
                        {
                            toolAggregators[idx] = (name.GetString()!, agg.Args);
                        }
                        if (fn.TryGetProperty("arguments", out var args) && args.ValueKind == JsonValueKind.String)
                        {
                            agg.Args.Append(args.GetString());
                        }
                    }
                }
                if (ttft < 0) ttft = (int)sw.ElapsedMilliseconds;
            }
        }

        sw.Stop();
        usage ??= EstimateUsage(request, textSb.ToString(), reasoningSb.ToString());

        if (finish == "tool_calls" && toolAggregators.Count > 0)
        {
            foreach (var (idx, (name, argsSb)) in toolAggregators.OrderBy(kv => kv.Key))
            {
                var argsJson = argsSb.ToString();
                JsonObject? args = null;
                if (!string.IsNullOrEmpty(argsJson))
                {
                    try { args = JsonNode.Parse(argsJson) as JsonObject; } catch (JsonException) { /* 忽略非法参数 */ }
                }
                var id = $"call_{Guid.NewGuid():N}"[..16];
                yield return new LlmChunk.ToolUse(new LlmToolCall(id, name, args));
            }
        }

        yield return new LlmChunk.Done(usage, finish == "tool_calls" ? LlmFinishReason.ToolCalls : LlmFinishReason.Stop,
            _model, textSb.ToString(), reasoningSb.ToString());
    }

    private string BuildChatUrl()
    {
        var baseUrl = (_provider.BaseUrl ?? "").TrimEnd('/');
        if (baseUrl.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase)) return baseUrl;
        return $"{baseUrl}/chat/completions";
    }

    private HttpContent BuildPayload(LlmRequest request, bool stream)
    {
        var payload = new JsonObject
        {
            ["model"] = request.Model ?? _model,
            ["stream"] = stream,
        };
        if (request.Temperature.HasValue) payload["temperature"] = request.Temperature.Value;
        if (request.MaxTokens.HasValue) payload["max_tokens"] = request.MaxTokens.Value;

        // 思考模式（聊天窗口全局开关 + 强度档位）——按供应商「思考参数模式」下发：
        //   None（默认，llm-cs 等网关推荐）：开启/关闭都不发送任何思考参数，交给网关默认行为
        //   DeepSeek（官方 API）：严格按官方协议 —— thinking 对象只含 type（enabled/disabled），
        //     思考强度 reasoning_effort 是独立的顶层字段（SDK 对应 extra_body={"thinking":{"type":"enabled"}} + reasoning_effort="high"）
        //   Qwen（DashScope/vLLM 兼容）：开启发 enable_thinking:true + 顶层 effort；关闭发 enable_thinking:false
        //   OpenAIEffort：开启仅发 reasoning_effort；关闭不发
        // 强度档位（实证 llm-cs 网关 = vLLM 平台，校验枚举 low/medium/xhigh；DeepSeek 官方 = low/high/max）：
        //   UI 档 low→low；medium→medium(Gateway)/high(官方)；high→xhigh(Gateway)/high(官方)；max→xhigh(Gateway)/max(官方)
        if (request.ThinkingEnabled)
        {
            // llm-cs 网关（vLLM）：xhigh/medium/low（发 high/max 会被 400 拒绝）
            var isGateway = !(_provider.BaseUrl?.Contains("api.deepseek.com", StringComparison.OrdinalIgnoreCase) == true);
            var thinkingWire = request.ThinkingEffort switch
            {
                LlmThinkingEffort.Low => "low",
                LlmThinkingEffort.Medium => isGateway ? "medium" : "high",
                LlmThinkingEffort.High => isGateway ? "xhigh" : "high",
                _ => isGateway ? "xhigh" : "max",
            };
            switch (_provider.ThinkingParam)
            {
                case "DeepSeek":
                    // 官方要求：thinking 里只放 type，reasoning_effort 独立顶层（多放字段会被官方严格校验拒绝/忽略）
                    payload["reasoning_effort"] = thinkingWire;
                    payload["thinking"] = new JsonObject { ["type"] = "enabled" };
                    break;
                case "Qwen":
                    // llm-cs Qwen：思考开关 enable_thinking + 顶层 effort（low/medium/xhigh）
                    payload["enable_thinking"] = true;
                    payload["reasoning_effort"] = thinkingWire;
                    break;
                case "OpenAIEffort":
                    payload["reasoning_effort"] = thinkingWire;
                    break;
                default:
                    // None：不发送（网关默认行为）
                    break;
            }
        }
        else if (_provider.ThinkingParam == "DeepSeek")
        {
            // 显式关闭（官方协议支持 type:disabled）
            payload["thinking"] = new JsonObject { ["type"] = "disabled" };
        }
        else if (_provider.ThinkingParam == "Qwen")
        {
            payload["enable_thinking"] = false;
        }

        if (request.Tools is { Count: > 0 })
        {
            // 注意：多轮 Agent 循环会复用同一套工具定义；InputSchema 必须深拷贝，否则同一 JsonNode
            // 二次挂载到新 payload 树会抛 "The node already has a parent" → LLM 调用中断。
            payload["tools"] = new JsonArray(request.Tools.Select(t => new JsonObject
            {
                ["type"] = "function",
                ["function"] = new JsonObject
                {
                    ["name"] = t.Name,
                    ["description"] = t.Description,
                    ["parameters"] = t.InputSchema?.DeepClone() ?? new JsonObject { ["type"] = "object", ["properties"] = new JsonObject() },
                },
            }).ToArray());
            payload["tool_choice"] = "auto";
        }

        // 思考模式下思维链须回传：携带 tools 的对话轮，assistant 消息必须带 reasoning_content 回传，
        // 否则 DeepSeek 官方 API 返回 400（工具调用场景强制要求；无 tools 传了也会被忽略）
        payload["messages"] = BuildMessages(request.Messages, request.Tools is { Count: > 0 });

        if (stream)
        {
            payload["stream_options"] = new JsonObject { ["include_usage"] = true };
        }

        return new StringContent(payload.ToJsonString(), Encoding.UTF8, "application/json");
    }

    private static JsonArray BuildMessages(IReadOnlyList<LlmChatMessage> messages, bool includeReasoning = false)
    {
        var arr = new JsonArray();
        foreach (var m in messages)
        {
            var o = new JsonObject { ["role"] = m.Role, ["content"] = m.Content ?? "" };
            if (m.ToolCallId is not null) o["tool_call_id"] = m.ToolCallId;
            // DeepSeek 官方协议：携带 tools 的对话轮，assistant 的 reasoning_content 必须回传（否则 400）
            if (includeReasoning && m.Role == "assistant" && !string.IsNullOrEmpty(m.Reasoning))
            {
                o["reasoning_content"] = m.Reasoning;
            }
            if (m.ToolCalls is { Count: > 0 })
            {
                o["tool_calls"] = new JsonArray(m.ToolCalls.Select(tc => new JsonObject
                {
                    ["id"] = tc.Id,
                    ["type"] = "function",
                    ["function"] = new JsonObject
                    {
                        ["name"] = tc.Name,
                        ["arguments"] = tc.Arguments?.ToJsonString() ?? "{}",
                    },
                }).ToArray());
            }
            arr.Add(o);
        }
        return arr;
    }

    private static IReadOnlyList<LlmToolCall>? ParseToolCalls(JsonElement msg)
    {
        if (msg.ValueKind != JsonValueKind.Object || !msg.TryGetProperty("tool_calls", out var tcs) || tcs.ValueKind != JsonValueKind.Array)
        {
            return null;
        }
        var calls = new List<LlmToolCall>();
        foreach (var tc in tcs.EnumerateArray())
        {
            var id = tc.TryGetProperty("id", out var idp) ? idp.GetString() : $"call_{Guid.NewGuid():N}";
            var name = tc.TryGetProperty("function", out var fn) && fn.TryGetProperty("name", out var np) ? np.GetString() ?? "" : "";
            var argsStr = tc.TryGetProperty("function", out var fn2) && fn2.TryGetProperty("arguments", out var ap) ? ap.GetString() : null;
            JsonObject? args = null;
            if (!string.IsNullOrEmpty(argsStr))
            {
                try { args = JsonNode.Parse(argsStr!) as JsonObject; } catch (JsonException) { /* ignore */ }
            }
            calls.Add(new LlmToolCall(id, name, args));
        }
        return calls;
    }

    private static LlmUsage ParseUsage(JsonElement root, LlmRequest request)
    {
        if (root.TryGetProperty("usage", out var u) && u.ValueKind == JsonValueKind.Object)
        {
            // OpenAI 兼容协议：reasoning_token 通常在 usage.completion_tokens_details.reasoning_tokens（DeepSeek R1 / K1 等）
            var reasoning = 0;
            if (u.TryGetProperty("completion_tokens_details", out var det) && det.ValueKind == JsonValueKind.Object)
            {
                reasoning = GetInt(det, "reasoning_tokens");
            }
            return new LlmUsage(GetInt(u, "prompt_tokens"), GetInt(u, "completion_tokens"), reasoning);
        }
        // 兜底估算
        var promptChars = request.Messages.Sum(m => (m.Content?.Length ?? 0) + (m.ToolCalls?.Sum(tc => tc.Name.Length + (tc.Arguments?.ToJsonString().Length ?? 0)) ?? 0));
        return new LlmUsage(promptChars / 4, 0);
    }

    private static LlmUsage EstimateUsage(LlmRequest request, string text, string reasoning)
    {
        var promptChars = request.Messages.Sum(m => m.Content?.Length ?? 0);
        return new LlmUsage(promptChars / 4, (text.Length + reasoning.Length) / 4);
    }

    private static int GetInt(JsonElement o, string name) =>
        o.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.Number ? p.GetInt32() : 0;

    private static string MaskBody(string body)
    {
        try
        {
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("error", out var err))
            {
                var msg = err.TryGetProperty("message", out var m) ? m.GetString() : "unknown";
                return (msg ?? "unknown")[..Math.Min(300, msg?.Length ?? 0)];
            }
        }
        catch (JsonException) { /* 非 JSON */ }
        return body[..Math.Min(300, body.Length)];
    }
}

/// <summary>LLM HTTP 调用异常（只带状态码与脱敏后的消息，不暴露 Endpoint/Header）</summary>
public sealed class LlmHttpException : Exception
{
    public int StatusCode { get; }

    public LlmHttpException(int statusCode, string message) : base(message)
    {
        StatusCode = statusCode;
    }
}
