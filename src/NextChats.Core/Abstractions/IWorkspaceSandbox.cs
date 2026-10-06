using NextChats.Core.Domain;

namespace NextChats.Core.Abstractions;

/// <summary>已解析的工作空间执行上下文（根目录 + 角色派生级别）</summary>
public sealed record WsContext(Guid WorkspaceId, string Name, string Root, WorkspaceAccessLevel Level)
{
    /// <summary>是否达到写级别</summary>
    public bool CanWrite => Level >= WorkspaceAccessLevel.WorkspaceWrite;

    /// <summary>是否完全访问（任意路径 + 任意命令）</summary>
    public bool CanFull => Level >= WorkspaceAccessLevel.FullAccess;

    /// <summary>工具允许的最小级别判等</summary>
    public bool AtLeast(WorkspaceAccessLevel required) => Level >= required;
}

/// <summary>ws_* 工具单次执行结果</summary>
public sealed record WsToolResult(bool Ok, string Text, string? ErrorCode = null, int ExitCode = 0)
{
    public static WsToolResult Succeed(string text) => new(true, text);
    public static WsToolResult Fail(string code, string text) => new(false, text, code);
}

/// <summary>
/// 工作空间编码沙箱（L1 应用层路径沙箱 + L2 Windows Job Object 进程隔离）。
/// 所有路径先经 <see cref="ResolveInside"/> 规范化并以工作空间根为界（<c>..</c>/符号链接/junction 逃逸一律拒绝）；
/// 命令子进程挂 Job Object（KILL_ON_JOB_CLOSE + 资源限制），超时/句柄释放即整树终止 —— 安全刷新。
/// </summary>
public interface IWorkspaceSandbox
{
    /// <summary>校验并组装执行上下文（工作空间存在且启用、根目录可访问）；失败返回 null</summary>
    Task<WsContext?> ResolveAsync(Guid workspaceId, WorkspaceAccessLevel level, CancellationToken ct = default);

    /// <summary>把工作空间内相对路径解析为受控绝对路径；逃逸/非法路径抛 WsPathEscapedException</summary>
    string ResolveInside(WsContext ctx, string? relPath, bool allowRoot = true);

    /// <summary>执行命令（挂 Job Object；超时 → 整树终止；输出上限截断）</summary>
    Task<(string Stdout, string Stderr, int ExitCode, bool Killed)> RunProcessAsync(
        WsContext ctx, string command, string? args, string? cwd, int timeoutMs, CancellationToken ct = default);

    /// <summary>执行一个 ws_* 工具（参数为 JSON 对象字符串；级别/路径逃逸返回结构化失败）</summary>
    WsToolResult Execute(WsContext ctx, string toolName, string? argsJson);
}

/// <summary>路径逃逸/非法（安全事件，调用方应记录审计）</summary>
public sealed class WsPathEscapedException(string path) : Exception($"workspace escape attempt: {path}")
{
    public string Path { get; } = path;
}
