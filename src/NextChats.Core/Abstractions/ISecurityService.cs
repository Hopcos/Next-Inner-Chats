using NextChats.Core.Domain;

namespace NextChats.Core.Abstractions;

public interface ISecurityService
{
    /// <summary>PBKDF2 加盐哈希密码</summary>
    (string Hash, string Salt) HashPassword(string plain);

    bool VerifyPassword(string plain, string hash, string salt);

    /// <summary>AES-GCM 加密敏感配置（API Key 等）</summary>
    string EncryptSecret(string plain);

    /// <summary>AES-GCM 解密（null / 空串返回空串）</summary>
    string DecryptSecret(string? encrypted);

    /// <summary>
    /// 输入 Injection 检测与过滤。返回清洗后的文本与检出标志。
    /// 检测：系统角色注入、忽略历史指令、Base64 编码攻击、Unicode 混淆、隐藏字符、越权指令等。
    /// </summary>
    (string Sanitized, bool Flagged, IReadOnlyList<string> Hints) SanitizeUserInput(string input);

    /// <summary>日志/响应脱敏：Key、Authorization、Password、Token、Cookie 等敏感值替换为 ***</summary>
    string MaskSecrets(string text);

    /// <summary>展示层脱敏：仅保留前 4 / 后 4 字符</summary>
    string MaskApiKey(string? key);
}
