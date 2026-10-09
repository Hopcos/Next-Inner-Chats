<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'
import { http } from '@/api/http'
import CanvasBoard from '@/components/chat/CanvasBoard.vue'
import { uuid } from '@/utils/uuid'

const { t } = useI18n()
const text = ref('')

// ---- 思考模式：全局开关（默认启用）+ 强度（默认 high），状态持久化到 localStorage ----
const LS_THINKING_ENABLED = 'nc.thinking.enabled'
const LS_THINKING_EFFORT = 'nc.thinking.effort'

const thinkingEnabled = ref(localStorage.getItem(LS_THINKING_ENABLED) !== '0')
const thinkingEffort = ref(localStorage.getItem(LS_THINKING_EFFORT) || 'high')

const effortOptions = [
  { value: 'low', labelKey: 'chat.effortLow' },
  { value: 'medium', labelKey: 'chat.effortMedium' },
  { value: 'high', labelKey: 'chat.effortHigh' },
  { value: 'max', labelKey: 'chat.effortMax' },
]

function saveThinking() {
  localStorage.setItem(LS_THINKING_ENABLED, thinkingEnabled.value ? '1' : '0')
  localStorage.setItem(LS_THINKING_EFFORT, thinkingEffort.value)
}

/** 仅当前会话在流式推理时视为“忙”：其它会话窗口可同时问答，互不影响 */
const streaming = computed(() => kernel.chat.isStreaming(kernel.session.state.currentId))

// 无当前会话时禁用输入并引导（首次使用兜底，与 ChatView 自动创建一致）
const noSession = computed(() => !kernel.session.state.currentId)

// 全屏展开状态：点击右上角 ICON 后输入框上延至覆盖层（类似新层），再点收缩
const expanded = ref(false)
// 输入框自动高度：最小 3 行；常规最大 20 行（超出出现内部滚动条），展开后放大到 40 行
const autoSize = computed(() =>
  expanded.value ? { minRows: 8, maxRows: 40 } : { minRows: 3, maxRows: 20 },
)

interface PendingImage {
  id: string
  fileName: string
  mimeType: string
  dataUrl: string
  base64: string
}

const images = ref<PendingImage[]>([])
const visionSupported = ref(false)
const fileInput = ref<HTMLInputElement | null>(null)
const MAX_RAW_BYTES = 3.75 * 1024 * 1024
const ACCEPT_MIME = ['image/png', 'image/jpeg', 'image/gif', 'image/webp']

onMounted(async () => {
  try {
    const cfg = await http.get<{ supported: boolean }>('/api/chat/vision-config')
    visionSupported.value = cfg.supported
  } catch {
    visionSupported.value = false
  }
})
onBeforeUnmount(() => window.removeEventListener('paste', onWindowPaste, true))

function readFile(file: File) {
  if (!ACCEPT_MIME.includes(file.type)) {
    kernel.notify.warning(t('chat.imageTypeInvalid'))
    return
  }
  if (file.size > MAX_RAW_BYTES) {
    kernel.notify.warning(t('chat.imageTooLarge'))
    return
  }
  const reader = new FileReader()
  reader.onload = () => {
    const dataUrl = reader.result as string
    const base64 = dataUrl.slice(dataUrl.indexOf(',') + 1)
    images.value.push({ id: uuid(), fileName: file.name, mimeType: file.type, dataUrl, base64 })
  }
  reader.readAsDataURL(file)
}

function onPickChange(e: Event) {
  const input = e.target as HTMLInputElement
  for (const f of Array.from(input.files ?? [])) readFile(f)
  input.value = ''
}

function onWindowPaste(e: ClipboardEvent) {
  if (!visionSupported.value || streaming.value) return
  for (const item of Array.from(e.clipboardData?.items ?? [])) {
    if (item.kind === 'file' && item.type.startsWith('image/')) {
      const f = item.getAsFile()
      if (f) readFile(f)
    }
  }
}

onMounted(() => window.addEventListener('paste', onWindowPaste, true))

function removeImage(id: string) {
  images.value = images.value.filter((i) => i.id !== id)
}

// ---- 聊天画板：确认后把画布 PNG 作为图片附件加入消息 ----
const boardVisible = ref(false)

function onBoardConfirm(dataUrl: string) {
  const base64 = dataUrl.slice(dataUrl.indexOf(',') + 1)
  images.value.push({ id: uuid(), fileName: 'board.png', mimeType: 'image/png', dataUrl, base64 })
}

/** 当前会话团队模式：发送走 责任→建议→评估 团队流水线（普通聊天零影响） */
const teamMode = computed(() => !!kernel.session.current?.teamMode)

async function send() {
  const content = text.value.trim()
  if ((!content && images.value.length === 0) || streaming.value) return
  // 团队模式：提问走团队流水线；图片附件暂不支持（后端团队管道为纯 LLM 调用）
  if (teamMode.value) {
    if (images.value.length > 0) {
      kernel.notify.warning(t('chat.teamConfig'))
      return
    }
    text.value = ''
    expanded.value = false
    await kernel.chat.sendTeam(content)
    return
  }
  const imgs = images.value.map((i) => ({ fileName: i.fileName, mimeType: i.mimeType, base64: i.base64 }))
  text.value = ''
  images.value = []
  // 发送即关闭覆盖层并恢复最小高度：不等 AI 回复完成（send 是流式完整流程，会阻塞到回复结束）
  expanded.value = false
  await kernel.chat.send(content, imgs, { enabled: thinkingEnabled.value, effort: thinkingEffort.value })
}

function onKeydown(e: KeyboardEvent) {
  if (e.key === 'Enter' && !e.shiftKey && !e.isComposing) {
    e.preventDefault()
    void send()
  }
}

function interrupt() {
  void kernel.chat.interrupt()
}
</script>

<template>
  <div class="input-bar-wrap" :class="{ expanded }">
    <div class="input-bar" :class="{ expanded }">
      <span
        class="expand-btn"
        :title="expanded ? t('chat.shrinkInput') : t('chat.expandInput')"
        :aria-label="expanded ? t('chat.shrinkInput') : t('chat.expandInput')"
        @click="expanded = !expanded"
      >
        <!-- 全屏/还原 ICON -->
        <svg v-if="!expanded" class="ico" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 2L2 4l3 3M12 2l2 2-3 3M4 14l-2-2 3-3M12 14l2-2-3-3" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" /></svg>
        <svg v-else class="ico" viewBox="0 0 16 16" aria-hidden="true"><path d="M2 5l3-3 3 3M8 3H3v5M14 11l-3 3-3-3M8 13h5V8" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linecap="round" stroke-linejoin="round" /></svg>
      </span>
      <div v-if="visionSupported && images.length > 0" class="image-row">
        <div v-for="img in images" :key="img.id" class="image-thumb">
          <img :src="img.dataUrl" :alt="img.fileName" />
          <el-button class="remove" size="small" circle text :aria-label="t('chat.removeImage')" @click="removeImage(img.id)">×</el-button>
        </div>
      </div>
      <el-input
        v-model="text"
        type="textarea"
        :autosize="autoSize"
        resize="none"
        :placeholder="noSession ? t('chat.inputPlaceholderNoSession') : t('chat.inputPlaceholder')"
        :disabled="noSession"
        @keydown="onKeydown"
      />
      <div class="bar-actions">
        <span class="nc-dim hint">
          <template v-if="noSession">
            <el-button size="small" text type="primary" @click="kernel.session.create()">＋ {{ t('chat.newSession') }}</el-button>
            <span>{{ t('chat.policyHintNoSession') }}</span>
          </template>
          <template v-else>
            {{ t('chat.policyHint') }}
          </template>
          <template v-if="teamMode">
            <span class="team-badge">👥 {{ t('chat.teamBadge') }}</span>
          </template>
          <template v-if="visionSupported">
            <el-tooltip :content="t('chat.imagePasteHint')" placement="top">
              <el-button text size="small" type="primary" @click="fileInput?.click()">🖼 {{ t('chat.uploadImage') }}</el-button>
            </el-tooltip>
            <el-tooltip :content="t('chat.boardHint')" placement="top">
              <el-button text size="small" type="primary" @click="boardVisible = true">🎨 {{ t('chat.board') }}</el-button>
            </el-tooltip>
            <input ref="fileInput" type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple class="hidden-file" @change="onPickChange" />
          </template>
          <span class="thinking-row">
            <el-switch v-model="thinkingEnabled" size="small" @change="saveThinking" />
            <span class="thinking-label">{{ t('chat.thinkingMode') }}</span>
            <el-select
              v-model="thinkingEffort"
              size="small"
              :disabled="!thinkingEnabled"
              class="thinking-effort"
              @change="saveThinking"
            >
              <el-option v-for="o in effortOptions" :key="o.value" :value="o.value" :label="t(o.labelKey)" />
            </el-select>
          </span>
        </span>
        <el-button v-if="streaming" type="danger" plain @click="interrupt">■ {{ t('chat.interrupt') }}</el-button>
        <el-button v-else type="primary" :disabled="noSession || (!text.trim() && images.length === 0)" @click="send">
          <template v-if="teamMode">👥 </template>{{ t('chat.send') }}
        </el-button>
      </div>
    </div>
  </div>
  <CanvasBoard v-model:visible="boardVisible" @confirm="onBoardConfirm" />
</template>

<style scoped>
.input-bar-wrap {
  padding: 12px 8% 20px;
  border-top: 1px solid var(--nc-border);
  background: var(--nc-surface);
  backdrop-filter: blur(10px);
  transition: padding 0.2s, background 0.2s;
}

/* 展开态：整块输入区上延为覆盖层（fixed 全区域，类似弹出新层） */
.input-bar-wrap.expanded {
  position: fixed;
  inset: 56px 6% 14px;
  z-index: 90;
  padding: 0;
  border-top: none;
  background: color-mix(in srgb, var(--nc-bg) 88%, transparent);
  backdrop-filter: blur(14px);
}

/* 圆角输入容器 */
.input-bar {
  position: relative;
  border: 1px solid var(--nc-border);
  border-radius: 14px;
  padding: 10px 14px 8px;
  background: color-mix(in srgb, var(--nc-bg) 55%, transparent);
}

.input-bar.expanded {
  height: 100%;
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  border-radius: 18px;
  box-shadow: 0 24px 70px rgba(0, 0, 0, 0.4);
}

/* 展开态：textarea 占满剩余高度，内部滚动 */
.input-bar.expanded .el-textarea {
  flex: 1;
  display: flex;
  min-height: 0;
}

.input-bar.expanded :deep(.el-textarea__inner) {
  height: 100% !important;
  min-height: 0 !important;
  flex: 1;
}

/* 右上角全屏/还原 ICON */
.expand-btn {
  position: absolute;
  top: 8px;
  right: 10px;
  z-index: 6;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 24px;
  height: 24px;
  border-radius: 6px;
  cursor: pointer;
  color: var(--nc-text-dim);
  background: color-mix(in srgb, var(--nc-text-dim) 8%, transparent);
  transition: background 0.15s, color 0.15s;
}

.expand-btn:hover {
  background: color-mix(in srgb, var(--nc-text-dim) 18%, transparent);
  color: var(--nc-text);
}

.expand-btn .ico {
  width: 14px;
  height: 14px;
}

/* 给 textarea 留出右上角 ICON 空间，避免首行文字被遮挡 */
.input-bar :deep(.el-textarea__inner) {
  padding-right: 38px;
  border-radius: 8px;
}

.bar-actions {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-top: 8px;
}

.hint {
  font-size: 12px;
  display: inline-flex;
  align-items: center;
  gap: 8px;
}

.thinking-row {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  margin-left: 8px;
  padding-left: 10px;
  border-left: 1px solid var(--nc-border);
}

.thinking-label {
  font-size: 12px;
  white-space: nowrap;
}

.team-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  font-size: 11.5px;
  font-weight: 600;
  color: var(--nc-primary);
  padding: 1px 8px;
  border-radius: 999px;
  border: 1px solid color-mix(in srgb, var(--nc-primary) 40%, transparent);
  background: color-mix(in srgb, var(--nc-primary) 8%, transparent);
}

.thinking-effort {
  width: 96px;
}

.image-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin-bottom: 8px;
}

.image-thumb {
  position: relative;
  width: 64px;
  height: 64px;
  border-radius: 6px;
  overflow: hidden;
  border: 1px solid var(--nc-border);
}

.image-thumb img {
  width: 100%;
  height: 100%;
  object-fit: cover;
}

.image-thumb .remove {
  position: absolute;
  top: 0;
  right: 0;
  min-width: 18px;
  height: 18px;
  padding: 0;
  background: rgba(0, 0, 0, 0.55);
  color: #fff;
  font-size: 13px;
  line-height: 18px;
  border-radius: 0 0 0 6px;
}

.hidden-file {
  display: none;
}
</style>
