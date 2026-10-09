# Next Chats

> [**中文版 README（简体中文）**](./README.zh.md)

<div align="center">
  <img src="docs/chat-window.png" alt="Chat window" width="88%" style="border-radius:8px;border:1px solid #333" />
  <p><em>Chat window (conversation / thinking / tools / topic rail)</em></p>
  <br/>
  <img src="docs/admin-window.png" alt="Admin console" width="88%" style="border-radius:8px;border:1px solid #333" />
  <p><em>Admin console (LLM providers / MCP / RBAC / internal auth / approvals / audit / usage)</em></p>
</div>

A B/S multimodal AI chat platform based on the **DeepSeek Harness principles**: LLM + MCP (Model Context Protocol) + Skill plugin orchestration, with a **Cordis**-driven, Three.js-powered 3D frontend.

> Reference paper: *A Programming Paradigm for Spatiotemporal Composability* — everything is a plugin, driven by Cordis.

**Multilingual UI**: English (default) and 简体中文 — switch from the top bar (🌐 EN / 中文); your choice is remembered in `localStorage`.

---

## Architecture & Flows

### Data Model (ER)

```mermaid
erDiagram
    APP_USER {
        Guid id PK
        string auth_type "default=password account; acs/ucs…=internal auth"
        string username "unique together with auth_type"
        string display_name
        string email
        string password_hash
        string password_salt
        string status
        datetime created_at
        datetime last_login_at
    }
    APP_ROLE {
        Guid id PK
        string name
        string code UK
        string description
        bool is_system
        datetime created_at
    }
    CHAT_SESSION {
        Guid id PK
        Guid user_id FK
        string title
        string status
        Guid llm_provider_id
        string context_json
        datetime created_at
        datetime updated_at
        datetime last_message_at
    }
    CHAT_MESSAGE {
        Guid id PK
        Guid session_id FK
        Guid user_id FK
        string role
        text content
        text reasoning
        string tool_calls_json
        string status
        string model
        int prompt_tokens
        int completion_tokens
        string trace_id
        string client_message_id UK
        datetime created_at
    }
    LLM_PROVIDER {
        Guid id PK
        string name
        string kind
        string base_url
        string api_key_encrypted
        int priority
        bool enabled
        bool is_healthy
        datetime created_at
        datetime updated_at
    }
    LLM_MODEL {
        Guid id PK
        Guid provider_id FK
        string name
        bool enabled
        bool is_vision
        int context_window
        decimal price_in_per_1k
        decimal price_out_per_1k
        int priority
        datetime created_at
    }
    INTERNAL_AUTH_PROVIDER {
        Guid id PK
        string name "acs / ucs…, unique"
        string api "auth center URL"
        string http_method
        string request_format "body(application/json)"
        string username_field
        string password_field
        bool enabled
        int timeout_seconds
        datetime created_at
    }
    INTERNAL_AUTH_SUCCESS_RULE {
        Guid id PK
        Guid provider_id FK
        string field "JSON path, e.g. sessionID"
        string operator "NotEmpty / Equals"
        string expected_value
    }
    MCP_SERVER {
        Guid id PK
        string name
        string transport
        string endpoint
        string headers_json
        string stdio_command
        bool enabled
        bool is_vision
        int timeout_seconds
        string description
        datetime created_at
    }
    MCP_CATALOG_ITEM {
        Guid id PK
        Guid mcp_server_id FK
        string kind
        string name
        string description
        string schema_json
        bool enabled
    }
    PROMPT {
        Guid id PK
        string name
        string description
        text content
        bool enabled
        int version
        datetime created_at
    }
    SKILL {
        Guid id PK
        string name
        string meta_tool_name
        text instruction
        bool enabled
        string model_override
        int max_nested_steps
    }
    TOOL_APPROVAL {
        Guid id PK
        string trace_id
        Guid user_id FK
        Guid session_id FK
        string mcp_server_name
        string tool_name
        string arguments_json
        string status
        datetime created_at
        datetime decided_at
        datetime expires_at
    }
    AUDIT_LOG {
        Guid id PK
        string trace_id
        Guid user_id FK
        string category
        string action
        string target
        string detail_json
        string ip
        bool is_suspicious
        datetime created_at
    }
    USER_SETTING {
        Guid id PK
        Guid user_id FK
        string key
        string value_json
    }
    TOKEN_USAGE_RECORD {
        Guid id PK
        string trace_id UK
        Guid user_id FK
        Guid session_id FK
        string provider_name
        string model
        int total_tokens
        decimal cost
        int ttft_ms
        datetime created_at
    }

    APP_USER ||--o{ CHAT_SESSION : "owns"
    APP_USER ||--o{ CHAT_MESSAGE : "sends"
    APP_USER }o--o{ APP_ROLE : "member_of (user_roles)"
    APP_USER ||--o{ USER_SETTING : "has"
    APP_ROLE }o--o{ MCP_SERVER : "can_use (role_mcp_servers)"
    APP_ROLE }o--o{ PROMPT : "can_use (role_prompts)"
    APP_ROLE }o--o{ SKILL : "can_use (role_skills)"
    APP_ROLE }o--o{ LLM_MODEL : "can_use (role_model_bindings)"
    APP_ROLE }o--o{ INTERNAL_AUTH_PROVIDER : "default roles (internal_auth_provider_roles)"
    LLM_PROVIDER ||--o{ LLM_MODEL : "models"
    INTERNAL_AUTH_PROVIDER ||--o{ INTERNAL_AUTH_SUCCESS_RULE : "success rules"
    MCP_SERVER ||--o{ MCP_CATALOG_ITEM : "catalog"
    CHAT_SESSION ||--o{ CHAT_MESSAGE : "contains"
    CHAT_SESSION }o--o| LLM_PROVIDER : "uses"
    TOOL_APPROVAL }o--|| APP_USER : "requested_by"
    TOOL_APPROVAL }o--|| CHAT_SESSION : "in"
    AUDIT_LOG }o--o| APP_USER : "actor"
    TOKEN_USAGE_RECORD }o--o| APP_USER : "accrued_by"
```

### System Architecture

```mermaid
flowchart LR
    subgraph Web["Web (Vue 3 + Cordis)"]
        UI[Chat UI / 3D Theme] --> K[Kernel Plugins]
        K -->|SSE / REST| HTTP[Vite proxy → /api]
    end

    subgraph Api["NextChats.Api (ASP.NET Core)"]
        Ctl[Controllers / RBAC / Audit] --> Mod[Error Localization Middleware]
        Ctl --> Orch{{ChatOrchestrator}}
        Ctl --> Adm[Admin: Providers / MCP / Prompts / Skills / RBAC / Internal Auth / Approvals]
        Ctl --> Auth[Auth: default password / internal auth]
        Auth -->|HTTP| IA[(Internal Auth Center acs / ucs…)]
    end

    Orch --> Loop{{AgentLoopEngine}}
    Loop -->|ReAct think→act→observe| Router[LLM Router]
    Router --> OpenAI[OpenAI-Compatible Client]
    Router --> Mock[Mock Client: localized output]
    Orch --> Driver[MCP Driver]
    Driver --> MCP1[(MCP Server: Demo 5300)]
    Driver --> MCP2[(MCP Server: more…)]
    Orch --> Skills[Skill Execution Engine]
    Orch --> Policy[Policy Engine / Approval Coordinator]

    Api --> DB[(SQLite: config / chat / audit)]
    UI --> V3[Three.js scenes]

    style Orch fill:#e3f2fd
    style Loop fill:#fff3e0
```

### Chat (ReAct) Flow — SSE streaming

```mermaid
sequenceDiagram
    participant U as User (Browser)
    participant K as Cordis Kernel
    participant C as ChatController
    participant O as ChatOrchestrator
    participant L as AgentLoopEngine
    participant LLM as LLM Client
    participant M as MCP Driver / Skills

    U->>K: type message (+ optional images)
    K->>C: POST /api/chat/stream (SSE)
    C->>O: ChatStreamRequest (lang, images)
    O-->>C: SESSION_NOT_FOUND? / INPUT_FLAGGED? (error event)
    O->>L: AgentRunRequest (tools, context window)
    loop ReAct round
        L->>LLM: stream prompt + tools
        LLM-->>L: reasoning / text / tool calls chunks
        L-->>U: thinking_start · thinking_delta · text_delta (SSE)
        alt tool call
            L->>Policy: evaluate (allow/deny/approval)
            Policy-->>U: approval_updated (pending) → user approves
            L->>M: execute tool (approval / args)
            M-->>L: tool result
            L-->>U: tool_start · tool_result (SSE)
        end
    end
    L-->>U: done (usage: tokens / ttft / cost)
    O->>DB: persist assistant message + usage + audit (trace_id)
    O-->>C: SSE end event
    C-->>K: event stream → UI renders
```

### Image / Vision Flow (image_source standard base64)

```mermaid
sequenceDiagram
    participant U as User (Browser)
    participant B as ChatInputBar
    participant C as ChatController
    participant O as ChatOrchestrator
    participant V as Vision MCP Tool (image_source)
    participant LLM as LLM (Mock / real)
    participant P as Provider · MCP Vision Flags

    U->>C: GET /api/chat/vision-config
    C->>P: provider.IsVision ∨ bound MCP.IsVision
    P-->>U: supported: true → show upload button + Ctrl+V

    U->>B: paste (Ctrl+V) or upload N images
    B->>B: validate type/size → dataURL → base64
    B->>C: POST /api/chat/stream { message, images[] }
    C->>O: ChatStreamRequest.Images (standard base64)
    loop each image (multi-image recognized one by one)
        O->>V: call vision tool { image_source: base64 }
        V-->>O: recognition text (per image)
    end
    O->>LLM: system + history + [vision texts] + user(message + <image_source> blocks)
    LLM-->>U: streamed answer (SSE) using the recognized text
```

## Tech Stack

| Layer | Technology |
| --- | --- |
| Backend | .NET 10 · ASP.NET Core · EF Core (SQLite now → MySQL 8 ready) |
| MCP | **ModelContextProtocol SDK 2.2.0** (latest stable) · Streamable HTTP transport · STDIO extensible |
| Frontend | Vue 3 · TypeScript · Vite · Element Plus · Three.js · **Cordis ^3.18.1** (plugin kernel) |
| Security | JWT dual-token (30-min access + rotating refresh, revoked on disable/logout) · PBKDF2-SHA256(210k) password hashing · AES-256-GCM secret encryption · sanitized audit logs |
| i18n | vue-i18n 10 (en / zh-CN, English default) · `X-Lang` error localization on the API |

## Repository Layout

```
next-chats/
├── src/
│   ├── NextChats.Core/           # Domain model, orchestration, engines, drivers (no ASP.NET dependency)
│   ├── NextChats.Infrastructure/ # EF Core data, caching, security services (swappable)
│   └── NextChats.Api/            # Web API (SSE streaming, admin, RBAC, audit, metrics, i18n)
├── samples/McpDemoServer/        # Demo MCP Server (Streamable HTTP)
├── e2e/                          # Internal-auth E2E (mock auth center + PowerShell suite)
└── web/                          # Vue 3 + Cordis frontend
    ├── src/i18n/                 # en / zh dictionaries (per-domain locale files)
    └── scripts/smoke*.mjs        # End-to-end smoke scripts (Node fetch streaming)
```

## Quick Start

```bash
# 1. Start the demo MCP Server (port 5300)
dotnet run --project samples/McpDemoServer -c Debug

# 2. Start the API (port 5210; DB is auto-created and seeded on first boot)
dotnet run --project src/NextChats.Api -c Debug

# 3. Start the frontend (Vite proxies /api → 5210)
cd web && npm install && npm run dev
# Open http://localhost:5173  (seeded admin: admin / admin123)
```

## Core Design

### Authentication (default accounts + internal auth)
- **default（system accounts）**: local password accounts (PBKDF2-SHA256), created by an admin in **User Management**; the login page defaults to `default`.
- **Internal auth (acs / ucs…)**: admins maintain auth-center configs in **Internal Auth Management** — API URL, HTTP method, the request-body field names for the username/password (`body(application/json)`), **success-response rules** (one or more fields that must be **not empty** or **equal a fixed value**, dotted paths such as `data.sessionID` supported), and the category's **default roles** (multi-select).
- The login page shows a sign-in-method radio group (`default` + enabled auth centers, from `GET /api/auth/providers`); choosing an internal method makes the backend validate the credentials against the configured auth center.
- After a successful validation the user is **auto-provisioned or reused**: internal users are unique by `(AuthType, Username)` (a `default` and an `acs` account may share a username); when the user already exists their roles are read directly, otherwise a user is created (no password, displayName = username, status Active, bound to the provider's default roles).
- Internal-auth users share the **same JWT mechanism** as default accounts: chat / catalog / permissions behave identically afterwards (role → model/MCP/Prompt/Skill bindings decide the visible scope).
- **Dual-token session (double-token)**: login issues a short-lived access token (**30 min**) plus a persisted refresh token (**7 days**, stored as SHA-256 hash). When the access token expires, the frontend silently exchanges the refresh token (`POST /api/auth/refresh`) and replays the request — active users are never logged out mid-work, idle sessions expire after 7 days. Every refresh **rotates** the token (the old one is revoked and recorded as replaced, so replays are rejected). Re-login, logout, and **user disable** revoke that user's refresh tokens immediately (a disabled account can no longer renew, and its access token dies within 30 min).

```mermaid
sequenceDiagram
    participant U as User (Browser)
    participant C as AuthController
    participant P as InternalAuthProvider config
    participant A as Auth Center (external)
    participant DB as Users table

    U->>C: POST /api/auth/login { authType: "acs", username, password }
    C->>P: load provider (api / fields / success rules / default roles)
    C->>A: HTTP POST api { username_field: …, password_field: … }
    A-->>C: response JSON
    C->>C: evaluate success rules (field not empty / equals value)
    alt rule satisfied
        C->>DB: find user by (AuthType=acs, Username)
        alt not exists
            C->>DB: auto-create (no password, default roles)
        end
        C-->>U: JWT (same mechanism as default accounts) + user info
    else rule failed / auth center error
        C-->>U: 401 (AUTH_INVALID_CREDENTIALS / AUTH_PROVIDER_ERROR)
    end
```

### Orchestration (Core)
- **Effective tool intersection**: `role binding ∩ user selection ∩ MCP global enabled` per user, with data isolation.
- **Active Skill matching**: Skills are exposed to the model as *meta tools* (`skill_<slug>`); instructions are lazy-loaded to avoid token explosion.
- **Prompt building**: a template engine (`{{var}}` / `#if` / `#each` / `#section`) renders system prompts.
- **ReAct loop (AgentLoopEngine)**: producer–consumer Channel architecture decouples LLM streaming from the main loop; safe interruption.

### Team collaboration (TeamOrchestrator)
- **Execution model**: each team message runs 「responsible engineer drafts a solution → assistants give independent suggestions (parallel or sequential, isolated) → responsible evaluates and produces the next-round plan (early stop on consensus) → final wrap-up」, up to `maxRounds`.
- **SSE orchestration**: a single Channel decouples production/consumption; events carry `round/phase/engineerId`: `team_start → team_round → team_text (open segment) → team_think_delta (reasoning stream) → team_tool_start/team_tool_result (tool calls) → team_delta (text stream) → team_end → end`. Interleaved parallel suggestions are routed to the right collapsible segment (Round / participant double-folding) by round+phase+engineer.
- **Tool loop (same lever as normal chat)**: engineer calls expose the same tool set as normal chat (settings-enabled MCP/skills ∩ role binding ∩ built-ins `http_fetch`/`mcp_*` ∩ session workspace `ws_*`). Models may take multiple decision steps: thinking streams live → tool calls execute directly (no approval in team mode, like Autonomous) → results feed back into `messages` → until direct text output. Tool cards reuse the normal-chat `ToolCard` component and the same toolTrace persistence shape.
- **Tool-loop guard**: at most 8 tool decision steps per engineer call; if a model keeps calling tools without ever emitting text (occasional with deep-reasoning models), one final **tool-less call** is forced so the answer is produced from the tool results — avoiding “empty solution”. Thinking accumulates across steps; text streams live.
- **Stop on consensus**: after consensus is detected in an eval round the loop converges early (no further rounds). Detection is dual-channel: the explicit `[CONSENSUS]` marker, or natural-language consensus phrasing in the output tail ("no remaining dispute", "达成一致"…); only the tail is checked to avoid false positives from “partly agreed but still disputed”.
- **Failure tolerance**: solution/suggestion/eval/final calls retry once on transient errors or empty replies (tokens/cost from both attempts are counted); only then a `TEAM_*_FAIL` is recorded, with the reason persisted as placeholder content, and the session stays usable.
- **Config semantics**: disabling team mode allows an empty/missing engineer list (config is kept for reuse and completeness checks are skipped); strict checks (1 responsible + ≥1 assistant, unique names, model availability/role binding) apply only when **enabling**.
- **Token / timing semantics**: TTFT = real first-token latency per segment (first reasoning/text delta); total time = wall-clock sum of segments; `ReasoningTokens` is a separate field (shown in the usage panel) and **included in cost** (output price). `RoundsJson` persists `text/thinking/tools` per segment for faithful replay rendering.
- **Engineer model settings**: reasoning enabled (`ThinkingEnabled/EnableReasoning`, effort Medium); reasoning deltas stream as `team_think_delta` and persist as collapsible blocks.

### Server-side Unified Configuration (Admin)
- LLM providers (enable flag / model / endpoint / encrypted api-key / temperature / context-window)
- MCP servers (Name / Endpoint / Headers JSON encrypted; **"Fetch" auto-discovers** tools/prompts/resources with schemas; per-item disable)
- Multiple Prompts & Skills (pluggable, lazy-load)
- RBAC: users / roles; role ↔ MCP / Prompt / Skill / **LLM model** bindings —— bound models are the only models the role's users can see/select, and direct API calls with an unauthorized model id are rejected server-side (`MODEL_NOT_AUTHORIZED`)
- **Internal Auth**: auth-center configs (acs / ucs…) with success-response rules and default roles (see Authentication above)

### Engines (Core)
- **LLM Router**: multi-vendor priority + round-robin + failover (mark-unhealthy circuit breaking); OpenAI-compatible & Mock clients.
- **MCP driver**: strictly follows the latest MCP spec (see [MCP 2026-07 revision](https://blog.modelcontextprotocol.io/posts/2026-07-28/)); connection pooling; call retry with backoff.
  **MCP errors go into the loop** — tool errors are fed back to the model as tool results for retry/explanation; sessions never break because of an MCP error.
- **Policy engine**: `Allow → Deny → RequireApproval` with dangerous-operation heuristics (`delete_all` etc.); approvals support pending/approved/rejected/expired.
- **Context manager**: token estimate → compress (LLM summary) → truncate; never exceeds the model length limit and never interrupts the session.

### Observability & Cost
- `trace_id` (`trc_...`) flows through Orchestration / LLM / MCP / Audit; token accounting in/out; metrics: TTFT, tokens, cost, tool latency, approval count (`/api/admin/metrics/usage`).
- Idempotent writes: unique (UserId, ClientMessageId) key; key chat logs persist across restarts.

### Logging, three channels
- **To the user**: friendly message + error code (no stack / endpoint / header)
- **To the model**: tool errors are re-fed as tool results (retryable, explainable)
- **To the logs**: full context (sanitized)

## Internationalization
- Frontend: `vue-i18n` with per-domain locale files under `web/src/i18n/locales/{en,zh}/`; English is the default locale.
- Backend: every user-visible message lives in one dictionary (`src/NextChats.Core/Localization/Texts.cs`, en/zh) — no hard-coded UI text in code. The API localizes errors via the `X-Lang` request header (or `Accept-Language`), so non-browser clients get the same language, e.g. `curl -H "X-Lang: en"`; seeded content (roles, prompts, skills, demo provider) is English.
- README: this English file and [README.zh.md](./README.zh.md) are linked at the top.

## Smoke Verification (web/scripts/ + e2e/)

```bash
node scripts/smoke.mjs          # login → create session → SSE streaming chat → persistence → metrics
node scripts/smoke-tools.mjs    # ReAct tool call (tool:echo) → dangerous approval flow (danger:delete_all)

# Internal-auth E2E (mock auth center + PowerShell suite, see e2e/README.md)
node e2e/mock-auth.js           # start mock auth center (127.0.0.1:53131)
powershell -File e2e/run-internal-auth-e2e.ps1   # assert CRUD / providers / auto-provision / uniqueness / role-based catalog
```

## MCP References
- [What's new in MCP (2026-07-28)](https://blog.modelcontextprotocol.io/posts/2026-07-28/)
- ModelContextProtocol C# SDK 2.2.0: `McpClient.CreateAsync` + `HttpClientTransport` (Streamable HTTP) / `StdioClientTransport`
