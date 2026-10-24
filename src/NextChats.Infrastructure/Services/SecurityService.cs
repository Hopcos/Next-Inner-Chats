using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Options;
using Microsoft.AspNetCore.Cryptography.KeyDerivation;
using NextChats.Core.Abstractions;
using NextChats.Core.Configuration;

namespace NextChats.Infrastructure.Services;

/// <summary>
/// 安全服务：
///  - 密码：PBKDF2-SHA256 加盐哈希（永不存明文）；
///  - 敏感配置：AES-256-GCM 加密（API Key / MCP 请求头）；
///  - Injection 检测与过滤；
///  - 日志/响应脱敏。
/// </summary>
public sealed partial class SecurityService : ISecurityService
{
    private const int Pbkdf2Iterations = 210_000;
    private const int SaltSize = 16;
    private const int NonceSize = 12;
    private const int TagSize = 16;
    private const string V1Prefix = "v1:";

    private readonly byte[] _key;

    public SecurityService(IOptions<SecurityOptions> options)
    {
        var raw = string.IsNullOrWhiteSpace(options.Value.EncryptionKey)
            ? DeriveFallbackKey(options.Value.JwtKey)
            : options.Value.EncryptionKey;
        _key = NormalizeKey(raw);
    }

    private static byte[] NormalizeKey(string raw)
    {
        try
        {
            var bytes = Convert.FromBase64String(raw);
            if (bytes.Length >= 32) return bytes[..32];
        }
        catch (FormatException)
        {
            // 非 base64，走 SHA256 派生
        }
        return SHA256.HashData(Encoding.UTF8.GetBytes(raw));
    }

    private static string DeriveFallbackKey(string jwtKey) => jwtKey.Length >= 32 ? jwtKey : jwtKey.PadRight(32, 'x');

    public (string Hash, string Salt) HashPassword(string plain)
    {
        var salt = RandomNumberGenerator.GetBytes(SaltSize);
        var hash = KeyDerivation.Pbkdf2(plain, salt, KeyDerivationPrf.HMACSHA256, Pbkdf2Iterations, 32);
        return (Convert.ToBase64String(hash), Convert.ToBase64String(salt));
    }

    public bool VerifyPassword(string plain, string hash, string salt)
    {
        try
        {
            var saltBytes = Convert.FromBase64String(salt);
            var expected = Convert.FromBase64String(hash);
            var actual = KeyDerivation.Pbkdf2(plain, saltBytes, KeyDerivationPrf.HMACSHA256, Pbkdf2Iterations, expected.Length);
            return CryptographicOperations.FixedTimeEquals(expected, actual);
        }
        catch (Exception)
        {
            return false;
        }
    }

    public string EncryptSecret(string plain)
    {
        var nonce = RandomNumberGenerator.GetBytes(NonceSize);
        var plainBytes = Encoding.UTF8.GetBytes(plain);
        var tag = new byte[TagSize];
        var cipher = new byte[plainBytes.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Encrypt(nonce, plainBytes, cipher, tag);
        var payload = new byte[NonceSize + TagSize + cipher.Length];
        nonce.CopyTo(payload, 0);
        tag.CopyTo(payload, NonceSize);
        cipher.CopyTo(payload, NonceSize + TagSize);
        return V1Prefix + Convert.ToBase64String(payload);
    }

    public string DecryptSecret(string? encrypted)
    {
        if (string.IsNullOrEmpty(encrypted)) return string.Empty;
        if (!encrypted.StartsWith(V1Prefix, StringComparison.Ordinal)) return encrypted; // 兼容未加密旧值
        var payload = Convert.FromBase64String(encrypted[V1Prefix.Length..]);
        if (payload.Length < NonceSize + TagSize) return string.Empty;

        var nonce = payload.AsSpan(0, NonceSize).ToArray();
        var tag = payload.AsSpan(NonceSize, TagSize).ToArray();
        var cipher = payload.AsSpan(NonceSize + TagSize).ToArray();
        var plain = new byte[cipher.Length];
        using var aes = new AesGcm(_key, TagSize);
        aes.Decrypt(nonce, cipher, tag, plain);
        return Encoding.UTF8.GetString(plain);
    }

    public string MaskApiKey(string? key)
    {
        if (string.IsNullOrEmpty(key)) return "(未配置)";
        if (key.Length <= 8) return "****";
        return key[..4] + "****" + key[^4..];
    }

    public (string Sanitized, bool Flagged, IReadOnlyList<string> Hints) SanitizeUserInput(string input)
    {
        var hints = new List<string>();

        // 1) 控制字符与零宽/不可见字符清理
        var sanitized = ZeroWidthRegex().Replace(input, "");
        sanitized = ControlRegex().Replace(sanitized, " ");

        // 2) 系统角色/指令注入特征
        if (InjectionRegex().IsMatch(input))
        {
            hints.Add("包含指令覆盖/系统提示注入模式");
        }

        // 3) 伪装系统消息（role/system 越权）
        if (RoleSpoofRegex().IsMatch(input))
        {
            hints.Add("疑似伪造 system 消息");
        }

        // 4) 长 Base64 / Hex 载荷（隐藏指令编码）
        if (input.Length > 80 && Base64PayloadRegex().IsMatch(input))
        {
            hints.Add("疑似 Base64/编码载荷注入");
        }

        // 5) Unicode 混淆
        if (ConfusableRegex().IsMatch(input))
        {
            hints.Add("疑似 Unicode 混淆字符");
        }

        // 6) 外部工具滥用（提示模型调用危险工具）
        if (DangerRegex().IsMatch(input))
        {
            hints.Add("包含越权工具调用指令");
        }

        return (sanitized.Trim(), hints.Count > 0, hints);
    }

    public string MaskSecrets(string text)
    {
        if (string.IsNullOrEmpty(text)) return text;
        return MaskKeyPattern().Replace(text, "$1\"***\"")
            .Then(ReplaceBearer)
            .Then(ReplacePwd)
            .Then(ReplaceToken);
    }

    private static string ReplaceBearer(string text) =>
        BearerRegex().Replace(text, "Authorization: Bearer ***");

    private static string ReplacePwd(string text) =>
        PwdRegex().Replace(text, "$1***");

    private static string ReplaceToken(string text) =>
        TokenRegex().Replace(text, "$1***");

    [GeneratedRegex(@"[\u200B\u200C\u200D\u2060\uFEFF\u202E\u202D]")]
    private static partial Regex ZeroWidthRegex();

    [GeneratedRegex(@"[\x00-\x08\x0B\x0C\x0E-\x1F]")]
    private static partial Regex ControlRegex();

    [GeneratedRegex(@"\b(ignore|disregard|forget|override)\s+(all\s+)?(the\s+)?(previous|prior|above|system)\s+(instructions?|prompts?|rules?|guidelines?)\b|\byou\s+are\s+now\b|\byou\s+are\s+(an?\s+)?(admin|root|system|developer)\b|new\s+instructions?\s+follow|system\s+prompt\s*[:=]", RegexOptions.IgnoreCase)]
    private static partial Regex InjectionRegex();

    [GeneratedRegex(@"(role\s*[:=]\s*[\""'](system|developer)[\""'])|<\|?(system|developer)\|?>|<system>", RegexOptions.IgnoreCase)]
    private static partial Regex RoleSpoofRegex();

    [GeneratedRegex(@"[A-Za-z0-9+/]{80,}={0,2}|[0-9a-fA-F]{80,}")]
    private static partial Regex Base64PayloadRegex();

    [GeneratedRegex(@"[\u0300-\u036F\u202A-\u202E\u2066-\u2069]")]
    private static partial Regex ConfusableRegex();

    [GeneratedRegex(@"\b(call|invoke|execute|run)\s+(the\s+)?(dangerous|destructive|admin|privileged)\s+tools?\b|disable\s+safety|bypass\s+(policy|approval|security|guardrails)", RegexOptions.IgnoreCase)]
    private static partial Regex DangerRegex();

    [GeneratedRegex(@"(api[_-]?key|secret|password|client[_-]?secret)\s*[:=]\s*\""([^\""]{4,})\""", RegexOptions.IgnoreCase)]
    private static partial Regex MaskKeyPattern();

    [GeneratedRegex(@"(Authorization\s*[:=]\s*Bearer\s+)[A-Za-z0-9._\-]+", RegexOptions.IgnoreCase)]
    private static partial Regex BearerRegex();

    [GeneratedRegex(@"(\""password\""\s*:\s*\"")([^\""]{3,})(\""\))", RegexOptions.IgnoreCase)]
    private static partial Regex PwdRegex();

    [GeneratedRegex(@"(\""(?:token|access_token|refresh_token)\""\s*:\s*\"")([^\""]{6,})(\""\))", RegexOptions.IgnoreCase)]
    private static partial Regex TokenRegex();
}

internal static class StringThenExtensions
{
    public static string Then(this string s, Func<string, string> f) => f(s);
}
