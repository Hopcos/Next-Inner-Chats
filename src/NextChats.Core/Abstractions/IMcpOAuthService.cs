using NextChats.Core.Entities;

namespace NextChats.Core.Abstractions;

/// <summary>
/// MCP OAuth 2.1 集成异常（浏览器授权 / PKCE / 令牌刷新）：
/// Code 用于对外结构化错误（如 OAUTH_NOT_CONFIGURED / OAUTH_REAUTHORIZE / OAUTH_TOKEN_FAILED）。
/// </summary>
public sealed class McpOAuthException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;

    public static McpOAuthException NotConfigured() =>
        new("OAUTH_NOT_CONFIGURED", "OAuth is not configured for this MCP server; run discovery/authorize first.");

    public static McpOAuthException Reauthorize() =>
        new("OAUTH_REAUTHORIZE", "OAuth access token expired and no refresh token is available; re-authorize in the admin console.");

    public static McpOAuthException TokenFailed(string detail) =>
        new("OAUTH_TOKEN_FAILED", $"OAuth token request failed: {detail}");
}

/// <summary>OAuth 配置（authorization server 元数据 + 动态注册结果），落库时整体加密。</summary>
public sealed record McpOAuthConfig
{
    public string? Resource { get; init; }
    public string? Issuer { get; init; }
    public string? AuthorizationEndpoint { get; init; }
    public string? TokenEndpoint { get; init; }
    public string? RegistrationEndpoint { get; init; }
    public string? ClientId { get; init; }
    public string? ClientSecret { get; init; }
    public string[] Scopes { get; init; } = [];
    public string? RedirectUri { get; init; }
    public string? TokenEndpointAuthMethod { get; init; }
    public string? CodeChallengeMethod { get; init; }
}

/// <summary>OAuth 令牌（access/refresh + 过期时间），落库时整体加密。</summary>
public sealed record McpOAuthToken
{
    public string? AccessToken { get; init; }
    public string? RefreshToken { get; init; }
    public string? TokenType { get; init; } = "Bearer";
    public long ExpiresAt { get; init; }
    public string? Scope { get; init; }
}

/// <summary>授权状态（管理端展示用）。</summary>
public sealed record McpOAuthStatus
{
    public bool Authorized { get; init; }
    public DateTimeOffset? AuthorizedAt { get; init; }
    public long? ExpiresAt { get; init; }
    public string? Scopes { get; init; }
    public string? ClientId { get; init; }
    public string? AuthorizationEndpoint { get; init; }
    public string? TokenEndpoint { get; init; }
    public string? RegistrationEndpoint { get; init; }
    public string? RedirectUri { get; init; }
}

/// <summary>
/// MCP OAuth 2.1（RFC 9728 保护资源元数据 + RFC 8414 授权服务器元数据 + RFC 7591 动态客户端注册
/// + RFC 7636 PKCE + 令牌刷新）。用于远程 MCP 服务器（如 Coralogix）的浏览器授权集成。
/// </summary>
public interface IMcpOAuthService
{
    /// <summary>发现授权服务器元数据；若存在 registration_endpoint 且尚未注册，则自动动态注册客户端。返回保存后的配置。</summary>
    Task<McpOAuthConfig> DiscoverAsync(McpServer server, string redirectUri, CancellationToken ct = default);

    /// <summary>生成浏览器授权 URL（含 state + PKCE），待用户登录并点击 Authorize。</summary>
    Task<string> StartAuthorizationAsync(McpServer server, string redirectUri, CancellationToken ct = default);

    /// <summary>回调：用授权码换取令牌并加密保存到 McpServer。</summary>
    Task<(bool Ok, string Message)> CompleteAuthorizationAsync(McpServer server, string code, string state, CancellationToken ct = default);

    /// <summary>取有效 access token；过期时用 refresh_token 自动刷新。无 refresh 时抛 OAUTH_REAUTHORIZE。</summary>
    Task<string> GetAccessTokenAsync(McpServer server, CancellationToken ct = default);

    /// <summary>清除令牌（撤销授权）。</summary>
    Task RevokeAsync(McpServer server, CancellationToken ct = default);

    /// <summary>读取当前授权状态与配置（对外展示，secret 不返回）。</summary>
    McpOAuthStatus GetStatus(McpServer server);
}
