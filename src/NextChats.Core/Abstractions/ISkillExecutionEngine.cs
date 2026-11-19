using NextChats.Core.Abstractions;
using NextChats.Core.Clients;
using NextChats.Core.Entities;

namespace NextChats.Core.Abstractions;

/// <summary>
/// 标准 Agent Skill 执行引擎（机制 A）：
/// - 启动时：通过轻量目录（Name + Description）告知主模型有哪些可用技能；
/// - 触发时：模型通过调用 Skill 元工具（或统一原子加载工具），引擎从本地内存/数据库直接渲染并返回
///   完整的技能规范与指南内容（Skill Instruction），就地加载进当前上下文，不发起二次嵌套 LLM HTTP 请求。
/// </summary>
public interface ISkillExecutionEngine
{
    /// <summary>把启用的 Skill 转成元工具定义（追加到工具列表，随每轮请求下发）</summary>
    IReadOnlyList<LlmToolDef> BuildMetaTools(IEnumerable<Skill> skills);

    /// <summary>展开 Skill：懒加载渲染完整指令与规范内容，返回给当前会话上下文（纯本地直读，零外部网络请求）</summary>
    Task<(bool Success, string Result, string? Error)> ExecuteAsync(Skill skill, string input, string traceId, CancellationToken ct);
}
