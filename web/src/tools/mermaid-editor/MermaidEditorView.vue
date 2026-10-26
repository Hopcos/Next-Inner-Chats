<script setup lang="ts">
/** Mermaid Editor 工具页：防抖实时渲染 + 平移缩放 + SVG/PNG 导出 */
import { onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import mermaid from 'mermaid'
import { kernel } from '@/kernel'
import { downloadBlob, stamp } from '@/utils/capture'
import SplitPane from '../components/SplitPane.vue'
import { loadCode, saveCode } from './config'

const { t } = useI18n()

const code = ref(loadCode())
const error = ref<string | null>(null)
const scale = ref(1)
const offset = ref({ x: 0, y: 0 })
const dragging = ref(false)
const previewEl = ref<HTMLElement | null>(null)
const lastPos = ref({ x: 0, y: 0 })

let renderSeq = 0
let timer: ReturnType<typeof setTimeout> | undefined

mermaid.initialize({ startOnLoad: false, theme: 'default', securityLevel: 'loose' })

async function renderDiagram() {
  if (!previewEl.value) return
  if (!code.value.trim()) {
    previewEl.value.innerHTML = ''
    error.value = null
    return
  }
  try {
    await mermaid.parse(code.value)
    const id = `nc-mermaid-svg-${++renderSeq}`
    const out = await mermaid.render(id, code.value)
    if (previewEl.value) previewEl.value.innerHTML = out.svg
    error.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Invalid Mermaid syntax'
    if (previewEl.value) previewEl.value.innerHTML = ''
  }
}

watch(code, (v) => {
  clearTimeout(timer)
  timer = setTimeout(() => {
    saveCode(v)
    void renderDiagram()
  }, 500)
})

onMounted(() => void renderDiagram())
onBeforeUnmount(() => clearTimeout(timer))

function handleZoom(delta: number) {
  scale.value = Math.max(0.1, Math.min(5, scale.value + delta))
}

function resetView() {
  scale.value = 1
  offset.value = { x: 0, y: 0 }
}

function clearAll() {
  code.value = ''
  saveCode('')
  error.value = null
  if (previewEl.value) previewEl.value.innerHTML = ''
  resetView()
}

function onDown(e: MouseEvent) {
  if (e.button !== 0) return
  dragging.value = true
  lastPos.value = { x: e.clientX, y: e.clientY }
}

function onMove(e: MouseEvent) {
  if (!dragging.value) return
  offset.value = { x: offset.value.x + (e.clientX - lastPos.value.x), y: offset.value.y + (e.clientY - lastPos.value.y) }
  lastPos.value = { x: e.clientX, y: e.clientY }
}

function onUp() {
  dragging.value = false
}

function onWheel(e: WheelEvent) {
  e.preventDefault()
  handleZoom(-e.deltaY * 0.001)
}

function currentSvg(): SVGSVGElement | null {
  return previewEl.value?.querySelector('svg') ?? null
}

/** 克隆 SVG 并补齐导出所需：xmlns + 显式像素尺寸（mermaid 常输出 width="100%"） */
function serializeSvg(): string | null {
  const el = currentSvg()
  if (!el) return null
  const copy = el.cloneNode(true) as SVGSVGElement
  if (!copy.getAttribute('xmlns')) copy.setAttribute('xmlns', 'http://www.w3.org/2000/svg')
  honorPixelSize(copy)
  return new XMLSerializer().serializeToString(copy)
}

/** 若 width/height 缺失或为百分比（100%），按 viewBox 补成具体像素，避免导出空白/零尺寸 */
function honorPixelSize(svg: SVGSVGElement) {
  const w = svg.getAttribute('width')
  const h = svg.getAttribute('height')
  if ((w && !w.includes('%')) && (h && !h.includes('%'))) return
  const vb = svg.getAttribute('viewBox')
  if (vb) {
    const p = vb.trim().split(/\s+/).map(Number)
    if (p.length === 4 && p[2] > 0 && p[3] > 0) {
      svg.setAttribute('width', String(Math.round(p[2])))
      svg.setAttribute('height', String(Math.round(p[3])))
    }
  }
}

function download(href: string, name: string) {
  const link = document.createElement('a')
  link.href = href
  link.download = name
  document.body.appendChild(link)
  link.click()
  link.remove()
}

/** SVG 下载：Blob 化后下载（补 xmlns + 像素尺寸） */
function saveSvg() {
  const data = serializeSvg()
  if (!data) {
    kernel.notify.warning(t('tools.mermaid.noDiagram'))
    return
  }
  const url = URL.createObjectURL(new Blob([data], { type: 'image/svg+xml;charset=utf-8' }))
  download(url, `mermaid-${stamp()}.svg`)
  setTimeout(() => URL.revokeObjectURL(url), 4000)
}

/**
 * PNG 下载：预览图使用 HTML label（foreignObject），任何"对 DOM 截图"的方案
 * 都会因 foreignObject 内容无法序列化而导出白板。因此导出时用 htmlLabels:false
 * 重新渲染一份纯 SVG（无 foreignObject），再走 Image → canvas 经典管线，必然出图。
 * 导出结束后立即恢复预览配置。
 */
async function savePng() {
  if (!code.value.trim()) {
    kernel.notify.warning(t('tools.mermaid.noDiagram'))
    return
  }
  let srcUrl = ''
  let svgWidth = 0
  try {
    await mermaid.parse(code.value)
    // 导出模式：关闭富文本 label，输出纯 SVG
    mermaid.initialize({
      startOnLoad: false,
      theme: 'default',
      securityLevel: 'loose',
      htmlLabels: false,
      flowchart: { htmlLabels: false },
      class: { htmlLabels: false },
    })
    const out = await mermaid.render(`nc-mermaid-export-${++renderSeq}`, code.value)
    // 规范化：xmlns + 像素尺寸（mermaid 常输出 width="100%"）
    const doc = new DOMParser().parseFromString(out.svg, 'image/svg+xml')
    const svg = doc.documentElement as unknown as SVGSVGElement
    if (!svg.getAttribute('xmlns')) svg.setAttribute('xmlns', 'http://www.w3.org/2000/svg')
    honorPixelSize(svg)
    svgWidth = Number(svg.getAttribute('width')) || 0
    const data = new XMLSerializer().serializeToString(svg)
    srcUrl = URL.createObjectURL(new Blob([data], { type: 'image/svg+xml;charset=utf-8' }))
  } catch {
    kernel.notify.error(t('tools.mermaid.exportFailed'))
    return
  } finally {
    mermaid.initialize({ startOnLoad: false, theme: 'default', securityLevel: 'loose' })
  }

  const img = new Image()
  img.onload = () => {
    const width = svgWidth > 0 ? svgWidth : img.naturalWidth
    const ratio = img.naturalHeight / Math.max(1, img.naturalWidth)
    const height = Math.max(1, Math.round(width * ratio))
    const factor = 2
    const canvas = document.createElement('canvas')
    canvas.width = Math.max(1, Math.round(width * factor))
    canvas.height = Math.max(1, Math.round(height * factor))
    const ctx = canvas.getContext('2d')
    if (!ctx) {
      URL.revokeObjectURL(srcUrl)
      return
    }
    ctx.fillStyle = '#ffffff'
    ctx.fillRect(0, 0, canvas.width, canvas.height)
    ctx.drawImage(img, 0, 0, canvas.width, canvas.height)
    URL.revokeObjectURL(srcUrl)
    canvas.toBlob((blob) => {
      if (blob) downloadBlob(blob, `mermaid-${stamp()}.png`)
      else kernel.notify.error(t('tools.mermaid.exportFailed'))
    }, 'image/png')
  }
  img.onerror = () => {
    URL.revokeObjectURL(srcUrl)
    kernel.notify.error(t('tools.mermaid.exportFailed'))
  }
  img.src = srcUrl
}
</script>

<template>
  <div class="me-page">
    <div class="me-bar">
      <el-button size="small" type="danger" plain @click="clearAll">{{ t('tools.mermaid.clear') }}</el-button>
      <div class="me-download">
        <el-button size="small" @click="saveSvg">
          <svg class="me-ico" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2v8m0 0 3-3M8 10 5 7M2.5 11.5v1A1.5 1.5 0 0 0 4 14h8a1.5 1.5 0 0 0 1.5-1.5v-1" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" /></svg>
          SVG
        </el-button>
        <el-button size="small" @click="savePng">
          <svg class="me-ico" viewBox="0 0 16 16" aria-hidden="true"><path d="M8 2v8m0 0 3-3M8 10 5 7M2.5 11.5v1A1.5 1.5 0 0 0 4 14h8a1.5 1.5 0 0 0 1.5-1.5v-1" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" /></svg>
          PNG
        </el-button>
      </div>
    </div>

    <div class="me-split">
      <SplitPane :left-title="t('tools.mermaid.code')" :right-title="t('tools.mermaid.preview')">
        <template #left>
          <textarea v-model="code" class="me-area mono" spellcheck="false" :placeholder="t('tools.mermaid.placeholder')" />
        </template>
        <template #right>
          <div class="me-toolbar">
            <button class="me-zoom" :title="t('tools.mermaid.zoomIn')" @click="handleZoom(0.1)">＋</button>
            <button class="me-zoom" :title="t('tools.mermaid.zoomOut')" @click="handleZoom(-0.1)">－</button>
            <button class="me-zoom" :title="t('tools.mermaid.reset')" @click="resetView">⛶</button>
            <span class="me-scale">{{ Math.round(scale * 100) }}%</span>
            <span class="me-hint">{{ t('tools.mermaid.dragHint') }}</span>
          </div>
          <div class="me-stage" :class="{ dragging }" @mousedown="onDown" @mousemove="onMove" @mouseup="onUp" @mouseleave="onUp" @wheel="onWheel">
            <div v-if="error" class="me-error">
              <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true">
                <circle cx="12" cy="12" r="9" />
                <path d="M12 7.5V13" />
                <path d="M12 16.5v.01" />
              </svg>
              <pre class="me-error-text">{{ error }}</pre>
            </div>
            <div ref="previewEl" class="me-out" :style="{ transform: `translate(${offset.x}px, ${offset.y}px) scale(${scale})`, transformOrigin: 'center center' }" />
          </div>
        </template>
      </SplitPane>
    </div>
  </div>
</template>

<style scoped>
.me-page {
  flex: 1;
  height: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
  padding: 18px 22px 20px;
  gap: 14px;
  overflow: hidden;
}

.me-bar {
  display: flex;
  gap: 8px;
}

.me-download {
  margin-left: auto;
  display: flex;
  gap: 8px;
}

.me-ico {
  width: 13px;
  height: 13px;
  margin-right: 4px;
  vertical-align: -2px;
}

.me-split {
  flex: 1;
  min-height: 0;
}

.me-area {
  flex: 1;
  width: 100%;
  border: 0;
  outline: none;
  resize: none;
  background: var(--nc-surface-solid);
  color: var(--nc-text);
  padding: 14px 16px;
  font-size: 13px;
  line-height: 1.65;
}

.mono {
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
}

.me-area::placeholder {
  color: var(--nc-text-faint);
  opacity: 0.8;
}

.me-toolbar {
  display: flex;
  align-items: center;
  gap: 6px;
  padding: 6px 10px;
  border-bottom: 1px solid var(--nc-border);
  background: var(--nc-surface-2);
}

.me-zoom {
  width: 26px;
  height: 26px;
  border-radius: 7px;
  border: 1px solid var(--nc-border-strong);
  background: var(--nc-surface-solid);
  color: var(--nc-text-dim);
  cursor: pointer;
  font-size: 13px;
  line-height: 1;
}

.me-zoom:hover {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
}

.me-scale {
  font-size: 12px;
  color: var(--nc-text-dim);
  min-width: 42px;
  text-align: center;
}

.me-hint {
  margin-left: auto;
  font-size: 11.5px;
  color: var(--nc-text-faint);
  opacity: 0.9;
}

.me-stage {
  flex: 1;
  overflow: hidden;
  position: relative;
  cursor: grab;
  background:
    radial-gradient(circle at 1px 1px, rgba(148, 163, 184, 0.22) 1px, transparent 0) 0 0 / 18px 18px,
    #ffffff;
  display: flex;
  align-items: center;
  justify-content: center;
}

.me-stage.dragging {
  cursor: grabbing;
}

.me-out {
  transition: transform 0.05s linear;
  max-width: 100%;
  display: flex;
  align-items: center;
  justify-content: center;
}

.me-out :deep(svg) {
  max-width: 90vw;
  height: auto;
}

.me-error {
  position: absolute;
  top: 14px;
  left: 14px;
  right: 14px;
  display: flex;
  gap: 10px;
  align-items: flex-start;
  color: var(--nc-danger);
  background: color-mix(in srgb, var(--nc-danger) 12%, transparent);
  border: 1px solid color-mix(in srgb, var(--nc-danger) 30%, transparent);
  border-radius: 10px;
  padding: 12px 14px;
  z-index: 2;
}

.me-error-text {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-all;
  font-size: 12px;
  line-height: 1.55;
  max-height: 150px;
  overflow: auto;
  font-family: Consolas, Menlo, monospace;
}
</style>
