using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NextChats.Core.Abstractions;
using NextChats.Core.Entities;
using NextChats.Core.Domain;

namespace NextChats.Core.Services;

/// <summary>
/// MCP OAuth 2.1 集成实现：
///  - RFC 9728 保护资源元数据发现（/.well-known/oauth-protected-resource[/path]）
///  - RFC 8414 授权服务器元数据发现（/.well-known/oauth-authorization-server）
///  - RFC 7591 动态客户端注册（registration_endpoint）
///  - RFC 7636 PKCE（S256）浏览器授权码流程
///  - refresh_token 自动刷新；令牌与配置 AES-GCM 加密落库（复用 ISecurityService）
/// </summary>
public sealed class McpOAuthService : IMcpOAuthService
{
    private sealed record PendingAuthorization(Guid ServerId, string Verifier, string RedirectUri, DateTimeOffset CreatedAt);

    private static readonly ConcurrentDictionary<string, PendingAuthorization> Pending = new();
    private static readonly SemaphoreSlim RefreshGate = new(1, 1);
    private static readonly TimeSpan PendingTtl = TimeSpan.FromMinutes(15);

    private readonly HttpClient _client;
    private readonly ISecurityService _security;
    private readonly IAdminStore _store;
    private readonly ILogger _logger;

    public McpOAuthService(IHttpClientProvider http, ISecurityService security, IAdminStore store, ILoggerFactory loggerFactory)
    {
        _client = http.Create("mcp");
        _security = security;
        _store = store;
        _logger = loggerFactory.CreateLogger<McpOAuthService>();
    }

    // ---------------------------------------------------------------- 状态查询

    public McpOAuthStatus GetStatus(McpServer server)
    {
        var cfg = LoadConfig(server);
        var tok = LoadTokens(server);
        return new McpOAuthStatus
        {
            Authorized = !string.IsNullOrWhiteSpace(tok?.AccessToken) || server.OAuthAuthorizedAt is not null,
            AuthorizedAt = server.OAuthAuthorizedAt,
            ExpiresAt = tok?.ExpiresAt > 0 ? tok.ExpiresAt : null,
            Scopes = tok?.Scope,
            ClientId = cfg?.ClientId,
            AuthorizationEndpoint = cfg?.AuthorizationEndpoint,
            TokenEndpoint = cfg?.TokenEndpoint,
            RegistrationEndpoint = cfg?.RegistrationEndpoint,
            RedirectUri = cfg?.RedirectUri,
        };
    }

    // ---------------------------------------------------------------- 发现 + 注册

    public async Task<McpOAuthConfig> DiscoverAsync(McpServer server, string redirectUri, CancellationToken ct = default)
    {
        if (server.Transport != McpTransportType.Http || string.IsNullOrWhiteSpace(server.Endpoint))
        {
            throw new McpOAuthException("OAUTH_ENDPOINT_REQUIRED", "OAuth requires an HTTP transport with an MCP endpoint.");
        }

        var (resourceMeta, authMeta) = await DiscoverMetadataAsync(server, ct);

        var resource = resourceMeta is null ? server.Endpoint : (Str(resourceMeta, "resource") ?? server.Endpoint);
        var issuer = authMeta is null ? null : Str(authMeta, "issuer");
        var authEndpoint = authMeta is null ? null : Str(authMeta, "authorization_endpoint");
        var tokenEndpoint = authMeta is null ? null : Str(authMeta, "token_endpoint");
        var registrationEndpoint = authMeta is null ? null : Str(authMeta, "registration_endpoint");

        if (string.IsNullOrWhiteSpace(authEndpoint) || string.IsNullOrWhiteSpace(tokenEndpoint))
        {
            throw new McpOAuthException("OAUTH_DISCOVERY_FAILED",
                "Authorization server metadata was not discovered. Provide the authorization/token endpoints manually or check the server endpoint.");
        }

        var scopes = ReadScopes(resourceMeta) ?? ReadScopes(authMeta) ?? [];
        var authMethod = ChooseTokenAuthMethod(authMeta);
        var challengeMethod = ChooseChallengeMethod(authMeta);

        // 动态客户端注册（RFC 7591）：无 client_id 或授权端点/回调地址变化时注册；一致则复用
        var existing = LoadConfig(server);
        string? clientId = existing?.ClientId;
        string? clientSecret = existing?.ClientSecret;
        var reuse = !string.IsNullOrWhiteSpace(clientId)
            && string.Equals(existing!.AuthorizationEndpoint, authEndpoint, StringComparison.Ordinal)
            && string.Equals(existing.RedirectUri, redirectUri, StringComparison.Ordinal);
        if (!reuse && !string.IsNullOrWhiteSpace(registrationEndpoint))
        {
            (clientId, clientSecret) = await RegisterAsync(registrationEndpoint!, redirectUri, scopes, authMethod, ct);
        }

        var cfg = new McpOAuthConfig
        {
            Resource = resource,
            Issuer = issuer,
            AuthorizationEndpoint = authEndpoint,
            TokenEndpoint = tokenEndpoint,
            RegistrationEndpoint = registrationEndpoint,
            ClientId = clientId,
            ClientSecret = clientSecret,
            Scopes = scopes,
            RedirectUri = redirectUri,
            TokenEndpointAuthMethod = authMethod,
            CodeChallengeMethod = challengeMethod,
        };
        SaveConfig(server, cfg);
        await _store.UpdateMcpServerAsync(server, ct);
        _logger.LogInformation("MCP OAuth 发现完成 server={Server} auth={AuthEndpoint} clientId={ClientId}",
            server.Name, cfg.AuthorizationEndpoint, cfg.ClientId);
        return cfg;
    }

    // ---------------------------------------------------------------- 浏览器授权（start / callback）

    public async Task<string> StartAuthorizationAsync(McpServer server, string redirectUri, CancellationToken ct = default)
    {
        var cfg = LoadConfig(server);
        // 回调地址/端口变化（如 localhost:6510 → localhost:6500）会让原注册失效，需重新发现并按新回调注册
        if (cfg is null
            || string.IsNullOrWhiteSpace(cfg.AuthorizationEndpoint)
            || string.IsNullOrWhiteSpace(cfg.ClientId)
            || !string.Equals(cfg.RedirectUri, redirectUri, StringComparison.Ordinal))
        {
            cfg = await DiscoverAsync(server, redirectUri, ct);
        }

        if (string.IsNullOrWhiteSpace(cfg.AuthorizationEndpoint) || string.IsNullOrWhiteSpace(cfg.ClientId))
        {
            throw McpOAuthException.NotConfigured();
        }

        var verifier = RandomUrlSafe(32);
        var challenge = Base64Url(SHA256.HashData(Encoding.UTF8.GetBytes(verifier)));
        var state = RandomUrlSafe(24);
        Pending[state] = new PendingAuthorization(server.Id, verifier, redirectUri, DateTimeOffset.UtcNow);

        var query = new List<string>
        {
            $"response_type=code",
        $"client_id={Uri.EscapeDataString(cfg.ClientId!)}",
            $"redirect_uri={Uri.EscapeDataString(redirectUri)}",
            $"state={Uri.EscapeDataString(state)}",
            $"code_challenge={Uri.EscapeDataString(challenge)}",
            $"code_challenge_method=S256",
        };
        if (!string.IsNullOrWhiteSpace(cfg.CodeChallengeMethod) && !string.Equals(cfg.CodeChallengeMethod, "S256", StringComparison.OrdinalIgnoreCase))
        {
            // 服务器不支持 S256 时由上层拒绝（这里保持 S256，OAuth 2.1 强制要求）
        }
        if (cfg.Scopes is { Length: > 0 }) query.Add($"scope={Uri.EscapeDataString(string.Join(" ", cfg.Scopes))}");
        if (!string.IsNullOrWhiteSpace(cfg.Resource)) query.Add($"resource={Uri.EscapeDataString(cfg.Resource!)}");

        var sep = cfg.AuthorizationEndpoint!.Contains('?') ? "&" : "?";
        return cfg.AuthorizationEndpoint + sep + string.Join("&", query);
    }

    public async Task<(bool Ok, string Message)> CompleteAuthorizationAsync(McpServer server, string code, string state, CancellationToken ct = default)
    {
        CleanupPending();
        if (string.IsNullOrWhiteSpace(code) || string.IsNullOrWhiteSpace(state))
        {
            return (false, "missing_code_or_state");
        }
        if (!Pending.TryRemove(state, out var pending) || pending.ServerId != server.Id || pending.CreatedAt < DateTimeOffset.UtcNow - PendingTtl)
        {
            return (false, "invalid_state");
        }

        var cfg = LoadConfig(server);
        if (cfg is null || string.IsNullOrWhiteSpace(cfg.TokenEndpoint) || string.IsNullOrWhiteSpace(cfg.ClientId))
        {
            return (false, "not_configured");
        }

        var form = new Dictionary<string, string>
        {
            ["grant_type"] = "authorization_code",
            ["code"] = code,
            ["redirect_uri"] = pending.RedirectUri,
            ["client_id"] = cfg.ClientId!,
            ["code_verifier"] = pending.Verifier,
        };
        if (!string.IsNullOrWhiteSpace(cfg.Resource)) form["resource"] = cfg.Resource!;
        if (!string.IsNullOrWhiteSpace(cfg.ClientSecret)) form["client_secret"] = cfg.ClientSecret!;

        var json = await PostTokenAsync(cfg.TokenEndpoint!, form, ct);
        var token = ParseToken(json);
        if (string.IsNullOrWhiteSpace(token.AccessToken))
        {
            return (false, "no_access_token");
        }

        SaveTokens(server, token);
        server.OAuthAuthorizedAt = DateTimeOffset.UtcNow;
        await _store.UpdateMcpServerAsync(server, ct);
        _logger.LogInformation("MCP OAuth 授权成功 server={Server} expiresAt={ExpiresAt}", server.Name, token.ExpiresAt);
        return (true, "ok");
    }

    // ---------------------------------------------------------------- 令牌获取 / 刷新

    public async Task<string> GetAccessTokenAsync(McpServer server, CancellationToken ct = default)
    {
        var cfg = LoadConfig(server) ?? throw McpOAuthException.NotConfigured();
        var token = LoadTokens(server);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        if (token is { AccessToken.Length: > 0 } && token.ExpiresAt > now + 60)
        {
            return token.AccessToken!;
        }

        await RefreshGate.WaitAsync(ct);
        try
        {
            // 二次检查：等锁期间可能已被其它请求刷新
            cfg = LoadConfig(server) ?? cfg;
            token = LoadTokens(server);
            now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
            if (token is { AccessToken.Length: > 0 } && token.ExpiresAt > now + 60)
            {
                return token.AccessToken!;
            }

            if (token is { RefreshToken.Length: > 0 } && !string.IsNullOrWhiteSpace(cfg.TokenEndpoint))
            {
                var form = new Dictionary<string, string>
                {
                    ["grant_type"] = "refresh_token",
                    ["refresh_token"] = token.RefreshToken!,
                    ["client_id"] = cfg.ClientId ?? string.Empty,
                };
                if (!string.IsNullOrWhiteSpace(cfg.ClientSecret)) form["client_secret"] = cfg.ClientSecret!;
                try
                {
                    var json = await PostTokenAsync(cfg.TokenEndpoint!, form, ct);
                    var refreshed = ParseToken(json);
                    if (!string.IsNullOrWhiteSpace(refreshed.AccessToken))
                    {
                        SaveTokens(server, new McpOAuthToken
                        {
                            AccessToken = refreshed.AccessToken,
                            RefreshToken = refreshed.RefreshToken ?? token.RefreshToken,
                            TokenType = refreshed.TokenType ?? token.TokenType,
                            ExpiresAt = refreshed.ExpiresAt,
                            Scope = refreshed.Scope ?? token.Scope,
                        });
                        await _store.UpdateMcpServerAsync(server, ct);
                        return refreshed.AccessToken!;
                    }
                }
                catch (McpOAuthException ex)
                {
                    _logger.LogWarning("MCP OAuth 刷新失败 server={Server} code={Code}", server.Name, ex.Code);
                }
            }

            throw McpOAuthException.Reauthorize();
        }
        finally
        {
            RefreshGate.Release();
        }
    }

    public async Task RevokeAsync(McpServer server, CancellationToken ct = default)
    {
        server.OAuthTokensJson = null;
        server.IsOAuthTokensEncrypted = false;
        server.OAuthAuthorizedAt = null;
        await _store.UpdateMcpServerAsync(server, ct);
    }

    // ---------------------------------------------------------------- 元数据发现

    private async Task<(JsonObject? ResourceMeta, JsonObject? AuthMeta)> DiscoverMetadataAsync(McpServer server, CancellationToken ct)
    {
        var mcpUri = new Uri(server.Endpoint!);
        var origin = $"{mcpUri.Scheme}://{mcpUri.Authority}";
        var path = mcpUri.AbsolutePath.Trim('/');

        // RFC 9728：保护资源元数据（path 变体优先，其次 origin 根）
        JsonObject? resourceMeta = null;
        var resourceCandidates = new List<string>();
        if (path.Length > 0) resourceCandidates.Add($"{origin}/.well-known/oauth-protected-resource/{path}");
        resourceCandidates.Add($"{origin}/.well-known/oauth-protected-resource");
        foreach (var candidate in resourceCandidates)
        {
            resourceMeta = await TryGetJsonAsync(candidate, ct);
            if (resourceMeta is not null) break;
        }

        // RFC 8414：授权服务器元数据
        var asUrls = new List<string>();
        if (resourceMeta?["authorization_servers"] is JsonArray servers)
        {
            foreach (var item in servers)
            {
                var url = item is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : null;
                if (!string.IsNullOrWhiteSpace(url)) asUrls.Add(url);
            }
        }
        if (asUrls.Count == 0) asUrls.Add(origin);

        JsonObject? authMeta = null;
        foreach (var asUrl in asUrls)
        {
            foreach (var candidate in AuthorizationServerMetadataUrls(asUrl))
            {
                authMeta = await TryGetJsonAsync(candidate, ct);
                if (authMeta is not null) break;
            }
            if (authMeta is not null) break;
        }

        if (authMeta is null && !string.IsNullOrWhiteSpace(server.Endpoint))
        {
            // 兜底：MCP 端点本身可能提供 authorization_endpoint / token_endpoint（少见）
            authMeta = await TryGetJsonAsync($"{origin}/.well-known/oauth-authorization-server", ct);
        }
        return (resourceMeta, authMeta);
    }

    private static IEnumerable<string> AuthorizationServerMetadataUrls(string asUrl)
    {
        var uri = new Uri(asUrl);
        var origin = $"{uri.Scheme}://{uri.Authority}";
        var path = uri.AbsolutePath.Trim('/');
        yield return $"{origin}/.well-known/oauth-authorization-server" + (path.Length > 0 ? $"/{path}" : string.Empty);
        yield return $"{asUrl.TrimEnd('/')}/.well-known/oauth-authorization-server";
        yield return $"{asUrl.TrimEnd('/')}/.well-known/openid-configuration";
        yield return $"{origin}/.well-known/openid-configuration" + (path.Length > 0 ? $"/{path}" : string.Empty);
    }

    private async Task<JsonObject?> TryGetJsonAsync(string url, CancellationToken ct)
    {
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.Accept.ParseAdd("application/json");
            using var res = await _client.SendAsync(req, ct);
            if (!res.IsSuccessStatusCode) return null;
            var text = await res.Content.ReadAsStringAsync(ct);
            return JsonNode.Parse(text) as JsonObject;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "OAuth 元数据探测失败 url={Url}", url);
            return null;
        }
    }

    private async Task<(string? ClientId, string? ClientSecret)> RegisterAsync(string registrationEndpoint, string redirectUri, string[] scopes, string authMethod, CancellationToken ct)
    {
        var body = new JsonObject
        {
            ["client_name"] = "NextChats",
            ["redirect_uris"] = new JsonArray(JsonValue.Create(redirectUri)!),
            ["grant_types"] = new JsonArray(JsonValue.Create("authorization_code")!, JsonValue.Create("refresh_token")!),
            ["response_types"] = new JsonArray(JsonValue.Create("code")!),
            ["token_endpoint_auth_method"] = authMethod,
        };
        if (scopes.Length > 0) body["scope"] = string.Join(" ", scopes);

        using var req = new HttpRequestMessage(HttpMethod.Post, registrationEndpoint)
        {
            Content = new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json"),
        };
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await _client.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            // 尽量透出提供方的错误原因（如 invalid_redirect_uri），便于定位回调地址不合法
            JsonObject? errorJson = null;
            try { errorJson = JsonNode.Parse(text) as JsonObject; } catch { /* 非 JSON 错误页 */ }
            var detail = string.Join(": ", new[] { Str(errorJson, "error"), Str(errorJson, "error_description") }
                .Where(s => !string.IsNullOrWhiteSpace(s)));
            throw new McpOAuthException("OAUTH_REGISTRATION_FAILED",
                $"Dynamic client registration failed: HTTP {(int)res.StatusCode}{(detail.Length > 0 ? $" ({detail})" : string.Empty)} — redirect_uri={redirectUri}");
        }
        if (JsonNode.Parse(text) is not JsonObject json)
        {
            throw new McpOAuthException("OAUTH_REGISTRATION_FAILED", "Dynamic client registration returned malformed JSON.");
        }
        var clientId = Str(json, "client_id");
        if (string.IsNullOrWhiteSpace(clientId))
        {
            throw new McpOAuthException("OAUTH_REGISTRATION_FAILED", "Dynamic client registration did not return client_id.");
        }
        return (clientId, Str(json, "client_secret"));
    }

    // ---------------------------------------------------------------- HTTP 令牌请求

    private async Task<JsonObject> PostTokenAsync(string tokenEndpoint, Dictionary<string, string> form, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Post, tokenEndpoint)
        {
            Content = new FormUrlEncodedContent(form),
        };
        req.Headers.Accept.ParseAdd("application/json");
        using var res = await _client.SendAsync(req, ct);
        var text = await res.Content.ReadAsStringAsync(ct);
        if (!res.IsSuccessStatusCode)
        {
            throw McpOAuthException.TokenFailed($"HTTP {(int)res.StatusCode}: {Truncate(text, 200)}");
        }
        if (JsonNode.Parse(text) is not JsonObject json)
        {
            throw McpOAuthException.TokenFailed("malformed token response JSON");
        }
        if (Str(json, "error") is { } error)
        {
            throw McpOAuthException.TokenFailed($"{error}: {Str(json, "error_description")}");
        }
        return json;
    }

    private static McpOAuthToken ParseToken(JsonObject json)
    {
        var expiresIn = LongOf(json["expires_in"]);
        var now = DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        return new McpOAuthToken
        {
            AccessToken = Str(json, "access_token"),
            RefreshToken = Str(json, "refresh_token"),
            TokenType = Str(json, "token_type") ?? "Bearer",
            ExpiresAt = expiresIn > 0 ? now + expiresIn - 60 : now + 3600,
            Scope = Str(json, "scope"),
        };
    }

    // ---------------------------------------------------------------- 配置/令牌持久化（加密）

    private void SaveConfig(McpServer server, McpOAuthConfig cfg)
    {
        var json = new JsonObject
        {
            ["resource"] = cfg.Resource,
            ["issuer"] = cfg.Issuer,
            ["authorization_endpoint"] = cfg.AuthorizationEndpoint,
            ["token_endpoint"] = cfg.TokenEndpoint,
            ["registration_endpoint"] = cfg.RegistrationEndpoint,
            ["client_id"] = cfg.ClientId,
            ["client_secret"] = cfg.ClientSecret,
            ["scopes"] = new JsonArray(cfg.Scopes.Select(s => (JsonNode?)JsonValue.Create(s)!).ToArray()),
            ["redirect_uri"] = cfg.RedirectUri,
            ["token_endpoint_auth_method"] = cfg.TokenEndpointAuthMethod,
            ["code_challenge_method"] = cfg.CodeChallengeMethod,
        };
        server.OAuthConfigJson = _security.EncryptSecret(json.ToJsonString());
        server.IsOAuthConfigEncrypted = true;
    }

    private void SaveTokens(McpServer server, McpOAuthToken token)
    {
        var json = new JsonObject
        {
            ["access_token"] = token.AccessToken,
            ["refresh_token"] = token.RefreshToken,
            ["token_type"] = token.TokenType,
            ["expires_at"] = token.ExpiresAt,
            ["scope"] = token.Scope,
        };
        server.OAuthTokensJson = _security.EncryptSecret(json.ToJsonString());
        server.IsOAuthTokensEncrypted = true;
    }

    private McpOAuthConfig? LoadConfig(McpServer server)
    {
        var json = ReadJson(server.OAuthConfigJson, server.IsOAuthConfigEncrypted);
        if (json is null) return null;
        var scopes = new List<string>();
        if (json["scopes"] is JsonArray arr)
        {
            foreach (var s in arr)
            {
                if (s is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String) scopes.Add(v.GetValue<string>());
            }
        }
        return new McpOAuthConfig
        {
            Resource = Str(json, "resource"),
            Issuer = Str(json, "issuer"),
            AuthorizationEndpoint = Str(json, "authorization_endpoint"),
            TokenEndpoint = Str(json, "token_endpoint"),
            RegistrationEndpoint = Str(json, "registration_endpoint"),
            ClientId = Str(json, "client_id"),
            ClientSecret = Str(json, "client_secret"),
            Scopes = scopes.ToArray(),
            RedirectUri = Str(json, "redirect_uri"),
            TokenEndpointAuthMethod = Str(json, "token_endpoint_auth_method"),
            CodeChallengeMethod = Str(json, "code_challenge_method"),
        };
    }

    private McpOAuthToken? LoadTokens(McpServer server)
    {
        var json = ReadJson(server.OAuthTokensJson, server.IsOAuthTokensEncrypted);
        if (json is null) return null;
        return new McpOAuthToken
        {
            AccessToken = Str(json, "access_token"),
            RefreshToken = Str(json, "refresh_token"),
            TokenType = Str(json, "token_type"),
            ExpiresAt = LongOf(json["expires_at"]),
            Scope = Str(json, "scope"),
        };
    }

    private JsonObject? ReadJson(string? raw, bool encrypted)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        var text = encrypted ? _security.DecryptSecret(raw) : raw;
        try { return JsonNode.Parse(text) as JsonObject; }
        catch { return null; }
    }

    // ---------------------------------------------------------------- 小工具

    private static string? Str(JsonObject? json, string key)
    {
        if (json?[key] is not JsonValue v) return null;
        return v.GetValueKind() == System.Text.Json.JsonValueKind.String ? v.GetValue<string>() : v.ToJsonString();
    }

    private static long LongOf(JsonNode? node)
    {
        if (node is not JsonValue v) return 0;
        var raw = v.ToJsonString().Trim('"');
        return long.TryParse(raw, out var n) ? n : 0;
    }

    private static string Truncate(string text, int max) =>
        text.Length <= max ? text : text[..max] + "…";

    private static string[]? ReadScopes(JsonObject? meta)
    {
        if (meta?["scopes_supported"] is not JsonArray arr) return null;
        var scopes = new List<string>();
        foreach (var item in arr)
        {
            if (item is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String) scopes.Add(v.GetValue<string>());
        }
        return scopes.Count > 0 ? scopes.ToArray() : null;
    }

    private static string ChooseTokenAuthMethod(JsonObject? authMeta)
    {
        if (authMeta?["token_endpoint_auth_methods_supported"] is not JsonArray arr) return "none";
        var methods = new List<string>();
        foreach (var item in arr)
        {
            if (item is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String) methods.Add(v.GetValue<string>());
        }
        if (methods.Contains("none")) return "none";
        if (methods.Contains("client_secret_post")) return "client_secret_post";
        return methods.Count > 0 ? methods[0] : "none";
    }

    private static string ChooseChallengeMethod(JsonObject? authMeta)
    {
        if (authMeta?["code_challenge_methods_supported"] is JsonArray arr)
        {
            foreach (var item in arr)
            {
                if (item is JsonValue v && v.GetValueKind() == System.Text.Json.JsonValueKind.String && v.GetValue<string>() == "S256") return "S256";
            }
        }
        return "S256";
    }

    private static void CleanupPending()
    {
        var cutoff = DateTimeOffset.UtcNow - PendingTtl - TimeSpan.FromMinutes(5);
        foreach (var pair in Pending)
        {
            if (pair.Value.CreatedAt < cutoff) Pending.TryRemove(pair.Key, out _);
        }
    }

    private static string RandomUrlSafe(int bytes)
    {
        var buf = new byte[bytes];
        RandomNumberGenerator.Fill(buf);
        return Base64Url(buf);
    }

    private static string Base64Url(byte[] bytes) =>
        Convert.ToBase64String(bytes).Replace("+", "-").Replace("/", "_").TrimEnd('=');
}
