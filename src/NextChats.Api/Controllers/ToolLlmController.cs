using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NextChats.Core.Abstractions;
using NextChats.Core.Clients;

namespace NextChats.Api.Controllers;

/// <summary>
/// 工具专用 LLM 端点（沉浸式工具调用）。
/// 与聊天链路完全隔离：**无任何持久化**（不落会话/消息/usage/审计），
/// 无状态、固定非思考模式；模型可见性沿用聊天的角色绑定白名单。
/// </summary>
[ApiController]
[Authorize]
[Route("api/tools")]
public sealed class ToolLlmController(IConfigStore config, ILlmRouter router) : ApiControllerBase
{
    private const int MaxPromptChars = 50000;
    private const int MaxSystemChars = 4000;
    /// <summary>输出上限：原文长度 5 万字符（≈7.5 万 token），译文同量级，按 1 字 ≈ 2 token 上浮留足余量</summary>
    private const int MaxOutputTokens = 200000;

    private static readonly JsonSerializerOptions SseJson = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public sealed record CompleteRequest(Guid ModelId, string? SystemPrompt, string Prompt, int? MaxTokens);

    /// <summary>无状态单轮补全：system + prompt → text（不建会话、不落库；工具侧内容仅存在于浏览器 localStorage）</summary>
    [HttpPost("llm/complete")]
    public async Task<IActionResult> Complete([FromBody] CompleteRequest req, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(req.Prompt)) return BadRequest(Err("INPUT_EMPTY"));
        if (req.Prompt.Length > MaxPromptChars) return BadRequest(Err("INPUT_TOO_LARGE", MaxPromptChars));
        if (req.SystemPrompt is { Length: > MaxSystemChars }) return BadRequest(Err("INPUT_TOO_LARGE", MaxSystemChars));

        // 与聊天一致的模型白名单语义：admin 或系统未做角色绑定时全量可用，否则仅限绑定模型
        var (_, _, _, modelIds) = await config.GetRoleBindingsAsync(UserId, ct);
        var isAdmin = await config.IsAdminAsync(UserId, ct);
        var restricted = !isAdmin && modelIds.Length > 0;

        var providers = await config.GetActiveProvidersAsync(ct);
        var model = providers.SelectMany(p => p.Models).FirstOrDefault(m => m.Id == req.ModelId && m.Enabled);
        if (model is null) return NotFound(Err("MODEL_NOT_AVAILABLE"));
        if (restricted && !modelIds.Contains(model.Id)) return StatusCode(StatusCodes.Status403Forbidden, Err("MODEL_NOT_AUTHORIZED"));

        var client = await router.SelectClientAsync(null, req.ModelId, Lang, ct, restricted ? modelIds.ToArray() : null);
        var messages = new List<LlmChatMessage>();
        if (!string.IsNullOrWhiteSpace(req.SystemPrompt)) messages.Add(LlmChatMessage.System(req.SystemPrompt));
        messages.Add(LlmChatMessage.User(req.Prompt));

        var request = new LlmRequest
        {
            Messages = messages,
            Stream = false,
            MaxTokens = Math.Clamp(req.MaxTokens ?? 2048, 1, MaxOutputTokens),
            // ThinkingEnabled 默认 false：工具调用固定非思考模式，不占用思考 token
        };
        var result = await client.CompleteAsync(request, ct);
        return Ok(new { text = result.Message.Content ?? "" });
    }

    /// <summary>
    /// 无状态流式补全（SSE）：与 Complete 相同的隔离语义，输出为增量 text_delta 事件；
    /// 供工具页（如 AI 翻译）流式渲染译文。事件：{kind:'text_delta',text} → {kind:'error',code,message} → {kind:'end'}
    /// </summary>
    [HttpPost("llm/stream")]
    public async Task Stream([FromBody] CompleteRequest req)
    {
        async Task Fail(string code, string message)
        {
            await WriteSse(new { kind = "error", code, message });
        }

        if (string.IsNullOrWhiteSpace(req.Prompt))
        {
            await Fail("INPUT_EMPTY", "");
            return;
        }
        if (req.Prompt.Length > MaxPromptChars)
        {
            await Fail("INPUT_TOO_LARGE", MaxPromptChars.ToString());
            return;
        }
        if (req.SystemPrompt is { Length: > MaxSystemChars })
        {
            await Fail("INPUT_TOO_LARGE", MaxSystemChars.ToString());
            return;
        }

        // 模型白名单语义：admin 或系统未做角色绑定时全量可用，否则仅限绑定模型
        var (_, _, _, modelIds) = await config.GetRoleBindingsAsync(UserId, HttpContext.RequestAborted);
        var isAdmin = await config.IsAdminAsync(UserId, HttpContext.RequestAborted);
        var restricted = !isAdmin && modelIds.Length > 0;

        var providers = await config.GetActiveProvidersAsync(HttpContext.RequestAborted);
        var model = providers.SelectMany(p => p.Models).FirstOrDefault(m => m.Id == req.ModelId && m.Enabled);
        if (model is null)
        {
            await Fail("MODEL_NOT_AVAILABLE", "");
            return;
        }
        if (restricted && !modelIds.Contains(model.Id))
        {
            await Fail("MODEL_NOT_AUTHORIZED", "");
            return;
        }

        var client = await router.SelectClientAsync(
            null, req.ModelId, Lang, HttpContext.RequestAborted, restricted ? modelIds.ToArray() : null);

        var messages = new List<LlmChatMessage>();
        if (!string.IsNullOrWhiteSpace(req.SystemPrompt)) messages.Add(LlmChatMessage.System(req.SystemPrompt));
        messages.Add(LlmChatMessage.User(req.Prompt));

        var request = new LlmRequest
        {
            Messages = messages,
            Stream = true,
            MaxTokens = Math.Clamp(req.MaxTokens ?? 2048, 1, MaxOutputTokens),
        };

        Response.Headers.Append("Cache-Control", "no-cache");
        Response.Headers.Append("Connection", "keep-alive");
        Response.Headers.Append("X-Accel-Buffering", "no");
        Response.ContentType = "text/event-stream; charset=utf-8";

        try
        {
            var chunkCount = 0;
            await foreach (var chunk in client.StreamAsync(request, HttpContext.RequestAborted))
            {
                if (chunk is LlmChunk.TextDelta td)
                {
                    chunkCount++;
                    // 取证：首个文本增量打点（偶现"译文缺开头几个字"时据此判定丢失发生在上游还是本链路）
                    if (chunkCount == 1)
                    {
                        var prefix = td.Text.Length <= 40 ? td.Text : td.Text[..40];
                        Serilog.Log.Information(
                            "Tool llm stream 首delta trace={TraceId} provider={Provider} prefix={Prefix} len={Len}",
                            HttpContext.TraceIdentifier, client.ProviderName, prefix, td.Text.Length);
                    }
                    await WriteSse(new { kind = "text_delta", text = td.Text });
                }
            }
        }
        catch (OperationCanceledException)
        {
            await WriteSse(new { kind = "error", code = "INTERRUPTED", message = "" });
        }
        catch (Exception ex)
        {
            Serilog.Log.Error(ex, "Tool llm stream 异常 TraceId={TraceId}", HttpContext.TraceIdentifier);
            try
            {
                await WriteSse(new { kind = "error", code = "STREAM_ERROR", message = "" });
            }
            catch
            {
                /* 客户端已断开 */
            }
        }
        finally
        {
            try
            {
                await WriteSse(new { kind = "end" });
            }
            catch
            {
                /* 客户端已断开 */
            }
        }
    }

    private async Task WriteSse(object payload)
    {
        var json = JsonSerializer.Serialize(payload, SseJson);
        await Response.WriteAsync($"data: {json}\n\n");
        await Response.Body.FlushAsync(HttpContext.RequestAborted);
    }
}
