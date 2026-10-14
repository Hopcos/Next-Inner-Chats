<template>
  <teleport to="body">
    <div
      v-if="visible"
      class="iv-mask"
      @wheel.prevent="onWheel"
      @pointerdown="onPointerDown"
      @pointermove="onPointerMove"
      @pointerup="onPointerUp"
      @pointerleave="onPointerUp"
      @dblclick="reset"
    >
      <img :src="srcs[index]" class="iv-img" :style="imgStyle" alt="" draggable="false" @error="onImgError" />
      <div class="iv-toolbar">
        <button type="button" class="iv-btn" :title="t('chat.viewerZoomOut')" @click.stop="zoom(-1)">−</button>
        <span class="iv-scale">{{ Math.round(scale * 100) }}%</span>
        <button type="button" class="iv-btn" :title="t('chat.viewerZoomIn')" @click.stop="zoom(1)">+</button>
        <button type="button" class="iv-btn" :title="t('chat.viewerReset')" @click.stop="reset">1:1</button>
        <span class="iv-sep"></span>
        <button type="button" class="iv-btn" :title="t('chat.viewerCopy')" @click.stop="copyImage">
          <svg class="iv-ico" viewBox="0 0 16 16" aria-hidden="true">
            <rect x="5.5" y="5.5" width="8" height="8" rx="1.5" fill="none" stroke="currentColor" stroke-width="1.4" />
            <path d="M10.5 5.5v-1A1.5 1.5 0 0 0 9 3H4.5A1.5 1.5 0 0 0 3 4.5V9a1.5 1.5 0 0 0 1.5 1.5h1" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" />
          </svg>
        </button>
        <button type="button" class="iv-btn" :title="t('chat.viewerDownload')" @click.stop="downloadImage">
          <svg class="iv-ico" viewBox="0 0 16 16" aria-hidden="true">
            <path d="M8 2v7m0 0 2.5-2.5M8 9 5.5 6.5M3 11.5v.5A1.5 1.5 0 0 0 4.5 13.5h7A1.5 1.5 0 0 0 13 12v-.5" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linecap="round" stroke-linejoin="round" />
          </svg>
        </button>
        <button type="button" class="iv-btn iv-close" :title="t('chat.viewerClose')" @click.stop="close">✕</button>
      </div>
      <transition name="iv-fade">
        <div v-if="feedback" class="iv-feedback">{{ feedback }}</div>
      </transition>
      <span v-if="!imgOk" class="iv-err">{{ t('chat.viewerLoadFailed') }}</span>
    </div>
  </teleport>
</template>

<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'
import { copyText } from '@/utils/clipboard'

const props = defineProps<{
  visible: boolean
  srcs: string[]
  index: number
  /** 与 srcs 一一对应的"可复制"链接（原始 /api/chat/images/... 或 data: URI），供复制回退使用 */
  links?: string[]
}>()
const emit = defineEmits<{ close: [] }>()

const { t } = useI18n()
const scale = ref(1)
const tx = ref(0)
const ty = ref(0)
const imgOk = ref(true)

// 复制/下载等操作的内嵌反馈（放在 mask 内部，避免被 viewer 自身层级遮挡的提示不可见）
const feedback = ref('')
let feedbackTimer: ReturnType<typeof setTimeout> | undefined
function showFeedback(msg: string) {
  feedback.value = msg
  clearTimeout(feedbackTimer)
  feedbackTimer = setTimeout(() => (feedback.value = ''), 2600)
}

/** 打开/切换图片时复位视图 */
watch(
  () => [props.visible, props.index] as const,
  () => {
    if (props.visible) {
      scale.value = 1
      tx.value = 0
      ty.value = 0
      imgOk.value = true
    }
  },
)

const imgStyle = computed(() => ({
  transform: `translate(${tx.value}px, ${ty.value}px) scale(${scale.value})`,
}))

function zoom(dir: number) {
  scale.value = Math.min(8, Math.max(0.1, scale.value * (1 + dir * 0.2)))
}

function reset() {
  scale.value = 1
  tx.value = 0
  ty.value = 0
}

function close() {
  emit('close')
}

function onImgError() {
  imgOk.value = false
}

// ---- 复制 / 下载（支持 data: URI 与 /api/chat/images/... 相对 URL） ----
async function fetchBlob(src: string): Promise<Blob> {
  if (src.startsWith('data:')) {
    const comma = src.indexOf(',')
    const head = src.slice(0, comma)
    const mime = head.match(/data:(.*?)(;|$)/)?.[1] || 'image/png'
    const bin = atob(src.slice(comma + 1))
    const bytes = new Uint8Array(bin.length)
    for (let i = 0; i < bin.length; i++) bytes[i] = bin.charCodeAt(i)
    return new Blob([bytes], { type: mime })
  }
  const res = await fetch(src)
  return await res.blob()
}

/** 把任意图片 blob 重编码为 PNG，保证 ClipboardItem 可解码（解决 JPEG/blob type 导致的 DataError） */
async function toPngBlob(blob: Blob): Promise<Blob | null> {
  try {
    const bmp = await createImageBitmap(blob)
    const canvas = document.createElement('canvas')
    canvas.width = bmp.width
    canvas.height = bmp.height
    canvas.getContext('2d')!.drawImage(bmp, 0, 0)
    const out = await new Promise<Blob | null>((res) => canvas.toBlob(res, 'image/png'))
    bmp.close?.()
    return out
  } catch {
    return null
  }
}

/** 复制回退用的可复制链接（blob URL 无意义，用原始 links；相对路径转绝对） */
function toCopyLink(url: string): string {
  if (!url) return ''
  try {
    return new URL(url, location.origin).href
  } catch {
    return url
  }
}

function imageName(src: string, blobType = ''): string {
  try {
    const u = new URL(src, location.origin)
    const seg = u.pathname.split('/').filter(Boolean).pop()
    if (seg && seg.includes('.')) return seg
  } catch {
    /* fallthrough */
  }
  // blob: URL 等无扩展名场景：用 blob 的 MIME 推断扩展名
  const mime = (blobType || 'image/png').split('/')[1] || 'png'
  const ext = mime === 'jpeg' ? 'jpg' : mime
  return `image-${Date.now()}.${ext}`
}

async function copyImage() {
  const src = props.srcs[props.index]
  if (!src) return
  // 1) 安全上下文 + Clipboard API：canvas 转 PNG 后写图片到剪贴板（保证 ClipboardItem 可解码）
  if (window.isSecureContext && navigator.clipboard?.write) {
    try {
      const blob = await fetchBlob(src)
      const pngBlob = (await toPngBlob(blob)) ?? blob
      await navigator.clipboard.write([new ClipboardItem({ [pngBlob.type || 'image/png']: pngBlob })])
      showFeedback(t('chat.viewerCopied'))
      return
    } catch {
      /* fall through to link fallback */
    }
  }
  // 2) 回退：复制图片链接（局域网 http://IP 等非安全上下文，或图片写剪贴板受限）
  const link = toCopyLink(props.links?.[props.index] ?? src)
  if (link && (await copyText(link))) {
    showFeedback(t('chat.viewerCopyFallback'))
    return
  }
  showFeedback(t('chat.viewerCopyFailed'))
}

async function downloadImage() {
  const src = props.srcs[props.index]
  if (!src) return
  try {
    const blob = await fetchBlob(src)
    const url = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = url
    a.download = imageName(src, blob.type)
    document.body.appendChild(a)
    a.click()
    a.remove()
    setTimeout(() => URL.revokeObjectURL(url), 3000)
  } catch {
    showFeedback(t('chat.viewerLoadFailed'))
  }
}

// ---- ESC 关闭 ----
function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Escape' && props.visible) emit('close')
}
onMounted(() => window.addEventListener('keydown', onKeydown))
onBeforeUnmount(() => window.removeEventListener('keydown', onKeydown))

// ---- 拖拽平移（pointer capture） ----
let dragging = false
let lastX = 0
let lastY = 0

function onPointerDown(e: PointerEvent) {
  if (e.button !== 0) return
  dragging = true
  lastX = e.clientX
  lastY = e.clientY
  const mask = e.currentTarget as HTMLElement
  mask.style.cursor = 'grabbing'
}

function onPointerMove(e: PointerEvent) {
  if (!dragging) return
  tx.value += e.clientX - lastX
  ty.value += e.clientY - lastY
  lastX = e.clientX
  lastY = e.clientY
}

function onPointerUp(e: PointerEvent) {
  dragging = false
  const mask = e.currentTarget as HTMLElement
  mask.style.cursor = 'grab'
}

// 滚轮缩放：deltaY < 0 放大
function onWheel(e: WheelEvent) {
  zoom(e.deltaY < 0 ? 1 : -1)
  // 围绕中心缩放时保持图片中心稳定
}
</script>

<style scoped>
.iv-mask {
  position: fixed;
  inset: 0;
  z-index: 3000;
  background: rgba(10, 16, 30, 0.92);
  display: flex;
  align-items: center;
  justify-content: center;
  cursor: grab;
  user-select: none;
}

.iv-img {
  max-width: 90vw;
  max-height: 88vh;
  pointer-events: none;
  transition: transform 0.12s ease-out;
  user-select: none;
  -webkit-user-drag: none;
  box-shadow: 0 8px 40px rgba(0, 0, 0, 0.5);
  border-radius: 4px;
}

.iv-toolbar {
  position: absolute;
  bottom: 22px;
  left: 50%;
  transform: translateX(-50%);
  display: flex;
  align-items: center;
  gap: 10px;
  background: rgba(30, 41, 59, 0.85);
  border: 1px solid rgba(148, 163, 184, 0.35);
  border-radius: 10px;
  padding: 6px 12px;
  backdrop-filter: blur(4px);
}

.iv-btn {
  min-width: 30px;
  height: 30px;
  border: none;
  border-radius: 6px;
  background: rgba(148, 163, 184, 0.18);
  color: #e2e8f0;
  font-size: 16px;
  line-height: 30px;
  cursor: pointer;
  padding: 0;
}

.iv-btn:hover {
  background: rgba(148, 163, 184, 0.35);
}

.iv-close {
  background: rgba(239, 68, 68, 0.55);
  margin-left: 6px;
}

.iv-close:hover {
  background: rgba(239, 68, 68, 0.8);
}

.iv-sep {
  width: 1px;
  height: 18px;
  background: rgba(148, 163, 184, 0.35);
  margin: 0 2px;
}

.iv-ico {
  width: 15px;
  height: 15px;
  vertical-align: middle;
}

.iv-scale {
  min-width: 52px;
  text-align: center;
  color: #e2e8f0;
  font-size: 13px;
  font-variant-numeric: tabular-nums;
}

.iv-err {
  position: absolute;
  top: 50%;
  left: 50%;
  transform: translate(-50%, -50%);
  color: #f87171;
  font-size: 14px;
}

.iv-feedback {
  position: absolute;
  bottom: 78px;
  left: 50%;
  transform: translateX(-50%);
  max-width: 80vw;
  padding: 8px 16px;
  border-radius: 8px;
  background: rgba(30, 41, 59, 0.92);
  border: 1px solid rgba(148, 163, 184, 0.35);
  color: #e2e8f0;
  font-size: 13px;
  text-align: center;
  white-space: nowrap;
  box-shadow: 0 4px 20px rgba(0, 0, 0, 0.4);
}

.iv-fade-enter-active,
.iv-fade-leave-active {
  transition: opacity 0.2s ease, transform 0.2s ease;
}

.iv-fade-enter-from,
.iv-fade-leave-to {
  opacity: 0;
  transform: translateX(-50%) translateY(6px);
}
</style>
