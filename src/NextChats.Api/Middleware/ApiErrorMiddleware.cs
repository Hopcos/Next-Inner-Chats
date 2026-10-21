using System.Text.Json;
using Microsoft.AspNetCore.Http;
using NextChats.Core.Localization;
using Serilog;

namespace NextChats.Api.Middleware;

/// <summary>
/// 统一错误处理：对用户只返回友好文案 + 错误码（不暴露 stack / Endpoint / Header）；
/// 完整上下文（含堆栈）写入日志，并通过日志脱敏组件掩盖敏感信息。
/// </summary>
public sealed class ApiErrorMiddleware(RequestDelegate next)
{
    public async Task InvokeAsync(HttpContext context)
    {
        try
        {
            await next(context);
        }
        catch (Exception ex)
        {
            var code = ex switch
            {
                UnauthorizedAccessException => "FORBIDDEN",
                KeyNotFoundException => "NOT_FOUND",
                InvalidOperationException => "BAD_REQUEST",
                NextChats.Core.Services.LlmUnavailableException => "LLM_UNAVAILABLE",
                // 上游 LLM 网关错误（4xx/5xx）业务上不是本服务故障：归 502 语义，不污染 500 监控
                NextChats.Core.Clients.LlmHttpException => "LLM_ERROR",
                // 网络层失败（连接拒绝/重置等）同样归因上游可达性，而非本服务内部错误
                System.Net.Http.HttpRequestException => "LLM_ERROR",
                _ => "INTERNAL_ERROR",
            };

            Log.Error(ex, "Unhandled exception Path={Path} TraceId={TraceId} UserId={UserId}",
                context.Request.Path, context.TraceIdentifier, context.User.FindFirst("uid")?.Value);

            if (context.Response.HasStarted)
            {
                return; // SSE 流中已写入头，交由流内错误码处理
            }

            context.Response.StatusCode = code switch
            {
                "FORBIDDEN" => 403,
                "NOT_FOUND" => 404,
                "LLM_UNAVAILABLE" => 503,
                "LLM_ERROR" => 502,
                _ => 500,
            };
            context.Response.ContentType = "application/json; charset=utf-8";
            var lang = LanguageOf(context);
            var payload = JsonSerializer.Serialize(new { code, message = Texts.Get(code, lang) });
            await context.Response.WriteAsync(payload);
        }
    }

    /// <summary>从请求头解析语言（X-Lang > Accept-Language 前缀）</summary>
    private static string LanguageOf(HttpContext ctx)
    {
        var header = ctx.Request.Headers["X-Lang"].ToString();
        if (string.IsNullOrWhiteSpace(header))
        {
            header = ctx.Request.Headers.AcceptLanguage.ToString();
        }
        return header;
    }
}
