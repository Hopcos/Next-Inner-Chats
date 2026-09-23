using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NextChats.Core.Abstractions;
using NextChats.Core.Agents;
using NextChats.Core.Domain;
using NextChats.Core.Localization;

namespace NextChats.Api.Controllers;

[ApiController]
[Authorize]
[Route("api/chat")]
public sealed class ChatController(
    IChatStore chat,
    IChatOrchestrator orchestrator,
    ISessionCancellationRegistry cancellations,
    NextChats.Core.Abstractions.IConfigStore config,
    IAuditLogger audit,
    NextChats.Api.Services.ChatImageStorage images) : ApiControllerBase
{
    private static readonly JsonSerializerOptions SseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>图片附件（标准 base64；MCP 视觉工具参数名 image_source）</summary>
    public sealed record ImageInput(string? FileName, string? MimeType, string Base64);

    /// <summary>
    /// 把请求图片 base64 落盘（uploads/chat），返回持久化附件 JSON（[{fileName,mimeType,url}]），
    /// 随用户消息入库；保存失败（非法/IO）的图跳过并记日志，不阻塞对话。
    /// </summary>
    private string? PersistAttachments(List<ImageInput>? requestImages)
    {
        if (requestImages is not { Count: > 0 }) return null;
        var list = new List<object>();
        foreach (var img in requestImages)
        {
            var rel = images.SaveImage(img.MimeType, img.Base64, img.FileName);
            if (rel is null) continue;
            list.Add(new { img.FileName, img.MimeType, url = $"/api/chat/images/{rel}" });
        }
        return list.Count > 0 ? JsonSerializer.Serialize(list, SseJson) : null;
    }

    /// <summary>图片读取端点：/api/chat/images/{yyyyMM}/{guid}.{ext}（登录鉴权 + 缓存头）</summary>
    [HttpGet("images/{**name}")]
    public IActionResult Image(string name)
    {
        var img = images.ReadImage(name);
        if (img is null) return NotFound(Err("IMAGE_NOT_FOUND", name));
        Response.Headers.CacheControl = "public, max-age=3600";
        return File(img.Value.Bytes, img.Value.Mime);
    }

    public sealed record StreamRequest(
        Guid SessionId,
        string Message,
        string? ClientMessageId,
        Guid? ProviderId,
        Guid? ModelId,
        Guid? PromptId,
        List<Guid>? McpServerIds,
        List<Guid>? SkillIds,
        List<ImageInput>? Images,
        bool? ThinkingEnabled,
        string? ThinkingEffort,
        Guid? RegenerateFromMessageId = null);

    // ---------------- 视觉能力判断（模型视觉 ∨ MCP 视觉） ----------------

    /// <summary>当前聊天是否支持图片（所选模型支持视觉，或绑定的 MCP 含视觉支持）</summary>
    [HttpGet("vision-config")]
    public async Task<IActionResult> VisionConfig()
    {
        var settings = await config.GetUserSettingsAsync(UserId);
        var preferredId = ParseGuid(GetSetting(settings, "chat.providerId"));
        var preferredModelId = ParseGuid(GetSetting(settings, "chat.modelId"));
        var providers = await config.GetActiveProvidersAsync();
        var provider = providers.FirstOrDefault(p => p.Id == preferredId && p.Enabled)
            ?? providers.Where(p => p.Enabled && p.IsHealthy).OrderBy(p => p.Priority).FirstOrDefault();

        // 模型级视觉判断：所选模型 → 供应商内启用模型任一支持
        var selectedModel = provider?.Models.FirstOrDefault(m => m.Id == (preferredModelId ?? Guid.Empty))
            ?? provider?.Models.Where(m => m.Enabled).OrderBy(m => m.Priority).FirstOrDefault();
        var providerVision = provider is not null && (selectedModel?.IsVision == true || provider.Models.Any(m => m.IsVision));

        var (roleMcpIds, _, _, _) = await config.GetRoleBindingsAsync(UserId);
        var servers = (await config.GetEnabledMcpServersAsync())
            .Where(s => s.Enabled && roleMcpIds.Contains(s.Id))
            .ToList();

        return Ok(new
        {
            supported = providerVision || servers.Any(s => s.IsVision),
            providerVision,
            mcpVision = servers.Any(s => s.IsVision),
            maxImages = 6,
            maxImageBase64Chars = 5_000_000,
            acceptedMimeTypes = new[] { "image/png", "image/jpeg", "image/gif", "image/webp" },
            providerName = provider?.Name,
            modelName = selectedModel?.Name,
        });
    }

    private static string? GetSetting(IDictionary<string, string> settings, string key) =>
        settings.TryGetValue(key, out var value) ? value : null;

    private static Guid? ParseGuid(string? s) => Guid.TryParse(s, out var g) ? g : null;

    // ---------------- 会话管理 ----------------

    [HttpGet("sessions")]
    public async Task<IActionResult> Sessions() => Ok(await chat.ListSessionsAsync(UserId));

    [HttpPost("sessions")]
    public async Task<IActionResult> CreateSession([FromBody] CreateSessionRequest? req)
    {
        // 默认标题留空：前端按用户语言显示 “未命名/Untitled”，且首条消息时自动以前文命名（避免硬编码中文“新会话”）
        var session = await chat.CreateSessionAsync(UserId, req?.Title?.Trim() ?? "");
        await audit.RecordAsync(AuditCategory.Chat, "SESSION.CREATE", $"trc_{Guid.NewGuid():N}"[..24], UserId, session.Id.ToString());
        return Ok(session);
    }

    public sealed record CreateSessionRequest(string? Title);

    [HttpPut("sessions/{sessionId:guid}")]
    public async Task<IActionResult> RenameSession(Guid sessionId, [FromBody] RenameSessionRequest req)
    {
        var ok = await chat.RenameSessionAsync(UserId, sessionId, req.Title);
        if (!ok) return NotFound(Err("SESSION_NOT_FOUND"));
        await audit.RecordAsync(AuditCategory.Chat, "SESSION.RENAME", $"trc_{Guid.NewGuid():N}"[..24], UserId, sessionId.ToString());
        return NoContent();
    }

    public sealed record RenameSessionRequest(string? Title);

    /// <summary>置顶 / 取消置顶会话（置顶后会话出现在侧栏顶部的置顶区域）</summary>
    [HttpPut("sessions/{sessionId:guid}/pin")]
    public async Task<IActionResult> PinSession(Guid sessionId, [FromBody] PinSessionRequest req)
    {
        var ok = await chat.SetSessionPinnedAsync(UserId, sessionId, req.Pinned, HttpContext.RequestAborted);
        if (!ok) return NotFound(Err("SESSION_NOT_FOUND"));
        await audit.RecordAsync(AuditCategory.Chat, req.Pinned ? "SESSION.PIN" : "SESSION.UNPIN",
            $"trc_{Guid.NewGuid():N}"[..24], UserId, sessionId.ToString());
        return NoContent();
    }

    public sealed record PinSessionRequest(bool Pinned);

    [HttpDelete("sessions/{sessionId:guid}")]
    public async Task<IActionResult> DeleteSession(Guid sessionId)
    {
        var toDelete = await CollectSessionAttachmentsAsync(sessionId);
        await chat.DeleteSessionAsync(UserId, sessionId);
        images.DeleteImages(toDelete);
        await audit.RecordAsync(AuditCategory.Chat, "SESSION.DELETE", $"trc_{Guid.NewGuid():N}"[..24], UserId, sessionId.ToString());
        return NoContent();
    }

    /// <summary>删除指定消息及其之后的所有消息（截断会话；用于话题级“删除”与“重新生成”）</summary>
    [HttpDelete("sessions/{sessionId:guid}/messages/{messageId:guid}")]
    public async Task<IActionResult> DeleteMessage(Guid sessionId, Guid messageId)
    {
        var toDelete = await CollectSessionAttachmentsAsync(sessionId, fromMessageId: messageId);
        var ok = await chat.TruncateFromMessageAsync(UserId, sessionId, messageId);
        if (toDelete.Count > 0) images.DeleteImages(toDelete);
        return ok ? NoContent() : NotFound(Err("MESSAGE_NOT_FOUND"));
    }

    /// <summary>收集会话消息附件文件相对路径（删会话全量；删消息窗口取 fromMessageId 起的消息），供删除时清理。</summary>
    private async Task<List<string>> CollectSessionAttachmentsAsync(Guid sessionId, Guid? fromMessageId = null)
    {
        var result = new List<string>();
        try
        {
            var messages = await chat.ListMessagesAsync(UserId, sessionId, HttpContext.RequestAborted);
            if (fromMessageId.HasValue)
            {
                var start = messages.ToList().FindIndex(m => m.Id == fromMessageId.Value);
                if (start < 0) return result;
                messages = messages.Skip(start).ToList();
            }
            foreach (var m in messages)
            {
                if (string.IsNullOrWhiteSpace(m.AttachmentsJson)) continue;
                try
                {
                    using var doc = JsonDocument.Parse(m.AttachmentsJson);
                    foreach (var el in doc.RootElement.EnumerateArray())
                    {
                        if (el.TryGetProperty("url", out var urlEl) && urlEl.ValueKind == JsonValueKind.String)
                        {
                            var url = urlEl.GetString();
                            if (url is not null && url.StartsWith("/api/chat/images/", StringComparison.Ordinal))
                            {
                                result.Add(url["/api/chat/images/".Length..]);
                            }
                        }
                    }
                }
                catch (JsonException) { /* 忽略坏数据 */ }
            }
        }
        catch (Exception ex)
        {
            Serilog.Log.Warning(ex, "收集会话图片附件失败 session={Session}", sessionId);
        }
        return result;
    }

    [HttpGet("sessions/{sessionId:guid}/messages")]
    public async Task<IActionResult> Messages(Guid sessionId, [FromQuery] int? limit = null, [FromQuery] Guid? beforeId = null)
    {
        var session = await chat.GetSessionAsync(UserId, sessionId);
        if (session is null) return NotFound(Err("SESSION_NOT_FOUND"));
        if (limit.HasValue)
        {
            var n = Math.Clamp(limit.Value, 1, 200);
            var items = await chat.ListMessagesWindowAsync(UserId, sessionId, n, beforeId, HttpContext.RequestAborted);
            // more：返回条数等于窗口大小，说明可能还有更早的消息
            return Ok(new { items, more = items.Count == n });
        }
        return Ok(await chat.ListMessagesAsync(UserId, sessionId));
    }

    /// <summary>会话话题索引（user 提问：id + 原文，正序）—— 话题导航条全量渲染用</summary>
    [HttpGet("sessions/{sessionId:guid}/topics")]
    public async Task<IActionResult> Topics(Guid sessionId)
    {
        var session = await chat.GetSessionAsync(UserId, sessionId);
        if (session is null) return NotFound(Err("SESSION_NOT_FOUND"));
        return Ok(await chat.ListTopicsAsync(UserId, sessionId, HttpContext.RequestAborted));
    }

    // ---------------- 流式对话（SSE） ----------------

    [HttpPost("stream")]
    public async Task Stream([FromBody] StreamRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.Message) && (request.Images is not { Count: > 0 }))
        {
            await WriteSse(AgentEvent.Error("EMPTY_MESSAGE", Texts.Get("EMPTY_MESSAGE", Lang), ""));
            return;
        }

        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");
        Response.ContentType = "text/event-stream; charset=utf-8";

        // 图片落盘 + 生成附件 JSON（随用户消息持久化，刷新/切会话后仍能展示）；失败不阻塞对话
        var attachmentsJson = PersistAttachments(request.Images);

        var req = new ChatStreamRequest
        {
            UserId = UserId,
            SessionId = request.SessionId,
            UserInput = request.Message,
            Images = request.Images?.Select(i => new ImageAttachment
            {
                FileName = i.FileName,
                MimeType = i.MimeType,
                Base64 = i.Base64,
            }).ToList(),
            ClientMessageId = request.ClientMessageId,
            ProviderId = request.ProviderId,
            ModelId = request.ModelId,
            PromptId = request.PromptId,
            McpServerIds = request.McpServerIds,
            SkillIds = request.SkillIds,
            ThinkingEnabled = request.ThinkingEnabled,
            ThinkingEffort = request.ThinkingEffort,
            RegenerateFromMessageId = request.RegenerateFromMessageId,
            Lang = Lang,
            AttachmentsJson = attachmentsJson,
        };

        try
        {
            await foreach (var ev in orchestrator.StreamAsync(req, HttpContext.RequestAborted))
            {
                await WriteSse(ev);
            }
        }
        catch (OperationCanceledException)
        {
            await WriteSse(AgentEvent.Error("INTERRUPTED", Texts.Get("INTERRUPTED", Lang), ""));
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Chat stream 异常 TraceId={TraceId}", HttpContext.TraceIdentifier);
            await WriteSse(AgentEvent.Error("STREAM_ERROR", Texts.Get("STREAM_ERROR", Lang), ""));
        }
        finally
        {
            await WriteSse(new { kind = "end" });
        }
    }

    /// <summary>中断按钮：LLM 推理过程中取消当前会话的推理</summary>
    [HttpPost("sessions/{sessionId:guid}/interrupt")]
    public IActionResult Interrupt(Guid sessionId)
    {
        var cancelled = cancellations.Cancel(UserId, sessionId);
        return Ok(new { cancelled });
    }

    private async Task WriteSse(object payload)
    {
        var json = JsonSerializer.Serialize(payload, SseJson);
        await Response.WriteAsync($"data: {json}\n\n");
        await Response.Body.FlushAsync();
    }
}
