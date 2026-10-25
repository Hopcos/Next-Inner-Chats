using System.Text;
using NextChats.Core.Abstractions;
using NextChats.Core.Localization;

namespace NextChats.Core.Services;

/// <summary>
/// 工作空间编码会话的「完成标准」与项目指南（工作空间根目录的 AGENTS.md）。
///  - 完成标准：有写权限（≥ WorkspaceWrite）时注入，要求改动后必须用 ws_exec 构建/测试验证并汇报结果；
///  - 项目指南：读取根目录 AGENTS.md（每个工作空间自行声明构建/测试/验证命令与约定），作为系统提示注入。
/// 读取经 <see cref="IWorkspaceSandbox.ResolveInside"/>：junction/符号链接指向根外会被拒绝（FullAccess 以上按设计放开）。
/// 注意：这只是提示词层的引导，不是强制；模型是否遵守取决于模型本身。
/// </summary>
public static class WorkspaceGuide
{
    public const string FileName = "AGENTS.md";

    /// <summary>注入系统提示的字符上限，超出截断（防止 Token 爆炸）</summary>
    public const int MaxChars = 12_000;

    private const int MaxReadBytes = MaxChars * 4; // UTF-8 最坏每字符 4 字节

    /// <summary>读取工作空间根目录的 AGENTS.md；不存在/空白/越界/读取失败一律返回 null（不影响聊天）。</summary>
    public static async Task<string?> LoadAsync(IWorkspaceSandbox sandbox, WsContext ctx, CancellationToken ct = default)
    {
        try
        {
            var path = sandbox.ResolveInside(ctx, FileName);
            if (!File.Exists(path)) return null;

            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 4096, useAsync: true);
            var buffer = new byte[MaxReadBytes + 1];
            var total = 0;
            while (total < buffer.Length)
            {
                var n = await fs.ReadAsync(buffer.AsMemory(total, buffer.Length - total), ct);
                if (n == 0) break;
                total += n;
            }
            if (total == 0) return null;

            var truncatedByBytes = total > MaxReadBytes;
            var text = Encoding.UTF8.GetString(buffer, 0, Math.Min(total, MaxReadBytes)).TrimStart('\uFEFF').Trim();
            if (text.Length == 0) return null;

            if (text.Length > MaxChars || truncatedByBytes)
            {
                text = text[..Math.Min(text.Length, MaxChars)] + "\n…";
            }
            return text;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            // WsPathEscapedException / IO 异常：静默降级，聊天照常
            return null;
        }
    }

    /// <summary>构建要追加到系统提示的块（顺序：完成标准 → 项目指南）。</summary>
    public static IReadOnlyList<string> BuildBlocks(WsContext ctx, string? guide, string lang)
    {
        var blocks = new List<string>(2);
        if (ctx.CanWrite)
        {
            blocks.Add(Texts.Get("WS_COMPLETION_STANDARD", lang));
        }
        if (!string.IsNullOrWhiteSpace(guide))
        {
            blocks.Add(Texts.Get("WS_GUIDE_HEADER", lang) + "\n" + guide);
        }
        return blocks;
    }
}
