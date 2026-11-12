/**
 * 智能体工具（agent-runner）配置与类型定义：
 * - 智能体结构（id, name, prompt, icon, sortOrder）
 * - 默认内置预设智能体列表（开箱即用，支持增删改查，最多50个）
 * - 排序规则：sortOrder（序号）越大的在抽屉里排序越靠前；相同按创建/加入顺序
 * - localStorage 状态键管理
 */
export interface AgentDefinition {
  id: string
  name: string
  prompt: string
  icon: string
  sortOrder?: number
  isBuiltin?: boolean
}

export const MAX_AGENTS = 50

export const DEFAULT_AGENTS: AgentDefinition[] = [
  {
    id: 'general-assistant',
    name: '全能助手',
    icon: 'bot',
    sortOrder: 100,
    prompt: `你是一位全能 AI 助理。请以严谨、客观、清晰的方式回答用户提出的问题。重点突出、逻辑严密，并在必要时给出结构化建议。`,
    isBuiltin: true,
  },
  {
    id: 'code-expert',
    name: '代码专家',
    icon: 'code',
    sortOrder: 90,
    prompt: `你是一位资深全栈架构师与代码专家。请遵循：
1. 代码质量：遵循最佳实践与规范，提供优雅、高效、具备类型安全的代码。
2. 架构考量：关注可维护性、扩展性与异常边界处理。
3. 讲解清晰：简要说明核心思路与潜在陷阱，避免废话。`,
    isBuiltin: true,
  },
  {
    id: 'copywriter',
    name: '文案润色',
    icon: 'feather',
    sortOrder: 80,
    prompt: `你是一位资深文案策划与文字打磨专家。请帮助用户对文本进行润色、改写与优化。
- 保证表达生动、文笔流畅、富有感染力或契合特定场景（商业/新闻/社交/公文）。
- 纠正病句与错别字，提升阅读节奏感。`,
    isBuiltin: true,
  },
  {
    id: 'translator',
    name: '多语专家',
    icon: 'translate',
    sortOrder: 70,
    prompt: `你是一位母语级多语种翻译专家。
- 忠实原文、术语准确，拒绝生硬直译与翻译腔。
- 在翻译后可简要提供生词解析或文化背景差异，便于深入理解。`,
    isBuiltin: true,
  },
  {
    id: 'analyst',
    name: '分析与洞察',
    icon: 'chart',
    sortOrder: 60,
    prompt: `你是一位商业与数据分析师。
- 善于结构化拆解问题（采用 MECE 原则、SWOT、金字塔原理等）。
- 从现象深入本质，提炼核心痛点并给出可落地的行动建议。`,
    isBuiltin: true,
  },
  {
    id: 'brainstorm',
    name: '头脑风暴',
    icon: 'lightbulb',
    sortOrder: 50,
    prompt: `你是一位擅长创意思维与横向思考的头脑风暴伙伴。
- 尽可能提供打破常规、多维度、多视角的创新方案。
- 鼓励开放性探索，再逐步收敛至可执行的落地点。`,
    isBuiltin: true,
  },
]

export const LS = {
  activeAgentId: 'nc.tool.agent-runner.active-agent',
  model: 'nc.tool.agent-runner.model',
  stream: 'nc.tool.agent-runner.stream',
  // 每个智能体对应的问与答，存放在 JSON map 中：Record<agentId, { question: string; answer: string }>
  sessions: 'nc.tool.agent-runner.sessions',
} as const

export interface AgentSessionData {
  question: string
  answer: string
}

export function loadStoredSessions(): Record<string, AgentSessionData> {
  try {
    const raw = localStorage.getItem(LS.sessions)
    return raw ? JSON.parse(raw) : {}
  } catch {
    return {}
  }
}

export function saveStoredSessions(data: Record<string, AgentSessionData>): void {
  try {
    localStorage.setItem(LS.sessions, JSON.stringify(data))
  } catch {
    /* ignore */
  }
}

export function loadActiveAgentId(): string {
  try {
    return localStorage.getItem(LS.activeAgentId) || DEFAULT_AGENTS[0].id
  } catch {
    return DEFAULT_AGENTS[0].id
  }
}

export function saveActiveAgentId(id: string): void {
  try {
    localStorage.setItem(LS.activeAgentId, id)
  } catch {
    /* ignore */
  }
}

export function loadModel(): string {
  try {
    return localStorage.getItem(LS.model) || ''
  } catch {
    return ''
  }
}

export function saveModel(model: string): void {
  try {
    localStorage.setItem(LS.model, model)
  } catch {
    /* ignore */
  }
}

export function loadStreamMode(): boolean {
  try {
    const v = localStorage.getItem(LS.stream)
    return v === null ? true : v === 'true'
  } catch {
    return true
  }
}

export function saveStreamMode(v: boolean): void {
  try {
    localStorage.setItem(LS.stream, String(v))
  } catch {
    /* ignore */
  }
}
