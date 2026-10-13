<script setup lang="ts">
/** Markdown Preview 工具页：左侧编辑、右侧实时渲染；预览区域可导出 PNG（复用 html-to-image 封装） */
import { computed, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'
import { captureElementToPng, downloadBlob, stamp } from '@/utils/capture'
import SplitPane from '../components/SplitPane.vue'
import { loadContent, md, saveContent } from './config'

const { t } = useI18n()

const content = ref(loadContent())
const html = computed(() => md.render(content.value))
const previewEl = ref<HTMLElement | null>(null)
const exporting = ref(false)

let timer: ReturnType<typeof setTimeout> | undefined
watch(content, (v) => {
  clearTimeout(timer)
  timer = setTimeout(() => saveContent(v), 400)
})

/** PNG 下载：对预览 DOM 截图（html-to-image 会克隆节点并内联计算样式，scoped CSS 亦可生效）。
 *  预览是滚动容器（overflow:auto，高度受视口限制），默认只导出“当前可视区域”；
 *  因此截图前临时展开为完整内容高度（overflow visible + height=scrollHeight），导出后还原，
 *  从而下载**全部内容**而非仅窗口显示部分。 */
async function savePng() {
  if (!content.value.trim()) {
    kernel.notify.warning(t('tools.md.empty'))
    return
  }
  const el = previewEl.value
  if (!el || exporting.value) return
  exporting.value = true
  // 记录并临时展开滚动容器，导出完整高度
  const prevOverflow = el.style.overflow
  const prevHeight = el.style.height
  el.style.overflow = 'visible'
  el.style.height = el.scrollHeight + 'px'
  try {
    const blob = await captureElementToPng(el, { width: el.offsetWidth, height: el.scrollHeight })
    if (blob) {
      downloadBlob(blob, `markdown-${stamp()}.png`)
    } else {
      kernel.notify.error(t('tools.md.downloadPng'))
    }
  } catch {
    kernel.notify.error(t('tools.md.downloadPng'))
  } finally {
    el.style.overflow = prevOverflow
    el.style.height = prevHeight
    exporting.value = false
  }
}
</script>

<template>
  <div class="mp-page">
    <SplitPane :left-title="t('tools.md.source')" :right-title="t('tools.md.preview')">
      <template #left>
        <textarea v-model="content" class="mp-area mono" spellcheck="false" :placeholder="t('tools.md.placeholder')" />
      </template>
      <template #right>
        <div class="mp-toolbar">
          <button class="mp-download" :title="t('tools.md.downloadPng')" :disabled="exporting" @click="savePng">
            <svg class="mp-ico" viewBox="0 0 16 16" aria-hidden="true">
              <path d="M8 2v8m0 0 3-3M8 10 5 7M2.5 11.5v1A1.5 1.5 0 0 0 4 14h8a1.5 1.5 0 0 0 1.5-1.5v-1" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" stroke-linejoin="round" />
            </svg>
            <span>{{ t('tools.md.downloadPng') }}</span>
          </button>
        </div>
        <!-- markdown-it html:false，输出仅安全标签，无 XSS 风险 -->
        <div ref="previewEl" class="mp-preview nc-scroll" v-html="html" />
      </template>
    </SplitPane>
  </div>
</template>

<style scoped>
.mp-page {
  flex: 1;
  height: 100%;
  min-height: 0;
  display: flex;
  padding: 0; /* 满宽：原始 + 预览两边之和占满整个可视宽度 */
  overflow: hidden;
}

.mp-area {
  flex: 1;
  width: 100%;
  border: 0;
  outline: none;
  resize: none;
  background: #ffffff;
  color: #1e293b;
  padding: 14px 16px;
  font-size: 13px;
  line-height: 1.7;
}

.mono {
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
}

.mp-area::placeholder {
  color: #94a3b8;
  opacity: 0.8;
}

.mp-preview {
  flex: 1;
  overflow: auto;
  background: #ffffff;
  padding: 16px 22px;
  font-size: 14px;
  line-height: 1.75;
  color: #1f2937;
  word-break: break-word;
}

/* 预览面板顶部工具条：PNG 下载（与 Mermaid 编辑器导出按钮同风格） */
.mp-toolbar {
  display: flex;
  justify-content: flex-end;
  align-items: center;
  gap: 8px;
  padding: 6px 10px;
  border-bottom: 1px solid #eef2f7;
  background: #ffffff;
  flex: none;
}

.mp-download {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  padding: 4px 10px;
  font-size: 12.5px;
  color: #2563eb;
  background: #ffffff;
  border: 1px solid #dbe4ef;
  border-radius: 6px;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
}

.mp-download:hover {
  background: #f1f5f9;
  border-color: #bfd2e8;
}

.mp-download:disabled {
  opacity: 0.55;
  cursor: default;
}

.mp-ico {
  width: 14px;
  height: 14px;
}

.mp-preview :deep(h1),
.mp-preview :deep(h2),
.mp-preview :deep(h3) {
  margin: 18px 0 10px;
  font-weight: 700;
  line-height: 1.35;
}

.mp-preview :deep(h1) {
  font-size: 22px;
  padding-bottom: 8px;
  border-bottom: 1px solid #eef2f7;
}

.mp-preview :deep(h2) {
  font-size: 18px;
}

.mp-preview :deep(h3) {
  font-size: 15.5px;
}

.mp-preview :deep(p) {
  margin: 8px 0;
}

.mp-preview :deep(a) {
  color: #2563eb;
  text-decoration: none;
}

.mp-preview :deep(a:hover) {
  text-decoration: underline;
}

.mp-preview :deep(code) {
  font-family: Consolas, Menlo, monospace;
  font-size: 12.5px;
  background: rgba(148, 163, 184, 0.16);
  border-radius: 5px;
  padding: 2px 6px;
}

.mp-preview :deep(pre) {
  background: rgba(15, 23, 42, 0.85);
  color: #e2e8f0;
  border-radius: 10px;
  padding: 14px 16px;
  overflow: auto;
  margin: 12px 0;
}

.mp-preview :deep(pre code) {
  background: transparent;
  padding: 0;
  color: inherit;
  font-size: 12.5px;
  line-height: 1.6;
}

.mp-preview :deep(blockquote) {
  margin: 12px 0;
  padding: 6px 14px;
  border-left: 3px solid #2563eb;
  color: #64748b;
  background: #f1f5f9;
  border-radius: 0 8px 8px 0;
}

.mp-preview :deep(table) {
  border-collapse: collapse;
  margin: 12px 0;
  width: 100%;
  font-size: 13px;
}

.mp-preview :deep(th),
.mp-preview :deep(td) {
  border: 1px solid #e2e8f0;
  padding: 7px 12px;
  text-align: left;
}

.mp-preview :deep(th) {
  background: #f1f5f9;
  font-weight: 700;
}

.mp-preview :deep(ul),
.mp-preview :deep(ol) {
  padding-left: 22px;
  margin: 8px 0;
}

.mp-preview :deep(li) {
  margin: 3px 0;
}

.mp-preview :deep(hr) {
  border: 0;
  border-top: 1px solid var(--nc-border);
  margin: 16px 0;
}

.mp-preview :deep(img) {
  max-width: 100%;
  border-radius: 10px;
}

.mp-preview :deep(strong) {
  font-weight: 700;
}
</style>
