<script setup lang="ts">
import { computed, onBeforeUnmount, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'
import { http } from '@/api/http'
import CanvasBoard from '@/components/chat/CanvasBoard.vue'
import { uuid } from '@/utils/uuid'
import NcIcon from '@/components/ui/NcIcon.vue'

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
const focused = ref(false)
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
    <div class="input-bar" :class="{ expanded, focused, team: teamMode }">
      <button
        class="expand-btn"
        type="button"
        :title="expanded ? t('chat.shrinkInput') : t('chat.expandInput')"
        :aria-label="expanded ? t('chat.shrinkInput') : t('chat.expandInput')"
        @click="expanded = !expanded"
      >
        <NcIcon :name="expanded ? 'minimize' : 'maximize'" :size="15" />
      </button>
      <div v-if="visionSupported && images.length > 0" class="image-row">
        <div v-for="img in images" :key="img.id" class="image-thumb">
          <img :src="img.dataUrl" :alt="img.fileName" />
          <button class="remove" type="button" :aria-label="t('chat.removeImage')" @click="removeImage(img.id)"><NcIcon name="x" :size="12" :stroke="2.4" /></button>
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
        @focus="focused = true"
        @blur="focused = false"
      />
      <div class="bar-actions">
        <span class="tools">
          <template v-if="noSession">
            <el-button size="small" text type="primary" @click="kernel.session.create()">＋ {{ t('chat.newSession') }}</el-button>
          </template>
          <template v-if="visionSupported">
            <el-tooltip :content="t('chat.uploadImage') + ' · ' + t('chat.imagePasteHint')" placement="top">
              <button class="tool-btn" type="button" :aria-label="t('chat.uploadImage')" @click="fileInput?.click()"><NcIcon name="image" :size="17" /></button>
            </el-tooltip>
            <el-tooltip :content="t('chat.board') + ' · ' + t('chat.boardHint')" placement="top">
              <button class="tool-btn" type="button" :aria-label="t('chat.board')" @click="boardVisible = true"><NcIcon name="brush" :size="17" /></button>
            </el-tooltip>
            <input ref="fileInput" type="file" accept="image/png,image/jpeg,image/gif,image/webp" multiple class="hidden-file" @change="onPickChange" />
          </template>
          <span class="thinking-row" :class="{ on: thinkingEnabled }">
            <button
              class="think-chip"
              type="button"
              role="switch"
              :aria-checked="thinkingEnabled"
              :title="t('chat.thinkingMode')"
              @click="thinkingEnabled = !thinkingEnabled; saveThinking()"
            >
              <NcIcon name="lightbulb" :size="15" />
              <span class="thinking-label">{{ t('chat.thinkingMode') }}</span>
            </button>
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
          <span v-if="teamMode" class="team-badge"><NcIcon name="users" :size="12" /> {{ t('chat.teamBadge') }}</span>
        </span>
        <el-tooltip v-if="streaming" :content="t('chat.interrupt')" placement="top">
          <button class="send-btn stop" type="button" @click="interrupt">
            <NcIcon name="stop" :size="14" />
            <span class="sr-only">{{ t('chat.interrupt') }}</span>
          </button>
        </el-tooltip>
        <el-tooltip v-else :content="t('chat.send') + ' (Enter)'" placement="top">
          <button class="send-btn" type="button" :disabled="noSession || (!text.trim() && images.length === 0)" @click="send">
            <NcIcon :name="teamMode ? 'users' : 'arrow-up'" :size="17" :stroke="2.2" />
            <span class="sr-only">{{ t('chat.send') }}</span>
          </button>
        </el-tooltip>
      </div>
    </div>
    <div class="hint nc-dim">
      <template v-if="noSession">{{ t('chat.policyHintNoSession') }}</template>
      <template v-else>{{ t('chat.policyHint') }}</template>
    </div>
  </div>
  <CanvasBoard v-model:visible="boardVisible" @confirm="onBoardConfirm" />
</template>
<style scoped>
/* 输入区：悬浮式输入卡片（画布底部渐隐，无整条分隔带） */
.input-bar-wrap {
  position: relative;
  padding: 0 4% 10px;
  background: linear-gradient(to bottom, transparent, var(--nc-bg) 28px);
  transition: padding 0.2s, background 0.2s;
}

.input-bar-wrap > .input-bar,
.input-bar-wrap > .hint {
  max-width: 860px;
  margin-left: auto;
  margin-right: auto;
}

/* 展开态：整块输入区上延为覆盖层（fixed 全区域，类似弹出新层） */
.input-bar-wrap.expanded {
  position: fixed;
  inset: 52px 6% 14px;
  z-index: 90;
  padding: 0;
  background: color-mix(in srgb, var(--nc-bg) 88%, transparent);
  backdrop-filter: blur(14px);
}

.input-bar-wrap.expanded > .input-bar {
  max-width: none;
}

.input-bar-wrap.expanded > .hint {
  display: none;
}

/* 圆角输入卡片 */
.input-bar {
  position: relative;
  border: 1px solid var(--nc-border-strong);
  border-radius: 22px;
  padding: 10px 10px 8px 14px;
  background: var(--nc-surface-solid);
  box-shadow: var(--nc-shadow);
  transition: border-color 0.15s, box-shadow 0.15s;
}

.input-bar.focused {
  border-color: color-mix(in srgb, var(--nc-primary) 55%, var(--nc-border-strong));
  box-shadow: var(--nc-shadow), 0 0 0 3px var(--nc-primary-soft);
}

.input-bar.expanded {
  height: 100%;
  box-sizing: border-box;
  display: flex;
  flex-direction: column;
  border-radius: 22px;
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

/* textarea 去框去底：融入卡片 */
.input-bar :deep(.el-textarea__inner) {
  padding: 4px 38px 4px 2px;
  border-radius: 0;
  border: none;
  box-shadow: none !important;
  background: transparent !important;
  color: var(--nc-text);
  font-size: 14.5px;
  line-height: 1.55;
}

.input-bar :deep(.el-textarea__inner::placeholder) {
  color: var(--nc-text-faint);
}

.input-bar :deep(.el-textarea.is-disabled .el-textarea__inner) {
  background: transparent !important;
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
  width: 28px;
  height: 28px;
  padding: 0;
  border: none;
  border-radius: 8px;
  cursor: pointer;
  color: var(--nc-text-faint);
  background: transparent;
  transition: background 0.15s, color 0.15s;
}

.expand-btn:hover {
  background: var(--nc-surface-2);
  color: var(--nc-text);
}

.bar-actions {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 8px;
  margin-top: 6px;
}

.tools {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  min-width: 0;
  flex-wrap: wrap;
}

.tool-btn {
  display: inline-flex;
  align-items: center;
  justify-content: center;
  width: 32px;
  height: 32px;
  padding: 0;
  border: none;
  border-radius: 10px;
  background: transparent;
  color: var(--nc-text-dim);
  cursor: pointer;
  transition: background 0.15s, color 0.15s;
}

.tool-btn:hover {
  background: var(--nc-surface-2);
  color: var(--nc-text);
}

.thinking-row {
  display: inline-flex;
  align-items: center;
  gap: 2px;
  margin-left: 4px;
}

/* 思考模式：胶囊开关（开启时主题色描边+浅底） */
.think-chip {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  height: 30px;
  padding: 0 11px;
  border-radius: 999px;
  border: 1px solid var(--nc-border-strong);
  background: transparent;
  color: var(--nc-text-dim);
  font-size: 12.5px;
  cursor: pointer;
  transition: all 0.15s;
}

.think-chip:hover {
  color: var(--nc-text);
}

.thinking-row.on .think-chip {
  color: var(--nc-primary);
  border-color: color-mix(in srgb, var(--nc-primary) 45%, transparent);
  background: var(--nc-primary-soft);
}

.thinking-label {
  white-space: nowrap;
}

.thinking-effort {
  width: 84px;
}

.thinking-effort :deep(.el-select__wrapper) {
  box-shadow: none;
  background: transparent;
  border-radius: 999px;
  min-height: 30px;
}

.thinking-effort :deep(.el-select__wrapper:hover) {
  background: var(--nc-surface-2);
}

.thinking-row:not(.on) .thinking-effort {
  opacity: 0.5;
}

.team-badge {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-left: 6px;
  font-size: 11.5px;
  font-weight: 600;
  color: var(--nc-primary);
  padding: 2px 9px;
  border-radius: 999px;
  background: var(--nc-primary-soft);
}

/* 发送：圆形主色按钮（禁用为中性灰） */
.send-btn {
  flex-shrink: 0;
  width: 36px;
  height: 36px;
  border-radius: 50%;
  border: none;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  background: var(--nc-primary);
  color: var(--nc-primary-contrast);
  cursor: pointer;
  transition: background 0.15s, transform 0.1s, opacity 0.15s;
}

.send-btn:hover:not(:disabled) {
  background: color-mix(in srgb, var(--nc-primary) 86%, var(--nc-text));
}

.send-btn:active:not(:disabled) {
  transform: scale(0.94);
}

.send-btn:disabled {
  background: var(--nc-surface-3);
  color: var(--nc-text-faint);
  cursor: not-allowed;
}

.send-btn.stop {
  background: var(--nc-text);
  color: var(--nc-bg);
}

.send-btn:focus-visible,
.tool-btn:focus-visible,
.think-chip:focus-visible,
.expand-btn:focus-visible {
  outline: 2px solid color-mix(in srgb, var(--nc-primary) 70%, transparent);
  outline-offset: 1px;
}

.hint {
  font-size: 11.5px;
  text-align: center;
  margin-top: 7px;
  color: var(--nc-text-faint);
  white-space: nowrap;
  overflow: hidden;
  text-overflow: ellipsis;
}

.image-row {
  display: flex;
  flex-wrap: wrap;
  gap: 8px;
  margin: 2px 0 8px;
}

.image-thumb {
  position: relative;
  width: 64px;
  height: 64px;
  border-radius: 10px;
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
  top: 4px;
  right: 4px;
  width: 20px;
  height: 20px;
  padding: 0;
  border: none;
  border-radius: 50%;
  background: rgba(0, 0, 0, 0.6);
  color: #fff;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  cursor: pointer;
}

.hidden-file {
  display: none;
}

@media (max-width: 640px) {
  .thinking-label {
    display: none;
  }
}
</style>