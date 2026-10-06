using System.Diagnostics;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NextChats.Core.Domain;
using NextChats.Core.Entities;
using NextChats.Core.Localization;

namespace NextChats.Core.Clients;

/// <summary>
/// 内置 Mock LLM：无需网络/密钥的离线演示模型。
/// 行为（ReAct 演示）：
///   1. 若本轮可用工具中存在 mock.* 工具，且历史上还没有 tool 结果 → 发起工具调用（mock.now / mock.echo）；
///   2. 否则 → 流式输出一段文本。
/// 支持中断测试（流式过程中可被取消）。
/// 输出文案经 Texts 字典按 lang 本地化（默认英文）。
/// 思考模式由请求（聊天全局开关/强度）驱动；模型级思考力度配置已废弃。
/// </summary>
public sealed class MockLlmClient : ILlmClient
{
    private readonly LlmProvider _provider;
    private readonly string _model;
    private readonly ILogger _logger;
    private readonly string _lang;

    public MockLlmClient(LlmProvider provider, string model, ILogger logger, string? lang = null)
    {
        _provider = provider;
        _model = model;
        _logger = logger;
        _lang = lang ?? "en";
    }

    public string ProviderName => _provider.Name;

    public string Model => _model;

    public async Task<LlmResult> CompleteAsync(LlmRequest request, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (text, reasoning, toolCalls) = Compose(request);
        await Task.Delay(80, ct);
        sw.Stop();
        var usage = new LlmUsage(Estimate(request), Estimate(text) + Estimate(reasoning));
        return new LlmResult(
            LlmChatMessage.Assistant(text, toolCalls),
            usage,
            toolCalls.Count > 0 ? LlmFinishReason.ToolCalls : LlmFinishReason.Stop,
            Model,
            (int)sw.ElapsedMilliseconds,
            (int)sw.ElapsedMilliseconds);
    }

    public async IAsyncEnumerable<LlmChunk> StreamAsync(LlmRequest request, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();
        var (text, reasoning, toolCalls) = Compose(request);

        if (reasoning.Length > 0)
        {
            foreach (var part in Chunk(reasoning, 6))
            {
                await Task.Delay(12, ct);
                yield return new LlmChunk.ReasoningDelta(part);
            }
        }

        if (toolCalls.Count > 0)
        {
            await Task.Delay(40, ct);
            foreach (var tc in toolCalls)
            {
                yield return new LlmChunk.ToolUse(tc);
            }
        }
        else
        {
            foreach (var part in Chunk(text, 8))
            {
                await Task.Delay(10, ct);
                yield return new LlmChunk.TextDelta(part);
            }
        }

        var usage = new LlmUsage(Estimate(request), Estimate(text) + Estimate(reasoning));
        yield return new LlmChunk.Done(usage, toolCalls.Count > 0 ? LlmFinishReason.ToolCalls : LlmFinishReason.Stop,
            Model, text, reasoning);
    }

    private (string Text, string Reasoning, List<LlmToolCall> ToolCalls) Compose(LlmRequest request)
    {
        var lastUser = request.Messages.LastOrDefault(m => m.Role == "user")?.Content ?? "";
        var hasToolContext = request.Messages.Count(m => m.Role == "tool") > 0
                             || request.Messages.Any(m => m.ToolCallId is not null);

        var mockTools = request.Tools?.Where(t => t.Name.StartsWith("mock.", StringComparison.Ordinal)).ToList() ?? [];

        var reasoning = string.Empty;

        if (mockTools.Count > 0 && !hasToolContext)
        {
            reasoning = Texts.Get("MOCK_REASONING_TOOL", _lang);
            var calls = new List<LlmToolCall>();
            var now = mockTools.FirstOrDefault(t => t.Name == "mock.now");
            if (now is not null)
            {
                calls.Add(new LlmToolCall($"call_{Guid.NewGuid():N}", "mock.now", new JsonObject()));
            }
            var echo = mockTools.FirstOrDefault(t => t.Name == "mock.echo");
            if (echo is not null)
            {
                calls.Add(new LlmToolCall($"call_{Guid.NewGuid():N}", "mock.echo", new JsonObject { ["input"] = lastUser }));
            }
            return ("", reasoning, calls);
        }

        // 演示 ReAct：显式触发指定工具（tool:工具名 [可选 JSON 参数]）或危险工具（danger:工具名 → 审批流）
        var trigger = lastUser.Trim();
        if (!hasToolContext &&
            (trigger.StartsWith("tool:", StringComparison.OrdinalIgnoreCase) ||
             trigger.StartsWith("danger:", StringComparison.OrdinalIgnoreCase)))
        {
            var dangerous = trigger.StartsWith("danger:", StringComparison.OrdinalIgnoreCase);
            var rest = trigger[(trigger.IndexOf(':') + 1)..].Trim();
            var spaceIdx = rest.IndexOf(' ');
            var toolName = spaceIdx > 0 ? rest[..spaceIdx].Trim() : rest;
            var target = request.Tools?.FirstOrDefault(t => t.Name.Equals(toolName, StringComparison.OrdinalIgnoreCase));
            if (target is not null)
            {
                reasoning = dangerous
                    ? Texts.Get("MOCK_REASONING_DANGER", _lang)
                    : Texts.Get("MOCK_REASONING_CALL", _lang, toolName);
                // 显式 JSON 参数透传（ws_* / http_fetch 等需要参数的演示工具）
                var args = new JsonObject();
                if (spaceIdx > 0)
                {
                    var jsonTail = rest[(spaceIdx + 1)..].Trim();
                    try
                    {
                        if (JsonNode.Parse(jsonTail) is JsonObject jo) args = jo;
                    }
                    catch
                    {
                        /* 非 JSON 尾部忽略 */
                    }
                }
                switch (toolName.ToLowerInvariant())
                {
                    case "echo":
                        args["input"] = "hello from mock";
                        break;
                    case "mock.echo":
                        args["input"] = lastUser;
                        break;
                    case "add":
                        args["a"] = 12;
                        args["b"] = 30;
                        break;
                    case "say_hello":
                        args["name"] = "Next Chats";
                        break;
                    case "get_current_time":
                        break;
                    case "delete_all":
                        args["confirm"] = true;
                        break;
                    case "http_fetch":
                        if (!args.ContainsKey("url")) args["url"] = "https://raw.githubusercontent.com/Hopcos/next-chats/main/README.md";
                        break;
                    case "mcp_prompt":
                        if (!args.ContainsKey("name")) args["name"] = "code_review";
                        if (!args.ContainsKey("server")) args["server"] = "Vision";
                        break;
                    case "mcp_resources":
                        if (!args.ContainsKey("server")) args["server"] = "Vision";
                        break;
                    case "mcp_read_resource":
                        if (!args.ContainsKey("uri")) args["uri"] = "vision://status";
                        if (!args.ContainsKey("server")) args["server"] = "Vision";
                        break;
                    case "maybe_fail":
                        args["forceFail"] = true;
                        break;
                }
                return ("", reasoning, [new LlmToolCall($"call_{Guid.NewGuid():N}", target.Name, args)]);
            }
        }

        var text = new StringBuilder();
        text.AppendLine(Texts.Get("MOCK_TITLE", _lang));
        var imageCount = System.Text.RegularExpressions.Regex.Matches(lastUser, "<image_source>").Count;
        if (imageCount > 0)
        {
            text.AppendLine();
            text.AppendLine(Texts.Get("MOCK_IMAGES_RECEIVED", _lang, imageCount));
        }
        if (hasToolContext)
        {
            var toolMsg = request.Messages.LastOrDefault(m => m.Role == "tool")?.Content ?? "";
            text.AppendLine();
            text.AppendLine(Texts.Get("MOCK_TOOL_CONTEXT_INTRO", _lang));
            text.AppendLine();
            text.AppendLine($"> {toolMsg}");
            text.AppendLine();
            text.AppendLine(Texts.Get("MOCK_CONTINUE", _lang));
        }
        text.AppendLine();
        // 图片以 image_source base64 块内嵌在用户消息中，展示时替换为占位，避免输出海量 base64
        var displayInput = System.Text.RegularExpressions.Regex.Replace(lastUser, "<image_source>.*?</image_source>", "[image]", System.Text.RegularExpressions.RegexOptions.Singleline);
        text.AppendLine(Texts.Get("MOCK_RECEIVED", _lang, displayInput.Trim()));
        text.AppendLine();
        text.AppendLine(Texts.Get("MOCK_TIME", _lang, DateTimeOffset.UtcNow.ToString("yyyy-MM-dd HH:mm:ss")));
        text.AppendLine(Texts.Get("MOCK_PROVIDER", _lang, ProviderName, Model));
        text.AppendLine();
        text.AppendLine(Texts.Get("MOCK_PRODUCTION_HINT", _lang));
        // 思考模式（聊天全局开关 + 强度，统一作用于所有模型）：关闭 → 无模拟推理；开启 → 按强度档位递进模拟思考
        if (reasoning.Length == 0 && request.ThinkingEnabled)
        {
            reasoning = request.ThinkingEffort switch
            {
                LlmThinkingEffort.Low => Texts.Get("MOCK_REASONING_LOW", _lang),
                LlmThinkingEffort.Medium => Texts.Get("MOCK_REASONING_MEDIUM", _lang),
                LlmThinkingEffort.High => Texts.Get("MOCK_REASONING_HIGH", _lang),
                _ => Texts.Get("MOCK_REASONING_MAX", _lang),
            };
        }
        return (text.ToString(), reasoning, []);
    }

    private static IEnumerable<string> Chunk(string text, int size)
    {
        for (var i = 0; i < text.Length; i += size)
        {
            yield return text.Substring(i, Math.Min(size, text.Length - i));
        }
    }

    private static int Estimate(LlmRequest request)
    {
        return request.Messages.Sum(m => (m.Content?.Length ?? 0) / 4);
    }

    private static int Estimate(string? text) => (text?.Length ?? 0) / 4;
}
