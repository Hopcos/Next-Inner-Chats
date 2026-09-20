<script setup lang="ts">
/**
 * AI 翻译工具页（Cordis 工具插件 ai-translate 的实现界面）。
 * 约定：翻译内容零落库——每次调用无状态端点 /api/tools/llm/complete（非思考、无会话上下文），
 * 方向 / 模型 / 专家提示词等状态仅存 localStorage。
 */
import { computed, onBeforeUnmount, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'
import { http, streamPost } from '@/api/http'
import { copyText } from '@/utils/clipboard'
import { DEFAULT_TRANSLATE_PROMPT, LS, loadDirection, loadModel, loadPrompt, loadStreamMode, savePrompt, saveStreamMode } from './config'
import type { Direction } from './config'

const { t } = useI18n()

const MAX_CHARS = 50000

// ---------------- 状态（localStorage-only） ----------------
const direction = ref<Direction>(loadDirection())
const promptText = ref(loadPrompt())
const showPrompt = ref(false)
// 输出模式：false(默认)= 完整模式（非流式，杜绝上游流式首增量偶发丢字）；true = 流式打字机
const streamMode = ref(loadStreamMode())
watch(streamMode, (v) => saveStreamMode(v))

function setDir(d: Direction) {
  direction.value = d
  try {
    localStorage.setItem(LS.direction, d)
  } catch {
    /* ignore */
  }
}

function onPromptInput() {
  savePrompt(promptText.value)
}

function resetPrompt() {
  promptText.value = DEFAULT_TRANSLATE_PROMPT
  savePrompt(DEFAULT_TRANSLATE_PROMPT)
}

// ---------------- 模型（当前用户权限下的 LLM 列表） ----------------
if (!kernel.catalog.state.loaded) void kernel.catalog.load().catch(() => {})

const modelOptions = computed(() =>
  kernel.catalog.state.providers.flatMap((p) => p.models.map((m) => ({ id: m.id, label: `${m.name} · ${p.name}` }))),
)

const modelId = ref(loadModel())
watch(modelId, (v) => {
  try {
    localStorage.setItem(LS.model, v)
  } catch {
    /* ignore */
  }
})
// 记忆失效/首次使用 → 自动选中第一个可用模型
watch(
  modelOptions,
  (opts) => {
    if (opts.length && !opts.some((o) => o.id === modelId.value)) modelId.value = opts[0].id
  },
  { immediate: true },
)

// ---------------- 翻译（流式输出） ----------------
const source = ref('')
const target = ref('')
const busy = ref(false)
let abort: AbortController | null = null

const en2zh = computed(() => direction.value === 'en2zh')

async function run() {
  // 翻译中再次点击 = 停止当前流
  if (busy.value) {
    abort?.abort()
    return
  }
  if (!source.value.trim()) {
    kernel.notify.warning(t('tools.translate.emptyWarn'))
    return
  }
  if (!modelId.value) {
    kernel.notify.warning(t('tools.translate.modelRequired'))
    return
  }
  const userPrompt = en2zh.value
    ? `请将下面的英文翻译成简体中文：\n\n${source.value}`
    : `Please translate the following Chinese text into English:\n\n${source.value}`

  busy.value = true
  target.value = ''
  abort = new AbortController()
  let streamErr: { code?: string; message?: string } | null = null
  try {
    if (streamMode.value) {
      // 流式打字机模式（透传上游 text_delta；已知上游网关偶发首增量缺字，个别情况会缺开头几个字）
      await streamPost(
        '/api/tools/llm/stream',
        {
          modelId: modelId.value,
          systemPrompt: promptText.value.trim() || DEFAULT_TRANSLATE_PROMPT,
          prompt: userPrompt,
          // 输出上限对准 5 万字原文/译文（中文字 ≈ 2 token，5 万字 ≈ 100k token），留足余量
          maxTokens: 128000,
        },
        (ev) => {
          if (ev.kind === 'text_delta') target.value += String(ev.text ?? '')
          else if (ev.kind === 'error') streamErr = { code: String(ev.code ?? ''), message: String(ev.message ?? '') }
        },
        abort.signal,
      )
    } else {
      // 完整模式（默认）：非流式补全 —— 单次完整 JSON 响应，无增量边界，可根除"译文缺开头几个字"
      const res = await http.post<{ text: string }>('/api/tools/llm/complete', {
        modelId: modelId.value,
        systemPrompt: promptText.value.trim() || DEFAULT_TRANSLATE_PROMPT,
        prompt: userPrompt,
        maxTokens: 128000,
      })
      target.value = res?.text ?? ''
    }
    if (streamErr) throw streamErr
  } catch (e) {
    const err = e as { name?: string; code?: string; message?: string }
    if (err.name === 'AbortError' || err.code === 'INTERRUPTED') {
      // 用户主动停止：保留已生成的译文
    } else {
      kernel.notify.error(err.message ?? t('tools.translate.failed'), err.code as string)
    }
  } finally {
    busy.value = false
    abort = null
  }
}

/** 互换：原文/译文对调，方向同时翻转 */
function swap() {
  const prevTarget = target.value
  target.value = source.value
  source.value = prevTarget
  setDir(en2zh.value ? 'zh2en' : 'en2zh')
}

// ---------------- 复制 / 朗读 ----------------
async function copy(which: 'source' | 'target') {
  const text = which === 'source' ? source.value : target.value
  if (!text.trim()) return
  const ok = await copyText(text)
  if (ok) kernel.notify.success(t('tools.translate.copied'))
  else kernel.notify.error(t('tools.translate.copyFailed'))
}

const speaking = ref<'' | 'source' | 'target'>('')

/** 朗读语音语言：跟随方向（英文侧 en-US，中文侧 zh-CN） */
function voiceLang(which: 'source' | 'target'): string {
  const sourceIsEn = en2zh.value
  const isEn = which === 'source' ? sourceIsEn : !sourceIsEn
  return isEn ? 'en-US' : 'zh-CN'
}

function speak(which: 'source' | 'target') {
  const text = which === 'source' ? source.value : target.value
  if (!text.trim()) return
  if (!('speechSynthesis' in window)) {
    kernel.notify.warning(t('tools.translate.speakUnsupported'))
    return
  }
  if (speaking.value === which) {
    stopSpeak()
    return
  }
  window.speechSynthesis.cancel()
  const utter = new SpeechSynthesisUtterance(text)
  utter.lang = voiceLang(which)
  utter.onend = () => (speaking.value = '')
  utter.onerror = () => (speaking.value = '')
  speaking.value = which
  window.speechSynthesis.speak(utter)
}

function stopSpeak() {
  if ('speechSynthesis' in window) window.speechSynthesis.cancel()
  speaking.value = ''
}

onBeforeUnmount(stopSpeak)
</script>

<template>
  <div class="tr-page">
    <div class="tr-card">
      <!-- 顶部控制条：方向 / 模型 / 专家设置 -->
      <div class="tr-toolbar">
        <div class="tr-seg">
          <button class="tr-seg-btn" :class="{ on: en2zh }" @click="setDir('en2zh')">
            <span class="tr-seg-code">EN</span>→<span class="tr-seg-code">中</span>
          </button>
          <button class="tr-seg-btn" :class="{ on: !en2zh }" @click="setDir('zh2en')">
            <span class="tr-seg-code">中</span>→<span class="tr-seg-code">EN</span>
          </button>
        </div>

        <el-select v-model="modelId" size="small" class="tr-model" :placeholder="t('tools.translate.model')" :no-data-text="t('tools.translate.noModels')">
          <el-option v-for="opt in modelOptions" :key="opt.id" :label="opt.label" :value="opt.id" />
        </el-select>

        <button class="tr-ghost-btn" :class="{ on: showPrompt }" @click="showPrompt = !showPrompt">
          <svg viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M12 4.5l1.4 4.1L17.5 10l-4.1 1.4L12 15.5l-1.4-4.1L6.5 10l4.1-1.4L12 4.5Z" />
            <path d="M18.5 15.5l.8 2.2 2.2.8-2.2.8-.8 2.2-.8-2.2-2.2-.8 2.2-.8.8-2.2Z" />
          </svg>
          {{ t('tools.translate.prompt') }}
        </button>

        <!-- 输出模式：完整（默认，无丢字）vs 流式打字机 -->
        <label class="tr-mode" :title="t('tools.translate.streamModeTip')">
          <input v-model="streamMode" type="checkbox" />
          <span>{{ t('tools.translate.streamMode') }}</span>
        </label>
      </div>

      <!-- 专家提示词面板（仅存 localStorage） -->
      <el-collapse-transition>
        <div v-show="showPrompt" class="tr-prompt-panel">
          <div class="tr-prompt-head">
            <span class="tr-prompt-title">{{ t('tools.translate.prompt') }}</span>
            <span class="tr-prompt-tip">{{ t('tools.translate.promptTip') }}</span>
            <button class="tr-ghost-btn small" @click="resetPrompt">{{ t('tools.translate.promptReset') }}</button>
          </div>
          <el-input v-model="promptText" type="textarea" :rows="5" resize="none" @input="onPromptInput" />
        </div>
      </el-collapse-transition>

      <!-- 原文 / 译文 双栏 -->
      <div class="tr-grid">
        <section class="tr-pane">
          <header class="tr-pane-head">
            <span class="tr-pane-title">{{ t('tools.translate.source') }}</span>
            <span class="tr-pane-lang">{{ en2zh ? 'English' : '简体中文' }}</span>
            <span class="tr-pane-count">{{ source.length }} / {{ MAX_CHARS }}</span>
          </header>
          <textarea
            v-model="source"
            class="tr-area"
            :maxlength="MAX_CHARS"
            :placeholder="t('tools.translate.sourcePlaceholder')"
            spellcheck="false"
          />
          <footer class="tr-pane-foot">
            <button class="tr-icon-btn" :title="t('tools.translate.copy')" :disabled="!source.trim()" @click="copy('source')">
              <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <rect x="9" y="9" width="11" height="11" rx="2" />
                <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
              </svg>
            </button>
            <button
              class="tr-icon-btn"
              :class="{ speaking: speaking === 'source' }"
              :title="speaking === 'source' ? t('tools.translate.stopSpeak') : t('tools.translate.speak')"
              :disabled="!source.trim()"
              @click="speak('source')"
            >
              <svg v-if="speaking !== 'source'" viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="M11 5 6.5 9H3v6h3.5L11 19V5Z" />
                <path d="M15 9.5a3.5 3.5 0 0 1 0 5" />
                <path d="M17.8 7a7 7 0 0 1 0 10" />
              </svg>
              <svg v-else viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="M11 5 6.5 9H3v6h3.5L11 19V5Z" />
                <path d="M16 9.5l5 5M21 9.5l-5 5" />
              </svg>
            </button>
          </footer>
        </section>

        <div class="tr-swap-col">
          <button class="tr-swap" :title="t('tools.translate.swap')" @click="swap">
            <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
              <path d="M8 4 4 8l4 4" />
              <path d="M4 8h12a4 4 0 0 1 4 4v1" />
              <path d="m16 20 4-4-4-4" />
              <path d="M20 16H8a4 4 0 0 1-4-4V11" />
            </svg>
          </button>
          <el-button type="primary" class="tr-go" :loading="busy" size="large" @click="run">
            {{ busy ? t('tools.translate.stop') : t('tools.translate.translateBtn') }}
          </el-button>
        </div>

        <section class="tr-pane">
          <header class="tr-pane-head">
            <span class="tr-pane-title">{{ t('tools.translate.target') }}</span>
            <span class="tr-pane-lang">{{ en2zh ? '简体中文' : 'English' }}</span>
            <span v-if="target" class="tr-pane-count">{{ target.length }}</span>
          </header>
          <textarea v-model="target" class="tr-area out" readonly spellcheck="false" :placeholder="t('tools.translate.translatedPlaceholder')" />
          <footer class="tr-pane-foot">
            <button class="tr-icon-btn" :title="t('tools.translate.copy')" :disabled="!target.trim()" @click="copy('target')">
              <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <rect x="9" y="9" width="11" height="11" rx="2" />
                <path d="M5 15H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2h9a2 2 0 0 1 2 2v1" />
              </svg>
            </button>
            <button
              class="tr-icon-btn"
              :class="{ speaking: speaking === 'target' }"
              :title="speaking === 'target' ? t('tools.translate.stopSpeak') : t('tools.translate.speak')"
              :disabled="!target.trim()"
              @click="speak('target')"
            >
              <svg v-if="speaking !== 'target'" viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="M11 5 6.5 9H3v6h3.5L11 19V5Z" />
                <path d="M15 9.5a3.5 3.5 0 0 1 0 5" />
                <path d="M17.8 7a7 7 0 0 1 0 10" />
              </svg>
              <svg v-else viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                <path d="M11 5 6.5 9H3v6h3.5L11 19V5Z" />
                <path d="M16 9.5l5 5M21 9.5l-5 5" />
              </svg>
            </button>
          </footer>
        </section>
      </div>

      <p class="tr-foot-note">{{ t('tools.translate.noPersistNote') }}</p>
    </div>
  </div>
</template>

<style scoped>
.tr-page {
  height: 100%;
  overflow: auto;
  padding: 20px 24px;
  display: flex;
  flex-direction: column;
  align-items: center;
}

.tr-card {
  width: 90%;
  max-width: none;
  flex: 1;
  min-height: 0;
  background: var(--nc-surface);
  border: 1px solid var(--nc-border);
  border-radius: 16px;
  padding: 18px 20px 14px;
  box-shadow: 0 10px 34px rgba(2, 12, 27, 0.18);
  display: flex;
  flex-direction: column;
  gap: 14px;
}

.tr-toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.tr-seg {
  display: inline-flex;
  border: 1px solid var(--nc-border);
  border-radius: 9px;
  overflow: hidden;
  background: var(--nc-surface-2, rgba(128, 128, 128, 0.06));
}

.tr-seg-btn {
  border: 0;
  background: transparent;
  color: var(--nc-text-dim, #8a94a6);
  padding: 7px 14px;
  font-size: 13px;
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  gap: 5px;
  transition: all 0.15s;
}

.tr-seg-btn.on {
  background: var(--nc-primary);
  color: #fff;
}

.tr-seg-code {
  font-weight: 700;
}

.tr-model {
  width: 250px;
}

.tr-mode {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  font-size: 12.5px;
  color: var(--nc-text-dim, #8a94a6);
  cursor: pointer;
  user-select: none;
  white-space: nowrap;
}

.tr-mode input {
  accent-color: var(--nc-primary, #4e7cff);
  cursor: pointer;
  margin: 0;
}

.tr-mode:hover {
  color: var(--nc-text);
}

.tr-ghost-btn {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  border: 1px solid var(--nc-border);
  background: transparent;
  color: var(--nc-text-dim, #8a94a6);
  border-radius: 8px;
  padding: 6px 11px;
  font-size: 12.5px;
  cursor: pointer;
  transition: all 0.15s;
}

.tr-ghost-btn:hover,
.tr-ghost-btn.on {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
}

.tr-ghost-btn.small {
  padding: 3px 9px;
  font-size: 12px;
}

.tr-prompt-panel {
  border: 1px dashed var(--nc-border);
  border-radius: 12px;
  padding: 12px 14px;
  display: flex;
  flex-direction: column;
  gap: 8px;
  background: rgba(128, 128, 128, 0.04);
}

.tr-prompt-head {
  display: flex;
  align-items: center;
  gap: 10px;
}

.tr-prompt-title {
  font-size: 13px;
  font-weight: 700;
  color: var(--nc-text);
}

.tr-prompt-tip {
  flex: 1;
  font-size: 12px;
  color: var(--nc-text-dim, #8a94a6);
}

.tr-grid {
  flex: 1;
  min-height: 160px;
  display: grid;
  grid-template-columns: 1fr auto 1fr;
  gap: 14px;
  align-items: stretch;
}

.tr-pane {
  display: flex;
  flex-direction: column;
  border: 1px solid #e2e8f0;
  border-radius: 12px;
  overflow: hidden;
  background: #ffffff;
  min-width: 0;
  min-height: 0;
}

.tr-pane-head {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 8px 12px;
  border-bottom: 1px solid #eef2f7;
  background: #f8fafc;
}

.tr-pane-title {
  font-size: 13px;
  font-weight: 700;
  color: #1e293b;
}

.tr-pane-lang {
  font-size: 11.5px;
  color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 12%, transparent);
  border-radius: 999px;
  padding: 1px 8px;
}

.tr-pane-count {
  margin-left: auto;
  font-size: 11.5px;
  color: #94a3b8;
}

.tr-area {
  flex: 1;
  min-height: 0;
  border: 0;
  outline: none;
  resize: none;
  background: #ffffff;
  color: #1e293b;
  padding: 12px 14px;
  font-size: 14px;
  line-height: 1.7;
  font-family: inherit;
}

.tr-area.out {
  background: #ffffff;
}

.tr-area::placeholder {
  color: #94a3b8;
  opacity: 0.8;
}

.tr-pane-foot {
  display: flex;
  gap: 8px;
  padding: 7px 10px;
  border-top: 1px solid #eef2f7;
  background: #fafbfd;
}

.tr-icon-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 30px;
  height: 30px;
  border-radius: 8px;
  border: 1px solid transparent;
  background: transparent;
  color: var(--nc-text-dim, #8a94a6);
  cursor: pointer;
  transition: all 0.15s;
}

.tr-icon-btn:hover:not(:disabled) {
  color: var(--nc-primary);
  border-color: var(--nc-border);
  background: var(--nc-surface);
}

.tr-icon-btn:disabled {
  opacity: 0.4;
  cursor: not-allowed;
}

.tr-icon-btn.speaking {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
  animation: tr-pulse 1.2s ease-in-out infinite;
}

@keyframes tr-pulse {
  50% {
    opacity: 0.55;
  }
}

.tr-swap-col {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 14px;
}

.tr-swap {
  width: 36px;
  height: 36px;
  border-radius: 50%;
  border: 1px solid var(--nc-border);
  background: var(--nc-surface);
  color: var(--nc-text-dim, #8a94a6);
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  transition: all 0.15s;
}

.tr-swap:hover {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
  transform: rotate(180deg);
}

.tr-go {
  writing-mode: initial;
  border-radius: 12px !important;
  padding: 20px 18px !important;
  font-weight: 700;
}

.tr-foot-note {
  margin: 0;
  text-align: center;
  font-size: 12px;
  color: var(--nc-text-dim, #8a94a6);
  opacity: 0.8;
}

@media (max-width: 900px) {
  .tr-grid {
    grid-template-columns: 1fr;
  }

  .tr-swap-col {
    flex-direction: row;
  }

  .tr-area {
    min-height: 220px;
  }
}
</style>
