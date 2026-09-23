<script setup lang="ts">
import { computed, nextTick, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import MarkdownIt from 'markdown-it'
import mermaid from 'mermaid'
import type { ToolCard as ToolCardModel, UiMessage } from '@/kernel/plugins'
import ToolCard from '@/components/chat/ToolCard.vue'
import ImageViewer from '@/components/chat/ImageViewer.vue'
import { kernel } from '@/kernel'
import { copyText } from '@/utils/clipboard'
import { tokenStore } from '@/api/http'
import { captureElementToPng, downloadBlob, stamp } from '@/utils/capture'
import { installMarkdownMath } from '@/utils/markdownMath'
import { canFormat, formatCode, highlightCode, hljs } from '@/utils/codeBlock'
import 'katex/dist/katex.min.css'

const props = defineProps<{ message: UiMessage }>()

const emit = defineEmits<{ regenerate: [messageId: string]; remove: [message: UiMessage]; favorite: [message: UiMessage] }>()

const { t } = useI18n()
const thinkingOpen = ref(false)

const isAssistant = computed(() => props.message.role === 'assistant')

/** 图片可显示源（与 message.images 一一对应）：持久化 url 经鉴权 fetch → blob objectURL；内存 pending 直接 base64 data-url */
const loadedSrcs = ref<string[]>([])
watch(
  () => props.message.images,
  async (imgs) => {
    const items = imgs ?? []
    const srcs: string[] = []
    for (let i = 0; i < items.length; i++) {
      const x = items[i]
      if (x.url) {
        try {
          const res = await fetch(x.url, {
            headers: tokenStore.get() ? { Authorization: `Bearer ${tokenStore.get()}` } : {},
          })
          srcs.push(res.ok ? URL.createObjectURL(await res.blob()) : '')
        } catch {
          srcs.push('')
        }
      } else {
        srcs.push(`data:${x.mimeType || 'image/png'};base64,${x.base64 ?? ''}`)
      }
    }
    loadedSrcs.value.forEach((u) => {
      if (u.startsWith('blob:')) URL.revokeObjectURL(u)
    })
    loadedSrcs.value = srcs
  },
  { immediate: true },
)
/** 图片全屏查看器图源（与消息图片顺序一致） */
const previewSrcList = computed(() => [...loadedSrcs.value])
/** 全屏查看器当前图片索引（-1 = 关闭） */
const viewerIndex = ref(-1)
function openViewer(i: number) {
  if (loadedSrcs.value[i]) viewerIndex.value = i
}

interface MermaidView {
  scale: number
  panX: number
  panY: number
  dragging: boolean
  showingSource: boolean
  source: string
}
const mermaidViews = new Map<HTMLDivElement, MermaidView>()
const streamingNow = computed(() => props.message.status === 'sending')

// 话题操作按钮：消息就绪（非流式）时显示 —— 提问（复制/删除）、回答（复制/重新生成/删除）
const actionReady = computed(() => props.message.status !== 'sending')

async function copyContent() {
  const text = props.message.content || ''
  if (await copyText(text)) {
    kernel.notify.success(t('chat.copied'))
  } else {
    kernel.notify.warning(t('chat.copyFailed'))
  }
}

// ---- Token 使用情况浮窗明细 ----
const usageStats = computed(() => {
  const u = props.message.usage
  if (!u) return null
  const reasoning = u.reasoningTokens > 0 ? u.reasoningTokens : null
  const generated = reasoning != null ? Math.max(0, u.completionTokens - reasoning) : u.completionTokens
  const speed = generated > 0 && u.totalMs > 0 ? (generated / (u.totalMs / 1000)).toFixed(1) : null
  const rows: { label: string; value: string }[] = [
    { label: t('chat.tokenInput'), value: String(u.promptTokens) },
    { label: t('chat.tokenOutput'), value: String(u.completionTokens) },
  ]
  if (u.cacheTokens > 0) rows.push({ label: t('chat.tokenCacheHit'), value: String(u.cacheTokens) })
  if (reasoning != null) rows.push({ label: t('chat.tokenReasoning'), value: String(reasoning) })
  rows.push({ label: t('chat.tokenGenerated'), value: String(generated) })
  if (speed != null) rows.push({ label: t('chat.tokenSpeed'), value: `${speed} tok/s` })
  if (u.ttftMs > 0) rows.push({ label: t('chat.tokenTtft'), value: `${u.ttftMs} ms` })
  if (u.totalMs > 0) rows.push({ label: t('chat.tokenTotal'), value: `${u.totalMs} ms` })
  if (props.message.model) rows.push({ label: t('chat.tokenModel'), value: props.message.model })
  if (u.rounds > 1) rows.push({ label: t('chat.tokenRounds'), value: String(u.rounds) })
  if (u.tools > 0) rows.push({ label: t('chat.tokenTools'), value: String(u.tools) })
  if (u.subAgentCount > 0) rows.push({ label: t('chat.tokenSubAgents'), value: String(u.subAgentCount) })
  if (u.subAgentInputTokens > 0 || u.subAgentOutputTokens > 0) {
    rows.push({ label: t('chat.tokenSubTokens'), value: `↑${u.subAgentInputTokens} ↓${u.subAgentOutputTokens}` })
  }
  if (u.plannerInputTokens > 0 || u.plannerOutputTokens > 0) {
    rows.push({ label: t('chat.tokenPlanner'), value: `↑${u.plannerInputTokens} ↓${u.plannerOutputTokens}` })
  }
  if (u.cost > 0) rows.push({ label: t('chat.tokenCost'), value: '$' + u.cost.toFixed(4) })
  return rows
})

// ---- 回答生成图片下载 ----
const bubbleRef = ref<HTMLElement | null>(null)
const downloading = ref(false)

async function downloadImage() {
  const el = bubbleRef.value
  if (!el || downloading.value) return
  downloading.value = true
  try {
    const blob = await captureElementToPng(el)
    if (!blob) {
      kernel.notify.warning(t('chat.downloadFailed'))
      return
    }
    downloadBlob(blob, `answer-${stamp()}.png`)
    kernel.notify.success(t('chat.downloadOk'))
  } finally {
    downloading.value = false
  }
}

const statusText = computed<Record<string, string>>(() => ({
  sending: t('chat.thinking'),
  stopped: t('chat.stoppedShort'),
  failed: t('chat.failedShort'),
  complete: '',
}))

const showingThinking = computed(
  () => props.message.thinkingOpen || (props.message.status === 'sending' && !props.message.content),
)

// ------- 打字机（不依赖 Vue 批处理时序）：独立定时器逐字揭示 -------
// 每 32ms 无条件采样一次内容并向前推进游标。因此无论后端/事件是逐块到达、
// 一次性整批到达、还是组件挂载时内容已完整，都必然以“逐字输出”呈现；
// 仅历史/重放消息（live=false）跳过打字机直接全量。
const textShown = ref(0)
const thinkShown = ref(0)
let revealTimer: number | undefined
let revealSettled = false

const revealSpeed = 6 // 每 32ms 揭示 6 个字符（~190 字/秒）

function tick() {
  if (revealSettled) return
  const m = props.message
  if (!m.live) {
    textShown.value = m.content.length
    thinkShown.value = m.reasoning.length
    revealSettled = true
    return
  }
  if (textShown.value < m.content.length) textShown.value = Math.min(m.content.length, textShown.value + revealSpeed)
  if (thinkShown.value < m.reasoning.length) thinkShown.value = Math.min(m.reasoning.length, thinkShown.value + revealSpeed)
}

revealTimer = window.setInterval(tick, 32)

onUnmounted(() => {
  window.clearInterval(revealTimer)
  loadedSrcs.value.forEach((u) => {
    if (u.startsWith('blob:')) URL.revokeObjectURL(u)
  })
})

// 首 token 等待反馈（真实模型 TTFT 可能很长）：显示“思考中…N秒”
const waitSec = ref(0)
let waitTimer: number | undefined

watch(
  () => [props.message.status, props.message.content.length] as const,
  ([status, len]) => {
    if (status === 'sending' && len === 0) {
      waitSec.value = 0
      window.clearInterval(waitTimer)
      waitTimer = window.setInterval(() => waitSec.value++, 1000)
    } else {
      window.clearInterval(waitTimer)
    }
  },
  { immediate: true },
)

const showWait = computed(() => props.message.status === 'sending' && !props.message.content)

// 思考：进行中自动展开（完整展示全部推理）；结束后自动折叠回标题（点击可重新展开查看全部）
watch(
  () => props.message.thinkingOpen,
  (v) => {
    thinkingOpen.value = v
  },
)

// 思考进行中实时滚动到底（保证“所有思考过程”持续可见）
const thinkBodyRef = ref<HTMLElement | null>(null)
watch(
  () => [props.message.thinkingOpen, props.message.reasoning.length] as const,
  () => {
    if (props.message.thinkingOpen) {
      void nextTick(() => {
        const el = thinkBodyRef.value
        if (el) el.scrollTop = el.scrollHeight
      })
    }
  },
  { flush: 'post' },
)

// ---------------- Markdown + Mermaid 渲染 ----------------
// 打字机揭示期间显示纯文本（避免半截语法闪烁）；揭示完成后一次性渲染 Markdown，
// Mermaid 代码块经自定义 fence 输出 <pre class="mermaid">，由 mermaid.run 转成 SVG。

const md = new MarkdownIt({
  html: false, // 不渲染原始 HTML，防 XSS
  linkify: true,
  breaks: true,
})
installMarkdownMath(md) // 数学公式：块级 $$...$$ + 行内 $...$（KaTeX）

const defaultLinkOpen =
  md.renderer.rules.link_open ??
  ((tokens, idx, options, _env, self) => self.renderToken(tokens, idx, options))
md.renderer.rules.link_open = (tokens, idx, options, env, self) => {
  tokens[idx].attrSet('target', '_blank')
  tokens[idx].attrSet('rel', 'noopener noreferrer')
  return defaultLinkOpen(tokens, idx, options, env, self)
}

const defaultFence = md.renderer.rules.fence
md.renderer.rules.fence = (tokens, idx, options, env, self) => {
  const tok = tokens[idx]
  const info = (tok.info ?? '').trim().toLowerCase()
  if (info === 'mermaid' || info.startsWith('mermaid ')) {
    return `<pre class="mermaid">${md.utils.escapeHtml(tok.content)}</pre>\n`
  }
  // 代码块增强：语言高亮 + 顶栏（语言标签 / 格式化·复制按钮）
  const lang = (tok.info ?? '').trim().split(/\s+/)[0].toLowerCase()
  const safeLang = lang.replace(/[^\w-]/g, '')
  const label = lang || 'text'
  const body = highlightCode(tok.content, lang)
  const formatBtn = canFormat(lang)
    ? `<button type="button" class="code-act" data-act="format" data-lang="${md.utils.escapeHtml(lang)}">🪄 <span>${t('chat.formatCode')}</span></button>`
    : ''
  return (
    `<div class="code-wrap">` +
    `<div class="code-bar"><span class="code-lang">${md.utils.escapeHtml(label)}</span>` +
    `<span class="code-actions">${formatBtn}` +
    `<button type="button" class="code-act" data-act="copy" data-lang="${md.utils.escapeHtml(lang)}">📋 <span>${t('chat.copyCode')}</span></button></span></div>` +
    `<pre><code class="language-${safeLang} hljs" data-lang="${md.utils.escapeHtml(lang)}">${body}</code></pre>` +
    `</div>\n`
  )
}

mermaid.initialize({ startOnLoad: false, theme: 'default', securityLevel: 'loose' })

const mdHtml = ref('')
const mdReady = ref(false) // 揭示完成、Markdown 已渲染
const contentRef = ref<HTMLElement | null>(null)

const shownContent = computed(() => props.message.content.slice(0, textShown.value))

// ---------------- 按轮次分段展示（输出与工具调用交替） ----------------
// 工具卡带 outputBefore（触发时已输出的字符数）时，正文按轮切分并交替呈现：
// 第 N 轮输出文本段 → 第 N 轮工具卡 → 第 N+1 轮输出文本段 … → 最终结果。
// 旧数据（任意卡缺 outputBefore）回退原布局（思考块内工具卡 + 正文整段）。
interface ContentSeg {
  key: string
  text?: string
  card?: ToolCardModel
  /** 文本段在 content 中的起始偏移（打字机揭示按段截取） */
  start: number
}

const segmentedEnabled = computed(() => {
  const cs = props.message.tools
  return cs.length > 0 && cs.every((t) => t.outputBefore != null)
})

const segments = computed<ContentSeg[]>(() => {
  const content = props.message.content
  const cards = props.message.tools
  if (!cards.length) return []
  // 按 outputBefore 升序；并行调用（同偏移）保持 push 顺序（Array.sort 稳定）
  const sorted = [...cards].sort((a, b) => (a.outputBefore ?? 0) - (b.outputBefore ?? 0))
  const segs: ContentSeg[] = []
  let cursor = 0
  sorted.forEach((c, idx) => {
    const ob = c.outputBefore ?? 0
    if (ob > cursor) segs.push({ key: `t${idx}`, text: content.slice(cursor, ob), start: cursor })
    segs.push({ key: `c${idx}`, card: c, start: ob })
    cursor = Math.max(cursor, ob)
  })
  if (cursor < content.length) segs.push({ key: `t${segs.length}`, text: content.slice(cursor), start: cursor })
  return segs
})

// 揭示完成后才逐段渲染 Markdown（响应 mdReady / 段内容变化）
const segHtmlMap = computed<Record<string, string>>(() => {
  const map: Record<string, string> = {}
  if (!mdReady.value) return map
  for (const s of segments.value) {
    if (s.text) map[s.key] = md.render(s.text)
  }
  return map
})

/** 打字机揭示阶段：文本段按其在 content 中的偏移截取已揭示部分 */
function segShownText(s: ContentSeg): string {
  if (!s.text) return ''
  const shown = Math.max(0, Math.min(s.text.length, textShown.value - s.start))
  return s.text.slice(0, shown)
}

// ---------------- 按轮次展示（第 N 轮思考 → 第 N 轮输出 → 第 N 轮工具结果 → 第 N+1 轮思考） ----------------
// 后端按轮结构化持久化（RoundsJson：每轮 { thinking, content, tools[] }），流式事件天然按上述顺序到达
// （round_start 每轮必发，thinking/text/tool 事件依序落入当前轮），这里按轮直读渲染，无需偏移切片/重排。
// 旧数据（无轮结构）回退：分段（输出↔卡交替）或原布局。
interface RoundSeg {
  key: string
  idx: number
  /** 该轮输出文本（服务端按轮持久化，流式实时追加） */
  text: string
  /** 该轮思考文本 */
  thinkText: string
  /** 该轮发起的工具卡（服务端按轮持久化；运行中实时更新结果） */
  cards: ToolCardModel[]
}

const roundLayoutEnabled = computed(() => props.message.rounds.length > 0)

const roundSegs = computed<RoundSeg[]>(() =>
  props.message.rounds.map((r, i) => ({
    key: r.key,
    idx: i,
    text: r.content,
    thinkText: r.thinking,
    cards: r.tools,
  })),
)

/** 轮布局下：每轮思考独立折叠（完成态默认折叠，流式中最新一轮自动展开） */
const roundOpen = ref<boolean[]>([])
watch(
  () => props.message.rounds.length,
  (n) => {
    roundOpen.value = Array.from({ length: n }, (_, i) => (i === n - 1 ? props.message.thinkingOpen : false))
  },
  { immediate: true },
)
watch(
  () => props.message.thinkingOpen,
  (v) => {
    roundOpen.value = roundOpen.value.map((o, i) => (i === roundOpen.value.length - 1 ? v : o))
  },
)
function toggleRound(i: number) {
  roundOpen.value[i] = !roundOpen.value[i]
}

/** contextNotes 中“第 N 轮”类提示在轮布局下由轮标题承担，滤掉避免重复 */
const nonRoundNotes = computed(() =>
  props.message.contextNotes.filter((n) => !/^(第\s*\d+\s*轮|Round\s*\d+)/i.test(n.trim())),
)

// 揭示完成后才逐轮渲染 Markdown（响应 mdReady / 轮内容变化）
const roundHtmlMap = computed<Record<string, string>>(() => {
  const map: Record<string, string> = {}
  if (!mdReady.value) return map
  for (const s of roundSegs.value) {
    if (s.text) map[s.key] = md.render(s.text)
  }
  return map
})

watch(
  () => [shownContent.value, props.message.content] as const,
  async () => {
    const full = props.message.content
    const typing = props.message.live && textShown.value < full.length
    if (typing) {
      mdReady.value = false
      return
    }
    if (!segmentedEnabled.value && !roundLayoutEnabled.value) mdHtml.value = md.render(full)
    mdReady.value = true
    await nextTick()
    void renderMermaid()
  },
  { flush: 'post' },
)

async function renderMermaid() {
  const host = contentRef.value
  if (!host) return
  const blocks = Array.from(host.querySelectorAll('pre.mermaid')) as HTMLElement[]
  if (!blocks.length) return
  // 必须在 mermaid.run 之前保存原始源码（渲染后 pre 内容会变成 SVG/CSS）
  // 查看源码时把 ICON（emoji 图标）还原为实际字符：移除 🔍🛡️✈️🚦 等图形图标，
  // 保留其余纯文本字符 —— 仅作用于源码视图文本，不影响图上渲染
  const raws = blocks.map((b) => (b.textContent ?? '').replace(/[\u{1F000}-\u{1FAFF}\u{2600}-\u{27BF}\u{FE0F}]/gu, ''))
  try {
    await mermaid.run({ nodes: blocks, suppressErrors: true })
  } catch {
    // 渲染失败保留源码文本，不影响消息
  }
  // 为每个图包一层交互容器 + 工具条（放大/缩小/重置/拖动/源码）
  blocks.forEach((pre, i) => {
    if (pre.parentElement?.classList.contains('mermaid-box')) return
    const wrap = document.createElement('div')
    wrap.className = 'mermaid-box'
    wrap.dataset.idx = String(i)
    pre.parentNode!.insertBefore(wrap, pre)
    wrap.appendChild(pre)
    const view: MermaidView = { scale: 1, panX: 0, panY: 0, dragging: false, showingSource: false, source: raws[i] }
    mermaidViews.set(wrap, view)
    const tools = document.createElement('div')
    tools.className = 'mermaid-tools'
    // 动态创建按钮并绑定事件（避免模板与多图状态错乱）
    const acts: [string, string, string][] = [
      ['out', '缩小', '−'],
      ['in', '放大', '＋'],
      ['reset', '重置（1:1）', '↺'],
      ['drag', '拖动', '✋'],
      ['source', '查看源码', '📄'],
    ]
    for (const [act, title, label] of acts) {
      const btn = document.createElement('button')
      btn.dataset.act = act
      btn.title = title
      btn.textContent = label
      tools.appendChild(btn)
    }
    wrap.appendChild(tools)
    applyMermaidView(wrap, view)
  })
}

function applyMermaidView(box: HTMLDivElement, v: MermaidView) {
  const svg = box.querySelector('svg')
  if (svg) {
    svg.style.transform = `translate(${v.panX}px, ${v.panY}px) scale(${v.scale})`
    svg.style.transformOrigin = 'center center'
    svg.style.display = v.showingSource ? 'none' : ''
  }
  let src = box.querySelector<HTMLElement>('.mermaid-source')
  if (v.showingSource) {
    if (!src) {
      src = document.createElement('pre')
      src.className = 'mermaid-source'
      box.appendChild(src)
    }
    src.textContent = v.source
  } else {
    src?.remove()
  }
  box.classList.toggle('dragging', v.dragging)
}

/** 复制代码块内容（当前 DOM 文本，含已格式化结果） */
async function copyCodeBlock(btn: HTMLButtonElement) {
  const code = btn.closest('.code-wrap')?.querySelector('code')
  const text = code?.textContent ?? ''
  if (text && (await copyText(text))) {
    kernel.notify.success(t('chat.copied'))
  } else if (!text) {
    kernel.notify.warning(t('chat.copyFailed'))
  }
}

/** 按语言格式化代码块（prettier standalone 按需加载）；失败提示不支持 */
async function formatCodeBlock(btn: HTMLButtonElement) {
  const lang = btn.dataset.lang ?? ''
  const wrap = btn.closest('.code-wrap')
  const code = wrap?.querySelector('code')
  if (!code) return
  const label = btn.querySelector('span')
  const origLabel = label?.textContent
  btn.disabled = true
  if (label) label.textContent = '…'
  try {
    const formatted = await formatCode(lang, code.textContent ?? '')
    code.textContent = formatted
    if (code.classList.contains('hljs')) {
      void hljs.highlightElement(code)
    }
    kernel.notify.success(t('chat.formatDone'))
  } catch {
    kernel.notify.error(t('chat.formatFailed'))
  } finally {
    btn.disabled = false
    if (label && origLabel) label.textContent = origLabel
  }
}

/** 消息正文统一点击处理：代码块复制/格式化 → 其余（Mermaid 工具栏等） */
function onContentClick(e: MouseEvent) {
  const btn = (e.target as HTMLElement).closest<HTMLButtonElement>('button[data-act].code-act')
  if (btn) {
    if (btn.dataset.act === 'copy') void copyCodeBlock(btn)
    else if (btn.dataset.act === 'format') void formatCodeBlock(btn)
    return
  }
  onMermaidToolsClick(e)
}

function onMermaidToolsClick(e: MouseEvent) {
  const btn = (e.target as HTMLElement).closest<HTMLElement>('button[data-act]')
  if (!btn) return
  const box = btn.closest<HTMLDivElement>('.mermaid-box')
  if (!box) return
  const v = mermaidViews.get(box)
  if (!v) return
  switch (btn.dataset.act) {
    case 'in':
      v.scale = Math.min(4, v.scale * 1.2)
      break
    case 'out':
      v.scale = Math.max(0.25, v.scale / 1.2)
      break
    case 'reset':
      v.scale = 1
      v.panX = 0
      v.panY = 0
      break
    case 'drag':
      v.dragging = !v.dragging
      break
    case 'source':
      v.showingSource = !v.showingSource
      break
  }
  applyMermaidView(box, v)
  e.stopPropagation()
}

function onMermaidPanStart(e: MouseEvent) {
  const box = (e.target as HTMLElement).closest<HTMLDivElement>('.mermaid-box')
  if (!box) return
  const v = mermaidViews.get(box)
  if (!v || !v.dragging) return
  const start = { x: e.clientX, y: e.clientY }
  const move = (ev: MouseEvent) => {
    v.panX += ev.clientX - start.x
    v.panY += ev.clientY - start.y
    start.x = ev.clientX
    start.y = ev.clientY
    applyMermaidView(box, v)
  }
  const up = () => {
    window.removeEventListener('mousemove', move)
    window.removeEventListener('mouseup', up)
  }
  window.addEventListener('mousemove', move)
  window.addEventListener('mouseup', up)
  e.preventDefault()
}

function onMermaidWheel(e: WheelEvent) {
  const box = (e.target as HTMLElement).closest<HTMLDivElement>('.mermaid-box')
  if (!box) return
  const v = mermaidViews.get(box)
  if (!v) return
  const factor = e.deltaY < 0 ? 1.1 : 1 / 1.1
  v.scale = Math.min(4, Math.max(0.25, v.scale * factor))
  applyMermaidView(box, v)
  e.preventDefault()
}

/** 简单 Mermaid 源码格式化：按行拆分、常见分隔符后断行缩进，单行/紧凑源码变为可读多行 */
function formatMermaidSource(raw: string): string {
  if (!raw) return ''
  const lines = raw.split(/\r?\n/)
  // 多行源码原样返回（仅去除多余空行）
  if (lines.length > 1) {
    return lines.filter((l) => l.trim().length > 0).join('\n')
  }
  const single = raw.trim()
  // 单行：在关键分隔符后断行，并用缩进区分层级
  return single
    .replace(/[\n\r]+/g, ' ')
    .replace(/\s*(\{\s*)($)/g, '$1\n')
    .replace(/\s*(\}\s*)/g, '\n$1')
    .replace(/(\s*)(->>|-->|->|==>|-.->|---)|\s+(--)/g, (_m, p1) => (p1 ? `\n  ${p1} ` : '\n  -- '))
    .split('\n')
    .map((l, i) => {
      const depth = (l.match(/^\s{2}/g) ?? []).length
      return '  '.repeat(Math.min(depth, 4)) + l.trim()
    })
    .join('\n')
}

function prettyArgs(raw?: string): string {
  if (!raw) return ''
  try {
    return JSON.stringify(JSON.parse(raw), null, 2)
  } catch {
    return raw
  }
}
</script>

<template>
  <!-- user 话题锚点（话题导航条定位用）：topic- + 消息 id -->
  <div
    class="row"
    :class="message.role"
    :key="message.id"
    :id="message.role === 'user' ? 'topic-' + message.id : undefined"
  >
    <div v-if="isAssistant" class="avatar-mini">NC</div>
    <div class="body">
      <!-- 思考（可折叠）：浅灰背景与最终输出区分；折叠是收起动画，内容始终保留可展开。
           轮布局（roundBoundariesJson 存在）时思考按轮嵌入正文（每轮“思考→工具→输出”），此处整块思考不再渲染；
           否则旧数据（工具卡无轮次信息）保持原布局：思考之后是工具卡，再之后才是正文 -->
      <div
        v-if="isAssistant && !roundLayoutEnabled && (message.reasoning || message.thinkingOpen || message.contextNotes.length > 0 || (message.status === 'sending' && !message.content) || (!segmentedEnabled && message.tools.length > 0))"
        class="think-block"
      >
        <div class="think-head nc-dim" @click="thinkingOpen = !thinkingOpen">
          <span :class="['caret', { open: thinkingOpen }]">▸</span>
          <span v-if="showingThinking">
            🧠 {{ t('chat.thinkInProgress') }}
            <template v-if="showWait"> · {{ waitSec }}s</template>
          </span>
          <span v-else-if="message.reasoning">{{ t('chat.thinkProcess', { len: message.reasoning.length }) }}</span>
          <span v-else>{{ t('chat.thinkEmpty') }}</span>
        </div>
        <div class="think-body-wrap" :class="{ collapsed: !thinkingOpen && !message.thinkingOpen }">
          <div v-if="message.reasoning" ref="thinkBodyRef" class="think-body">{{ message.reasoning.slice(0, thinkShown) }}</div>
          <div v-else-if="thinkingOpen || message.thinkingOpen" class="think-body nc-dim think-wait">{{ t('chat.thinkingWait') }}</div>
        </div>
        <!-- 旧数据：工具卡保持在思考之后、正文之前 -->
        <template v-if="!segmentedEnabled">
          <ToolCard v-for="tCard in message.tools" :key="tCard.key" :card="tCard" />
        </template>
        <div v-for="(note, i) in message.contextNotes" :key="'n' + i" class="context-note nc-dim">
          ℹ️ {{ note }}
        </div>
      </div>

      <!-- 用户附件图片：持久化后走 url（/api/chat/images 鉴权 fetch → blob），内存 pending 走 base64；
           点击进入全屏查看器（滚轮/按钮调 Scale 比例，拖拽移动位置） -->
      <div v-if="!isAssistant && message.images && message.images.length" class="images">
        <img
          v-for="(img, i) in message.images"
          :key="i"
          class="msg-image"
          :src="loadedSrcs[i] || ''"
          :alt="img.fileName || ''"
          @click="openViewer(i)"
        />
      </div>
      <ImageViewer :visible="viewerIndex >= 0" :srcs="previewSrcList" :index="Math.max(0, viewerIndex)" @close="viewerIndex = -1" />

      <!-- 正文（打字机揭示 → Markdown + Mermaid；带轮次锚点的新数据按“思考→工具→输出”逐轮展示，
           仅带 outputBefore 的中期数据按“输出段↔工具卡”交替，旧数据整段展示） -->
      <div
        v-if="message.content || segmentedEnabled || roundLayoutEnabled || message.status !== 'sending'"
        ref="bubbleRef"
        class="bubble"
        :class="{ streaming: streamingNow }"
      >
        <template v-if="roundLayoutEnabled">
          <div ref="contentRef" class="md-host round-host" @click="onContentClick" @mousedown="onMermaidPanStart" @wheel="onMermaidWheel">
            <div v-if="nonRoundNotes.length" class="context-notes">
              <div v-for="(note, i) in nonRoundNotes" :key="'n' + i" class="context-note nc-dim">
                ℹ️ {{ note }}
              </div>
            </div>
            <div v-for="seg in roundSegs" :key="seg.key" class="round">
              <!-- 该轮思考（可折叠标题，默认折叠，点击展开；流式中最新一轮自动展开） -->
              <div v-if="seg.thinkText" class="round-think">
                <div class="think-head nc-dim" @click="toggleRound(seg.idx)">
                  <span :class="['caret', { open: roundOpen[seg.idx] }]">▸</span>
                  <span>{{ t('chat.thinkRound', { round: seg.idx + 1, len: seg.thinkText.length }) }}</span>
                </div>
                <div v-if="roundOpen[seg.idx]" class="think-body">{{ seg.thinkText }}</div>
              </div>
              <!-- 该轮输出（模型在该轮先输出文本，响应末尾才发起工具调用）：流式实时纯文本，完成后渲染 Markdown -->
              <div v-if="mdReady && seg.text && roundHtmlMap[seg.key]" class="md md-seg" v-html="roundHtmlMap[seg.key]"></div>
              <div v-else-if="seg.text" class="md md-seg">{{ seg.text }}</div>
              <!-- 该轮发起的工具调用结果（含参数/结果/耗时），紧随该轮输出显示 -->
              <ToolCard v-for="card in seg.cards" :key="card.key" :card="card" />
            </div>
            <span v-if="!message.content && streamingNow" class="skeleton">▍</span>
            <span v-else-if="!message.content && message.status === 'stopped'" class="nc-dim">{{ t('chat.stoppedNote') }}</span>
            <span v-else-if="!message.content && message.status === 'failed'" class="nc-dim">{{ t('chat.failedNote') }}</span>
          </div>
        </template>
        <template v-else-if="segmentedEnabled">
          <div ref="contentRef" class="md-host" @click="onContentClick" @mousedown="onMermaidPanStart" @wheel="onMermaidWheel">
            <template v-for="seg in segments" :key="seg.key">
              <ToolCard v-if="seg.card" :card="seg.card" />
              <div v-else-if="seg.text && (mdReady ? segHtmlMap[seg.key] : segShownText(seg))" class="md md-seg" v-html="mdReady ? segHtmlMap[seg.key] : segShownText(seg)"></div>
            </template>
            <span v-if="!message.content && streamingNow" class="skeleton">▍</span>
            <span v-else-if="!message.content && message.status === 'stopped'" class="nc-dim">{{ t('chat.stoppedNote') }}</span>
            <span v-else-if="!message.content && message.status === 'failed'" class="nc-dim">{{ t('chat.failedNote') }}</span>
          </div>
        </template>
        <template v-else>
          <template v-if="message.content && !mdReady"><div class="plain-text">{{ shownContent }}</div></template>
          <div v-else-if="mdReady" ref="contentRef" class="md" v-html="mdHtml" @click="onContentClick" @mousedown="onMermaidPanStart" @wheel="onMermaidWheel"></div>
          <span v-else-if="streamingNow" class="skeleton">▍</span>
          <span v-else-if="message.status === 'stopped'" class="nc-dim">{{ t('chat.stoppedNote') }}</span>
          <span v-else-if="message.status === 'failed'" class="nc-dim">{{ t('chat.failedNote') }}</span>
        </template>
      </div>

      <!-- 用量/模型信息 -->
      <div v-if="isAssistant && message.usage" class="usage nc-dim">
        {{ t('chat.usageTokens', { model: message.model ?? '', tokens: message.usage.totalTokens }) }}
        <template v-if="message.usage.ttftMs > 0">{{ t('chat.usageTtft', { ms: message.usage.ttftMs }) }}</template>
        <template v-if="message.usage.totalMs > 0">{{ t('chat.usageTotalMs', { ms: message.usage.totalMs }) }}</template>
      </div>

      <!-- 话题操作：收藏 / 复制 / 重新生成 / 删除（消息就绪后显示；收藏=提问+回答一起；提问框无 重新生成） -->
      <div v-if="actionReady" class="actions">
        <button class="act nc-dim" :title="t('chat.favorite')" @click="emit('favorite', message)">⭐ {{ t('chat.favorite') }}</button>
        <button class="act nc-dim" :title="t('chat.copy')" @click="copyContent">📋 {{ t('chat.copy') }}</button>
        <el-tooltip v-if="isAssistant && message.usage && usageStats" placement="top" :show-after="150" transition="false" popper-class="token-pop">
          <template #content>
            <div class="tp-title">{{ t('chat.tokenTitle') }}</div>
            <div v-for="r in usageStats" :key="r.label" class="tp-row">
              <span class="tp-label">{{ r.label }}</span><b class="tp-val">{{ r.value }}</b>
            </div>
          </template>
          <button class="act nc-dim" :title="t('chat.tokenTitle')">{{ t('chat.tokenAction') }} ⚡</button>
        </el-tooltip>
        <button v-if="isAssistant" class="act nc-dim" :title="t('chat.download')" :disabled="downloading" @click="downloadImage">{{ downloading ? '⏳' : '⬇️' }} {{ t('chat.download') }}</button>
        <button v-if="isAssistant" class="act nc-dim" :title="t('chat.regenerate')" @click="emit('regenerate', message.id)">🔄 {{ t('chat.regenerate') }}</button>
        <button class="act nc-dim danger" :title="t('common.delete')" @click="emit('remove', message)">🗑 {{ t('common.delete') }}</button>
      </div>
    </div>
    <div v-if="!isAssistant && statusText[message.status]" class="status nc-dim">
      {{ statusText[message.status] }}
    </div>
  </div>
</template>

<style scoped>
.row {
  /* 消息行宽度 = 剩余聊天区宽度的 80%（MessageList 注入 --nc-msg-w），随窗口动态缩放 */
  width: var(--nc-msg-w, 80%);
  /* 整个消息列（回答/思考/提问框）在页面中水平居中；行内依然所有框右对齐 */
  margin-left: auto;
  margin-right: auto;
  display: flex;
  gap: 8px;
  margin-bottom: 4px;
  align-items: flex-start;
}

.row.user {
  flex-direction: row-reverse;
}

.avatar-mini {
  width: 28px;
  height: 28px;
  border-radius: 8px;
  flex-shrink: 0;
  background: linear-gradient(135deg, var(--nc-primary), #a78bfa);
  color: #04121f;
  font-size: 10px;
  font-weight: 800;
  display: flex;
  align-items: center;
  justify-content: center;
}

/* 回答行的头像不占布局宽度：回答框（思考框）宽度 = 行宽 = 容器宽 */
.row.assistant {
  position: relative;
}

.row.assistant .avatar-mini {
  position: absolute;
  right: 100%;
  margin-right: 8px;
}

.body {
  /* 回答框/思考框：占满消息行（行宽 = 固定列宽），宽度与容器一致 */
  width: 100%;
  min-width: 120px;
  /* 行距随 .md 容器（1.15）；plain-text 打字机阶段继承此值 */
  line-height: 1.15;
}

.row.user .body {
  /* 提问框宽度 = 回答框宽度的 2/3；row-reverse 使其右缘与回答框右缘对齐 */
  width: calc(var(--nc-msg-w, 80%) * 2 / 3);
  min-width: 120px;
}

.bubble.streaming {
  border-color: color-mix(in srgb, var(--nc-primary) 55%, transparent);
}

.images {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 4px;
  justify-content: flex-end;
}

.msg-image {
  max-width: 220px;
  max-height: 220px;
  border-radius: 8px;
  border: 1px solid var(--nc-border);
  object-fit: cover;
  cursor: zoom-in;
}

.status {
  font-size: 12px;
  margin-top: 4px;
}

/* ---- 思考区：浅灰背景（与最终输出区分），折叠为收起动画，内容始终在 DOM ---- */
.think-block {
  margin-bottom: 6px;
  border: 1px solid var(--nc-border);
  border-radius: 8px;
  padding: 4px 10px;
  background: color-mix(in srgb, var(--nc-text-dim) 12%, transparent);
}

.think-head {
  font-size: 12.5px;
  cursor: pointer;
  user-select: none;
  display: flex;
  gap: 6px;
  align-items: center;
}

.caret {
  transition: transform 0.15s;
  display: inline-block;
}

.caret.open {
  transform: rotate(90deg);
}

.think-body-wrap {
  max-height: 420px;
  overflow: hidden;
  transition: max-height 0.25s ease;
}

.think-body-wrap.collapsed {
  max-height: 0;
}

.think-body {
  margin-top: 4px;
  font-size: 13px;
  line-height: 1.15;
  white-space: pre-wrap;
  opacity: 0.82;
  max-height: 320px;
  overflow-y: auto;
  padding: 6px 10px;
  border-left: 2px solid color-mix(in srgb, var(--nc-primary) 40%, transparent);
  background: color-mix(in srgb, var(--nc-bg) 30%, transparent);
}

.context-note {
  font-size: 11.5px;
  margin-top: 3px;
}

/* ---- 轮次布局（思考 → 工具 → 输出）---- */
.round-host .context-notes {
  margin-bottom: 6px;
}

.round {
  display: flex;
  flex-direction: column;
  gap: 4px;
  margin-bottom: 8px;
}

.round-think {
  border: 1px solid var(--nc-border);
  border-radius: 8px;
  padding: 2px 10px;
  background: color-mix(in srgb, var(--nc-text-dim) 8%, transparent);
}

.round-think .think-head {
  padding: 2px 0;
}

.round-host .md-seg {
  /* 轮内输出段与思考块/工具卡保持层级一致 */
  margin: 0;
}

.skeleton {
  animation: blink 1s infinite;
  color: var(--nc-primary);
}

@keyframes blink {
  50% {
    opacity: 0.2;
  }
}

.usage {
  font-size: 11px;
  margin-top: 4px;
}

/* ---- 话题操作按钮（hover 显示） ---- */
.actions {
  margin-top: 5px;
  opacity: 0;
  transition: opacity 0.15s;
  display: flex;
  gap: 2px;
}

.body:hover .actions,
.row:focus-within .actions {
  opacity: 1;
}

.act {
  border: none;
  background: transparent;
  cursor: pointer;
  padding: 2px 8px;
  font-size: 12px;
  border-radius: 6px;
  color: var(--nc-dim, var(--nc-text-dim));
  transition: background 0.15s, color 0.15s;
}

.act:hover {
  background: color-mix(in srgb, var(--nc-text-dim) 15%, transparent);
  color: var(--nc-text);
}

.act.danger:hover {
  color: var(--nc-danger, #f56c6c);
}

/* ---- 打字机阶段的纯文本 ---- */
.plain-text {
  white-space: pre-wrap;
}
</style>

<!--
  全局（非 scoped）Markdown 内容样式：
  v-html 渲染的内容不在组件 scoped 作用域内，scoped + :deep() 的覆盖不可靠，
  这里统一用全局类 .md 直接控制，行距压到 1.3（密集排版）。
-->
<style>
/* 行距唯一事实源 = .md 容器（继承给所有子元素），子元素不再单独设 line-height，
   避免任何子级规则被跳过/覆盖导致行距不一致；!important 免疫全局样式 */
.md {
  line-height: 1.1 !important;
  font-size: 13.5px;
}

.md p {
  margin: 0.08em 0 !important;
}

.md li {
  margin: 0.02em 0 !important;
}

.md ol,
.md ul {
  margin: 0.08em 0 !important;
  padding-left: 1.25em;
}

.md h1,
.md h2,
.md h3,
.md h4 {
  margin: 0.26em 0 0.12em !important;
}

.md h1 { font-size: 1.38em; }
.md h2 { font-size: 1.22em; }
.md h3 { font-size: 1.08em; }
.md h4 { font-size: 1em; }

.md a {
  color: var(--nc-primary);
}

.md hr {
  border: none;
  border-top: 1px solid var(--nc-border);
  margin: 0.4em 0 !important;
}

.md img {
  max-width: 100%;
  border-radius: 8px;
}

.md blockquote {
  border-left: 3px solid var(--nc-primary);
  margin: 0.2em 0 !important;
  padding-left: 10px;
  color: var(--nc-text-dim);
}

.md table {
  border-collapse: collapse;
  margin: 0.2em 0 !important;
}

.md th,
.md td {
  border: 1px solid var(--nc-border);
  padding: 1px 7px;
}

.md code {
  font-family: 'JetBrains Mono', ui-monospace, 'Cascadia Code', monospace;
  font-size: 0.9em;
}

.md :not(pre) > code {
  background: color-mix(in srgb, var(--nc-text-dim) 18%, transparent);
  padding: 0 4px;
  border-radius: 4px;
}

.md pre {
  background: color-mix(in srgb, var(--nc-text-dim) 10%, transparent);
  border: 1px solid var(--nc-border);
  border-radius: 8px;
  padding: 8px 10px;
  overflow-x: auto;
  margin: 0.3em 0 !important;
}

/* Mermaid 图：居中、自适应宽度 + 交互工具条（放大/缩小/重置/拖动） */
.md pre.mermaid {
  background: transparent;
  border: none;
  text-align: center;
  padding: 4px 0;
}

.md pre.mermaid svg {
  max-width: 100%;
  height: auto;
  margin: 0 auto;
}

.mermaid-box {
  position: relative;
  margin: 8px 0;
}

.mermaid-box .mermaid-tools {
  position: absolute;
  top: 4px;
  right: 4px;
  display: flex;
  gap: 2px;
  background: var(--nc-surface);
  border: 1px solid var(--nc-border);
  border-radius: 8px;
  padding: 2px;
  opacity: 0;
  transition: opacity 0.15s;
  z-index: 5;
  user-select: none;
}

.mermaid-box:hover .mermaid-tools {
  opacity: 1;
}

.mermaid-box .mermaid-tools button {
  min-width: 22px;
  height: 22px;
  border: none;
  background: transparent;
  color: var(--nc-text);
  border-radius: 6px;
  cursor: pointer;
  font-size: 13px;
  line-height: 1;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.mermaid-box .mermaid-tools button:hover {
  background: rgba(148, 163, 184, 0.15);
}

.mermaid-box.dragging {
  cursor: grab;
}

.mermaid-box.dragging:active {
  cursor: grabbing;
}

.mermaid-source {
  font-family: var(--nc-font-mono, ui-monospace, SFMono-Regular, Menlo, monospace);
  font-size: 12px;
  line-height: 1.5;
  color: var(--nc-text);
  background: rgba(0, 0, 0, 0.25);
  border-radius: 8px;
  padding: 10px 12px;
  margin: 4px 0;
  overflow-x: auto;
  white-space: pre;
  text-align: left;
}

/* ---- Token 使用情况浮窗（popper teleport 到 body，需非 scoped 样式） ---- */
.token-pop {
  max-width: 260px;
}

.token-pop .tp-title {
  font-size: 12px;
  font-weight: 600;
  margin-bottom: 6px;
  color: inherit;
}

.token-pop .tp-row {
  display: flex;
  justify-content: space-between;
  align-items: baseline;
  gap: 18px;
  font-size: 12px;
  line-height: 1.7;
}

.token-pop .tp-label {
  color: var(--nc-dim, #94a3b8);
  white-space: nowrap;
}

.token-pop .tp-val {
  font-weight: 600;
  white-space: nowrap;
}
</style>
