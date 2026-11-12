<script setup lang="ts">
/**
 * 智能体工具页（Cordis 工具插件 agent-runner）：
 * - 上部：智能体横向抽屉
 *   - 超出宽度时隐藏水平滚动条，但保持平滑左右拖拽与左右箭头平滑步进；
 *   - 右侧增加“查看更多/收起”图标按钮，点击后向下平铺展开所有智能体，展开后点击该图标可收缩回单行；
 *   - 单行收缩状态下可继续点击向左/向右 ICON 平滑滚动；
 *   - 支持新增、编辑、删除（数量限制不超过50），按 sortOrder 序号从大到小排序。
 * - 智能体持久化：与登录用户绑定，通过 /api/me/tool-settings/agent-runner 自动落库 DB
 * - 中间：模型选择、流式输出复选框、当前智能体提示词查看/折叠开关（记住在 localStorage）
 * - 下部：问与答双栏（左边问、右边答、中间解答按钮），将当前选中智能体的提示词作为 systemPrompt
 *   - 右侧的“答”采用 GFM Markdown 风格展示（富文本排版、代码块、表格、引用等）
 * - 底部朗读（BUG 修复）：
 *   - 问框下的朗读：朗读问题框里的所有内容；
 *   - 答框下的朗读：朗读回答框里的所有内容；
 *   - 朗读时完全忽略/过滤标点符号，防止念出标点符号名字；
 *   - 严格选择中式发音（lang: 'zh-CN'，优先选择中国普通话中式声音，如 Xiaoxiao, Yunxi, Huihui, Yaoyao, Kangkang, Google 普通话等）；
 *   - 支持长文本自动分块队列顺序朗读，确保再长的内容也能一字不漏完整读完。
 * - 记忆：localStorage 记住上次选中的智能体，以及每个智能体对应的上一次问与答
 */
import { computed, nextTick, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'
import { http, streamPost } from '@/api/http'
import { copyText } from '@/utils/clipboard'
import {
  DEFAULT_AGENTS,
  MAX_AGENTS,
  loadActiveAgentId,
  loadModel,
  loadStoredSessions,
  loadStreamMode,
  saveActiveAgentId,
  saveModel,
  saveStoredSessions,
  saveStreamMode,
  type AgentDefinition,
} from './config'
import { AGENT_ICONS, DEFAULT_AGENT_ICON, getAgentIcon } from './icons'
import { cleanTextForSpeech, md } from './markdown'

const { t } = useI18n()
const TOOL_KEY = 'agent-runner'
const MAX_CHARS = 50000

// ---------------- 智能体定义列表与持久化（服务端 UserToolSettings） ----------------
const rawAgents = ref<AgentDefinition[]>([...DEFAULT_AGENTS])
const activeAgentId = ref<string>(loadActiveAgentId())
const loadingAgents = ref(true)
const savingAgents = ref(false)

// 按序号降序排序后的智能体列表（序号越大的排在越靠前，相同则保持顺序）
const sortedAgents = computed(() => {
  return [...rawAgents.value].sort((a, b) => {
    const orderA = a.sortOrder ?? 0
    const orderB = b.sortOrder ?? 0
    return orderB - orderA
  })
})

// 选中的当前智能体
const currentAgent = computed<AgentDefinition>(() => {
  return sortedAgents.value.find((a) => a.id === activeAgentId.value) || sortedAgents.value[0] || DEFAULT_AGENTS[0]
})

// 加载用户的智能体设置
async function loadAgentsFromServer() {
  loadingAgents.value = true
  try {
    const res = await http.get<{ isDefault: boolean; config: { agents?: AgentDefinition[] } | null }>(
      `/api/me/tool-settings/${TOOL_KEY}`,
    )
    if (!res?.isDefault && res?.config?.agents && Array.isArray(res.config.agents) && res.config.agents.length > 0) {
      rawAgents.value = res.config.agents
    } else {
      rawAgents.value = [...DEFAULT_AGENTS]
      void persistAgentsToServer()
    }
  } catch {
    rawAgents.value = [...DEFAULT_AGENTS]
  } finally {
    loadingAgents.value = false
    // 校验 activeAgentId 是否在列表内
    if (!sortedAgents.value.some((a) => a.id === activeAgentId.value)) {
      activeAgentId.value = sortedAgents.value[0]?.id || DEFAULT_AGENTS[0].id
      saveActiveAgentId(activeAgentId.value)
    }
    // 同步当前智能体的问答
    syncSessionFromStorage()
  }
}

// 立即/防抖保存智能体定义到服务端数据库
let persistTimer: ReturnType<typeof setTimeout> | null = null
async function persistAgentsToServer() {
  savingAgents.value = true
  try {
    await http.put(`/api/me/tool-settings/${TOOL_KEY}`, {
      agents: rawAgents.value,
    })
  } catch (e) {
    const err = e as { message?: string }
    kernel.notify.error(err.message ?? t('tools.agentRunner.saveFailed'))
  } finally {
    savingAgents.value = false
  }
}

function queuePersistAgents() {
  if (persistTimer) clearTimeout(persistTimer)
  persistTimer = setTimeout(() => void persistAgentsToServer(), 600)
}

function selectAgent(id: string) {
  if (activeAgentId.value === id) return
  // 保存当前智能体的问答
  flushCurrentSession()
  activeAgentId.value = id
  saveActiveAgentId(id)
  // 切换后如果正在朗读则停止
  stopSpeak()
  // 加载新智能体的问答
  syncSessionFromStorage()
}

// ---------------- 智能体创建 / 编辑模态框 ----------------
const editDialogVisible = ref(false)
const isEditing = ref(false)
const editingId = ref<string | null>(null)
const formName = ref('')
const formPrompt = ref('')
const formIcon = ref(DEFAULT_AGENT_ICON)
const formSortOrder = ref<number>(100)
const iconCategory = ref<string>('all')

const filteredIcons = computed(() => {
  if (iconCategory.value === 'all') return AGENT_ICONS
  return AGENT_ICONS.filter((i) => i.category === iconCategory.value)
})

function openCreateModal() {
  if (rawAgents.value.length >= MAX_AGENTS) {
    kernel.notify.warning(t('tools.agentRunner.maxLimitReached', { max: MAX_AGENTS }))
    return
  }
  isEditing.value = false
  editingId.value = null
  formName.value = ''
  formPrompt.value = ''
  formIcon.value = DEFAULT_AGENT_ICON
  const maxOrder = rawAgents.value.reduce((max, cur) => Math.max(max, cur.sortOrder ?? 0), 0)
  formSortOrder.value = maxOrder > 0 ? maxOrder + 10 : 100
  iconCategory.value = 'all'
  editDialogVisible.value = true
}

function openEditModal(agent: AgentDefinition) {
  isEditing.value = true
  editingId.value = agent.id
  formName.value = agent.name
  formPrompt.value = agent.prompt
  formIcon.value = agent.icon || DEFAULT_AGENT_ICON
  formSortOrder.value = agent.sortOrder ?? 100
  iconCategory.value = 'all'
  editDialogVisible.value = true
}

function handleSaveAgent() {
  const name = formName.value.trim()
  const prompt = formPrompt.value.trim()
  const sortOrder = Number(formSortOrder.value) || 0
  if (!name) {
    kernel.notify.warning(t('tools.agentRunner.nameRequired'))
    return
  }
  if (!prompt) {
    kernel.notify.warning(t('tools.agentRunner.promptRequired'))
    return
  }

  if (isEditing.value && editingId.value) {
    const idx = rawAgents.value.findIndex((a) => a.id === editingId.value)
    if (idx !== -1) {
      rawAgents.value[idx] = {
        ...rawAgents.value[idx],
        name,
        prompt,
        icon: formIcon.value,
        sortOrder,
      }
    }
  } else {
    const newId = `agent-${Date.now()}-${Math.random().toString(36).slice(2, 7)}`
    rawAgents.value.push({
      id: newId,
      name,
      prompt,
      icon: formIcon.value,
      sortOrder,
    })
    activeAgentId.value = newId
    saveActiveAgentId(newId)
    syncSessionFromStorage()
  }

  editDialogVisible.value = false
  queuePersistAgents()
  kernel.notify.success(t('tools.agentRunner.agentSaved'))
}

function handleDeleteAgent(agent: AgentDefinition) {
  if (rawAgents.value.length <= 1) {
    kernel.notify.warning(t('tools.agentRunner.atLeastOne'))
    return
  }
  const idx = rawAgents.value.findIndex((a) => a.id === agent.id)
  if (idx !== -1) {
    rawAgents.value.splice(idx, 1)
    if (activeAgentId.value === agent.id) {
      activeAgentId.value = sortedAgents.value[0].id
      saveActiveAgentId(activeAgentId.value)
      syncSessionFromStorage()
    }
    queuePersistAgents()
    kernel.notify.success(t('tools.agentRunner.agentDeleted'))
  }
}

// ---------------- 智能体抽屉横向滚动 / 拖动 / 平铺展开与折叠 ----------------
const drawerScrollRef = ref<HTMLElement | null>(null)
const isExpanded = ref(false) // 是否平铺展开全部智能体
let isDragging = false
let startX = 0
let scrollLeft = 0

function toggleExpand() {
  isExpanded.value = !isExpanded.value
}

function onMouseDown(e: MouseEvent) {
  if (isExpanded.value) return // 展开平铺模式下不触发横向拖拽
  const el = drawerScrollRef.value
  if (!el) return
  isDragging = true
  startX = e.pageX - el.offsetLeft
  scrollLeft = el.scrollLeft
}

function onMouseMove(e: MouseEvent) {
  if (!isDragging || isExpanded.value) return
  const el = drawerScrollRef.value
  if (!el) return
  e.preventDefault()
  const x = e.pageX - el.offsetLeft
  const walk = (x - startX) * 1.5
  el.scrollLeft = scrollLeft - walk
}

function onMouseUp() {
  isDragging = false
}

function scrollDrawer(delta: number) {
  if (!drawerScrollRef.value) return
  if (isExpanded.value) isExpanded.value = false
  drawerScrollRef.value.scrollBy({ left: delta, behavior: 'smooth' })
}

// ---------------- 模型与流式输出配置 ----------------
if (!kernel.catalog.state.loaded) void kernel.catalog.load().catch(() => {})

const modelOptions = computed(() =>
  kernel.catalog.state.providers.flatMap((p) => p.models.map((m) => ({ id: m.id, label: `${m.name} · ${p.name}` }))),
)

const modelId = ref(loadModel())
watch(modelId, (v) => saveModel(v))

watch(
  modelOptions,
  (opts) => {
    if (opts.length && !opts.some((o) => o.id === modelId.value)) {
      modelId.value = opts[0].id
    }
  },
  { immediate: true },
)

const streamMode = ref(loadStreamMode())
watch(streamMode, (v) => saveStreamMode(v))

const showPromptDetail = ref(false)

// ---------------- 问与答会话状态（按智能体独立记忆） ----------------
const question = ref('')
const answer = ref('')
const busy = ref(false)
const elapsed = ref(0)
let elapsedTimer: ReturnType<typeof setInterval> | null = null
let abort: AbortController | null = null

// GFM Markdown 渲染 HTML
const answerHtml = computed(() => {
  if (!answer.value) return ''
  return md.render(answer.value)
})

const answerScrollRef = ref<HTMLElement | null>(null)

function scrollToBottom() {
  nextTick(() => {
    const el = answerScrollRef.value
    if (el) {
      el.scrollTop = el.scrollHeight
    }
  })
}

function syncSessionFromStorage() {
  const map = loadStoredSessions()
  const data = map[activeAgentId.value]
  question.value = data?.question ?? ''
  answer.value = data?.answer ?? ''
}

function flushCurrentSession() {
  const map = loadStoredSessions()
  map[activeAgentId.value] = {
    question: question.value,
    answer: answer.value,
  }
  saveStoredSessions(map)
}

let saveSessionTimer: ReturnType<typeof setTimeout> | null = null
function queueSaveSession() {
  if (saveSessionTimer) clearTimeout(saveSessionTimer)
  saveSessionTimer = setTimeout(() => flushCurrentSession(), 300)
}

watch(question, () => queueSaveSession())
watch(answer, () => queueSaveSession())

function startElapsed() {
  elapsed.value = 0
  if (elapsedTimer) clearInterval(elapsedTimer)
  elapsedTimer = setInterval(() => {
    elapsed.value++
  }, 1000)
}

function stopElapsed() {
  if (elapsedTimer) {
    clearInterval(elapsedTimer)
    elapsedTimer = null
  }
}

async function runAnswer() {
  if (busy.value) {
    abort?.abort()
    return
  }
  if (!question.value.trim()) {
    kernel.notify.warning(t('tools.agentRunner.emptyQuestionWarn'))
    return
  }
  if (!modelId.value) {
    kernel.notify.warning(t('tools.agentRunner.modelRequired'))
    return
  }

  stopSpeak()
  busy.value = true
  answer.value = ''
  startElapsed()
  abort = new AbortController()

  const systemPrompt = currentAgent.value.prompt.trim()
  const userPrompt = question.value

  let streamErr: { code?: string; message?: string } | null = null
  let gotDelta = false

  try {
    if (streamMode.value) {
      await streamPost(
        '/api/tools/llm/stream',
        {
          modelId: modelId.value,
          systemPrompt,
          prompt: userPrompt,
          maxTokens: 64000,
        },
        (ev) => {
          if (ev.kind === 'text_delta') {
            if (!gotDelta) stopElapsed()
            gotDelta = true
            answer.value += String(ev.text ?? '')
            scrollToBottom()
          } else if (ev.kind === 'error') {
            streamErr = { code: String(ev.code ?? ''), message: String(ev.message ?? '') }
          }
        },
        abort.signal,
      )
    } else {
      const res = await http.post<{ text: string }>(
        '/api/tools/llm/complete',
        {
          modelId: modelId.value,
          systemPrompt,
          prompt: userPrompt,
          maxTokens: 64000,
        },
        { signal: abort.signal },
      )
      answer.value = res?.text ?? ''
      scrollToBottom()
    }
    if (streamErr) throw streamErr
  } catch (e) {
    const err = e as { name?: string; code?: string; message?: string }
    if (err.name === 'AbortError' || err.code === 'INTERRUPTED') {
      // 用户主动停止：保留已输出解答
    } else {
      kernel.notify.error(err.message ?? t('tools.agentRunner.failed'), err.code as string)
    }
  } finally {
    stopElapsed()
    busy.value = false
    abort = null
  }
}

function clearAll() {
  stopSpeak()
  question.value = ''
  answer.value = ''
  flushCurrentSession()
}

async function copy(text: string) {
  if (!text.trim()) return
  const ok = await copyText(text)
  if (ok) kernel.notify.success(t('tools.agentRunner.copied'))
  else kernel.notify.error(t('tools.agentRunner.copyFailed'))
}

// ---------------- 语音朗读（中式普通话发音 + 忽略标点符号 + 完整朗读所有内容） ----------------
const speaking = ref<'' | 'question' | 'answer'>('')
let speechChunks: string[] = []
let currentChunkIndex = 0

/**
 * 寻找最佳中式中文发音人（优先匹配普通话人声）：
 * 覆盖 Edge / Chrome / Safari / Windows / macOS 的优质中文人声：
 * Xiaoxiao, Yunxi, Huihui, Yaoyao, Kangkang, Zhiwei, Google 普通话, Tingting 等
 */
function findChineseVoice(): SpeechSynthesisVoice | null {
  if (!('speechSynthesis' in window)) return null
  const voices = window.speechSynthesis.getVoices()
  if (!voices || voices.length === 0) return null

  // 1. 优先匹配主流高品质普通话发音人
  const preferredNames = [
    'xiaoxiao',
    'yunxi',
    'huihui',
    'yaoyao',
    'kangkang',
    'zhiwei',
    'yunjian',
    'wanlung',
    'google 普通话',
    'tingting',
    'sin-ji',
    'mei-jia',
  ]

  for (const name of preferredNames) {
    const found = voices.find(
      (v) => (v.lang.startsWith('zh') || v.lang.includes('CN')) && v.name.toLowerCase().includes(name),
    )
    if (found) return found
  }

  // 2. 次选任何 zh-CN 语言发音人
  const zhCnVoice = voices.find((v) => v.lang === 'zh-CN' || v.lang === 'zh_CN')
  if (zhCnVoice) return zhCnVoice

  // 3. 兜底任何中文前缀发音人 (zh)
  const anyZhVoice = voices.find((v) => v.lang.toLowerCase().startsWith('zh'))
  return anyZhVoice || null
}

/**
 * 播放下一个分块（解决浏览器 SpeechSynthesis 在朗读超长文本时的丢词或中断问题）
 */
function speakNextChunk(target: 'question' | 'answer') {
  if (speaking.value !== target) return
  if (currentChunkIndex >= speechChunks.length) {
    speaking.value = ''
    return
  }

  const chunk = speechChunks[currentChunkIndex]
  const utter = new SpeechSynthesisUtterance(chunk)
  utter.lang = 'zh-CN'

  const voice = findChineseVoice()
  if (voice) {
    utter.voice = voice
  }

  utter.onend = () => {
    currentChunkIndex++
    speakNextChunk(target)
  }

  utter.onerror = () => {
    speaking.value = ''
  }

  window.speechSynthesis.speak(utter)
}

function speak(which: 'question' | 'answer') {
  const rawText = which === 'question' ? question.value : answer.value
  if (!rawText.trim()) return
  if (!('speechSynthesis' in window)) {
    kernel.notify.warning(t('tools.agentRunner.speakUnsupported'))
    return
  }

  // 再次点击同一按钮则停止朗读
  if (speaking.value === which) {
    stopSpeak()
    return
  }

  stopSpeak()

  // 净化文本：对内容里的所有文字完整保留，去除/过滤所有中英文标点符号（避免念出“顿号、句号、冒号”等名字）
  const cleanText = cleanTextForSpeech(rawText)
  if (!cleanText) {
    kernel.notify.warning(t('tools.agentRunner.noSpokenContent'))
    return
  }

  // 按长度（每段 ~200 字，按空格边界划分）分块，保证浏览器稳定完整朗读全部内容
  const words = cleanText.split(' ')
  speechChunks = []
  let cur = ''
  for (const w of words) {
    if ((cur + ' ' + w).length > 180) {
      if (cur) speechChunks.push(cur.trim())
      cur = w
    } else {
      cur = cur ? cur + ' ' + w : w
    }
  }
  if (cur.trim()) speechChunks.push(cur.trim())

  if (speechChunks.length === 0) return

  currentChunkIndex = 0
  speaking.value = which
  speakNextChunk(which)
}

function stopSpeak() {
  if ('speechSynthesis' in window) {
    window.speechSynthesis.cancel()
  }
  speaking.value = ''
  speechChunks = []
  currentChunkIndex = 0
}

onMounted(() => {
  void loadAgentsFromServer()
  window.addEventListener('mouseup', onMouseUp)
  // 确保浏览器语音列表异步就绪
  if ('speechSynthesis' in window && window.speechSynthesis.onvoiceschanged !== undefined) {
    window.speechSynthesis.onvoiceschanged = () => {
      // 声音列表已刷新
    }
  }
})

onBeforeUnmount(() => {
  stopSpeak()
  window.removeEventListener('mouseup', onMouseUp)
  flushCurrentSession()
  if (persistTimer) {
    clearTimeout(persistTimer)
    persistTimer = null
    void persistAgentsToServer()
  }
})
</script>

<template>
  <div class="ar-page">
    <div class="ar-card">
      <!-- 1. 顶部：可左右拖拽滚动 / 平铺展开收起的智能体抽屉区 -->
      <div class="ar-drawer-header">
        <div class="ar-drawer-title-group">
          <span class="ar-drawer-title">{{ t('tools.agentRunner.drawerTitle') }}</span>
          <span class="ar-drawer-count">{{ sortedAgents.length }} / {{ MAX_AGENTS }}</span>
          <span v-if="savingAgents" class="ar-saving-tag">{{ t('tools.agentRunner.saving') }}</span>
        </div>

        <div class="ar-drawer-actions">
          <!-- 左右平滑滚动按钮（在非平铺状态下可用） -->
          <button
            class="ar-scroll-arrow"
            :class="{ disabled: isExpanded }"
            :title="t('tools.agentRunner.scrollLeft')"
            @click="scrollDrawer(-240)"
          >
            <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <path d="m15 18-6-6 6-6" />
            </svg>
          </button>
          <button
            class="ar-scroll-arrow"
            :class="{ disabled: isExpanded }"
            :title="t('tools.agentRunner.scrollRight')"
            @click="scrollDrawer(240)"
          >
            <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round">
              <path d="m9 18 6-6-6-6" />
            </svg>
          </button>

          <!-- 查看更多 / 收起 平铺展开图标 -->
          <button
            class="ar-expand-toggle-btn"
            :class="{ active: isExpanded }"
            :title="isExpanded ? t('tools.agentRunner.collapseDrawer') : t('tools.agentRunner.expandDrawer')"
            @click="toggleExpand"
          >
            <svg
              v-if="!isExpanded"
              viewBox="0 0 24 24"
              width="14"
              height="14"
              fill="none"
              stroke="currentColor"
              stroke-width="1.8"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <rect x="3" y="3" width="7" height="7" rx="1.5" />
              <rect x="14" y="3" width="7" height="7" rx="1.5" />
              <rect x="3" y="14" width="7" height="7" rx="1.5" />
              <rect x="14" y="14" width="7" height="7" rx="1.5" />
            </svg>
            <svg
              v-else
              viewBox="0 0 24 24"
              width="14"
              height="14"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path d="m18 15-6-6-6 6" />
            </svg>
            <span>{{ isExpanded ? t('tools.agentRunner.collapse') : t('tools.agentRunner.viewMore') }}</span>
          </button>

          <el-button
            type="primary"
            size="small"
            class="ar-add-btn"
            :disabled="rawAgents.length >= MAX_AGENTS"
            @click="openCreateModal"
          >
            <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" style="margin-right: 4px;">
              <path d="M12 5v14M5 12h14" />
            </svg>
            {{ t('tools.agentRunner.newAgent') }}
          </el-button>
        </div>
      </div>

      <!-- 智能体抽屉容器：单行水平拖动滚动（隐藏滚动条） vs 平铺全部展开 -->
      <div
        ref="drawerScrollRef"
        class="ar-drawer-container"
        :class="{ expanded: isExpanded }"
        @mousedown="onMouseDown"
        @mousemove="onMouseMove"
      >
        <div
          v-for="item in sortedAgents"
          :key="item.id"
          class="ar-agent-card"
          :class="{ active: item.id === activeAgentId }"
          @click="selectAgent(item.id)"
        >
          <!-- 智能体 ICON -->
          <div class="ar-agent-avatar">
            <svg
              viewBox="0 0 24 24"
              width="20"
              height="20"
              fill="none"
              stroke="currentColor"
              stroke-width="1.8"
              stroke-linecap="round"
              stroke-linejoin="round"
              aria-hidden="true"
            >
              <path
                v-for="(p, pidx) in (getAgentIcon(item.icon)?.paths || getAgentIcon('bot')?.paths || [])"
                :key="pidx"
                :d="p"
              />
            </svg>
          </div>

          <!-- 智能体信息 -->
          <div class="ar-agent-meta">
            <div class="ar-agent-head-line">
              <span class="ar-agent-name" :title="item.name">{{ item.name }}</span>
              <span v-if="item.sortOrder !== undefined" class="ar-order-badge" :title="t('tools.agentRunner.orderTip')">
                #{{ item.sortOrder }}
              </span>
            </div>
            <span class="ar-agent-hint">{{ item.prompt.slice(0, 24) }}…</span>
          </div>

          <!-- 卡片右上操作按钮（编辑 / 删除） -->
          <div class="ar-agent-ops" @click.stop>
            <button
              class="ar-op-btn"
              :title="t('common.edit')"
              @click.stop="openEditModal(item)"
            >
              <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
                <path d="M12 20h9" />
                <path d="M16.5 3.5a2.12 2.12 0 0 1 3 3L7 19l-4 1 1-4Z" />
              </svg>
            </button>
            <button
              v-if="sortedAgents.length > 1"
              class="ar-op-btn del"
              :title="t('common.delete')"
              @click.stop="handleDeleteAgent(item)"
            >
              <svg viewBox="0 0 24 24" width="12" height="12" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
                <path d="M3 6h18" />
                <path d="M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6" />
              </svg>
            </button>
          </div>
        </div>
      </div>

      <!-- 2. 中间：模型选择、流式输出复选框、提示词展开查看 -->
      <div class="ar-toolbar">
        <div class="ar-tool-group">
          <!-- 当前激活智能体徽章提示 -->
          <div class="ar-current-badge">
            <span class="ar-active-dot" />
            <span class="ar-badge-label">{{ t('tools.agentRunner.currentAgent') }}:</span>
            <span class="ar-badge-val">{{ currentAgent.name }}</span>
            <span v-if="currentAgent.sortOrder !== undefined" class="ar-badge-order">#{{ currentAgent.sortOrder }}</span>
          </div>

          <!-- 模型选择 -->
          <el-select
            v-model="modelId"
            size="small"
            class="ar-model-select"
            :placeholder="t('tools.agentRunner.model')"
            :no-data-text="t('tools.agentRunner.noModels')"
          >
            <el-option
              v-for="opt in modelOptions"
              :key="opt.id"
              :label="opt.label"
              :value="opt.id"
            />
          </el-select>

          <!-- 流式输出复选框 -->
          <label class="ar-stream-mode" :title="t('tools.agentRunner.streamModeTip')">
            <input v-model="streamMode" type="checkbox" />
            <span>{{ t('tools.agentRunner.streamMode') }}</span>
          </label>
        </div>

        <div class="ar-tool-group">
          <!-- 提示词查看开关 -->
          <button
            class="ar-ghost-btn"
            :class="{ on: showPromptDetail }"
            @click="showPromptDetail = !showPromptDetail"
          >
            <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
              <path d="M12 4.5l1.4 4.1L17.5 10l-4.1 1.4L12 15.5l-1.4-4.1L6.5 10l4.1-1.4L12 4.5Z" />
              <path d="M18.5 15.5l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8.8-2.2Z" />
            </svg>
            {{ t('tools.agentRunner.promptView') }}
          </button>
        </div>
      </div>

      <!-- 当前智能体提示词折叠面板 -->
      <el-collapse-transition>
        <div v-show="showPromptDetail" class="ar-prompt-panel">
          <div class="ar-prompt-head">
            <span class="ar-prompt-title">{{ currentAgent.name }} · {{ t('tools.agentRunner.agentPrompt') }}</span>
            <button class="ar-ghost-btn small" @click="openEditModal(currentAgent)">
              {{ t('common.edit') }}
            </button>
          </div>
          <div class="ar-prompt-body">{{ currentAgent.prompt }}</div>
        </div>
      </el-collapse-transition>

      <!-- 3. 下部：问与答双栏布局（左边问、右边答、中间解答按钮） -->
      <div class="ar-grid">
        <!-- 左侧：问 -->
        <section class="ar-pane">
          <header class="ar-pane-head">
            <div class="ar-head-tag">
              <span class="ar-tag-dot q" />
              <span class="ar-pane-title">{{ t('tools.agentRunner.question') }}</span>
            </div>
            <span class="ar-pane-count">{{ question.length }} / {{ MAX_CHARS }}</span>
          </header>
          <textarea
            v-model="question"
            class="ar-area"
            :maxlength="MAX_CHARS"
            :placeholder="t('tools.agentRunner.questionPlaceholder', { name: currentAgent.name })"
            spellcheck="false"
            @keydown.ctrl.enter="runAnswer"
          />
          <footer class="ar-pane-foot">
            <button
              class="ar-icon-btn"
              :title="t('common.copy')"
              :disabled="!question.trim()"
              @click="copy(question)"
            >
              <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <rect x="9" y="9" width="11" height="11" rx="2" />
                <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
              </svg>
            </button>
            <button
              class="ar-icon-btn"
              :class="{ speaking: speaking === 'question' }"
              :title="speaking === 'question' ? t('tools.agentRunner.stopSpeak') : t('tools.agentRunner.speakQuestion')"
              :disabled="!question.trim()"
              @click="speak('question')"
            >
              <svg v-if="speaking !== 'question'" viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="M11 5 6.5 9H3v6h3.5L11 19V5Z" />
                <path d="M15 9.5a3.5 3.5 0 0 1 0 5" />
                <path d="M17.8 7a7 7 0 0 1 0 10" />
              </svg>
              <svg v-else viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="M11 5 6.5 9H3v6h3.5L11 19V5Z" />
                <path d="M16 9.5l5 5M21 9.5l-5 5" />
              </svg>
            </button>
            <button
              class="ar-icon-btn"
              :title="t('common.clear')"
              :disabled="!question.trim() && !answer.trim()"
              @click="clearAll"
            >
              <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="M3 6h18" />
                <path d="M19 6v14c0 1-1 2-2 2H7c-1 0-2-1-2-2V6" />
                <path d="M8 6V4c0-1 1-2 2-2h4c1 0 2 1 2 2v2" />
              </svg>
            </button>
          </footer>
        </section>

        <!-- 中间：操作列（解答 / 停止 / 等待时间） -->
        <div class="ar-action-col">
          <el-button
            :type="busy ? 'danger' : 'primary'"
            class="ar-go-btn"
            size="large"
            @click="runAnswer"
          >
            <svg
              v-if="busy"
              viewBox="0 0 24 24"
              width="14"
              height="14"
              fill="currentColor"
              aria-hidden="true"
              style="margin-right: 6px; flex-shrink: 0;"
            >
              <rect x="6" y="6" width="12" height="12" rx="2" />
            </svg>
            <svg
              v-else
              viewBox="0 0 24 24"
              width="15"
              height="15"
              fill="none"
              stroke="currentColor"
              stroke-width="2"
              stroke-linecap="round"
              stroke-linejoin="round"
              style="margin-right: 6px; flex-shrink: 0;"
            >
              <polygon points="5 3 19 12 5 21 5 3" />
            </svg>
            {{ busy ? t('tools.agentRunner.stop') : t('tools.agentRunner.answerBtn') }}
          </el-button>
          <span v-if="busy && elapsed > 0" class="ar-wait-tag">
            {{ t('tools.agentRunner.waiting', { s: elapsed }) }}
          </span>
          <span class="ar-shortcut-hint">Ctrl + Enter</span>
        </div>

        <!-- 右侧：答（GFM Markdown 风格渲染展示） -->
        <section class="ar-pane">
          <header class="ar-pane-head">
            <div class="ar-head-tag">
              <span class="ar-tag-dot a" />
              <span class="ar-pane-title">{{ t('tools.agentRunner.answer') }}</span>
              <span class="ar-md-badge">GFM Markdown</span>
            </div>
            <span v-if="answer" class="ar-pane-count">{{ answer.length }}</span>
          </header>

          <!-- Markdown 富文本展示面板 -->
          <div
            ref="answerScrollRef"
            class="ar-md-view nc-scroll"
          >
            <div v-if="answerHtml" class="ar-md-content" v-html="answerHtml" />
            <div v-else class="ar-empty-tip">
              {{ t('tools.agentRunner.answerPlaceholder') }}
            </div>
          </div>

          <footer class="ar-pane-foot">
            <button
              class="ar-icon-btn"
              :title="t('common.copy')"
              :disabled="!answer.trim()"
              @click="copy(answer)"
            >
              <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <rect x="9" y="9" width="11" height="11" rx="2" />
                <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
              </svg>
            </button>
            <button
              class="ar-icon-btn"
              :class="{ speaking: speaking === 'answer' }"
              :title="speaking === 'answer' ? t('tools.agentRunner.stopSpeak') : t('tools.agentRunner.speakAnswer')"
              :disabled="!answer.trim()"
              @click="speak('answer')"
            >
              <svg v-if="speaking !== 'answer'" viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="M11 5 6.5 9H3v6h3.5L11 19V5Z" />
                <path d="M15 9.5a3.5 3.5 0 0 1 0 5" />
                <path d="M17.8 7a7 7 0 0 1 0 10" />
              </svg>
              <svg v-else viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="M11 5 6.5 9H3v6h3.5L11 19V5Z" />
                <path d="M16 9.5l5 5M21 9.5l-5 5" />
              </svg>
            </button>
          </footer>
        </section>
      </div>

      <p class="ar-foot-note">{{ t('tools.agentRunner.footerNote') }}</p>
    </div>

    <!-- 4. 自定义/编辑智能体弹窗 -->
    <el-dialog
      v-model="editDialogVisible"
      :title="isEditing ? t('tools.agentRunner.modalEditTitle') : t('tools.agentRunner.modalCreateTitle')"
      width="600px"
      append-to-body
      destroy-on-close
      class="ar-modal"
    >
      <div class="ar-form-body">
        <div class="ar-form-row">
          <label class="ar-form-field flex-2">
            <span class="ar-form-label">{{ t('tools.agentRunner.agentName') }}</span>
            <el-input
              v-model="formName"
              :placeholder="t('tools.agentRunner.agentNamePh')"
              maxlength="20"
              show-word-limit
            />
          </label>

          <label class="ar-form-field flex-1">
            <span class="ar-form-label">
              {{ t('tools.agentRunner.sortOrder') }}
              <span class="ar-form-label-hint">({{ t('tools.agentRunner.sortOrderHint') }})</span>
            </span>
            <el-input-number
              v-model="formSortOrder"
              :min="0"
              :max="9999"
              :step="10"
              style="width: 100%;"
            />
          </label>
        </div>

        <!-- ICON 选择器 -->
        <div class="ar-form-field">
          <div class="ar-icon-header">
            <span class="ar-form-label">{{ t('tools.agentRunner.chooseIcon') }}</span>
            <!-- 分类选项卡 -->
            <div class="ar-icon-tabs">
              <button
                class="ar-tab-btn"
                :class="{ active: iconCategory === 'all' }"
                @click="iconCategory = 'all'"
              >
                {{ t('tools.agentRunner.iconAll') }}
              </button>
              <button
                class="ar-tab-btn"
                :class="{ active: iconCategory === 'role' }"
                @click="iconCategory = 'role'"
              >
                {{ t('tools.agentRunner.iconRole') }}
              </button>
              <button
                class="ar-tab-btn"
                :class="{ active: iconCategory === 'tech' }"
                @click="iconCategory = 'tech'"
              >
                {{ t('tools.agentRunner.iconTech') }}
              </button>
              <button
                class="ar-tab-btn"
                :class="{ active: iconCategory === 'creativity' }"
                @click="iconCategory = 'creativity'"
              >
                {{ t('tools.agentRunner.iconCreative') }}
              </button>
              <button
                class="ar-tab-btn"
                :class="{ active: iconCategory === 'efficiency' }"
                @click="iconCategory = 'efficiency'"
              >
                {{ t('tools.agentRunner.iconEfficiency') }}
              </button>
              <button
                class="ar-tab-btn"
                :class="{ active: iconCategory === 'tools' }"
                @click="iconCategory = 'tools'"
              >
                {{ t('tools.agentRunner.iconTools') }}
              </button>
            </div>
          </div>

          <div class="ar-icon-grid">
            <div
              v-for="ico in filteredIcons"
              :key="ico.key"
              class="ar-icon-cell"
              :class="{ selected: formIcon === ico.key }"
              :title="ico.name"
              @click="formIcon = ico.key"
            >
              <svg viewBox="0 0 24 24" width="22" height="22" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round">
                <path v-for="(p, pidx) in ico.paths" :key="pidx" :d="p" />
              </svg>
              <span class="ar-icon-name">{{ ico.name }}</span>
            </div>
          </div>
        </div>

        <label class="ar-form-field">
          <span class="ar-form-label">{{ t('tools.agentRunner.promptLabel') }}</span>
          <el-input
            v-model="formPrompt"
            type="textarea"
            :rows="6"
            resize="none"
            :placeholder="t('tools.agentRunner.promptPh')"
          />
        </label>
      </div>

      <template #footer>
        <div class="ar-modal-foot">
          <el-button @click="editDialogVisible = false">{{ t('common.cancel') }}</el-button>
          <el-button type="primary" @click="handleSaveAgent">{{ t('common.save') }}</el-button>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.ar-page {
  height: 100%;
  overflow: auto;
  padding: 16px 22px;
  display: flex;
  flex-direction: column;
  align-items: center;
  box-sizing: border-box;
}

.ar-card {
  width: 92%;
  max-width: none;
  flex: 1;
  min-height: 0;
  background: var(--nc-surface);
  border: 1px solid var(--nc-border);
  border-radius: 16px;
  padding: 16px 20px 14px;
  box-shadow: 0 10px 34px rgba(2, 12, 27, 0.18);
  display: flex;
  flex-direction: column;
  gap: 14px;
}

/* 顶部智能体抽屉区 */
.ar-drawer-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
}

.ar-drawer-title-group {
  display: flex;
  align-items: center;
  gap: 10px;
}

.ar-drawer-title {
  font-size: 14px;
  font-weight: 700;
  color: var(--nc-text);
  letter-spacing: -0.2px;
}

.ar-drawer-count {
  font-size: 12px;
  color: var(--nc-text-faint);
  background: color-mix(in srgb, var(--nc-border) 60%, transparent);
  padding: 2px 7px;
  border-radius: 6px;
}

.ar-saving-tag {
  font-size: 11px;
  color: var(--nc-primary);
  opacity: 0.8;
}

.ar-drawer-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.ar-scroll-arrow {
  width: 28px;
  height: 28px;
  border-radius: 7px;
  border: 1px solid var(--nc-border);
  background: var(--nc-bg);
  color: var(--nc-text-muted);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  transition: all 0.15s ease;
}

.ar-scroll-arrow:hover:not(.disabled) {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 10%, transparent);
}

.ar-scroll-arrow.disabled {
  opacity: 0.35;
  cursor: not-allowed;
}

/* 查看更多 / 收起平铺展开按钮 */
.ar-expand-toggle-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 28px;
  padding: 0 10px;
  border-radius: 7px;
  border: 1px solid var(--nc-border);
  background: var(--nc-bg);
  color: var(--nc-text-muted);
  font-size: 12px;
  cursor: pointer;
  transition: all 0.18s ease;
}

.ar-expand-toggle-btn:hover {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 8%, transparent);
}

.ar-expand-toggle-btn.active {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 14%, transparent);
  font-weight: 600;
}

.ar-add-btn {
  border-radius: 8px;
  font-weight: 600;
  height: 28px;
  padding: 0 12px;
}

/* 智能体抽屉容器：隐藏滚动条，单行横向拖动滚动 vs 平铺全部展开 */
.ar-drawer-container {
  display: flex;
  align-items: center;
  gap: 12px;
  overflow-x: auto;
  overflow-y: hidden;
  padding: 4px 2px 8px;
  user-select: none;
  cursor: grab;
  /* 隐藏系统滚动条但保留平滑滚动能力 */
  scrollbar-width: none; /* Firefox */
  -ms-overflow-style: none; /* IE/Edge */
  transition: max-height 0.25s cubic-bezier(0.4, 0, 0.2, 1);
  max-height: 76px;
}

.ar-drawer-container::-webkit-scrollbar {
  display: none; /* Chrome, Safari, Opera */
}

.ar-drawer-container:active:not(.expanded) {
  cursor: grabbing;
}

/* 平铺全部展开状态 */
.ar-drawer-container.expanded {
  cursor: default;
  flex-wrap: wrap;
  overflow-x: hidden;
  overflow-y: auto;
  max-height: 280px;
  scrollbar-width: thin;
}

.ar-drawer-container.expanded::-webkit-scrollbar {
  display: block;
  width: 6px;
}

.ar-drawer-container.expanded::-webkit-scrollbar-thumb {
  background: var(--nc-border);
  border-radius: 4px;
}

/* 智能体单个卡片 */
.ar-agent-card {
  flex-shrink: 0;
  width: 215px;
  height: 64px;
  border-radius: 12px;
  border: 1px solid var(--nc-border);
  background: var(--nc-bg);
  padding: 8px 12px;
  display: flex;
  align-items: center;
  gap: 10px;
  position: relative;
  cursor: pointer;
  transition: all 0.18s ease;
  box-sizing: border-box;
}

.ar-agent-card:hover {
  border-color: color-mix(in srgb, var(--nc-primary) 50%, var(--nc-border));
  background: color-mix(in srgb, var(--nc-primary) 4%, var(--nc-bg));
  transform: translateY(-1px);
}

.ar-agent-card.active {
  border-color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 10%, var(--nc-surface));
  box-shadow: 0 2px 10px color-mix(in srgb, var(--nc-primary) 20%, transparent);
}

.ar-agent-avatar {
  width: 38px;
  height: 38px;
  border-radius: 10px;
  background: color-mix(in srgb, var(--nc-primary) 12%, transparent);
  color: var(--nc-primary);
  display: flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  transition: transform 0.15s ease;
}

.ar-agent-card.active .ar-agent-avatar {
  background: var(--nc-primary);
  color: var(--nc-primary-contrast, #fff);
  box-shadow: 0 2px 6px color-mix(in srgb, var(--nc-primary) 35%, transparent);
}

.ar-agent-meta {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
  gap: 2px;
}

.ar-agent-head-line {
  display: flex;
  align-items: center;
  gap: 6px;
  min-width: 0;
}

.ar-agent-name {
  font-size: 13.5px;
  font-weight: 700;
  color: var(--nc-text);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.ar-order-badge {
  font-size: 10.5px;
  font-weight: 600;
  color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 12%, transparent);
  padding: 1px 4px;
  border-radius: 4px;
  flex-shrink: 0;
}

.ar-agent-hint {
  font-size: 11px;
  color: var(--nc-text-faint);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 卡片角标操作按钮 */
.ar-agent-ops {
  position: absolute;
  top: 4px;
  right: 4px;
  display: none;
  align-items: center;
  gap: 3px;
  background: color-mix(in srgb, var(--nc-surface) 90%, transparent);
  border-radius: 6px;
  padding: 2px;
}

.ar-agent-card:hover .ar-agent-ops {
  display: flex;
}

.ar-op-btn {
  width: 18px;
  height: 18px;
  border-radius: 4px;
  border: none;
  background: transparent;
  color: var(--nc-text-muted);
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  transition: all 0.15s ease;
}

.ar-op-btn:hover {
  background: var(--nc-border);
  color: var(--nc-text);
}

.ar-op-btn.del:hover {
  background: color-mix(in srgb, var(--el-color-danger, #f56c6c) 15%, transparent);
  color: var(--el-color-danger, #f56c6c);
}

/* 中间控制栏 */
.ar-toolbar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 12px;
  flex-wrap: wrap;
  padding-top: 4px;
  border-top: 1px solid color-mix(in srgb, var(--nc-border) 60%, transparent);
}

.ar-tool-group {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.ar-current-badge {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  padding: 4px 10px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--nc-primary) 10%, transparent);
  border: 1px solid color-mix(in srgb, var(--nc-primary) 25%, transparent);
  font-size: 12px;
}

.ar-active-dot {
  width: 6px;
  height: 6px;
  border-radius: 50%;
  background: var(--nc-primary);
}

.ar-badge-label {
  color: var(--nc-text-muted);
}

.ar-badge-val {
  font-weight: 700;
  color: var(--nc-primary);
}

.ar-badge-order {
  font-size: 11px;
  color: var(--nc-text-faint);
}

.ar-model-select {
  width: 250px;
}

.ar-stream-mode {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 12.5px;
  color: var(--nc-text-muted);
  cursor: pointer;
  user-select: none;
}

.ar-stream-mode input {
  cursor: pointer;
}

.ar-ghost-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 5px 12px;
  border-radius: 7px;
  border: 1px solid var(--nc-border);
  background: var(--nc-bg);
  color: var(--nc-text-muted);
  font-size: 12px;
  cursor: pointer;
  transition: all 0.15s ease;
}

.ar-ghost-btn:hover {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
}

.ar-ghost-btn.on {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 10%, transparent);
  font-weight: 600;
}

.ar-ghost-btn.small {
  padding: 3px 8px;
  font-size: 11.5px;
}

/* 提示词折叠面板 */
.ar-prompt-panel {
  background: var(--nc-bg);
  border: 1px solid var(--nc-border);
  border-radius: 10px;
  padding: 10px 14px;
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.ar-prompt-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.ar-prompt-title {
  font-size: 12px;
  font-weight: 700;
  color: var(--nc-text-muted);
}

.ar-prompt-body {
  font-size: 12.5px;
  line-height: 1.6;
  color: var(--nc-text);
  white-space: pre-wrap;
  word-break: break-word;
  max-height: 120px;
  overflow-y: auto;
}

/* 下部问答双栏网格 */
.ar-grid {
  flex: 1;
  min-height: 0;
  display: grid;
  grid-template-columns: 1fr auto 1fr;
  gap: 12px;
  align-items: stretch;
}

.ar-pane {
  display: flex;
  flex-direction: column;
  border: 1px solid var(--nc-border);
  border-radius: 12px;
  overflow: hidden;
  background: var(--nc-bg);
  min-height: 280px;
}

.ar-pane-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 8px 14px;
  background: color-mix(in srgb, var(--nc-surface) 60%, transparent);
  border-bottom: 1px solid var(--nc-border);
  font-size: 12px;
}

.ar-head-tag {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.ar-tag-dot {
  width: 7px;
  height: 7px;
  border-radius: 50%;
}

.ar-tag-dot.q {
  background: var(--nc-primary);
}

.ar-tag-dot.a {
  background: #10b981;
}

.ar-pane-title {
  font-weight: 700;
  color: var(--nc-text);
}

.ar-md-badge {
  font-size: 10px;
  font-weight: 600;
  color: #10b981;
  background: color-mix(in srgb, #10b981 12%, transparent);
  padding: 1px 5px;
  border-radius: 4px;
}

.ar-pane-count {
  color: var(--nc-text-faint);
  font-size: 11px;
}

.ar-area {
  flex: 1;
  min-height: 0;
  border: none;
  outline: none;
  background: transparent;
  color: var(--nc-text);
  padding: 14px 16px;
  font-size: 14px;
  line-height: 1.7;
  resize: none;
  font-family: inherit;
  box-sizing: border-box;
}

.ar-area::placeholder {
  color: var(--nc-text-faint);
}

/* Markdown 富文本展示面板 */
.ar-md-view {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 14px 18px;
  background: color-mix(in srgb, var(--nc-surface) 40%, transparent);
  box-sizing: border-box;
}

.ar-empty-tip {
  color: var(--nc-text-faint);
  font-size: 13.5px;
  line-height: 1.7;
  user-select: none;
}

/* GFM Markdown 内部富文本样式 */
.ar-md-content {
  font-size: 13.5px;
  line-height: 1.75;
  color: var(--nc-text);
  word-break: break-word;
}

.ar-md-content :deep(h1),
.ar-md-content :deep(h2),
.ar-md-content :deep(h3),
.ar-md-content :deep(h4) {
  margin: 14px 0 8px;
  font-weight: 700;
  color: var(--nc-text);
  line-height: 1.35;
}

.ar-md-content :deep(h1) {
  font-size: 18px;
  padding-bottom: 6px;
  border-bottom: 1px solid var(--nc-border);
}

.ar-md-content :deep(h2) {
  font-size: 16px;
}

.ar-md-content :deep(h3) {
  font-size: 14.5px;
}

.ar-md-content :deep(p) {
  margin: 8px 0;
}

.ar-md-content :deep(ul),
.ar-md-content :deep(ol) {
  margin: 6px 0;
  padding-left: 20px;
}

.ar-md-content :deep(li) {
  margin: 3px 0;
}

.ar-md-content :deep(code) {
  font-family: Consolas, 'JetBrains Mono', Menlo, monospace;
  font-size: 12px;
  background: rgba(148, 163, 184, 0.16);
  border-radius: 4px;
  padding: 2px 5px;
  color: var(--nc-primary);
}

.ar-md-content :deep(pre) {
  background: rgba(15, 23, 42, 0.88);
  color: #e2e8f0;
  border-radius: 8px;
  padding: 12px 14px;
  overflow-x: auto;
  margin: 10px 0;
}

.ar-md-content :deep(pre code) {
  background: transparent;
  padding: 0;
  color: inherit;
  font-size: 12px;
  line-height: 1.55;
}

.ar-md-content :deep(blockquote) {
  margin: 8px 0;
  padding: 4px 12px;
  border-left: 3px solid var(--nc-primary);
  color: var(--nc-text-muted);
  background: color-mix(in srgb, var(--nc-primary) 6%, transparent);
  border-radius: 0 6px 6px 0;
}

.ar-md-content :deep(table) {
  border-collapse: collapse;
  margin: 10px 0;
  width: 100%;
  font-size: 12.5px;
}

.ar-md-content :deep(th),
.ar-md-content :deep(td) {
  border: 1px solid var(--nc-border);
  padding: 6px 10px;
  text-align: left;
}

.ar-md-content :deep(th) {
  background: color-mix(in srgb, var(--nc-surface) 80%, transparent);
  font-weight: 600;
}

.ar-md-content :deep(hr) {
  border: 0;
  border-top: 1px solid var(--nc-border);
  margin: 12px 0;
}

.ar-pane-foot {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 12px;
  border-top: 1px solid color-mix(in srgb, var(--nc-border) 60%, transparent);
  background: color-mix(in srgb, var(--nc-surface) 30%, transparent);
}

.ar-icon-btn {
  width: 28px;
  height: 28px;
  border-radius: 6px;
  border: 1px solid transparent;
  background: transparent;
  color: var(--nc-text-muted);
  display: inline-flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
  transition: all 0.15s ease;
}

.ar-icon-btn:hover:not(:disabled) {
  background: var(--nc-border);
  color: var(--nc-text);
}

.ar-icon-btn.speaking {
  color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 15%, transparent);
  border-color: color-mix(in srgb, var(--nc-primary) 30%, transparent);
}

.ar-icon-btn:disabled {
  opacity: 0.35;
  cursor: not-allowed;
}

/* 中间操作列 */
.ar-action-col {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 10px;
  padding: 0 4px;
}

.ar-go-btn {
  border-radius: 10px;
  padding: 0 20px;
  height: 42px;
  font-weight: 700;
}

.ar-wait-tag {
  font-size: 11.5px;
  color: var(--nc-text-muted);
}

.ar-shortcut-hint {
  font-size: 11px;
  color: var(--nc-text-faint);
}

.ar-foot-note {
  margin: 0;
  font-size: 11.5px;
  color: var(--nc-text-faint);
  text-align: center;
}

/* 模态框表单样式 */
.ar-form-body {
  display: flex;
  flex-direction: column;
  gap: 16px;
}

.ar-form-row {
  display: flex;
  align-items: flex-start;
  gap: 12px;
}

.flex-1 {
  flex: 1;
}

.flex-2 {
  flex: 2;
}

.ar-form-field {
  display: flex;
  flex-direction: column;
  gap: 6px;
}

.ar-form-label {
  font-size: 12.5px;
  font-weight: 600;
  color: var(--nc-text);
}

.ar-form-label-hint {
  font-size: 11px;
  font-weight: 400;
  color: var(--nc-text-faint);
}

.ar-icon-header {
  display: flex;
  align-items: center;
  justify-content: space-between;
  flex-wrap: wrap;
  gap: 8px;
}

.ar-icon-tabs {
  display: flex;
  gap: 4px;
}

.ar-tab-btn {
  padding: 2px 7px;
  font-size: 11.5px;
  border-radius: 5px;
  border: 1px solid var(--nc-border);
  background: var(--nc-bg);
  color: var(--nc-text-muted);
  cursor: pointer;
  transition: all 0.15s ease;
}

.ar-tab-btn.active {
  background: var(--nc-primary);
  color: var(--nc-primary-contrast, #fff);
  border-color: var(--nc-primary);
}

.ar-icon-grid {
  display: grid;
  grid-template-columns: repeat(6, 1fr);
  gap: 8px;
  max-height: 180px;
  overflow-y: auto;
  border: 1px solid var(--nc-border);
  border-radius: 10px;
  padding: 10px;
  background: var(--nc-bg);
}

.ar-icon-cell {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 4px;
  padding: 8px 4px;
  border-radius: 8px;
  border: 1px solid transparent;
  color: var(--nc-text-muted);
  cursor: pointer;
  transition: all 0.15s ease;
}

.ar-icon-cell:hover {
  background: var(--nc-surface);
  color: var(--nc-primary);
}

.ar-icon-cell.selected {
  border-color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 15%, transparent);
  color: var(--nc-primary);
}

.ar-icon-name {
  font-size: 10.5px;
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
  max-width: 100%;
}

.ar-modal-foot {
  display: flex;
  justify-content: flex-end;
  gap: 10px;
}

@media (max-width: 860px) {
  .ar-grid {
    grid-template-columns: 1fr;
    grid-template-rows: auto auto auto;
  }
  .ar-action-col {
    flex-direction: row;
    padding: 6px 0;
  }
  .ar-pane {
    min-height: 200px;
  }
  .ar-form-row {
    flex-direction: column;
  }
}
</style>
