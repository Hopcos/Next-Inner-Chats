<script setup lang="ts">
/**
 * Color Picker 工具页（移植自 dev-tools 并增强）：
 * EyeDropper 屏幕取色（Alt+C）+ 原生色板 + HEX 手输，HEX/RGB/HSL 三格式复制，
 * WCAG 对比度实时校验，历史色板 localStorage 持久化。
 * 兼容性增强：
 * 1. 挂载时主动探测环境是否为安全上下文（isSecureContext）及是否存在 EyeDropper API。
 * 2. 若在非安全上下文（如局域网 HTTP IP 访问）或不支持的浏览器中：
 *    - 页面顶部/提示区给出明确说明，并在用户点击取色时智能降级（自动触发原生色板弹窗），保证操作不断流。
 */
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'
import { copyText } from '@/utils/clipboard'
import {
  LS,
  MAX_HISTORY,
  contrastRatio,
  hexToRgb,
  isValidHex,
  loadHistory,
  rgbToHex,
  rgbToHsl,
  saveHistory,
} from './config'

const { t } = useI18n()

type EyeDropperLike = { open: () => Promise<{ sRGBHex: string }> }

const current = ref(normalize(localStorage.getItem(LS.current) || '#6366F1'))
const hexInput = ref(current.value)
const history = ref<string[]>(loadHistory())
const supported = ref(true)
const isSecure = ref(true)
const colorInputRef = ref<HTMLInputElement | null>(null)

function normalize(hex: string): string {
  const rgb = hexToRgb(hex)
  return rgb ? rgbToHex(...rgb) : '#6366F1'
}

const rgb = computed<[number, number, number]>(() => hexToRgb(current.value) ?? [99, 102, 241])
const rgbText = computed(() => `rgb(${rgb.value.join(', ')})`)
const hslText = computed(() => {
  const [h, s, l] = rgbToHsl(...rgb.value)
  return `hsl(${h}, ${s}%, ${l}%)`
})
/** 原生 <input type=color> 需要 #rrggbb 小写 */
const nativeColor = computed(() => current.value.toLowerCase())

const onWhite = computed(() => contrastRatio(current.value, [255, 255, 255]))
const onBlack = computed(() => contrastRatio(current.value, [0, 0, 0]))

function ratioBadge(r: number): { level: string; cls: string } {
  if (r >= 7) return { level: 'AAA', cls: 'pass-aaa' }
  if (r >= 4.5) return { level: 'AA', cls: 'pass-aa' }
  if (r >= 3) return { level: 'AA-LG', cls: 'pass-aa-lg' }
  return { level: 'FAIL', cls: 'fail' }
}

function applyColor(hex: string, addToHistory = true) {
  const n = normalize(hex)
  current.value = n
  hexInput.value = n
  try {
    localStorage.setItem(LS.current, n)
  } catch {
    /* ignore */
  }
  if (addToHistory) {
    const rest = history.value.filter((h) => h.toUpperCase() !== n)
    history.value = [n, ...rest].slice(0, MAX_HISTORY)
    saveHistory(history.value)
  }
}

function onHexInput(v: string) {
  hexInput.value = v
  if (isValidHex(v)) applyColor(v)
}

function onNativePick(e: Event) {
  applyColor((e.target as HTMLInputElement).value)
}

async function pickFromScreen() {
  const Ctor = (window as unknown as { EyeDropper?: new () => EyeDropperLike }).EyeDropper
  if (!Ctor) {
    supported.value = false
    // 检查是否是非安全上下文导致的
    if (!window.isSecureContext) {
      isSecure.value = false
      kernel.notify.warning(t('tools.color.insecureContextTip'))
    } else {
      kernel.notify.warning(t('tools.color.noEyeDropper'))
    }
    // 自动平滑降级唤起原生调色板
    triggerNativePicker()
    return
  }
  try {
    const result = await new Ctor().open()
    applyColor(result.sRGBHex)
  } catch {
    /* 用户取消取色：静默 */
  }
}

function triggerNativePicker() {
  if (colorInputRef.value) {
    try {
      if (typeof colorInputRef.value.showPicker === 'function') {
        colorInputRef.value.showPicker()
      } else {
        colorInputRef.value.click()
      }
    } catch {
      colorInputRef.value.click()
    }
  }
}

function onKey(e: KeyboardEvent) {
  if (e.altKey && e.key.toLowerCase() === 'c') {
    e.preventDefault()
    void pickFromScreen()
  }
}

onMounted(() => {
  supported.value = 'EyeDropper' in window
  isSecure.value = window.isSecureContext
  window.addEventListener('keydown', onKey)
})
onBeforeUnmount(() => window.removeEventListener('keydown', onKey))

async function copy(text: string, label: string) {
  if (await copyText(text)) kernel.notify.success(t('tools.color.copiedFmt', { fmt: label }))
  else kernel.notify.error(t('tools.common.copyFailed'))
}

function removeHistory(hex: string) {
  history.value = history.value.filter((h) => h !== hex)
  saveHistory(history.value)
}

function clearHistory() {
  history.value = []
  saveHistory([])
}
</script>

<template>
  <div class="cp-page">
    <div class="cp-main">
      <!-- 大色块预览：白/黑文字样本 + 对比度 -->
      <div class="cp-preview" :style="{ background: current }">
        <div class="cp-preview-light">
          <span class="cp-aa" style="color: #ffffff">Aa</span>
          <span class="cp-ratio" style="color: #ffffff">{{ onWhite.toFixed(2) }}:1</span>
        </div>
        <div class="cp-preview-dark">
          <span class="cp-aa" style="color: #000000">Aa</span>
          <span class="cp-ratio" style="color: #000000">{{ onBlack.toFixed(2) }}:1</span>
        </div>
      </div>

      <div class="cp-controls">
        <el-button type="primary" size="large" class="cp-pick" @click="pickFromScreen">
          <svg viewBox="0 0 24 24" width="17" height="17" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="m14.5 3.8 5.7 5.7-2 2-1.2-1.2-7.4 7.4-.3 3-2.7-2.7 3-.3 7.4-7.4-1.2-1.2 2-2Z" />
            <path d="M4 20.5 6.3 18.2" />
          </svg>
          {{ t('tools.color.pick') }} <kbd class="cp-kbd">Alt + C</kbd>
        </el-button>

        <label class="cp-swatch" :title="t('tools.color.palette')">
          <input ref="colorInputRef" type="color" :value="nativeColor" @input="onNativePick" />
          <span class="cp-swatch-face" :style="{ background: current }" />
          <span class="cp-swatch-label">{{ t('tools.color.palette') }}</span>
        </label>

        <div class="cp-hex">
          <span class="cp-hex-label">HEX</span>
          <el-input v-model="hexInput" size="large" class="cp-hex-input" @input="onHexInput" />
          <span v-if="!isValidHex(hexInput)" class="cp-hex-bad">{{ t('tools.color.invalidHex') }}</span>
        </div>
      </div>

      <!-- 格式行：点击复制 -->
      <div class="cp-formats">
        <button class="cp-fmt" @click="copy(current, 'HEX')">
          <span class="cp-fmt-key">HEX</span><code>{{ current }}</code><span class="cp-fmt-copy">{{ t('tools.common.copy') }}</span>
        </button>
        <button class="cp-fmt" @click="copy(rgbText, 'RGB')">
          <span class="cp-fmt-key">RGB</span><code>{{ rgbText }}</code><span class="cp-fmt-copy">{{ t('tools.common.copy') }}</span>
        </button>
        <button class="cp-fmt" @click="copy(hslText, 'HSL')">
          <span class="cp-fmt-key">HSL</span><code>{{ hslText }}</code><span class="cp-fmt-copy">{{ t('tools.common.copy') }}</span>
        </button>
      </div>

      <!-- WCAG -->
      <div class="cp-wcag">
        <span class="cp-wcag-title">{{ t('tools.color.wcag') }}</span>
        <span class="cp-wcag-item">
          <i class="cp-chip white" /> vs #FFF
          <b :class="['cp-badge', ratioBadge(onWhite).cls]">{{ ratioBadge(onWhite).level }}</b>
          <em>{{ onWhite.toFixed(2) }}:1</em>
        </span>
        <span class="cp-wcag-item">
          <i class="cp-chip black" /> vs #000
          <b :class="['cp-badge', ratioBadge(onBlack).cls]">{{ ratioBadge(onBlack).level }}</b>
          <em>{{ onBlack.toFixed(2) }}:1</em>
        </span>
      </div>

      <!-- 状态提示：在非安全上下文（如局域网IP访问）或浏览器无原生 EyeDropper 时展示友好帮助 -->
      <div v-if="!supported" class="cp-note-card">
        <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" stroke-linejoin="round" class="cp-note-icon">
          <circle cx="12" cy="12" r="10" />
          <line x1="12" y1="8" x2="12" y2="12" />
          <line x1="12" y1="16" x2="12.01" y2="16" />
        </svg>
        <div class="cp-note-text">
          <template v-if="!isSecure">
            {{ t('tools.color.insecureContextTip') }}
          </template>
          <template v-else>
            {{ t('tools.color.noEyeDropper') }}
          </template>
        </div>
      </div>
    </div>

    <!-- 历史 -->
    <aside class="cp-history">
      <div class="cp-history-head">
        <span>{{ t('tools.color.history') }}</span>
        <button v-if="history.length" class="cp-clear" @click="clearHistory">{{ t('tools.color.clearHistory') }}</button>
      </div>
      <div v-if="history.length" class="cp-history-grid">
        <div
          v-for="hex in history"
          :key="hex"
          class="cp-hcell"
          :style="{ background: hex }"
          :title="`${hex} — ${t('tools.color.pickFromHistory')}`"
          @click="applyColor(hex, false)"
        >
          <button class="cp-hdel" :title="t('tools.common.delete')" @click.stop="removeHistory(hex)">×</button>
        </div>
      </div>
      <div v-else class="cp-history-empty">{{ t('tools.color.historyEmpty') }}</div>
    </aside>
  </div>
</template>

<style scoped>
.cp-page {
  flex: 1;
  min-height: 0;
  overflow: auto;
  display: grid;
  grid-template-columns: minmax(0, 1fr) 300px;
  gap: 20px;
  padding: 24px;
  max-width: 1100px;
  margin: 0 auto;
  width: 100%;
  box-sizing: border-box;
}

.cp-main {
  display: flex;
  flex-direction: column;
  gap: 18px;
}

.cp-preview {
  height: 220px;
  border-radius: 14px;
  border: 1px solid var(--nc-border);
  box-shadow: inset 0 2px 4px rgba(0, 0, 0, 0.08);
  display: grid;
  grid-template-columns: 1fr 1fr;
  overflow: hidden;
  transition: background 0.15s ease;
}

.cp-preview-light,
.cp-preview-dark {
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 6px;
}

.cp-aa {
  font-size: 42px;
  font-weight: 700;
  letter-spacing: -1px;
}

.cp-ratio {
  font-size: 13px;
  font-weight: 600;
  opacity: 0.9;
}

.cp-controls {
  display: flex;
  flex-wrap: wrap;
  gap: 12px;
  align-items: center;
}

.cp-pick {
  font-weight: 600;
  border-radius: 9px;
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.cp-kbd {
  font-size: 11px;
  padding: 1px 6px;
  border-radius: 4px;
  background: rgba(255, 255, 255, 0.22);
  margin-left: 2px;
}

.cp-swatch {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 8px;
  border: 1px solid var(--nc-border);
  background: var(--nc-surface);
  border-radius: 9px;
  padding: 6px 12px;
  cursor: pointer;
  user-select: none;
  height: 40px;
  box-sizing: border-box;
}

.cp-swatch input {
  position: absolute;
  inset: 0;
  opacity: 0;
  width: 100%;
  height: 100%;
  cursor: pointer;
}

.cp-swatch-face {
  width: 24px;
  height: 24px;
  border-radius: 6px;
  border: 1px solid rgba(0, 0, 0, 0.12);
  box-shadow: inset 0 0 0 1px rgba(255, 255, 255, 0.2);
}

.cp-swatch-label {
  font-size: 13px;
  color: var(--nc-text);
  font-weight: 500;
}

.cp-hex {
  position: relative;
  display: inline-flex;
  align-items: center;
  gap: 8px;
  flex: 1;
  min-width: 180px;
}

.cp-hex-label {
  font-size: 12px;
  font-weight: 700;
  color: var(--nc-text-faint);
}

.cp-hex-input {
  flex: 1;
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-weight: 600;
}

.cp-hex-bad {
  position: absolute;
  right: 12px;
  font-size: 11px;
  color: var(--el-color-danger, #f56c6c);
}

.cp-formats {
  display: grid;
  grid-template-columns: repeat(auto-fit, minmax(180px, 1fr));
  gap: 10px;
}

.cp-fmt {
  display: flex;
  align-items: center;
  justify-content: space-between;
  border: 1px solid var(--nc-border);
  background: var(--nc-surface);
  border-radius: 8px;
  padding: 10px 14px;
  cursor: pointer;
  transition: all 0.15s;
  color: var(--nc-text);
}

.cp-fmt:hover {
  border-color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 5%, var(--nc-surface));
}

.cp-fmt-key {
  font-size: 11px;
  font-weight: 700;
  color: var(--nc-text-faint);
}

.cp-fmt code {
  font-family: ui-monospace, SFMono-Regular, Menlo, Monaco, Consolas, monospace;
  font-size: 12.5px;
  font-weight: 600;
}

.cp-fmt-copy {
  font-size: 11px;
  color: var(--nc-primary);
}

.cp-wcag {
  border: 1px solid var(--nc-border);
  background: var(--nc-surface);
  border-radius: 10px;
  padding: 12px 16px;
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 16px;
  font-size: 12.5px;
}

.cp-wcag-title {
  font-weight: 700;
  color: var(--nc-text-faint);
  text-transform: uppercase;
  letter-spacing: 0.5px;
  font-size: 11px;
}

.cp-wcag-item {
  display: inline-flex;
  align-items: center;
  gap: 8px;
  color: var(--nc-text);
}

.cp-wcag-item em {
  font-style: normal;
  color: var(--nc-text-muted);
  font-family: ui-monospace, monospace;
  font-size: 11.5px;
}

.cp-chip {
  width: 14px;
  height: 14px;
  border-radius: 4px;
  display: inline-block;
}

.cp-chip.white {
  background: #ffffff;
  border: 1px solid rgba(0, 0, 0, 0.15);
}

.cp-chip.black {
  background: #000000;
}

.cp-badge {
  font-size: 11px;
  padding: 1px 6px;
  border-radius: 4px;
  font-weight: 700;
}

.cp-badge.pass-aaa {
  background: rgba(34, 197, 94, 0.15);
  color: #16a34a;
}

.cp-badge.pass-aa,
.cp-badge.pass-aa-lg {
  background: rgba(59, 130, 246, 0.15);
  color: #2563eb;
}

.cp-badge.fail {
  background: rgba(239, 68, 68, 0.15);
  color: #dc2626;
}

.cp-note-card {
  display: flex;
  align-items: flex-start;
  gap: 10px;
  padding: 10px 14px;
  border-radius: 8px;
  background: color-mix(in srgb, var(--el-color-warning, #e6a23c) 10%, transparent);
  border: 1px solid color-mix(in srgb, var(--el-color-warning, #e6a23c) 30%, transparent);
  color: var(--nc-text);
  font-size: 12.5px;
  line-height: 1.5;
}

.cp-note-icon {
  flex-shrink: 0;
  color: var(--el-color-warning, #e6a23c);
  margin-top: 2px;
}

.cp-note-text {
  flex: 1;
  color: var(--nc-text-muted);
}

.cp-history {
  border: 1px solid var(--nc-border);
  background: var(--nc-surface);
  border-radius: 12px;
  padding: 14px 16px;
  display: flex;
  flex-direction: column;
  gap: 12px;
  height: fit-content;
}

.cp-history-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  font-size: 12px;
  font-weight: 700;
  color: var(--nc-text-faint);
  text-transform: uppercase;
  letter-spacing: 0.5px;
}

.cp-clear {
  border: none;
  background: transparent;
  color: var(--nc-text-muted);
  font-size: 11.5px;
  cursor: pointer;
  padding: 2px 4px;
}

.cp-clear:hover {
  color: var(--el-color-danger, #f56c6c);
}

.cp-history-grid {
  display: grid;
  grid-template-columns: repeat(4, 1fr);
  gap: 8px;
}

.cp-hcell {
  position: relative;
  aspect-ratio: 1;
  border-radius: 8px;
  border: 1px solid rgba(0, 0, 0, 0.1);
  box-shadow: inset 0 0 0 1px rgba(255, 255, 255, 0.15);
  cursor: pointer;
  transition: transform 0.15s;
}

.cp-hcell:hover {
  transform: scale(1.06);
}

.cp-hdel {
  position: absolute;
  top: 2px;
  right: 2px;
  width: 16px;
  height: 16px;
  border-radius: 50%;
  border: none;
  background: rgba(0, 0, 0, 0.55);
  color: #fff;
  font-size: 11px;
  line-height: 1;
  display: none;
  align-items: center;
  justify-content: center;
  cursor: pointer;
}

.cp-hcell:hover .cp-hdel {
  display: flex;
}

.cp-history-empty {
  font-size: 12.5px;
  color: var(--nc-text-faint);
  text-align: center;
  padding: 24px 8px;
}

@media (max-width: 820px) {
  .cp-page {
    grid-template-columns: 1fr;
    padding: 16px;
  }
}
</style>
