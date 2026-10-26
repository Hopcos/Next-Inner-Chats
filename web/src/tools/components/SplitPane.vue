<script setup lang="ts">
/**
 * 工具共享的可拖拽左右分栏（移植自 dev-tools 的 SplitPane）。
 * 中缝拖拽调整比例（20%~80%），左右面板各带标题。
 */
import { ref } from 'vue'

defineProps<{ leftTitle?: string; rightTitle?: string }>()

const ratio = ref(50)
const dragging = ref(false)
const rootEl = ref<HTMLElement | null>(null)

function onDown(e: MouseEvent) {
  dragging.value = true
  e.preventDefault()
  const onMove = (ev: MouseEvent) => {
    const box = rootEl.value?.getBoundingClientRect()
    if (!box || box.width === 0) return
    ratio.value = Math.min(80, Math.max(20, ((ev.clientX - box.left) / box.width) * 100))
  }
  const onUp = () => {
    dragging.value = false
    window.removeEventListener('mousemove', onMove)
    window.removeEventListener('mouseup', onUp)
  }
  window.addEventListener('mousemove', onMove)
  window.addEventListener('mouseup', onUp)
}
</script>

<template>
  <div ref="rootEl" class="sp" :class="{ dragging }">
    <section class="sp-pane" :style="{ width: ratio + '%' }">
      <header class="sp-head">{{ leftTitle ?? 'Input' }}</header>
      <div class="sp-body"><slot name="left" /></div>
    </section>
    <div class="sp-handle" @mousedown="onDown"><span class="sp-dot" /><span class="sp-dot" /><span class="sp-dot" /></div>
    <section class="sp-pane" :style="{ width: 100 - ratio + '%' }">
      <header class="sp-head">{{ rightTitle ?? 'Output' }}</header>
      <div class="sp-body"><slot name="right" /></div>
    </section>
  </div>
</template>

<style scoped>
.sp {
  flex: 1;
  width: 100%;
  min-width: 0;
  display: flex;
  align-items: stretch;
  height: 100%;
  min-height: 0;
  gap: 0;
}

.sp.dragging {
  cursor: col-resize;
  user-select: none;
}

.sp-pane {
  display: flex;
  flex-direction: column;
  min-width: 0;
  border: 1px solid var(--nc-border-strong);
  border-radius: 12px;
  overflow: hidden;
  background: var(--nc-surface-solid);
}

.sp-head {
  padding: 8px 12px;
  font-size: 12.5px;
  font-weight: 700;
  color: var(--nc-text-dim);
  border-bottom: 1px solid var(--nc-border);
  background: var(--nc-surface-2);
  letter-spacing: 0.02em;
}

.sp-body {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.sp-handle {
  width: 14px;
  flex-shrink: 0;
  cursor: col-resize;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 5px;
}

.sp-dot {
  width: 3px;
  height: 3px;
  border-radius: 50%;
  background: var(--nc-text-dim, #8a94a6);
  opacity: 0.5;
  transition: all 0.15s;
}

.sp-handle:hover .sp-dot {
  background: var(--nc-primary);
  opacity: 1;
}

@media (max-width: 860px) {
  .sp {
    flex-direction: column;
  }

  .sp-pane {
    width: 100% !important;
    flex: 1;
    min-height: 220px;
  }

  .sp-handle {
    display: none;
  }
}
</style>
