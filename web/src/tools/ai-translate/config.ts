/**
 * 翻译专家系统提示词（本插件内可配置）：
 * UI「专家设置」面板允许用户修改并持久到 localStorage，此常量为出厂默认。
 */
export const DEFAULT_TRANSLATE_PROMPT = `你是一位专业翻译专家，精通英语与简体中文（母语级）。请遵循以下规则：
1. 忠实原文：准确传达语义、语气与细微含义，不增删信息、不概括、不发挥。
2. 保留格式：换行、编号、列表、代码块、Markdown 结构与占位符（如 {name}）原样保留。
3. 术语规范：使用行业通行译法；专有名词、产品名、缩写首次出现时可在括号内保留原文。
4. 译文自然流畅：符合目标语言的表达习惯，避免翻译腔。
5. 只输出译文本身：不要附带解释、备注、道歉或客套话。`

/** localStorage 状态键（工具内所有状态仅存浏览器本地） */
export const LS = {
  prompt: 'nc.tool.ai-translate.prompt',
  direction: 'nc.tool.ai-translate.direction',
  model: 'nc.tool.ai-translate.model',
  stream: 'nc.tool.ai-translate.stream',
} as const

export type Direction = 'en2zh' | 'zh2en'

export function loadPrompt(): string {
  try {
    return localStorage.getItem(LS.prompt) || DEFAULT_TRANSLATE_PROMPT
  } catch {
    return DEFAULT_TRANSLATE_PROMPT
  }
}

export function savePrompt(v: string): void {
  try {
    localStorage.setItem(LS.prompt, v)
  } catch {
    /* ignore */
  }
}

export function loadDirection(): Direction {
  try {
    return localStorage.getItem(LS.direction) === 'zh2en' ? 'zh2en' : 'en2zh'
  } catch {
    return 'en2zh'
  }
}

export function loadModel(): string {
  try {
    return localStorage.getItem(LS.model) ?? ''
  } catch {
    return ''
  }
}

/**
 * 输出模式开关（默认 false = 完整模式）：上游网关（llm-cs）在流式首增量存在偶发丢字
 * （本服务逐帧透传、无法感知/补全），完整模式走非流式补全（单次完整 JSON，无增量边界，
 * 可根除"译文缺开头几个字"）；true = 流式打字机模式（保留给需要即时预览的用户）。
 */
export function loadStreamMode(): boolean {
  try {
    const v = localStorage.getItem(LS.stream)
    return v !== null ? v === '1' || v === 'true' : false
  } catch {
    return false
  }
}

export function saveStreamMode(v: boolean): void {
  try {
    localStorage.setItem(LS.stream, v ? '1' : '0')
  } catch {
    /* ignore */
  }
}
