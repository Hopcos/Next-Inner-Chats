namespace NextChats.Core.Abstractions;

/// <summary>
/// 聊天图片存储抽象（base64 校验落盘与文件回读）。
/// </summary>
public interface IChatImageStorage
{
    /// <summary>校验并保存一张图片，返回相对访问名（yyyyMM/guid.ext）；非法/失败返回 null（不阻塞对话）。</summary>
    string? SaveImage(string? mimeType, string base64, string? fileName);

    /// <summary>按相对名读取图片；非法路径或不存在返回 null。路径仅允许 yyyyMM/guid.ext。</summary>
    (byte[] Bytes, string Mime)? ReadImage(string rel);

    /// <summary>删除一组图片文件（忽略不存在的文件；路径非法或删除失败仅记日志，不抛异常）。</summary>
    void DeleteImages(IEnumerable<string> rels);
}
