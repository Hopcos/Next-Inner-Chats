using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NextChats.Core.Abstractions;
using NextChats.Core.Clients;
using NextChats.Core.Configuration;
using NextChats.Core.Localization;

namespace NextChats.Core.Services;

/// <summary>
/// 上下文管理器：Token 估算 → 达到水位线先压缩（LLM 摘要）后截断（丢最旧轮），确保不超出长度限制。
/// </summary>
public sealed class ContextManager : IContextManager
{
    private readonly ContextOptions _context;
    private readonly ILlmRouter _router;
    private readonly ILogger _logger;

    // 压缩摘要的对话内标记（进入会话历史，给模型作背景；压缩指令用英文，摘要语言跟随对话）
    private static readonly string SummaryMarker = Texts.Get("CONTEXT_SUMMARY_MARKER", "en");

    public ContextManager(IOptions<ContextOptions> context, ILlmRouter router, ILogger<ContextManager> logger)
    {
        _context = context.Value;
        _router = router;
        _logger = logger;
    }

    public int EstimateTokens(string text) => (text?.Length ?? 0) / _context.CharsPerToken;

    public int EstimateMessagesTokens(IEnumerable<LlmChatMessage> messages)
    {
        var total = 0;
        foreach (var m in messages)
        {
            total += EstimateTokens(m.Content ?? "");
            if (m.ToolCalls is not null)
            {
                foreach (var tc in m.ToolCalls)
                {
                    total += tc.Name.Length / _context.CharsPerToken + EstimateTokens(tc.Arguments?.ToJsonString() ?? "{}");
                }
            }
        }
        return total;
    }

    public bool NeedsCompression(IReadOnlyList<LlmChatMessage> messages, int contextWindow)
    {
        if (contextWindow <= 0) return false;
        return EstimateMessagesTokens(messages) >= contextWindow * _context.CompressThreshold;
    }

    public async Task<IReadOnlyList<LlmChatMessage>> CompressAsync(
        IReadOnlyList<LlmChatMessage> messages, int contextWindow, IReadOnlyList<LlmToolDef>? tools, CancellationToken ct)
    {
        // 必须保留：system 首条 + 最近的 user 轮
        var keepHead = 1;
        var keepTail = 2;
        if (messages.Count <= keepHead + keepTail + 1)
        {
            return Truncate(messages, contextWindow);
        }

        var compressible = messages.Skip(keepHead).Take(messages.Count - keepHead - keepTail).ToList();
        var head = messages.Take(keepHead).ToList();
        var tail = messages.Skip(messages.Count - keepTail).ToList();

        try
        {
            var summary = await SummarizeAsync(compressible, ct);
            var summaryMessage = LlmChatMessage.User($"{SummaryMarker}\n{summary}");
            var result = head.Concat([summaryMessage]).Concat(tail).ToList();

            // 压缩后仍超限 → 截断
            if (EstimateMessagesTokens(result) >= contextWindow * _context.CompressTarget)
            {
                return Truncate(result, contextWindow);
            }
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "上下文压缩失败，退回截断策略");
            return Truncate(messages, contextWindow);
        }
    }

    public IReadOnlyList<LlmChatMessage> Truncate(IReadOnlyList<LlmChatMessage> messages, int contextWindow)
    {
        var budget = (int)(contextWindow * _context.CompressTarget);
        var list = messages.ToList();
        var idx = 1; // 保留 system 首条
        while (list.Count - idx > _context.MinMessagesAfterTruncate && EstimateMessagesTokens(list) > budget)
        {
            list.RemoveAt(idx);
        }
        if (EstimateMessagesTokens(list) > budget)
        {
            // 仍然超限：截断每条内容
            for (var i = 0; i < list.Count; i++)
            {
                var content = list[i].Content ?? "";
                if (content.Length > 12_000)
                {
                    list[i] = list[i] with { Content = content[..12_000] + "… " + Texts.Get("TRUNCATED_SUFFIX", "en") };
                }
            }
        }
        return list;
    }

    private async Task<string> SummarizeAsync(IReadOnlyList<LlmChatMessage> messages, CancellationToken ct)
    {
        var client = await _router.SelectClientAsync(null, null, null, ct);
        var summaryPrompt = string.Join("\n", messages
            .Where(m => m.Role is "user" or "assistant")
            .Select(m => $"{m.Role}: {TruncateText(m.Content ?? Texts.Get("MCP_EMPTY", "en"), 800)}")
            .TakeLast(40));
        var request = new LlmRequest
        {
            Messages =
            [
                LlmChatMessage.System(Texts.Get("CONTEXT_COMPRESS_PROMPT", "en")),
                LlmChatMessage.User(summaryPrompt),
            ],
            Stream = false,
            MaxTokens = 800,
        };
        var result = await client.CompleteAsync(request, ct);
        return TruncateText(result.Message.Content ?? "", 4000);
    }

    private static string TruncateText(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";
}
