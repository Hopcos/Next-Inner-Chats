using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using NextChats.Core.Abstractions;
using NextChats.Core.Clients;
using NextChats.Core.Entities;
using NextChats.Core.Localization;

namespace NextChats.Core.Services;

/// <summary>
/// 标准 Agent Skill 执行引擎（机制 A）：
///  - 启动时：元工具仅下发名称与描述（Name + Description），只占用极低 Token（~15 Tokens/个）。
///  - 触发时：当用户意图命中某技能时，模型调用该技能元工具，引擎在本地直接读取并渲染出该 Skill 的完整
///    规范、操作守则与工作流模板（Instruction），将其作为工具结果返回注入进当前会话上下文；
///  - 零网络开销：完全在内存中展开，不发起嵌套的二次远程 LLM 网络请求，彻底避免网络波动、上游不可达及状态割裂。
/// </summary>
public sealed class SkillExecutionEngine : ISkillExecutionEngine
{
    private readonly IPromptTemplateEngine _templates;
    private readonly ILogger _logger;

    public SkillExecutionEngine(IPromptTemplateEngine templates, ILogger<SkillExecutionEngine> logger)
    {
        _templates = templates;
        _logger = logger;
    }

    public IReadOnlyList<LlmToolDef> BuildMetaTools(IEnumerable<Skill> skills)
    {
        var defs = new List<LlmToolDef>();
        foreach (var skill in skills)
        {
            var schema = new JsonObject
            {
                ["type"] = "object",
                ["properties"] = new JsonObject
                {
                    ["query"] = new JsonObject
                    {
                        ["type"] = "string",
                        ["description"] = "Optional context or specific focus when loading this skill",
                    },
                },
            };
            var toolDesc = $"{skill.Description ?? Texts.Get("SKILL_DESC_FALLBACK", "en", skill.Name)}. Call this skill to load its full instructions, rules, and guidelines into your current context.";
            defs.Add(new LlmToolDef(skill.MetaToolName, toolDesc, schema));
        }
        return defs;
    }

    /// <summary>展开 Skill：在本地直接渲染完整指令并注入会话上下文，零外部网络请求</summary>
    public Task<(bool Success, string Result, string? Error)> ExecuteAsync(Skill skill, string input, string traceId, CancellationToken ct)
    {
        try
        {
            // 懒加载：将 Skill 的完整指南、排版准则与流程模板在本地渲染（支持模板变量插值）
            var rendered = _templates.Render(skill.Instruction, new Dictionary<string, object?>
            {
                ["input"] = input,
                ["skill"] = skill.Name,
                ["trace_id"] = traceId,
            });

            // 包装为标准的 Skill 上下文挂载结果，指导当前主模型在此后的回复中无缝遵循该规则
            var output = $"<skill_content name=\"{skill.MetaToolName}\">\n" +
                         $"<skill_name>{skill.Name}</skill_name>\n" +
                         $"<skill_instructions>\n{rendered.Trim()}\n</skill_instructions>\n" +
                         $"</skill_content>\n\n" +
                         $"Skill \"{skill.Name}\" is now active in your context. Follow the above skill_instructions strictly to complete the user's request in your following response.";

            _logger.LogInformation("Skill {Skill} ({Meta}) activated and loaded into context trace={TraceId}", skill.Name, skill.MetaToolName, traceId);
            return Task.FromResult<(bool Success, string Result, string? Error)>((true, output, null));
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Skill {Skill} 展开失败 trace={TraceId}", skill.Name, traceId);
            return Task.FromResult<(bool Success, string Result, string? Error)>((false, string.Empty, ex.Message));
        }
    }
}
