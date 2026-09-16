# AGENTS.md — 长期协作规则（每次任务开始前必须先读）

本文件是 AI 助手在本仓库工作的**持久化指令清单**。每次任务开始时先读取本文件并遵守；有更新时以本文件为准。

## 1. Git 操作

- **不要执行 `git commit`**。代码改动、构建、部署、验证照常完成，提交由人工自行处理。
- 若确实需要了解当前改动，可用 `git status` / `git diff` 查看，但**一律不提交**。

## 2. 部署协议

### 6500（生产，IIS/w3wp），目标目录 `E:\mcp-tools\next-chats`
1. 写 `app_offline.htm` 到站点根目录 → sleep 4s
2. 逐个解锁拷贝三 DLL（`NextChats.Api.dll` / `NextChats.Core.dll` / `NextChats.Infrastructure.dll`，源自 `src\NextChats.Api\bin\Release\net10.0\`）：打开文件句柄 `FileShare.None` 轮询，解锁超时 90s
3. 删除 `wwwroot\assets` 整目录，用 `web\dist\*` 整目录替换 wwwroot
4. 删 `app_offline.htm` → sleep 12s → probe `http://localhost:6500/api/auth/providers` 必须 200

### 沙箱（测试环境），目标目录 `E:\temp\verify\next-chats-test`，端口 6510
- 杀 dotnet（`CommandLine` 含 `*next-chats-test*`）→ 拷三 DLL → **静默启动**（不弹控制台窗口）：
  ```powershell
  Remove-Item "$dst\start-out.log","$dst\start-err.log" -ErrorAction SilentlyContinue
  Start-Process dotnet -ArgumentList 'NextChats.Api.dll','--urls','http://localhost:6510' -WorkingDirectory $dst -WindowStyle Hidden -RedirectStandardOutput "$dst\start-out.log" -RedirectStandardError "$dst\start-err.log"
  ```
  → sleep 14s → probe 200（失败时查看 `start-err.log`）
- **用户偏好（2026-09）：沙箱必须静默启动（`-WindowStyle Hidden` + 输出重定向到日志），禁止弹出控制台窗口**

### 数据库迁移
- 本项目用 `EnsureCreated + EnsureCompatibleSchemaAsync` 轻量补列（`AddColumnIfMissingAsync`）。新增实体字段时：
  1. `Entities.cs` 加字段
  2. `InfrastructureExtensions.EnsureCompatibleSchemaAsync` 加 `AddColumnIfMissingAsync` 行
  3. 部署后（沙箱与 6500）用 sqlite 确认补列成功（`PRAGMA table_info(<Table>)`，node:sqlite 直读 `data\nextchats.db`）

## 3. 构建与验证协议

- 前端构建：`cd web && npm run build`（产物 `web\dist\`）
- 后端构建：`dotnet build src\NextChats.Api\NextChats.Api.csproj -c Release`
- **每个功能必须验证后才算完成**：
  - 逻辑/回归验证优先用 node 脚本（先用 `write` 工具写脚本，不要用 `node -e` 长内联）；**脚本用完必须删除**
  - UI 端到端验证用 headless Edge CDP：`--remote-debugging-port=9223` + **独立** `--user-data-dir` profile（跨脚本复用 profile 会残留历史 UI 状态，影响断言）
  - 验证涉及修改沙箱环境（MCP 服务器、appsettings、LLM 提供方、用户设置）时，**验证结束后必须恢复原状**
- 沙箱上游 `llm-cs.everymatrix.local` 网关可能挂起（流式响应 ~400s+ 才结束）；需要快速流式验证时改用本地 Mock Provider（`Mock Demo Provider`，kind=Mock，需先启用并指向其模型；可用 `tool:<工具名>` 触发单轮工具调用）
- 部署 6500 的 DLL 必须是不含调试日志的干净构建

## 4. 其他约定

- 涉及危险/外网操作（抓取外部 URL 等）先在沙箱验证，产生文件用后即删
- 用户偏好更新时同步维护本文件
