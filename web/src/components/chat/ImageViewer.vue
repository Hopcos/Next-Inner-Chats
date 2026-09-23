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
        <button type="button" class="iv-btn iv-close" :title="t('chat.viewerClose')" @click.stop="close">✕</button>
      </div>
      <span v-if="!imgOk" class="iv-err">{{ t('chat.viewerLoadFailed') }}</span>
    </div>
  </teleport>
</template>

<script setup lang="ts">
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'

const props = defineProps<{
  visible: boolean
  srcs: string[]
  index: number
}>()
const emit = defineEmits<{ close: [] }>()

const { t } = useI18n()
const scale = ref(1)
const tx = ref(0)
const ty = ref(0)
const imgOk = ref(true)

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
</style>
