import { reactive, watch } from 'vue'
import { Service, Context } from 'cordis'
import { ElMessage } from 'element-plus'
import { http, streamPost, tokenStore, translateError } from '@/api/http'
import { i18n } from '@/i18n'
import type {
  AgentEventDto,
  AuthProviderDto,
  ChatMessageDto,
  ChatSessionDto,
  ChatSettings,
  LoginResponse,
  UserProfile,
} from '@/api/types'
import { uuid } from '@/utils/uuid'

export type ApprovalDecision = 'Approved' | 'Rejected'

// ---------------------------------------------------------------- 工具

function uid(): string {
  return uuid()
}

const nowIso = () => new Date().toISOString()

// ---------------------------------------------------------------- 消息模型

export type UiMessageStatus = 'sending' | 'complete' | 'stopped' | 'failed'

export interface ToolCard {
  key: string
  serverName?: string
  toolName: string
  argumentsJson?: string
  approvalId?: string
  approvalStatus?: 'pending' | 'approved' | 'rejected' | 'expired'
  status: 'running' | 'ok' | 'error'
  resultPreview?: string
  errorCode?: string
  durationMs?: number
}

export interface UiMessage {
  id: string
  role: 'user' | 'assistant' | 'system'
  content: string
  reasoning: string
  thinkingOpen: boolean
  tools: ToolCard[]
  contextNotes: string[]
  status: UiMessageStatus
  /** 实时流消息（本轮 send 产生）：前端以打字机呈现；历史/重放消息为 false，直接全量显示 */
  live: boolean
  images?: { fileName?: string; mimeType?: string; base64: string }[]
  model?: string
  usage?: {
    promptTokens: number
    completionTokens: number
    reasoningTokens: number
    totalTokens: number
    rounds: number
    tools: number
    cost: number
    ttftMs: number
    totalMs: number
    subAgentCount: number
    subAgentInputTokens: number
    subAgentOutputTokens: number
  }
  createdAt: number
  clientMessageId?: string
}

const emptyAssistant = (): UiMessage => ({
  id: uid(),
  role: 'assistant',
  content: '',
  reasoning: '',
  thinkingOpen: false,
  tools: [],
  contextNotes: [],
  status: 'sending',
  live: true,
  createdAt: Date.now(),
})

// ---------------------------------------------------------------- 通知

export class NotifyService extends Service {
  constructor(ctx: Context) {
    super(ctx, 'notify')
  }

  error(message: string, code?: string) {
    ElMessage.error(code ? `${message}（${code}）` : message)
  }

   info(message: string) {
    ElMessage.info(message)
  }

  warning(message: string) {
    ElMessage.warning(message)
  }

  success(message: string) {
    ElMessage.success(message)
  }
}

// ---------------------------------------------------------------- 主题（换肤）

export type ThemeName = 'aurora' | 'dawn' | 'midnight'

export class ThemeService extends Service {
  state = reactive({ theme: 'aurora' as ThemeName })

  constructor(ctx: Context) {
    super(ctx, 'theme')
    const saved = localStorage.getItem('nextchats.theme') as ThemeName | null
    if (saved === 'aurora' || saved === 'dawn' || saved === 'midnight') this.state.theme = saved
    this.apply()
  }

  apply() {
    document.documentElement.dataset.theme = this.state.theme
    document.documentElement.classList.toggle('dark', this.state.theme === 'midnight')
    localStorage.setItem('nextchats.theme', this.state.theme)
    this.ctx.emit('theme:changed', this.state.theme)
  }

  set(theme: ThemeName) {
    this.state.theme = theme
    this.apply()
  }
}

// ---------------------------------------------------------------- 个人偏好（localStorage + 服务端同步）

export class SettingsService extends Service {
  state = reactive<{
    chat: ChatSettings
    threeEnabled: boolean
    loaded: boolean
  }>({
    chat: { providerId: null, modelId: null, promptId: null, mcpServerIds: [], skillIds: [], delegationEnabled: false, subAgentModelId: null },
    threeEnabled: true,
    loaded: false,
  })

  private static readonly LOCAL_KEY = 'nextchats.prefs'

  constructor(ctx: Context) {
    super(ctx, 'settings')
    this.loadLocal()
  }

  private loadLocal() {
    try {
      const raw = localStorage.getItem(SettingsService.LOCAL_KEY)
      if (raw) {
        const parsed = JSON.parse(raw) as { chat?: ChatSettings; threeEnabled?: boolean }
        if (parsed.chat) this.state.chat = { ...this.state.chat, ...parsed.chat }
        if (typeof parsed.threeEnabled === 'boolean') this.state.threeEnabled = parsed.threeEnabled
      }
    } catch {
      /* 忽略损坏的本地缓存 */
    }
  }

  private persistLocal() {
    localStorage.setItem(
      SettingsService.LOCAL_KEY,
      JSON.stringify({ chat: this.state.chat, threeEnabled: this.state.threeEnabled }),
    )
  }

  updateChat(patch: Partial<ChatSettings>) {
    this.state.chat = { ...this.state.chat, ...patch }
    this.persistLocal()
    this.ctx.emit('chat:settings-changed', this.state.chat)
    void this.syncToServer()
  }

  toggleThree(enabled?: boolean) {
    this.state.threeEnabled = enabled ?? !this.state.threeEnabled
    this.persistLocal()
    this.ctx.emit('three:toggled', this.state.threeEnabled)
  }

  /** 推送到服务端（下次自动记住）；失败延迟重试一次，防止记忆丢失 */
  async syncToServer() {
    const put = () =>
      http.put('/api/me/settings', {
        'chat.providerId': this.state.chat.providerId ?? '',
        'chat.modelId': this.state.chat.modelId ?? '',
        'chat.promptId': this.state.chat.promptId ?? '',
        'chat.mcpServers': JSON.stringify(this.state.chat.mcpServerIds),
        'chat.skills': JSON.stringify(this.state.chat.skillIds),
        'agent.delegationEnabled': this.state.chat.delegationEnabled ? 'true' : 'false',
        'chat.subAgentModelId': this.state.chat.subAgentModelId ?? '',
      })
    try {
      await put()
    } catch {
      await new Promise((r) => setTimeout(r, 5000))
      try {
        await put()
      } catch {
        /* 仍失败则放弃，本地已持久化，下次打开会再次尝试 */
      }
    }
  }

  /** 登录后拉取服务端记忆（服务端优先） */
  async pullFromServer() {
    try {
      const remote = (await http.get<Record<string, string>>('/api/me/settings')) ?? {}
      const providerId = remote['chat.providerId'] || null
      const modelId = remote['chat.modelId'] || null
      const promptId = remote['chat.promptId'] || null
      const delegationEnabled = remote['agent.delegationEnabled'] === 'true'
      const subAgentModelId = remote['chat.subAgentModelId'] || null
      let mcpServerIds: string[] = []
      let skillIds: string[] = []
      try {
        mcpServerIds = JSON.parse(remote['chat.mcpServers'] ?? '[]')
        skillIds = JSON.parse(remote['chat.skills'] ?? '[]')
      } catch {
        /* 忽略 */
      }
      this.state.chat = { providerId, modelId, promptId, mcpServerIds, skillIds, delegationEnabled, subAgentModelId }
      this.persistLocal()
      this.state.loaded = true
    } catch {
      this.state.loaded = true
    }
  }
}

// ---------------------------------------------------------------- 认证

export class AuthService extends Service {
  state = reactive({ user: null as UserProfile | null, ready: false })

  constructor(ctx: Context) {
    super(ctx, 'auth')
  }

  async login(username: string, password: string, authType?: string) {
    const res = await http.post<LoginResponse>('/api/auth/login', {
      username,
      password,
      authType: authType || 'default',
    })
    tokenStore.set(res.token)
    tokenStore.setRefresh(res.refreshToken)
    this.state.user = res.user
    this.state.ready = true
    this.ctx.emit('auth:changed', res.user)
  }

  /** 登录页可选的鉴权方式（default 恒存在；其余为已启用的内部鉴权，如 acs / ucs） */
  async fetchAuthProviders() {
    return http.get<AuthProviderDto[]>('/api/auth/providers')
  }

  /** 登录失效（401 且刷新失败）时清空会话状态：避免守卫误判“已登录”导致跳转死锁 */
  invalidate() {
    tokenStore.clearAll()
    this.state.user = null
    this.state.ready = true
  }

  async logout() {
    // 通知服务端撤销本会话刷新令牌（尽力而为：access 已过期时 401 也不阻塞本地登出）
    const rt = tokenStore.getRefresh()
    if (rt) {
      try {
        await http.post('/api/auth/logout', { refreshToken: rt })
      } catch {
        /* 忽略：本地登出照常进行 */
      }
    }
    tokenStore.clearAll()
    this.state.user = null
    this.state.ready = true
    this.ctx.emit('auth:changed', null)
  }

  /** 启动时恢复会话 */
  async restore() {
    if (!tokenStore.get()) {
      this.state.ready = true
      return
    }
    try {
      this.state.user = await http.get<UserProfile>('/api/me')
      if (this.state.user) {
        // 刷新后对齐最新能力目录与服务端记忆（失败不阻塞）
        void (this.ctx.get('catalog') as CatalogService).load().catch(() => {})
        void (this.ctx.get('settings') as SettingsService).pullFromServer().catch(() => {})
        // 登录态恢复后尽早预载会话列表（ChatView 挂载后还会再兜底一次）
        void (this.ctx.get('session') as SessionService).loadAll().catch(() => {})
      }
    } catch {
      tokenStore.clear()
      this.state.user = null
    } finally {
      this.state.ready = true
    }
  }
}

// ---------------------------------------------------------------- 能力目录（个人可见范围，服务端按角色过滤）

export class CatalogService extends Service {
  state = reactive({
    prompts: [] as { id: string; name: string; description?: string; summary?: string }[],
    mcps: [] as {
      id: string
      name: string
      description?: string
      transport: string
      endpoint?: string
      items: { id: string; kind: string; name: string; description?: string }[]
    }[],
    skills: [] as { id: string; name: string; description?: string; summary?: string; metaToolName: string }[],
    providers: [] as {
      id: string
      name: string
      kind: string
      isHealthy: boolean
      models: { id: string; name: string; isVision: boolean; contextWindow: number; priceInPer1K: number; priceOutPer1K: number }[]
    }[],
    loaded: false,
  })

  constructor(ctx: Context) {
    super(ctx, 'catalog')
  }

  async load() {
    const data = await http.get<{
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
        isHealthy: boolean
        models: { id: string; name: string; isVision: boolean; contextWindow: number; priceInPer1K: number; priceOutPer1K: number }[]
      }[]
    }>('/api/me/catalog')
    Object.assign(this.state, data, { loaded: true })
  }
}

// ---------------------------------------------------------------- 会话

export class SessionService extends Service {
  state = reactive({
    sessions: [] as ChatSessionDto[],
    currentId: null as string | null,
    loading: false,
    sidebarCollapsed: false,
  })

  constructor(ctx: Context) {
    super(ctx, 'session')
    try {
      this.state.sidebarCollapsed = localStorage.getItem('nextchats.sidebar') === '1'
    } catch {
      /* 忽略 */
    }
  }

  get current(): ChatSessionDto | undefined {
    return this.state.sessions.find((s) => s.id === this.state.currentId)
  }

  async loadAll() {
    // 幂等重入：并发/重复调用都真实执行（fetch 幂等），不做 loading 去重，
    // 避免“某次调用被跳过”导致侧栏永不出现
    this.state.loading = true
    try {
      this.state.sessions = await http.get<ChatSessionDto[]>('/api/chat/sessions')
      if (!this.state.currentId && this.state.sessions.length > 0) {
        this.select(this.state.sessions[0].id)
      }
    } catch (err) {
      console.error('[session] loadAll failed, retrying once:', err)
      // 瞬时网络/服务波动：短暂等待后重试一次
      await new Promise((r) => setTimeout(r, 900))
      try {
        this.state.sessions = await http.get<ChatSessionDto[]>('/api/chat/sessions')
        if (!this.state.currentId && this.state.sessions.length > 0) {
          this.select(this.state.sessions[0].id)
        }
      } catch (err2) {
        console.error('[session] loadAll retry failed:', err2)
        /* 重试仍失败：静默保留空列表，侧栏提供手动刷新 */
      }
    } finally {
      this.state.loading = false
    }
  }

  async ensureSession() {
    if (this.state.sessions.length === 0 || !this.state.currentId) {
      const session = await http.post<ChatSessionDto>('/api/chat/sessions', { title: '' })
      this.state.sessions.unshift(session)
      this.select(session.id)
    }
    return this.current!
  }

  select(id: string | null) {
    this.state.currentId = id
    this.ctx.emit('session:current-changed', id)
  }

  async create() {
    const session = await http.post<ChatSessionDto>('/api/chat/sessions', { title: '' })
    this.state.sessions.unshift(session)
    this.select(session.id)
  }

  async rename(id: string, title: string) {
    await http.put(`/api/chat/sessions/${id}`, { title })
    const found = this.state.sessions.find((s) => s.id === id)
    if (found) found.title = title
  }

  async remove(id: string) {
    await http.delete(`/api/chat/sessions/${id}`)
    this.state.sessions = this.state.sessions.filter((s) => s.id !== id)
    if (this.state.currentId === id) {
      this.select(this.state.sessions[0]?.id ?? null)
    }
    this.ctx.get('chat')?.clearSessionCache?.(id)
  }

  toggleSidebar() {
    this.state.sidebarCollapsed = !this.state.sidebarCollapsed
    localStorage.setItem('nextchats.sidebar', this.state.sidebarCollapsed ? '1' : '0')
  }
}

// ---------------------------------------------------------------- 聊天（SSE 流式 + 可折叠思考 + 中断）

export class ChatService extends Service {
  state = reactive({
    /** sessionId → 消息列表（含进行中的流式消息） */
    messages: {} as Record<string, UiMessage[]>,
    /** 是否为派生值：streamingSids 非空即 true（保持字段名兼容旧消费方） */
    streaming: false,
    /** 当前正在流式推理的会话 id 集合（多会话可同时推理，互不影响） */
    streamingSids: [] as string[],
    /** 待用户审批的工具调用 */
    pendingApproval: null as {
      approvalId: string
      serverName: string
      toolName: string
      argumentsJson?: string
      messageId?: string
    } | null,
    /** 当前正在渲染的 assistant 消息（供流式刷新） */
    activeMessageId: null as string | null,
  })

  private controllers = new Map<string, AbortController>()
  private historyLoaded = new Set<string>()

  /** 记录/解除某会话的流式状态：streaming 与事件按集合派生，多会话并发互不覆盖 */
  private markStreaming(sid: string, on: boolean) {
    const sids = this.state.streamingSids
    const i = sids.indexOf(sid)
    if (on && i < 0) sids.push(sid)
    if (!on && i >= 0) sids.splice(i, 1)
    const any = sids.length > 0
    if (this.state.streaming !== any) {
      this.state.streaming = any
      this.ctx.emit('chat:streaming-changed', any)
    }
  }

  /** 指定会话当前是否正在流式推理 */
  isStreaming(sid: string | null): boolean {
    return !!sid && this.state.streamingSids.includes(sid)
  }

  constructor(ctx: Context) {
    super(ctx, 'chat')
    ctx.on('session:current-changed', (id) => {
      if (id && !this.historyLoaded.has(id)) {
        void this.loadHistory(id)
      }
    })
  }

  messagesOf(sessionId: string | null): UiMessage[] {
    if (!sessionId) return []
    return this.state.messages[sessionId] ?? []
  }

  async loadHistory(sessionId: string) {
    if (this.historyLoaded.has(sessionId)) return
    try {
      const list = await http.get<ChatMessageDto[]>(`/api/chat/sessions/${sessionId}/messages`)
      this.state.messages[sessionId] = list.map((m) => this.fromDto(m)).filter((m) => m.role !== 'system' || m.content?.length) ?? []
      this.historyLoaded.add(sessionId)
    } catch {
      /* 历史加载失败不阻塞 */
    }
  }

  private fromDto(m: ChatMessageDto): UiMessage {
    let tools: ToolCard[] = []
    try {
      if (m.role === 'Assistant' && m.toolCallsJson) {
        const parsed = JSON.parse(m.toolCallsJson)
        if (Array.isArray(parsed)) tools = parsed as ToolCard[]
      }
    } catch {
      /* 忽略 */
    }
    return {
      id: m.id,
      role: m.role === 'Assistant' ? 'assistant' : m.role === 'User' ? 'user' : 'system',
      content: m.content ?? '',
      reasoning: m.reasoning ?? '',
      thinkingOpen: false,
      tools,
      contextNotes: [],
      status: (m.status === 'Complete' ? 'complete' : m.status === 'Stopped' ? 'stopped' : m.status === 'Failed' ? 'failed' : 'sending') as UiMessageStatus,
      live: false,
      model: m.model,
      // 历史消息：指标已落库，刷新后同样可查看完整 Token 明细
      usage: {
        promptTokens: m.promptTokens ?? 0,
        completionTokens: m.completionTokens ?? 0,
        reasoningTokens: m.reasoningTokens ?? 0,
        totalTokens: (m.promptTokens ?? 0) + (m.completionTokens ?? 0),
        rounds: m.rounds ?? 0,
        tools: m.toolCalls ?? 0,
        cost: m.cost ?? 0,
        ttftMs: m.ttftMs ?? 0,
        totalMs: m.totalMs ?? 0,
        subAgentCount: m.subAgentCount ?? 0,
        subAgentInputTokens: m.subAgentInputTokens ?? 0,
        subAgentOutputTokens: m.subAgentOutputTokens ?? 0,
      },
      createdAt: new Date(m.createdAt).getTime(),
    }
  }

  clearSessionCache(sessionId: string) {
    delete this.state.messages[sessionId]
    this.historyLoaded.delete(sessionId)
  }

  async send(
    text: string,
    images?: { fileName?: string; mimeType?: string; base64: string }[],
    thinking?: { enabled: boolean; effort: string },
  ) {
    const sessionService = this.ctx.get('session') as SessionService
    const session = await sessionService.ensureSession()
    const list = this.messagesOf(session.id)

    const userMsg: UiMessage = {
      id: uid(),
      role: 'user',
      content: text,
      reasoning: '',
      thinkingOpen: false,
      tools: [],
      contextNotes: [],
      status: 'complete',
      live: false,
      images,
      createdAt: Date.now(),
    }
    const clientMessageId = uid()
    const pending = emptyAssistant()
    pending.clientMessageId = clientMessageId
    list.push(userMsg, pending)
    this.state.activeMessageId = pending.id

    // 首次消息自动命名会话
    if (!session.title) {
      const title = text.replace(/\s+/g, ' ').slice(0, 20)
      if (title) {
        session.title = title
        void sessionService.rename(session.id, title).catch(() => undefined)
      }
    }

    await this.runStream(session.id, pending, text, images, thinking, clientMessageId)
  }

  /** 话题级删除：删除该消息（含自身）及其后的所有消息 */
  async deleteFrom(messageId: string) {
    const sessionService = this.ctx.get('session') as SessionService
    const sid = sessionService.state.currentId
    if (!sid) return
    try {
      await http.delete(`/api/chat/sessions/${sid}/messages/${messageId}`)
    } catch (e) {
      ;(this.ctx.get('notify') as NotifyService).error((e as { message?: string }).message ?? '', (e as { code?: string }).code)
      return
    }
    this.historyLoaded.delete(sid)
    await this.loadHistory(sid)
  }

  /**
   * 话题级重新生成：截断该 assistant 消息（含自身）及其后，保留对应 user 提问，
   * 以该提问为输入原地流式重跑本轮（后端不追加重复 user 消息）。
   */
  async regenerate(messageId: string) {
    const sessionService = this.ctx.get('session') as SessionService
    const sid = sessionService.state.currentId
    if (!sid) return
    const list = this.messagesOf(sid)
    const idx = list.findIndex((m) => m.id === messageId)
    if (idx < 0) return
    let topic: UiMessage | undefined
    for (let i = idx - 1; i >= 0; i--) {
      if (list[i].role === 'user') {
        topic = list[i]
        break
      }
    }
    const text = topic?.content?.trim()
    const topicId = topic?.id
    if (!text || !topicId) return

    try {
      await http.delete(`/api/chat/sessions/${sid}/messages/${messageId}`)
    } catch (e) {
      ;(this.ctx.get('notify') as NotifyService).error((e as { message?: string }).message ?? '', (e as { code?: string }).code)
      return
    }
    // 内存截断：删除该条起（含）之后
    list.splice(idx)
    const pending = emptyAssistant()
    const clientMessageId = uid()
    pending.clientMessageId = clientMessageId
    list.push(pending)
    this.state.activeMessageId = pending.id
    await this.runStream(sid, pending, text, undefined, undefined, clientMessageId, topicId)
  }

  /** 流式主体：推送 pending 回复并消费 SSE 事件（send 与 regenerate 共用） */
  private async runStream(
    sid: string,
    pending: UiMessage,
    text: string,
    images?: { fileName?: string; mimeType?: string; base64: string }[],
    thinking?: { enabled: boolean; effort: string },
    clientMessageId?: string,
    regenerateFromMessageId?: string,
  ) {
    const settings = (this.ctx.get('settings') as SettingsService).state.chat
    const controller = new AbortController()
    this.controllers.set(sid, controller)
    this.markStreaming(sid, true)

    const onEvent = (ev: Record<string, unknown>) => this.applyEvent(sid, pending, ev as unknown as AgentEventDto)

    try {
      await streamPost(
        '/api/chat/stream',
        {
          sessionId: sid,
          message: text,
          images: images?.map((i) => ({ fileName: i.fileName ?? undefined, mimeType: i.mimeType ?? undefined, base64: i.base64 })),
          clientMessageId,
          providerId: settings.providerId,
          modelId: settings.modelId,
          promptId: settings.promptId,
          mcpServerIds: settings.mcpServerIds,
          skillIds: settings.skillIds,
          thinkingEnabled: thinking?.enabled,
          thinkingEffort: thinking?.effort,
          regenerateFromMessageId,
        },
        onEvent,
        controller.signal,
      )
      if (pending.status === 'sending') pending.status = 'complete'
    } catch (err) {
      const isAbort = (err as { name?: string })?.name === 'AbortError'
      if (isAbort) {
        pending.status = 'stopped'
      } else {
        pending.status = 'failed'
        const e = err as { code?: string; message?: string }
        ;(this.ctx.get('notify') as NotifyService).error(translateError(e.code, e.message ?? ''), e.code)
      }
    } finally {
      this.markStreaming(sid, false)
      this.controllers.delete(sid)
      this.state.activeMessageId = null
      // 流结束后，从服务端拉一次最新消息，保证持久化内容一致
      const sessionService2 = this.ctx.get('session') as SessionService
      if (sessionService2.state.currentId === sid) {
        this.historyLoaded.delete(sid)
        void this.loadHistory(sid)
      }
    }
  }

  private applyEvent(sessionId: string, pending: UiMessage, ev: AgentEventDto) {
    switch (ev.kind) {
      case 'thinking_start':
        // 展开思考区；不要清空 pending.reasoning —— 同一 AI 消息内多轮工具循环会产生多段思考，
        // 清空会让前一轮思考链从界面上"消失"（服务端也是聚合存储的，刷新后同样完整）
        pending.thinkingOpen = true
        break
      case 'round_start':
        // 每轮开始立即展开思考区：真实模型首 token（TTFT）可能长达数秒，
        // 提前让“正在思考…”占位可见，避免用户以为没有响应
        pending.thinkingOpen = true
        break
      case 'thinking_delta':
        pending.reasoning += ev.text ?? ''
        break
      case 'thinking_end':
        pending.thinkingOpen = false
        break
      case 'text_delta':
        if (pending.thinkingOpen) pending.thinkingOpen = false
        pending.content += ev.text ?? ''
        break
      case 'tool_start': {
        const card: ToolCard = {
          key: `${ev.serverName ?? ''}.${ev.toolName ?? ''}.${pending.tools.length}`,
          serverName: ev.serverName,
          toolName: ev.toolName ?? 'tool',
          argumentsJson: ev.argumentsJson,
          approvalId: ev.approvalId,
          approvalStatus: ev.approvalId ? 'pending' : undefined,
          status: 'running',
        }
        pending.tools.push(card)
        if (ev.approvalId) {
          this.state.pendingApproval = {
            approvalId: ev.approvalId,
            serverName: ev.serverName ?? '',
            toolName: ev.toolName ?? 'tool',
            argumentsJson: ev.argumentsJson,
            messageId: pending.id,
          }
        }
        break
      }
      case 'approval_updated': {
        const card = pending.tools.find((t) => t.approvalId === ev.approvalId)
        if (card) card.approvalStatus = (ev.approvalStatus as ToolCard['approvalStatus']) ?? 'pending'
        if (ev.approvalStatus === 'approved' || ev.approvalStatus === 'rejected' || ev.approvalStatus === 'expired') {
          if (this.state.pendingApproval?.approvalId === ev.approvalId) this.state.pendingApproval = null
        }
        break
      }
      case 'tool_result':
      case 'tool_error': {
        const card = pending.tools.find((t) => t.serverName === ev.serverName && t.toolName === ev.toolName)
        const target = card ?? pending.tools[pending.tools.length - 1]
        if (target) {
          target.status = ev.success ? 'ok' : 'error'
          target.resultPreview = ev.resultPreview
          target.errorCode = ev.errorCode
          target.durationMs = ev.durationMs
        }
        break
      }
      case 'message_done':
        if (ev.messageId) pending.id = ev.messageId
        break
      case 'round_start':
        pending.contextNotes.push(i18n.global.t('chat.roundsNote', { round: ev.round }))
        break
      case 'context': {
        const note = ev.text ?? ev.message
        if (note) pending.contextNotes.push(note)
        break
      }
        break
      case 'error': {
        if (ev.code === 'INTERRUPTED') {
          pending.status = 'stopped'
        } else {
          pending.status = 'failed'
          ;(this.ctx.get('notify') as NotifyService).error(ev.message ?? i18n.global.t('err.UNKNOWN'), ev.code)
        }
        break
      }
      case 'done': {
        pending.status = 'complete'
        pending.model = ev.model
        if (ev.totalTokens != null) {
          pending.usage = {
            promptTokens: ev.promptTokens ?? 0,
            completionTokens: ev.completionTokens ?? 0,
            reasoningTokens: ev.reasoningTokens ?? 0,
            totalTokens: ev.totalTokens ?? 0,
            rounds: ev.rounds ?? 0,
            tools: ev.toolCalls ?? 0,
            cost: ev.cost ?? 0,
            ttftMs: ev.ttftMs ?? 0,
            totalMs: ev.totalMs ?? 0,
            subAgentCount: ev.subAgentCount ?? 0,
            subAgentInputTokens: ev.subAgentInputTokens ?? 0,
            subAgentOutputTokens: ev.subAgentOutputTokens ?? 0,
          }
        }
        break
      }
      default:
        break
    }
  }

  /** 中断当前推理 */
  async interrupt() {
    const sessionService = this.ctx.get('session') as SessionService
    const session = sessionService.current
    if (!session) return
    void http.post(`/api/chat/sessions/${session.id}/interrupt`, {}).catch(() => undefined)
    this.controllers.get(session.id)?.abort()
  }

  /** 用户决策：批准/拒绝工具调用 */
  async decideApproval(approvalId: string, decision: ApprovalDecision, reason?: string) {
    await http.post(`/api/admin/approvals/${approvalId}/decide`, {
      approved: decision === 'Approved',
      reason: reason ?? null,
    })
    if (this.state.pendingApproval?.approvalId === approvalId) this.state.pendingApproval = null
    // 更新本地卡片状态
    for (const list of Object.values(this.state.messages)) {
      for (const m of list) {
        const card = m.tools.find((t) => t.approvalId === approvalId)
        if (card) card.approvalStatus = decision === 'Approved' ? 'approved' : 'rejected'
      }
    }
  }
}

// ---------------------------------------------------------------- 3D 背景（Three.js 场景开关）

export class ThreeService extends Service {
  state = reactive({ enabled: true })

  constructor(ctx: Context) {
    super(ctx, 'three')
    try {
      const saved = localStorage.getItem('nextchats.three')
      if (saved !== null) this.state.enabled = saved === '1'
    } catch {
      /* 忽略 */
    }
    watch(
      () => this.state.enabled,
      (v) => {
        localStorage.setItem('nextchats.three', v ? '1' : '0')
        this.ctx.emit('three:toggled', v)
      },
    )
  }
}
