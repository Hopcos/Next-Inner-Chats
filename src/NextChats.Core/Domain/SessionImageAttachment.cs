namespace NextChats.Core.Domain;

/// <summary>
/// 会话中的图片附件上下文（当前轮上传或历史消息附件）。
/// 提供统一的引用标识（attachment:1, attachment:{relPath} 等），并在向工具派发时按需解析出 Base64 数据 URI。
/// </summary>
public sealed class SessionImageAttachment
{
    /// <summary>会话内从 1 开始递增的序号（1, 2, ...）</summary>
    public int Index { get; init; }

    /// <summary>原始文件名（如 parking.png）</summary>
    public string? FileName { get; init; }

    /// <summary>MIME 类型（如 image/png）</summary>
    public string MimeType { get; init; } = "image/png";

    /// <summary>磁盘存储相对路径（如 202610/abc.png）</summary>
    public string RelPath { get; init; } = string.Empty;

    /// <summary>相对访问 URL（如 /api/chat/images/202610/abc.png）</summary>
    public string Url { get; init; } = string.Empty;

    /// <summary>Base64 编码（当前轮上传有内存直显缓存；历史消息通过 IChatImageStorage 延迟加载）</summary>
    public string? Base64 { get; set; }
}
