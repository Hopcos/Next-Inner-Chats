using System.Text.Json;
using System.Text.Json.Nodes;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;
using NextChats.Core.Entities;

namespace NextChats.Core.Services;

/// <summary>
/// 会话图片附件解析器：
/// 1. 收集会话内全部图片（当前轮上传 + 历史消息附件）并建立统一索引（attachment:1, attachment:{relPath} 等）；
/// 2. 构建注入 System Prompt 的图片列表与视觉工具使用指引；
/// 3. 在向 MCP 工具派发调用前，透明将工具参数中的图片引用（image_source 等）解析并替换为标准 Base64 Data URI；
///    避免向 MCP 传递宿主机物理文件路径（兼容跨机器/容器部署的远程 MCP 服务），同时避免 LLM 上下文中携带海量 Base64；
/// 4. 支持内置 mcp_resources 与 mcp_read_resource 发现与读取会话图片元数据。
/// </summary>
public static class SessionImageResolver
{
    private const string ImageUrlPrefix = "/api/chat/images/";

    /// <summary>
    /// 从历史消息与当前请求中收集所有会话图片附件，按时间顺序排列并分配从 1 开始的递增索引。
    /// </summary>
    public static List<SessionImageAttachment> CollectSessionImages(
        IReadOnlyList<ChatMessage>? history,
        string? currentAttachmentsJson,
        IReadOnlyList<ImageAttachment>? currentImages)
    {
        var result = new List<SessionImageAttachment>();
        var seenRelPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. 历史消息附件
        if (history is { Count: > 0 })
        {
            foreach (var m in history)
            {
                if (string.IsNullOrWhiteSpace(m.AttachmentsJson)) continue;
                AppendFromAttachmentsJson(m.AttachmentsJson, null, result, seenRelPaths);
            }
        }

        // 2. 当前轮请求附件与内存 Base64
        if (!string.IsNullOrWhiteSpace(currentAttachmentsJson))
        {
            AppendFromAttachmentsJson(currentAttachmentsJson, currentImages, result, seenRelPaths);
        }
        else if (currentImages is { Count: > 0 })
        {
            for (var i = 0; i < currentImages.Count; i++)
            {
                var img = currentImages[i];
                var placeholderRel = $"current_{i + 1}";
                if (seenRelPaths.Add(placeholderRel))
                {
                    result.Add(new SessionImageAttachment
                    {
                        Index = result.Count + 1,
                        FileName = img.FileName,
                        MimeType = string.IsNullOrWhiteSpace(img.MimeType) ? "image/png" : img.MimeType,
                        RelPath = placeholderRel,
                        Url = $"{ImageUrlPrefix}{placeholderRel}",
                        Base64 = img.Base64,
                    });
                }
            }
        }

        // 重新编号
        for (var i = 0; i < result.Count; i++)
        {
            typeof(SessionImageAttachment).GetProperty(nameof(SessionImageAttachment.Index))?
                .SetValue(result[i], i + 1);
        }

        return result;
    }

    private static void AppendFromAttachmentsJson(
        string json,
        IReadOnlyList<ImageAttachment>? inMemoryImages,
        List<SessionImageAttachment> target,
        HashSet<string> seenRelPaths)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return;

            var arrayIdx = 0;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                var fileName = el.TryGetProperty("fileName", out var fn) && fn.ValueKind == JsonValueKind.String ? fn.GetString() : null;
                var mimeType = el.TryGetProperty("mimeType", out var mt) && mt.ValueKind == JsonValueKind.String ? mt.GetString() : "image/png";
                var url = el.TryGetProperty("url", out var u) && u.ValueKind == JsonValueKind.String ? u.GetString() : null;

                if (string.IsNullOrWhiteSpace(url)) continue;

                var relPath = url.StartsWith(ImageUrlPrefix, StringComparison.OrdinalIgnoreCase)
                    ? url[ImageUrlPrefix.Length..]
                    : url;

                var base64 = inMemoryImages is not null && arrayIdx < inMemoryImages.Count
                    ? inMemoryImages[arrayIdx].Base64
                    : null;

                if (seenRelPaths.Add(relPath))
                {
                    target.Add(new SessionImageAttachment
                    {
                        Index = target.Count + 1,
                        FileName = fileName,
                        MimeType = string.IsNullOrWhiteSpace(mimeType) ? "image/png" : mimeType,
                        RelPath = relPath,
                        Url = url,
                        Base64 = base64,
                    });
                }
                else
                {
                    // 若已存在但内存有 base64，则补全缓存
                    var existing = target.FirstOrDefault(x => string.Equals(x.RelPath, relPath, StringComparison.OrdinalIgnoreCase));
                    if (existing is not null && string.IsNullOrWhiteSpace(existing.Base64) && !string.IsNullOrWhiteSpace(base64))
                    {
                        existing.Base64 = base64;
                    }
                }

                arrayIdx++;
            }
        }
        catch (JsonException)
        {
            // 忽略损坏的 JSON 格式
        }
    }

    /// <summary>
    /// 构建注入 System Prompt 的图片附件提示块。
    /// 明确向模型说明当前会话中存在的图片及引用标识，指导其在调用视觉工具时传入该标识。
    /// </summary>
    public static string? BuildPromptBlock(IReadOnlyList<SessionImageAttachment> images, string lang)
    {
        if (images is null || images.Count == 0) return null;

        var isZh = lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase);
        var sb = new System.Text.StringBuilder();

        if (isZh)
        {
            sb.AppendLine("## 会话图片附件（Session Image Attachments）");
            sb.AppendLine("当前会话包含以下由用户上传的图片：");
            foreach (var img in images)
            {
                var nameDesc = string.IsNullOrWhiteSpace(img.FileName) ? "" : $"「{img.FileName}」";
                sb.AppendLine($"- [图片 {img.Index}] {nameDesc}（格式: {img.MimeType}，引用标识: `attachment:{img.Index}`，相对路径: `{img.RelPath}`）");
            }
            sb.AppendLine();
            sb.AppendLine("【视觉工具调用规则】");
            sb.AppendLine("1. 当用户询问图片信息、分析图表、识别文字、对比或获取图片元数据时，如需调用视觉工具（如 get_image_info、describe_image、read_image_text、detect_image_boxes、extract_ui_layout、extract_diagram_info 等）：");
            sb.AppendLine("   - 必须将图片引用标识（如 `\"attachment:1\"` 或 `\"attachment:2\"`，也可使用图片文件名或相对路径）填入工具的 `image_source` 参数中。");
            sb.AppendLine("   - 系统底层会在执行工具前自动将引用解析为标准图片 Base64 数据并传给视觉服务，确保跨机器与远程 MCP 正常执行。");
            sb.AppendLine("2. 严禁向工具传递虚构的本地磁盘绝对路径（如 D:\\...），严禁回复用户「无法获取图片文件路径/可访问源」。");
        }
        else
        {
            sb.AppendLine("## Session Image Attachments");
            sb.AppendLine("The conversation contains the following image attachment(s) uploaded by the user:");
            foreach (var img in images)
            {
                var nameDesc = string.IsNullOrWhiteSpace(img.FileName) ? "" : $"\"{img.FileName}\" ";
                sb.AppendLine($"- [Image {img.Index}] {nameDesc}(MIME: {img.MimeType}, Reference: `attachment:{img.Index}`, RelPath: `{img.RelPath}`)");
            }
            sb.AppendLine();
            sb.AppendLine("Rules for vision tool calls:");
            sb.AppendLine("1. When analyzing images, inspecting layout, OCR text, or reading metadata with vision tools (e.g. get_image_info, describe_image, read_image_text, detect_image_boxes, etc.):");
            sb.AppendLine("   - ALWAYS pass the reference (e.g. `\"attachment:1\"` or `\"attachment:2\"`) as the `image_source` argument.");
            sb.AppendLine("   - The runtime automatically resolves this reference and supplies standard Base64 Data URI to the MCP server before invocation.");
            sb.AppendLine("2. DO NOT hallucinate imaginary local disk paths, and DO NOT claim you cannot access the uploaded image.");
        }

        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 将图片引用标识解析为标准 Base64 Data URI（data:image/png;base64,...）。
    /// 若非引用标识或无法解析，返回 null。
    /// </summary>
    public static string? ResolveToDataUri(string? val, IReadOnlyList<SessionImageAttachment> images, IChatImageStorage? storage)
    {
        if (string.IsNullOrWhiteSpace(val)) return null;

        var trimmed = val.Trim();

        // 已经为有效 Data URI 或纯 Base64 的不重复解析
        if (trimmed.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return null;

        // 查找匹配的图片附件
        var matched = images.Count > 0 ? MatchAttachment(trimmed, images) : null;
        if (matched is not null)
        {
            // 优先使用内存中已有的 Base64
            if (!string.IsNullOrWhiteSpace(matched.Base64))
            {
                var mime = string.IsNullOrWhiteSpace(matched.MimeType) ? "image/png" : matched.MimeType;
                return $"data:{mime};base64,{matched.Base64}";
            }

            // 从存储中加载
            if (storage is not null && !string.IsNullOrWhiteSpace(matched.RelPath) && !matched.RelPath.StartsWith("current_"))
            {
                var file = storage.ReadImage(matched.RelPath);
                if (file.HasValue && file.Value.Bytes.Length > 0)
                {
                    matched.Base64 = Convert.ToBase64String(file.Value.Bytes);
                    var mime = !string.IsNullOrWhiteSpace(file.Value.Mime) ? file.Value.Mime : matched.MimeType;
                    return $"data:{mime};base64,{matched.Base64}";
                }
            }
            return null;
        }

        // 兜底：如果 val 本身就是一个 /api/chat/images/xxx 或 yyyyMM/xxx.ext 相对路径，且 storage 能读到
        if (storage is not null)
        {
            var testRel = trimmed.StartsWith(ImageUrlPrefix, StringComparison.OrdinalIgnoreCase)
                ? trimmed[ImageUrlPrefix.Length..]
                : (trimmed.StartsWith("attachment:", StringComparison.OrdinalIgnoreCase) ? trimmed["attachment:".Length..] : trimmed);
            if (!testRel.Contains("..") && (testRel.EndsWith(".png", StringComparison.OrdinalIgnoreCase) ||
                                            testRel.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                            testRel.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                            testRel.EndsWith(".webp", StringComparison.OrdinalIgnoreCase) ||
                                            testRel.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)))
            {
                var file = storage.ReadImage(testRel);
                if (file.HasValue && file.Value.Bytes.Length > 0)
                {
                    var base64 = Convert.ToBase64String(file.Value.Bytes);
                    var mime = !string.IsNullOrWhiteSpace(file.Value.Mime) ? file.Value.Mime : "image/png";
                    return $"data:{mime};base64,{base64}";
                }
            }
        }

        return null;
    }

    private static SessionImageAttachment? MatchAttachment(string val, IReadOnlyList<SessionImageAttachment> images)
    {
        // 1. 精确匹配 attachment:N 或 attachment:relPath 或 attachment:fileName
        if (val.StartsWith("attachment:", StringComparison.OrdinalIgnoreCase))
        {
            var key = val["attachment:".Length..].Trim();
            if (int.TryParse(key, out var idx))
            {
                var hit = images.FirstOrDefault(i => i.Index == idx);
                if (hit is not null) return hit;
            }

            var byRel = images.FirstOrDefault(i => string.Equals(i.RelPath, key, StringComparison.OrdinalIgnoreCase));
            if (byRel is not null) return byRel;

            var byName = images.FirstOrDefault(i => !string.IsNullOrWhiteSpace(i.FileName) && string.Equals(i.FileName, key, StringComparison.OrdinalIgnoreCase));
            if (byName is not null) return byName;
        }

        // 2. 匹配纯数字序号 1, 2...
        if (int.TryParse(val, out var number))
        {
            var hit = images.FirstOrDefault(i => i.Index == number);
            if (hit is not null) return hit;
        }

        // 3. 匹配 [image N] 或 image N 或 img_N
        if (val.StartsWith("[image", StringComparison.OrdinalIgnoreCase) || val.StartsWith("image", StringComparison.OrdinalIgnoreCase))
        {
            var clean = val.Trim('[', ']', ' ', '#', ':');
            var parts = clean.Split(' ', '_', '-');
            if (parts.Length > 1 && int.TryParse(parts[^1], out var pIdx))
            {
                var hit = images.FirstOrDefault(i => i.Index == pIdx);
                if (hit is not null) return hit;
            }
        }

        // 4. 匹配 URL 或 RelPath 或 FileName
        var exact = images.FirstOrDefault(i =>
            string.Equals(i.Url, val, StringComparison.OrdinalIgnoreCase) ||
            val.EndsWith(i.Url, StringComparison.OrdinalIgnoreCase) ||
            string.Equals(i.RelPath, val, StringComparison.OrdinalIgnoreCase) ||
            (!string.IsNullOrWhiteSpace(i.FileName) && string.Equals(i.FileName, val, StringComparison.OrdinalIgnoreCase)));
        if (exact is not null) return exact;

        // 5. 若会话中仅有 1 张图片，并且传入诸如 "image" / "latest" / "current" / "this" / "此图片" / "图片" 等代词
        if (images.Count == 1)
        {
            var lowered = val.ToLowerInvariant();
            if (lowered is "image" or "latest" or "current" or "this" or "this image" or "attachment" or "photo" or "picture" or "此图片" or "图片" or "当前图片" or "图")
            {
                return images[0];
            }
        }

        return null;
    }

    /// <summary>
    /// 在向 MCP 工具派发之前，检查参数 JSON 中是否存在图片来源字段（如 image_source、image_source_1、imageSources 等），
    /// 若包含会话图片引用，自动将其替换为标准 Base64 Data URI。
    /// </summary>
    public static string RewriteToolArguments(string? argsJson, IReadOnlyList<SessionImageAttachment> images, IChatImageStorage? storage)
    {
        if (string.IsNullOrWhiteSpace(argsJson)) return argsJson ?? "{}";

        try
        {
            var node = JsonNode.Parse(argsJson);
            if (node is not JsonObject obj) return argsJson;

            var changed = false;

            // 检查常见的单图参数名
            string[] singleSourceKeys = ["image_source", "imageSource", "image", "source", "image_source_1", "image_source_2"];
            foreach (var key in singleSourceKeys)
            {
                if (obj.TryGetPropertyValue(key, out var valNode) && valNode is not null)
                {
                    var valStr = valNode.GetValue<object?>()?.ToString();
                    if (!string.IsNullOrWhiteSpace(valStr))
                    {
                        var dataUri = ResolveToDataUri(valStr, images, storage);
                        if (dataUri is not null)
                        {
                            obj[key] = dataUri;
                            changed = true;
                        }
                    }
                }
            }

            // 检查批量图片参数名（如 batch_describe_images 的 image_sources）
            string[] arraySourceKeys = ["image_sources", "imageSources", "images"];
            foreach (var key in arraySourceKeys)
            {
                if (obj.TryGetPropertyValue(key, out var arrNode) && arrNode is JsonArray arr)
                {
                    for (var i = 0; i < arr.Count; i++)
                    {
                        var itemStr = arr[i]?.GetValue<object?>()?.ToString();
                        if (!string.IsNullOrWhiteSpace(itemStr))
                        {
                            var dataUri = ResolveToDataUri(itemStr, images, storage);
                            if (dataUri is not null)
                            {
                                arr[i] = dataUri;
                                changed = true;
                            }
                        }
                    }
                }
            }

            return changed ? obj.ToJsonString() : argsJson;
        }
        catch (JsonException)
        {
            return argsJson;
        }
    }

    /// <summary>
    /// 为内置 mcp_resources 生成会话图片资源列表。
    /// </summary>
    public static string FormatResourcesListing(IReadOnlyList<SessionImageAttachment> images)
    {
        if (images.Count == 0) return string.Empty;
        var sb = new System.Text.StringBuilder();
        sb.AppendLine("## session-attachments (Uploaded Images in this Chat)");
        foreach (var img in images)
        {
            var name = string.IsNullOrWhiteSpace(img.FileName) ? $"Image #{img.Index}" : img.FileName;
            sb.AppendLine($"- uri: `attachment:{img.Index}` | name: \"{name}\" | mime: {img.MimeType} | path: {img.RelPath}");
        }
        return sb.ToString().TrimEnd();
    }

    /// <summary>
    /// 读取会话图片资源描述文本（内置 mcp_read_resource 响应）。
    /// </summary>
    public static string? ReadResourceDescription(string uri, IReadOnlyList<SessionImageAttachment> images)
    {
        var matched = MatchAttachment(uri, images);
        if (matched is null) return null;

        var name = string.IsNullOrWhiteSpace(matched.FileName) ? $"Image #{matched.Index}" : matched.FileName;
        return $"[Session Image Attachment]\n" +
               $"- Index: {matched.Index}\n" +
               $"- Name: {name}\n" +
               $"- MIME Type: {matched.MimeType}\n" +
               $"- Relative Path: {matched.RelPath}\n" +
               $"- Access URI: attachment:{matched.Index}\n" +
               $"- Usage: To inspect, OCR, or analyze this image with vision MCP tools (e.g. get_image_info, describe_image, read_image_text, detect_image_boxes, extract_ui_layout), pass \"attachment:{matched.Index}\" as the `image_source` parameter. The runtime will resolve the image data automatically.";
    }
}
