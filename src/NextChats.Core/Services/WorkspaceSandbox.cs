using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Microsoft.Win32.SafeHandles;
using NextChats.Core.Abstractions;
using NextChats.Core.Domain;
using NextChats.Core.Entities;

namespace NextChats.Core.Services;

/// <summary>
/// 工作空间编码沙箱实现：
///  - L1 应用层路径沙箱：GetFullPath 规范化 + 工作空间根前缀校验 + 真实路径（GetFinalPathNameByHandle，
///    防 Windows junction/符号链接逃逸）二次校验；所有 ws_* 文件工具一律以工作空间根为界（含 FullAccess）。
///  - L2 进程隔离：命令子进程挂 Job Object（KILL_ON_JOB_CLOSE + 活动进程数上限），超时即整树终止，
///    句柄释放自动清场 —— 安全刷新无孤儿进程。
///  - 权限强制：每个工具声明最小级别（ReadOnly/WorkspaceWrite）；级别不足直接 Denied（不依赖模型自律）。
/// </summary>
public sealed class WorkspaceSandbox : IWorkspaceSandbox
{
    private const int MaxOutputChars = 2_000_000; // 单命令输出上限（字符）
    private const int MaxReadBytes = 200 * 1024;  // ws_read 单文件上限
    private const int MaxGrepResults = 500;       // ws_grep 结果上限
    private const int WsExecTimeoutMs = 60_000;   // ws_exec 默认超时

    private static readonly string[] GrepExcludeDirs = [".git", ".vs", "node_modules", "bin", "obj", "dist", ".idea", "packages"];
    private static readonly string[] WriteBlacklistCmd = // WorkspaceWrite 下的系统级/提权命令黑名单（FullAccess 解锁）
    [
        "powershell", "pwsh", "cmd", "format", "diskpart", "reg", "sc", "net", "shutdown",
        "restart", "takeown", "icacls", "cacls", "attrib", "taskkill", "wmic", "mshta",
        "bitsadmin", "certutil", "wbadmin", "mountvol", "subst",
    ];

    private readonly IConfigStore _config;
    private readonly ILogger<WorkspaceSandbox> _logger;

    public WorkspaceSandbox(IConfigStore config, ILogger<WorkspaceSandbox> logger)
    {
        _config = config;
        _logger = logger;
    }

    // ================= IWorkspaceSandbox =================

    public async Task<WsContext?> ResolveAsync(Guid workspaceId, WorkspaceAccessLevel level, CancellationToken ct = default)
    {
        var ws = await _config.GetWorkspaceAsync(workspaceId, ct);
        if (ws is null || !ws.Enabled) return null;
        if (string.IsNullOrWhiteSpace(ws.RootPath)) return null;
        var root = Path.GetFullPath(ws.RootPath);
        if (!Directory.Exists(root)) return null;
        return new WsContext(ws.Id, ws.Name, root, level);
    }

    public string ResolveInside(WsContext ctx, string? relPath, bool allowRoot = true)
    {
        var root = Path.GetFullPath(ctx.Root);
        string target;
        if (string.IsNullOrWhiteSpace(relPath))
        {
            target = root;
        }
        else
        {
            var cleaned = relPath.Trim().Replace('/', Path.DirectorySeparatorChar);
            if (Path.IsPathRooted(cleaned))
            {
                target = Path.GetFullPath(cleaned);
            }
            else
            {
                target = Path.GetFullPath(Path.Combine(root, cleaned));
            }
        }

        if (!IsInside(root, target)) throw new WsPathEscapedException(relPath ?? "");

        // L2/P2：真实路径防逃逸 —— junction/符号链接指向根外时 GetFullPath 校验无效，必须对真实路径再校验
        var real = GetRealPath(target);
        if (!string.IsNullOrEmpty(real) && !IsInside(root, real)) throw new WsPathEscapedException(relPath ?? "");

        return target;
    }

    public async Task<(string Stdout, string Stderr, int ExitCode, bool Killed)> RunProcessAsync(
        WsContext ctx, string command, string? args, string? cwd, int timeoutMs, CancellationToken ct = default)
    {
        var workingDir = cwd is null ? ctx.Root : ResolveInside(ctx, cwd); // cwd 必须工作空间内（FullAccess 也限制在根内 cwd）
        var psi = new ProcessStartInfo
        {
            FileName = command,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            CreateNoWindow = true, // 永不弹窗（含 RDP/服务会话）
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (!string.IsNullOrEmpty(args)) psi.Arguments = args;
        psi.Environment["WORKSPACE_ROOT"] = ctx.Root;
        psi.Environment["WORKSPACE_LEVEL"] = ((int)ctx.Level).ToString();

        using var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
        try
        {
            process.Start();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ws] process start failed cmd={Cmd}", command);
            return ("", ex.Message, -2, false);
        }

        // Job Object：KILL_ON_JOB_CLOSE + 活动进程上限 —— 超时/释放即整树终止
        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job != IntPtr.Zero)
        {
            ConfigureJob(job, maxActiveProcesses: 32);
            try
            {
                AssignProcessToJobObject(job, process.Handle);
            }
            catch
            {
                // 进程可能已被其他 job 接管（如嵌套）；继续执行，超时兜底 process.Kill(true)
            }
        }

        var outTask = ReadLimitedAsync(process.StandardOutput, MaxOutputChars);
        var errTask = ReadLimitedAsync(process.StandardError, MaxOutputChars);

        var limit = timeoutMs <= 0 ? WsExecTimeoutMs : Math.Min(timeoutMs, 300_000);
        using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeoutCts.CancelAfter(limit);
        var exited = false;
        try
        {
            await process.WaitForExitAsync(timeoutCts.Token);
            exited = true;
        }
        catch (OperationCanceledException)
        {
            /* 超时或会话中断 */
        }

        var killed = false;
        if (!exited)
        {
            if (job != IntPtr.Zero)
            {
                try { TerminateJobObject(job, 137); killed = true; } catch { /* ignored */ }
            }
            if (!process.HasExited)
            {
                try { process.Kill(entireProcessTree: true); killed = true; } catch { /* ignored */ }
            }
        }

        var stdout = await SafeReadAsync(outTask);
        var stderr = await SafeReadAsync(errTask);
        if (job != IntPtr.Zero) CloseHandle(job); // KILL_ON_JOB_CLOSE 兜底清场

        return (stdout, stderr, exited ? process.ExitCode : -1, killed);
    }

    // ================= ws_* 工具执行 =================

    /// <summary>执行一个 ws_* 工具（参数为 JSON 对象字符串），级别不足/参数错误/路径逃逸返回结构化失败</summary>
    public WsToolResult Execute(WsContext ctx, string toolName, string? argsJson)
    {
        try
        {
            var args = string.IsNullOrWhiteSpace(argsJson) ? new JsonObject() : JsonNode.Parse(argsJson) as JsonObject ?? new JsonObject();

            return toolName switch
            {
                "ws_info" => WsInfo(ctx),
                "ws_list" => WsList(ctx, args),
                "ws_browse" => WsBrowse(ctx, args),
                "ws_stat" => WsStat(ctx, args, require: WorkspaceAccessLevel.ReadOnly),
                "ws_read" => WsRead(ctx, args),
                "ws_grep" => WsGrep(ctx, args),
                "ws_diff" => WsDiff(ctx),
                "ws_write" => WithLevel(ctx, WorkspaceAccessLevel.WorkspaceWrite, () => WsWrite(ctx, args)),
                "ws_edit" => WithLevel(ctx, WorkspaceAccessLevel.WorkspaceWrite, () => WsEdit(ctx, args)),
                "ws_mkdir" => WithLevel(ctx, WorkspaceAccessLevel.WorkspaceWrite, () => WsMkdir(ctx, args)),
                "ws_delete" => WithLevel(ctx, WorkspaceAccessLevel.WorkspaceWrite, () => WsDelete(ctx, args)),
                "ws_exec" => WithLevel(ctx, WorkspaceAccessLevel.WorkspaceWrite, () => WsExec(ctx, args)),
                _ => WsToolResult.Fail("WS_TOOL_UNKNOWN", $"unknown workspace tool: {toolName}"),
            };
        }
        catch (WsPathEscapedException ex)
        {
            _logger.LogWarning("[ws] escape attempt tool={Tool} path={Path}", toolName, ex.Path);
            return WsToolResult.Fail("WS_ESCAPE", $"path escapes workspace root: {ex.Path}");
        }
        catch (UnauthorizedAccessException ex)
        {
            return WsToolResult.Fail("WS_DENIED", $"access denied: {ex.Message}");
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[ws] tool failed tool={Tool}", toolName);
            return WsToolResult.Fail("WS_ERROR", ex.Message);
        }
    }

    private static WsToolResult WithLevel(WsContext ctx, WorkspaceAccessLevel required, Func<WsToolResult> impl) =>
        ctx.AtLeast(required) ? impl() : WsToolResult.Fail("WS_DENIED", $"requires level >= {required} (current {ctx.Level})");

    private static string Str(JsonObject o, string key) => o[key]?.GetValue<string>()?.Trim() ?? "";

    /// <summary>文件浏览器语义：列出目录内条目（名称/类型/大小），不递归</summary>
    private WsToolResult WsBrowse(WsContext ctx, JsonObject args)
    {
        var path = Str(args, "path");
        var target = ResolveInside(ctx, string.IsNullOrEmpty(path) ? "" : path);
        if (!Directory.Exists(target)) return WsToolResult.Fail("WS_NOT_FOUND", $"browse: directory not found: {path}");
        var sb = new StringBuilder();
        var rel = Path.GetRelativePath(ctx.Root, target);
        sb.AppendLine($"browse {(string.IsNullOrEmpty(rel) ? "." : rel)} in workspace \"{ctx.Name}\"");
        int dirs = 0, files = 0;
        foreach (var d in Directory.EnumerateDirectories(target).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            sb.AppendLine($"[dir ] {Path.GetFileName(d)}/");
            dirs++;
        }
        foreach (var f in Directory.EnumerateFiles(target).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            try { var fi = new FileInfo(f); sb.AppendLine($"[file] {fi.Name}  {fi.Length} bytes"); }
            catch { sb.AppendLine($"[file] {Path.GetFileName(f)}  ?"); }
            files++;
        }
        sb.AppendLine($"-- {dirs} dirs, {files} files");
        return WsToolResult.Succeed(sb.ToString().TrimEnd());
    }

    private WsToolResult WsInfo(WsContext ctx)
    {
        long files = 0, dirs = 0, totalBytes = 0;
        try
        {
            foreach (var f in Directory.EnumerateFiles(ctx.Root, "*", SearchOption.AllDirectories))
            {
                files++;
                try { totalBytes += new FileInfo(f).Length; } catch { /* ignored */ }
            }
            dirs = Directory.EnumerateDirectories(ctx.Root, "*", SearchOption.AllDirectories).LongCount();
        }
        catch { /* 统计失败不影响返回 */ }

        if (Directory.Exists(Path.Combine(ctx.Root, ".git")))
        {
            var (stdout, _, _, _) = RunProcessAsync(ctx, "git", "rev-parse --short HEAD 2>nul", ctx.Root, 10_000).GetAwaiter().GetResult();
            return WsToolResult.Succeed(
                $"workspace: {ctx.Name}\nroot: {ctx.Root}\nlevel: {ctx.Level}\nfiles: {files}, dirs: {dirs}, bytes: {totalBytes}\ngitHead: {stdout.Trim()}");
        }
        return WsToolResult.Succeed($"workspace: {ctx.Name}\nroot: {ctx.Root}\nlevel: {ctx.Level}\nfiles: {files}, dirs: {dirs}, bytes: {totalBytes}\n(git repo: no)");
    }

    private WsToolResult WsList(WsContext ctx, JsonObject o)
    {
        var dir = ResolveInside(ctx, o.ContainsKey("path") ? Str(o, "path") : null);
        if (!Directory.Exists(dir)) return WsToolResult.Fail("WS_NOT_FOUND", $"directory not found: {dir}");
        var entries = new List<string>();
        foreach (var d in Directory.EnumerateDirectories(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
            entries.Add("[dir]  " + Path.GetFileName(d));
        foreach (var f in Directory.EnumerateFiles(dir).OrderBy(x => x, StringComparer.OrdinalIgnoreCase))
        {
            var fi = new FileInfo(f);
            entries.Add($"{fi.Length,12}  {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm}  {Path.GetFileName(f)}");
        }
        return WsToolResult.Succeed($"listing {dir} ({entries.Count} entries):\n" + string.Join("\n", entries));
    }

    private WsToolResult WsStat(WsContext ctx, JsonObject o, WorkspaceAccessLevel require)
    {
        if (!ctx.AtLeast(require)) return WsToolResult.Fail("WS_DENIED", "requires read access");
        var p = ResolveInside(ctx, Str(o, "path"));
        if (Directory.Exists(p))
        {
            var di = new DirectoryInfo(p);
            var count = Directory.EnumerateFileSystemEntries(p).Count();
            return WsToolResult.Succeed($"[dir] {p}\nentries: {count}\nmodified: {di.LastWriteTimeUtc:yyyy-MM-dd HH:mm}");
        }
        var fi = new FileInfo(p);
        if (!fi.Exists) return WsToolResult.Fail("WS_NOT_FOUND", $"not found: {p}");
        return WsToolResult.Succeed($"[file] {p}\nsize: {fi.Length} bytes\nmodified: {fi.LastWriteTimeUtc:yyyy-MM-dd HH:mm}");
    }

    private WsToolResult WsRead(WsContext ctx, JsonObject o)
    {
        var p = ResolveInside(ctx, Str(o, "path"));
        if (!File.Exists(p)) return WsToolResult.Fail("WS_NOT_FOUND", $"file not found: {p}");
        var bytes = File.ReadAllBytes(p);
        var truncated = bytes.Length > MaxReadBytes;
        if (truncated) bytes = bytes[..MaxReadBytes];
        var text = SafeUtf8(bytes);
        return WsToolResult.Succeed($"--- {p} ({(truncated ? $"{MaxReadBytes} bytes of {new FileInfo(p).Length}" : bytes.Length.ToString() + " bytes")}) ---\n{text}" +
                                   (truncated ? $"\n[truncated at {MaxReadBytes} bytes]" : ""));
    }

    private static string SafeUtf8(byte[] bytes)
    {
        try { return Encoding.UTF8.GetString(bytes); }
        catch { return Encoding.Latin1.GetString(bytes); }
    }

    private WsToolResult WsGrep(WsContext ctx, JsonObject o)
    {
        var pattern = Str(o, "pattern");
        if (pattern.Length == 0) return WsToolResult.Fail("WS_INVALID", "pattern is required");
        var start = o.ContainsKey("path") && !string.IsNullOrWhiteSpace(Str(o, "path")) ? ResolveInside(ctx, Str(o, "path")) : ctx.Root;
        if (!Directory.Exists(start)) return WsToolResult.Fail("WS_NOT_FOUND", $"directory not found: {start}");
        var rx = new System.Text.RegularExpressions.Regex(pattern, System.Text.RegularExpressions.RegexOptions.IgnoreCase | System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        var hits = new List<string>();
        var queue = new Queue<string>();
        queue.Enqueue(start);
        while (queue.Count > 0 && hits.Count < MaxGrepResults)
        {
            var dir = queue.Dequeue();
            foreach (var sub in Directory.EnumerateDirectories(dir))
            {
                var name = Path.GetFileName(sub);
                if (GrepExcludeDirs.Contains(name, StringComparer.OrdinalIgnoreCase)) continue;
                queue.Enqueue(sub);
            }
            foreach (var f in Directory.EnumerateFiles(dir))
            {
                if (hits.Count >= MaxGrepResults) break;
                try
                {
                    var fi = new FileInfo(f);
                    if (fi.Length > 2_000_000) continue; // 跳过超大文件
                    var text = File.ReadAllText(f, Encoding.UTF8);
                    var lineNo = 0;
                    foreach (var line in text.Split('\n'))
                    {
                        lineNo++;
                        if (rx.IsMatch(line))
                        {
                            var rel = Path.GetRelativePath(ctx.Root, f);
                            var t = line.Trim();
                            hits.Add($"{rel}:{lineNo}: {(t.Length > 200 ? t[..200] : t)}");
                            if (hits.Count >= MaxGrepResults) break;
                        }
                    }
                }
                catch { /* 不可读文件跳过 */ }
            }
        }
        return WsToolResult.Succeed(hits.Count == 0 ? "no matches" : string.Join("\n", hits));
    }

    private WsToolResult WsDiff(WsContext ctx)
    {
        if (!Directory.Exists(Path.Combine(ctx.Root, ".git")))
            return WsToolResult.Fail("WS_NO_GIT", "not a git repository");
        var (stdout, stderr, exit, killed) = RunProcessAsync(ctx, "git", "diff --stat -- .", ctx.Root, 20_000).GetAwaiter().GetResult();
        if (killed) return WsToolResult.Fail("WS_TIMEOUT", "git diff timed out");
        return WsToolResult.Succeed(exit == 0 ? stdout : $"git diff failed ({exit}): {stderr}");
    }

    private WsToolResult WsWrite(WsContext ctx, JsonObject o)
    {
        var p = ResolveInside(ctx, Str(o, "path"));
        var content = o.ContainsKey("content") ? o["content"]?.ToString() ?? "" : "";
        var dir = Path.GetDirectoryName(p);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return WsToolResult.Succeed($"written {p} ({Encoding.UTF8.GetByteCount(content)} bytes)");
    }

    private WsToolResult WsEdit(WsContext ctx, JsonObject o)
    {
        var p = ResolveInside(ctx, Str(o, "path"));
        if (!File.Exists(p)) return WsToolResult.Fail("WS_NOT_FOUND", $"file not found: {p}");
        var oldText = o.ContainsKey("oldText") ? o["oldText"]?.ToString() ?? "" : "";
        var newText = o.ContainsKey("newText") ? o["newText"]?.ToString() ?? "" : "";
        var content = File.ReadAllText(p);
        var idx = content.IndexOf(oldText, StringComparison.Ordinal);
        if (idx < 0) return WsToolResult.Fail("WS_NOT_FOUND", "oldText not found in file");
        content = content[..idx] + newText + content[(idx + oldText.Length)..];
        File.WriteAllText(p, content, new UTF8Encoding(false));
        return WsToolResult.Succeed($"edited {p} (1 replacement)");
    }

    private WsToolResult WsMkdir(WsContext ctx, JsonObject o)
    {
        var p = ResolveInside(ctx, Str(o, "path"));
        Directory.CreateDirectory(p);
        return WsToolResult.Succeed($"created {p}");
    }

    private WsToolResult WsDelete(WsContext ctx, JsonObject o)
    {
        var p = ResolveInside(ctx, Str(o, "path"));
        if (File.Exists(p)) { File.Delete(p); return WsToolResult.Succeed($"deleted file {p}"); }
        if (Directory.Exists(p))
        {
            var recursive = o.ContainsKey("recursive") && o["recursive"]?.GetValue<bool>() == true;
            if (recursive) Directory.Delete(p, recursive: true);
            else if (Directory.EnumerateFileSystemEntries(p).Any()) return WsToolResult.Fail("WS_NOT_EMPTY", "directory not empty (use recursive=true)");
            else Directory.Delete(p);
            return WsToolResult.Succeed($"deleted dir {p}");
        }
        return WsToolResult.Fail("WS_NOT_FOUND", $"not found: {p}");
    }

    private WsToolResult WsExec(WsContext ctx, JsonObject o)
    {
        var command = Str(o, "command");
        if (command.Length == 0) return WsToolResult.Fail("WS_INVALID", "command is required");
        var args = o.ContainsKey("args") ? o["args"]?.ToString() : null;
        var cwd = o.ContainsKey("cwd") ? Str(o, "cwd") : null;
        var timeout = o.ContainsKey("timeoutMs") ? o["timeoutMs"]?.GetValue<int>() ?? WsExecTimeoutMs : WsExecTimeoutMs;

        // 命令黑名单（仅 WorkspaceWrite；FullAccess 解锁）
        var exeName = command.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0];
        exeName = Path.GetFileNameWithoutExtension(exeName);
        if (!ctx.CanFull && WriteBlacklistCmd.Contains(exeName, StringComparer.OrdinalIgnoreCase))
            return WsToolResult.Fail("WS_DENIED", $"command blacklisted at write level (needs FullAccess): {exeName}");

        var (stdout, stderr, exit, killed) = RunProcessAsync(ctx, command.Trim(), args, cwd, timeout).GetAwaiter().GetResult();
        if (killed) return WsToolResult.Fail("WS_TIMEOUT", $"command timed out after {timeout}ms and was terminated");
        if (exit != 0) return WsToolResult.Fail("WS_EXEC_FAILED", $"exit {exit}\n--- stdout ---\n{stdout}\n--- stderr ---\n{stderr}");
        return WsToolResult.Succeed(stdout.Length > 0 ? stdout : "(no output)");
    }

    // ================= 进程工具 =================

    private static async Task<string> ReadLimitedAsync(StreamReader reader, int maxChars)
    {
        var sb = new StringBuilder();
        var buf = new char[81920];
        var total = 0;
        while (true)
        {
            var n = await reader.ReadAsync(buf, 0, buf.Length);
            if (n <= 0) break;
            var take = Math.Min(n, maxChars - total);
            if (take > 0) sb.Append(buf, 0, take);
            total += n;
            if (total >= maxChars)
            {
                sb.Append($"\n[output truncated at {maxChars} chars]");
                break;
            }
        }
        return sb.ToString();
    }

    private static async Task<string> SafeReadAsync(Task<string> task)
    {
        try { return await task.ConfigureAwait(false); }
        catch { return "(read failed)"; }
    }

    // ================= Windows Job Object / 真实路径（P/Invoke） =================

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(IntPtr lpJobAttributes, string? lpName);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(IntPtr hJob, IntPtr hProcess);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(IntPtr hJob, uint uExitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr hObject);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(IntPtr hJob, int jobObjectInfoClass, IntPtr lpJobObjectInfo, uint cbJobObjectInfoLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle hFile, [Out] char[] lpszFilePath, uint cchFilePath, uint dwFlags);

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectBasicLimitInformation
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public nuint MinimumWorkingSetSize;
        public nuint MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public nuint Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JobObjectExtendedLimitInformation
    {
        public JobObjectBasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public nuint ProcessMemoryLimit;
        public nuint JobMemoryLimit;
        public nuint PeakProcessMemoryUsed;
        public nuint PeakJobMemoryUsed;
    }

    private const int JobObjectInfoClassExtendedLimit = 9;
    private const uint JobObjectLimitKillOnJobClose = 0x2000;
    private const uint JobObjectLimitActiveProcess = 0x0008;

    private static void ConfigureJob(IntPtr job, uint maxActiveProcesses)
    {
        var info = new JobObjectExtendedLimitInformation
        {
            BasicLimitInformation = new JobObjectBasicLimitInformation
            {
                LimitFlags = JobObjectLimitKillOnJobClose | JobObjectLimitActiveProcess,
                ActiveProcessLimit = maxActiveProcesses,
            },
        };
        var size = Marshal.SizeOf<JobObjectExtendedLimitInformation>();
        var ptr = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(info, ptr, false);
            SetInformationJobObject(job, JobObjectInfoClassExtendedLimit, ptr, (uint)size);
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }

    private static bool IsInside(string root, string target)
    {
        if (string.Equals(root, target, StringComparison.OrdinalIgnoreCase)) return true;
        return target.StartsWith(root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetRealPath(string path)
    {
        var full = Path.GetFullPath(path);
        if (File.Exists(full) || Directory.Exists(full))
        {
            try
            {
                using var fs = new FileStream(full, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
                return GetFinalPath(fs.SafeFileHandle);
            }
            catch
            {
                return full;
            }
        }
        var parent = Path.GetDirectoryName(full);
        if (string.IsNullOrEmpty(parent) || string.Equals(parent, full, StringComparison.OrdinalIgnoreCase)) return full;
        return Path.Combine(GetRealPath(parent), Path.GetFileName(full));
    }

    private static string GetFinalPath(SafeFileHandle handle)
    {
        var buf = new char[32_768];
        var len = GetFinalPathNameByHandle(handle, buf, (uint)buf.Length, 0);
        if (len == 0 || len >= buf.Length) return "";
        var s = new string(buf, 0, (int)len);
        if (s.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) return @"\\" + s[7..];
        if (s.StartsWith(@"\\?\", StringComparison.OrdinalIgnoreCase)) return s[4..];
        return s;
    }
}

/// <summary>ws_* 工具目录（按级别注入；执行时再强制校验，双保险）。读写/删除/执行等写操作由策略引擎自动触发审批（名字含 exec/delete）。</summary>
public static class WorkspaceToolCatalog
{
    public const string WsInfo = "ws_info", WsList = "ws_list", WsBrowse = "ws_browse", WsStat = "ws_stat", WsRead = "ws_read",
        WsGrep = "ws_grep", WsDiff = "ws_diff",
        WsWrite = "ws_write", WsEdit = "ws_edit", WsMkdir = "ws_mkdir", WsDelete = "ws_delete", WsExec = "ws_exec";

    /// <summary>只读工具（ReadOnly 起注入）</summary>
    public static readonly string[] ReadTools = [WsInfo, WsList, WsBrowse, WsStat, WsRead, WsGrep, WsDiff];

    /// <summary>写工具（WorkspaceWrite 起注入）</summary>
    public static readonly string[] WriteTools = [WsWrite, WsEdit, WsMkdir, WsDelete, WsExec];

    private static UnifiedTool T(string name, string description, string schema) => new("system", name, description, schema, IsSkill: false);

    /// <summary>按级别生成注入目录：全量注入（只读也用写工具名——执行时按级别强制拒绝并记审计）；ws_write/ws_delete/ws_exec 等写操作再由策略引擎启发式触发审批。</summary>
    public static IReadOnlyList<UnifiedTool> ForLevel(WorkspaceAccessLevel level, string wsName)
    {
        _ = level; // 注入不做级别裁剪：门控统一在 Execute 层（拒绝时返回明确错误 + 审计），保证工具对模型可见、行为可解释
        var tools = new List<UnifiedTool>(ReadTools.Length + WriteTools.Length)
        {
            T(WsInfo, $"Overview of the current workspace \"{wsName}\": root path, permission level, file counts and git HEAD.", """{"type":"object"}"""),
            T(WsList, "List files and directories inside the workspace (relative to its root). Returns name, size and modified time.", """{"type":"object","properties":{"path":{"type":"string","description":"relative path inside the workspace; omit for root"}},"additionalProperties":false}"""),
            T(WsBrowse, "Browse one directory inside the workspace like a file explorer: entries with name/type/size (non-recursive).", """{"type":"object","properties":{"path":{"type":"string","description":"relative path inside the workspace; omit for root"}},"additionalProperties":false}"""),
            T(WsStat, "Metadata of one file/dir inside the workspace (size, modified time, entries).", """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}"""),
            T(WsRead, $"Read a text file inside the workspace (UTF-8, capped at {200 * 1024} bytes).", """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}"""),
            T(WsGrep, "Case-insensitive regex search inside the workspace; returns file:line matches (up to 500).", """{"type":"object","properties":{"pattern":{"type":"string"},"path":{"type":"string","description":"optional subdirectory to limit search"}},"required":["pattern"],"additionalProperties":false}"""),
            T(WsDiff, "git diff --stat inside the workspace (requires a git repository).", """{"type":"object","properties":{"path":{"type":"string","description":"optional path filter"}},"additionalProperties":false}"""),
            T(WsWrite, "Create/overwrite a text file inside the workspace (UTF-8). Parent dirs are created. Requires write level or above (enforced at execution).", """{"type":"object","properties":{"path":{"type":"string"},"content":{"type":"string","description":"full file content (not JSON-escaped inner quotes)"}},"required":["path","content"],"additionalProperties":false}"""),
            T(WsEdit, "Replace the first occurrence of oldText with newText inside one workspace file. Requires write level or above (enforced at execution).", """{"type":"object","properties":{"path":{"type":"string"},"oldText":{"type":"string"},"newText":{"type":"string"}},"required":["path","oldText","newText"],"additionalProperties":false}"""),
            T(WsMkdir, "Create a directory inside the workspace (recursively). Requires write level or above (enforced at execution).", """{"type":"object","properties":{"path":{"type":"string"}},"required":["path"],"additionalProperties":false}"""),
            T(WsDelete, "Delete a file or empty directory inside the workspace (recursive=true for non-empty dirs). Triggers approval. Requires write level or above (enforced at execution).", """{"type":"object","properties":{"path":{"type":"string"},"recursive":{"type":"boolean"}},"required":["path"],"additionalProperties":false}"""),
            T(WsExec, "Run a command with working directory inside the workspace (job-object isolated, output capped, timeout). Triggers approval. FullAccess allows unrestricted commands; write level applies a system-command blacklist.", """{"type":"object","properties":{"command":{"type":"string","description":"executable name or full path (e.g. git, dotnet, node)"},"args":{"type":"string","description":"single string of arguments"},"cwd":{"type":"string","description":"relative cwd inside workspace"},"timeoutMs":{"type":"integer","description":"max 300000, default 60000"}},"required":["command"],"additionalProperties":false}"""),
        };
        return tools;
    }
}
