using System.Text.RegularExpressions;
using NextChats.Core.Abstractions;

namespace NextChats.Core.Services;

/// <summary>
/// 多 MCP 服务器工具名称消歧器：
/// 当用户在同一会话中勾选多个 MCP 服务器（或绑定多个内置/技能工具），若存在同名工具（例如 Kafka、Redis、K8S-Pods、UCS 均暴露 list_environments），
/// 各主流 LLM（尤其是 Google Gemini API）会直接报错 HTTP 400（Duplicate function declaration found: ...）。
/// 本消歧器检测所有传入工具的名称重复情况，将重名工具按其所属服务器前缀命名空间化（如 Kafka__list_environments），
/// 同时在 UnifiedTool 中保留 EffectiveRawName 以便在真正调用底层 MCP 服务时还原原始工具名称。
/// </summary>
public static partial class ToolDisambiguator
{
    private const int MaxToolNameLength = 64; // OpenAI / Gemini 常用工具名长度上限（一般不超过 64 字符）

    public static IReadOnlyList<UnifiedTool> Disambiguate(IReadOnlyList<UnifiedTool> tools)
    {
        if (tools == null || tools.Count <= 1)
        {
            return tools ?? [];
        }

        // 统计各工具名出现次数（忽略大小写，因为很多模型/网关对工具名不区分大小写）
        var counts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in tools)
        {
            var key = t.Name;
            counts[key] = counts.GetValueOrDefault(key, 0) + 1;
        }

        // 若无任何重复名称，直接原样返回
        if (counts.Values.All(c => c <= 1))
        {
            return tools;
        }

        var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var reservedTools = new HashSet<UnifiedTool>();

        // 第一轮：保留无需更名的工具名称。
        // 1. 无重名的普通工具
        // 2. 属于内置系统工具（system/ws-*）或技能工具（skill）的第 1 个实例，优先保留原名
        foreach (var t in tools)
        {
            var isDuplicate = counts[t.Name] > 1;
            var isSystemOrSkill = t.IsSkill || t.ServerName == "system" || t.ServerName.StartsWith("ws-", StringComparison.OrdinalIgnoreCase);

            if (!isDuplicate || (isSystemOrSkill && !usedNames.Contains(t.Name)))
            {
                usedNames.Add(t.Name);
                reservedTools.Add(t);
            }
        }

        var result = new List<UnifiedTool>(tools.Count);
        foreach (var t in tools)
        {
            if (reservedTools.Contains(t))
            {
                result.Add(t);
                continue;
            }

            // 发生命名冲突：加上服务器名前缀 ${prefix}__${toolName}
            var prefix = SanitizePrefix(t.ServerName);
            var candidate = $"{prefix}__{t.Name}";
            if (candidate.Length > MaxToolNameLength)
            {
                var avail = Math.Max(10, MaxToolNameLength - prefix.Length - 2);
                candidate = $"{prefix}__{t.Name[..Math.Min(avail, t.Name.Length)]}";
            }

            var finalName = candidate;
            var suffix = 2;
            while (usedNames.Contains(finalName))
            {
                var suf = $"_{suffix}";
                var maxBase = MaxToolNameLength - suf.Length;
                var baseName = candidate.Length > maxBase ? candidate[..maxBase] : candidate;
                finalName = $"{baseName}{suf}";
                suffix++;
            }

            usedNames.Add(finalName);

            // 在 Description 前缀加上来源服务器提示，辅助模型区分不同服务器的同名工具职责
            var descPrefix = $"[{t.ServerName}]";
            var newDesc = string.IsNullOrWhiteSpace(t.Description)
                ? descPrefix
                : (t.Description.StartsWith(descPrefix, StringComparison.OrdinalIgnoreCase)
                    ? t.Description
                    : $"{descPrefix} {t.Description}");

            result.Add(new UnifiedTool(
                ServerName: t.ServerName,
                Name: finalName,
                Description: newDesc,
                SchemaJson: t.SchemaJson,
                IsSkill: t.IsSkill,
                DestructiveHint: t.DestructiveHint,
                RawName: t.EffectiveRawName));
        }

        return result;
    }

    /// <summary>
    /// 将服务器名称清洗为符合 LLM 工具名称规范的前缀（^[a-zA-Z_][a-zA-Z0-9_-]*$）。
    /// </summary>
    public static string SanitizePrefix(string? serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return "mcp";

        // 替换非 a-zA-Z0-9_- 字符为下划线
        var clean = Regex.Replace(serverName, @"[^a-zA-Z0-9_-]+", "_").Trim('-', '_');
        while (clean.Contains("__"))
        {
            clean = clean.Replace("__", "_");
        }

        if (string.IsNullOrEmpty(clean)) return "mcp";

        // 首字符必须是字母或下划线
        if (!char.IsLetter(clean[0]) && clean[0] != '_')
        {
            clean = "s_" + clean;
        }

        // 控制前缀长度，留足够空间给工具名
        if (clean.Length > 20)
        {
            clean = clean[..20].TrimEnd('-', '_');
        }

        return string.IsNullOrEmpty(clean) ? "mcp" : clean;
    }
}
