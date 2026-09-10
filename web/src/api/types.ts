/** 与后端 DTO 对应的类型定义 */

export interface UserProfile {
  id: string
  username: string
  displayName: string
  email?: string
  roles: string[]
  isAdmin: boolean
  /** 只读模式：即使拥有 admin 角色也只能查看后台，不能新增/编辑/删除 */
  isReadonly?: boolean
  /** 账号类型：default=本地密码账号；其他（acs…）=内部鉴权账号 */
  authType?: string
}

export interface AuthProviderDto {
  name: string
}

export interface LoginResponse {
  token: string
  /** 刷新令牌：access 过期后用它静默续期（每次使用轮换） */
  refreshToken: string
  /** access token 有效期（秒） */
  expiresIn: number
  user: UserProfile
}

export interface LlmModelDto {
  id: string
  name: string
  enabled: boolean
  isVision: boolean
  contextWindow: number
  priceInPer1K: number
  priceOutPer1K: number
  priority: number
  createdAt: string
  updatedAt: string
}

export interface LlmProviderDto {
  id: string
  name: string
  kind: string
  baseUrl?: string
  timeoutSeconds: number
  enabled: boolean
  priority: number
  isHealthy: boolean
  lastError?: string
  apiKeyMasked?: string
  thinkingParam?: string
  createdAt: string
  updatedAt: string
  models: LlmModelDto[]
}

export interface McpCatalogItemDto {
  id: string
  kind: 'Tool' | 'Prompt' | 'Resource'
  name: string
  description?: string
  schemaJson?: string
  enabled: boolean
}

export interface McpServerDto {
  id: string
  name: string
  transport: string
  endpoint?: string
  headersMasked?: string
  enabled: boolean
  isVision: boolean
  timeoutSeconds: number
  description?: string
  instructions?: string
  metadataJson?: string
  lastError?: string
  lastFetchedAt?: string
  stdioCommand?: string
  stdioArgsJson?: string
  toolCount: number
  promptCount: number
  resourceCount: number
  items: McpCatalogItemDto[]
}

export interface PromptDto {
  id: string
  name: string
  description?: string
  summary?: string
  content: string
  enabled: boolean
  tags?: string[]
  version: number
  updatedAt: string
}

export interface SkillDto {
  id: string
  name: string
  description?: string
  summary?: string
  metaToolName: string
  instruction: string
  enabled: boolean
  exampleInput?: string
  exampleOutput?: string
  modelOverride?: string
  maxNestedSteps: number
}

export interface RoleDto {
  id: string
  name: string
  code: string
  description?: string
  isSystem: boolean
  mcpServerIds: string[]
  promptIds: string[]
  skillIds: string[]
  modelIds: string[]
}

export interface UserDto {
  id: string
  username: string
  displayName?: string
  email?: string
  status: string
  /** 账号类型：default=本地密码账号；其他（acs…）=内部鉴权账号 */
  authType?: string
  /** 只读模式：仅可查看后台，不能新增/编辑/删除 */
  isReadonly?: boolean
  createdAt: string
  lastLoginAt?: string
  roles: { id: string; name: string; code: string }[]
}

export type InternalAuthRuleOperator = 'NotEmpty' | 'Equals'

export interface InternalAuthSuccessRuleDto {
  id?: string
  field: string
  operator: InternalAuthRuleOperator
  expectedValue?: string
}

export interface InternalAuthProviderDto {
  id: string
  name: string
  api: string
  httpMethod: string
  requestFormat: string
  usernameField: string
  passwordField: string
  enabled: boolean
  timeoutSeconds: number
  createdAt: string
  updatedAt: string
  successRules: InternalAuthSuccessRuleDto[]
  defaultRoleIds: string[]
}

export interface ChatSessionDto {
  id: string
  userId: string
  title: string
  status: string
  createdAt: string
  updatedAt: string
  lastMessageAt?: string
}

export interface ChatMessageDto {
  id: string
  sessionId: string
  role: 'System' | 'User' | 'Assistant' | 'Tool'
  content?: string
  reasoning?: string
  toolCallsJson?: string
  status: 'Sending' | 'Complete' | 'Stopped' | 'Failed'
  model?: string
  promptTokens: number
  completionTokens: number
  reasoningTokens: number
  ttftMs: number
  totalMs: number
  rounds: number
  toolCalls: number
  cost: number
  createdAt: string
}

export interface ToolApprovalDto {
  id: string
  traceId: string
  userId: string
  sessionId: string
  mcpServerName: string
  toolName: string
  argumentsJson?: string
  status: 'Pending' | 'Approved' | 'Rejected' | 'Expired' | 'Cancelled'
  reason?: string
  expiresAt: string
  createdAt: string
}

export interface AuditLogDto {
  id: string
  traceId: string
  userId?: string
  category: string
  action: string
  target?: string
  detailJson?: string
  ip?: string
  isSuspicious: boolean
  createdAt: string
}

export interface UsageTotals {
  promptTokens: number
  completionTokens: number
  totalTokens: number
  cost: number
  requests: number
  avgTtftMs: number
  avgTotalMs: number
  toolCalls: number
  toolErrors: number
  approvals: number
}

export interface CatalogDto {
  prompts: { id: string; name: string; description?: string; summary?: string }[]
  mcps: {
    id: string
    name: string
    description?: string
    transport: string
    endpoint?: string
    items: { id: string; kind: string; name: string; description?: string }[]
  }[]
  skills: { id: string; name: string; description?: string; summary?: string; metaToolName: string }[]
  providers: {
    id: string
    name: string
    kind: string
    models: { id: string; name: string; isVision: boolean; contextWindow: number; priceInPer1K: number; priceOutPer1K: number }[]
  }[]
}

/** SSE Agent 事件 */
export interface AgentEventDto {
  kind: string
  traceId?: string
  text?: string
  reason?: string
  serverName?: string
  toolName?: string
  argumentsJson?: string
  resultPreview?: string
  success?: boolean
  durationMs?: number
  attempt?: number
  errorCode?: string
  approvalId?: string
  approvalStatus?: string
  round?: number
  messageId?: string
  code?: string
  message?: string
  promptTokens?: number
  completionTokens?: number
  reasoningTokens?: number
  totalTokens?: number
  rounds?: number
  toolCalls?: number
  cost?: number
  ttftMs?: number
  totalMs?: number
  model?: string
}

export interface ChatSettings {
  providerId?: string | null
  modelId?: string | null
  promptId?: string | null
  mcpServerIds: string[]
  skillIds: string[]
}
