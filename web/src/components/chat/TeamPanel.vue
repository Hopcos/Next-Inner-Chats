<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import MarkdownIt from 'markdown-it'
import { kernel } from '@/kernel'
import type { TeamRoundSeg } from '@/kernel/plugins'
import { installMarkdownMath } from '@/utils/markdownMath'
import { canFormat, formatCode, highlightCode } from '@/utils/codeBlock'
import { copyText } from '@/utils/clipboard'
import ToolCard from '@/components/chat/ToolCard.vue'

const props = defineProps<{ rounds: TeamRoundSeg[]; running: boolean }>()
const { t } = useI18n()

// ---------------- Markdown 渲染（与消息正文同一管线：KaTeX / 代码高亮 / 外链新开） ----------------
const md = new MarkdownIt({
  html: false, // 不渲染原始 HTML，防 XSS
  linkify: true,
  breaks: true,
})
installMarkdownMath(md)

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

/** 段级渲染缓存：文本长度变化才重渲染（流式增长期间避免整篇重复 parse） */
const mdCache = new Map<string, { len: number; html: string }>()
function mdHtml(key: string, text: string) {
  const hit = mdCache.get(key)
  if (hit && hit.len === text.length) return hit.html
  const html = md.render(text)
  mdCache.set(key, { len: text.length, html })
  return html
}

/** 面板内代码块操作（复制 / 格式化），事件委托到面板根 */
function onPanelClick(e: MouseEvent) {
  const btn = (e.target as HTMLElement).closest<HTMLButtonElement>('button[data-act].code-act')
  if (!btn) return
  if (btn.dataset.act === 'copy') void copyCode(btn)
  else if (btn.dataset.act === 'format') void formatBlock(btn)
}

async function copyCode(btn: HTMLButtonElement) {
  const code = btn.closest('.code-wrap')?.querySelector('code')
  const text = code?.textContent ?? ''
  if (text && (await copyText(text))) {
    kernel.notify.success(t('chat.copied'))
  } else if (!text) {
    kernel.notify.warning(t('chat.copyFailed'))
  }
}

async function formatBlock(btn: HTMLButtonElement) {
  const lang = btn.dataset.lang ?? ''
  const code = btn.closest('.code-wrap')?.querySelector('code')
  if (!code) return
  const label = btn.querySelector('span')
  const origLabel = label?.textContent
  btn.disabled = true
  if (label) label.textContent = '…'
  try {
    const formatted = await formatCode(lang, code.textContent ?? '')
    code.textContent = formatted
    kernel.notify.success(t('chat.formatDone'))
  } catch {
    kernel.notify.error(t('chat.formatFailed'))
  } finally {
    btn.disabled = false
    if (label && origLabel) label.textContent = origLabel
  }
}

// ---------------- 分组（Round → 段） ----------------
const groups = computed(() => {
  const map = new Map<number, TeamRoundSeg[]>()
  for (const s of props.rounds) {
    const arr = map.get(s.round) ?? []
    arr.push(s)
    map.set(s.round, arr)
  }
  return [...map.entries()]
    .map(([round, segs]) => ({ round, segs }))
    .sort((a, b) => a.round - b.round)
})

// ---------------- 折叠（Round / 参与者 / 思考 各自独立折叠，默认展开） ----------------
const collapsedRounds = ref<Set<number>>(new Set())
const collapsedSegs = ref<Set<string>>(new Set())
const collapsedThink = ref<Set<string>>(new Set())

const segKey = (s: TeamRoundSeg) => `${s.round}:${s.phase}:${s.engineerId ?? s.engineer ?? ''}`

function toggleRound(r: number) {
  const next = new Set(collapsedRounds.value)
  if (next.has(r)) next.delete(r)
  else next.add(r)
  collapsedRounds.value = next
}

function toggleSeg(k: string) {
  const next = new Set(collapsedSegs.value)
  if (next.has(k)) next.delete(k)
  else next.add(k)
  collapsedSegs.value = next
}

function toggleThink(k: string) {
  const next = new Set(collapsedThink.value)
  if (next.has(k)) next.delete(k)
  else next.add(k)
  collapsedThink.value = next
}

// 流式中最新到达的段自动展开（不被此前的折叠状态挡住）
watch(
  () => props.rounds.length,
  () => {
    const last = props.rounds[props.rounds.length - 1]
    if (!last) return
    const rs = new Set(collapsedRounds.value)
    rs.delete(last.round)
    collapsedRounds.value = rs
    const key = segKey(last)
    const ss = new Set(collapsedSegs.value)
    ss.delete(key)
    collapsedSegs.value = ss
    const ts = new Set(collapsedThink.value)
    ts.delete(key)
    collapsedThink.value = ts
  },
)

/** 活动段 = 流式中的最新段：纯文本揭示（避免半截 MD 语法闪烁）；切换/结束后渲染 Markdown */
const activeKey = computed(() => {
  if (!props.running || props.rounds.length === 0) return null
  return segKey(props.rounds[props.rounds.length - 1])
})

const phaseLabel = (phase: string) => {
  switch (phase) {
    case 'solution':
      return t('chat.teamPhaseSolution')
    case 'suggestion':
      return t('chat.teamPhaseSuggestion')
    case 'eval':
      return t('chat.teamPhaseEval')
    case 'final':
      return t('chat.teamPhaseFinal')
    default:
      return phase
  }
}

const phaseIcon = (phase: string) =>
  phase === 'suggestion' ? '💡' : phase === 'eval' ? '🚦' : phase === 'final' ? '✅' : '⚙️'

const phaseClass = (phase: string) => {
  if (phase === 'suggestion') return 'phase-suggestion'
  if (phase === 'eval') return 'phase-eval'
  if (phase === 'final') return 'phase-final'
  return 'phase-solution'
}
</script>

<template>
  <div class="team-panel" @click="onPanelClick">
    <div class="team-head">
      <span class="team-icon">👥</span>
      <span class="team-title">{{ t('chat.teamPhases', { rounds: groups.length || '–' }) }}</span>
      <span v-if="running" class="team-run">▍{{ t('chat.teamRunning') }}</span>
      <span v-else-if="!groups.length" class="team-run nc-dim">{{ t('chat.teamEmpty') }}</span>
    </div>

    <div v-for="g in groups" :key="'r' + g.round" class="team-round">
      <!-- Round 折叠头部：点击整体收起/展开该 Round -->
      <div
        class="round-title"
        role="button"
        tabindex="0"
        @click="toggleRound(g.round)"
        @keydown.enter.prevent="toggleRound(g.round)"
      >
        <span :class="['caret', { open: !collapsedRounds.has(g.round) }]">▸</span>
        <span class="round-no">Round {{ g.round }}</span>
        <span class="round-count nc-dim">{{ g.segs.length }}</span>
      </div>

      <div v-if="!collapsedRounds.has(g.round)" class="round-body">
        <div
          v-for="seg in g.segs"
          :key="segKey(seg)"
          class="team-row"
          :class="[phaseClass(seg.phase), { collapsed: collapsedSegs.has(segKey(seg)) }]"
        >
          <!-- 参与者折叠头部：点击收起/展开该段正文 -->
          <div
            class="row-head"
            role="button"
            tabindex="0"
            @click="toggleSeg(segKey(seg))"
            @keydown.enter.prevent="toggleSeg(segKey(seg))"
          >
            <span :class="['caret', { open: !collapsedSegs.has(segKey(seg)) }]">▸</span>
            <span class="row-icon">{{ phaseIcon(seg.phase) }}</span>
            <span class="row-eng nc-dim">{{ seg.engineer || '—' }}</span>
            <span class="row-phase">{{ phaseLabel(seg.phase) }}</span>
            <span class="row-notice nc-dim">
              <span v-if="activeKey === segKey(seg) && running" class="live-dot">●</span>
            </span>
          </div>

          <div v-if="!collapsedSegs.has(segKey(seg))" class="row-body">
            <!-- 工具调用（与普通聊天 ToolCard 同展示）：流式中 running → 完成/失败 -->
            <ToolCard v-for="card in seg.tools" :key="card.key" :card="card" />
            <!-- 思考过程（可折叠）：与普通聊天的思考块一致 —— 推理增量流式可见，正文开始后仍可展开/收起 -->
            <div
              v-if="seg.thinking"
              class="team-think"
              :class="{ collapsed: collapsedThink.has(segKey(seg)) }"
            >
              <div class="think-head" role="button" tabindex="0" @click="toggleThink(segKey(seg))" @keydown.enter.prevent="toggleThink(segKey(seg))">
                <span :class="['caret', { open: !collapsedThink.has(segKey(seg)) }]">▸</span>
                🧠 <span>{{ t('chat.thinkProcess', { len: seg.thinking.length }) }}</span>
              </div>
              <div v-if="!collapsedThink.has(segKey(seg))" class="think-body">{{ seg.thinking }}</div>
            </div>
            <!-- 活动段：纯文本揭示；其余段：Markdown 渲染（与消息正文同款样式） -->
            <div v-if="activeKey !== segKey(seg) && seg.text" class="md team-md" v-html="mdHtml(segKey(seg), seg.text)"></div>
            <pre v-else class="row-text">{{ seg.text }}<span v-if="activeKey === segKey(seg) && running" class="skeleton">▍</span></pre>
          </div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.team-panel {
  margin: 2px 0 10px;
  padding: 10px 12px;
  border: 1px solid var(--nc-border);
  border-radius: 12px;
  background: color-mix(in srgb, var(--nc-primary) 6%, var(--nc-surface));
}

.team-head {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12.5px;
  font-weight: 600;
  margin-bottom: 6px;
}

.team-icon {
  font-size: 14px;
}

.team-title {
  color: var(--nc-text);
}

.team-run {
  color: var(--nc-primary);
  font-weight: 500;
  font-size: 12px;
}

.team-round {
  margin-top: 8px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--nc-bg) 60%, transparent);
  border: 1px solid var(--nc-border);
  overflow: hidden;
}

/* Round 折叠头 */
.round-title {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 7px 10px;
  font-size: 11.5px;
  font-weight: 700;
  letter-spacing: 0.4px;
  text-transform: uppercase;
  color: var(--nc-text-dim);
  cursor: pointer;
  user-select: none;
  transition: background 0.15s;
}

.round-title:hover {
  background: color-mix(in srgb, var(--nc-text-dim) 8%, transparent);
}

.round-no {
  color: var(--nc-text);
}

.round-count {
  font-size: 10.5px;
  font-weight: 500;
  letter-spacing: 0;
  text-transform: none;
  padding: 0 6px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--nc-text-dim) 14%, transparent);
}

.round-body {
  padding: 0 10px 8px;
}

.team-row {
  margin-top: 6px;
  padding: 5px 8px;
  border-radius: 8px;
  border-left: 3px solid var(--nc-border);
  background: color-mix(in srgb, var(--nc-surface) 70%, transparent);
}

.team-row.phase-suggestion {
  border-left-color: #4c8bf5;
}

.team-row.phase-eval {
  border-left-color: #e6a23c;
}

.team-row.phase-final {
  border-left-color: #67c23a;
}

/* 参与者折叠头 */
.row-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  cursor: pointer;
  user-select: none;
  padding: 2px 0;
  border-radius: 6px;
}

.row-head:hover .row-eng {
  color: var(--nc-text);
}

.row-eng {
  font-weight: 600;
  transition: color 0.15s;
}

.row-phase {
  font-size: 11px;
  padding: 1px 7px;
  border-radius: 999px;
  background: color-mix(in srgb, var(--nc-text-dim) 12%, transparent);
}

.row-notice {
  margin-left: auto;
  font-size: 10px;
}

.live-dot {
  color: var(--nc-primary);
  animation: blink 1s step-start infinite;
}

@keyframes blink {
  50% {
    opacity: 0.25;
  }
}

.row-body {
  margin-top: 4px;
}

/* 活动段纯文本 / 收缩光标 */
.row-text {
  margin: 3px 0 0;
  white-space: pre-wrap;
  word-break: break-word;
  font-family: inherit;
  font-size: 13px;
  line-height: 1.5;
  color: var(--nc-text);
}

/* 思考过程块（与普通聊天 think-block 视觉一致：浅灰底 + 可折叠头部） */
.team-think {
  margin: 4px 0 2px;
  padding: 5px 8px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--nc-bg) 75%, transparent);
  border-left: 2px solid color-mix(in srgb, var(--nc-text-dim) 35%, transparent);
  font-size: 12.5px;
}

.think-head {
  display: flex;
  align-items: center;
  gap: 6px;
  font-size: 12px;
  color: var(--nc-text-dim);
  cursor: pointer;
  user-select: none;
}

.think-head:hover {
  color: var(--nc-text);
}

.think-body {
  margin-top: 4px;
  white-space: pre-wrap;
  word-break: break-word;
  font-size: 12.5px;
  line-height: 1.55;
  color: var(--nc-text);
}

.skeleton {
  color: var(--nc-primary);
  animation: blink 0.8s step-start infinite;
}

/* 通用折叠箭头 */
.caret {
  display: inline-block;
  font-size: 10px;
  color: var(--nc-text-dim);
  transition: transform 0.15s;
}

.caret.open {
  transform: rotate(90deg);
}
</style>
