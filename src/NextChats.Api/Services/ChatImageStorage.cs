using Microsoft.Extensions.Options;

namespace NextChats.Api.Services;

/// <summary>
/// 聊天图片持久化：base64 → 站点 uploads/chat/{yyyyMM}/{guid}.{ext} 文件。
/// 消息只存相对访问 URL（如 202609/ab12…n.png），图片经 /api/chat/images/{**name} 鉴权回读。
/// 独立于 wwwroot —— 6500 部署协议整体替换 wwwroot，图片目录不受影响。
/// </summary>
public sealed class ChatImageStorage(
    Microsoft.AspNetCore.Hosting.IWebHostEnvironment env,
    ILogger<ChatImageStorage> logger)
{
    private string Root => Path.Combine(env.ContentRootPath, "uploads", "chat");

    private static readonly Dictionary<string, string> ExtByMime = new(StringComparer.OrdinalIgnoreCase)
    {
        ["image/png"] = "png",
        ["image/jpeg"] = "jpg",
        ["image/gif"] = "gif",
        ["image/webp"] = "webp",
    };

    private static readonly Dictionary<string, string> MimeByExt = new(StringComparer.OrdinalIgnoreCase)
    {
        ["png"] = "image/png",
        ["jpg"] = "image/jpeg",
        ["jpeg"] = "image/jpeg",
        ["gif"] = "image/gif",
        ["webp"] = "image/webp",
    };

    private const long MaxImageBytes = 5_000_000; // base64 字符数上限（与 ChatController 校验一致）

    /// <summary>校验并保存一张图片，返回相对访问名（yyyyMM/guid.ext）；非法/失败返回 null（不阻塞对话）。</summary>
    public string? SaveImage(string? mimeType, string base64, string? fileName)
    {
        if (string.IsNullOrWhiteSpace(base64) || base64.Length > MaxImageBytes) return null;
        if (mimeType is null || !ExtByMime.TryGetValue(mimeType, out var ext)) return null;
        try
        {
            _ = Convert.FromBase64String(base64); // 合法性预检（后续写入仍可能失败）
        }
        catch (FormatException)
        {
            return null;
        }

        var month = DateTimeOffset.UtcNow.ToString("yyyyMM");
        var dir = Path.Combine(Root, month);
        try
        {
            Directory.CreateDirectory(dir);
            var name = $"{Guid.NewGuid():N}.{ext}";
            File.WriteAllBytes(Path.Combine(dir, name), Convert.FromBase64String(base64));
            return $"{month}/{name}";
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "聊天图片保存失败 mime={Mime} file={File}", mimeType, fileName);
            return null;
        }
    }

    /// <summary>按相对名读取图片；非法路径或不存在返回 null。路径仅允许 yyyyMM/guid.ext（无穿越风险）。</summary>
    public (byte[] Bytes, string Mime)? ReadImage(string rel)
    {
        if (!TryResolve(rel, out var path, out var mime) || !File.Exists(path)) return null;
        try
        {
            return (File.ReadAllBytes(path), mime);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "聊天图片读取失败 rel={Rel}", rel);
            return null;
        }
    }

    /// <summary>删除一组图片文件（忽略不存在的文件；路径非法或删除失败仅记日志，不抛异常）。</summary>
    public void DeleteImages(IEnumerable<string> rels)
    {
        foreach (var rel in rels)
        {
            if (!TryResolve(rel, out var path, out _)) continue;
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "聊天图片删除失败 rel={Rel}", rel);
            }
        }
    }

    private bool TryResolve(string? rel, out string path, out string mime)
    {
        path = string.Empty;
        mime = string.Empty;
        if (string.IsNullOrWhiteSpace(rel)) return false;
        var parts = rel.Split('/');
        if (parts.Length != 2) return false;
        if (parts[0].Length != 6 || !parts[0].All(char.IsAsciiDigit)) return false;
        var name = parts[1];
        if (name.Length != 36 || !name[..32].All(c => char.IsAsciiHexDigit(c)) || name[32] != '.') return false;
        var ext = name[(name.IndexOf('.') + 1)..];
        if (!MimeByExt.TryGetValue(ext, out mime)) return false;
        path = Path.Combine(Root, parts[0], name);
        return true;
    }
}
