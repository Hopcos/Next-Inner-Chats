<script setup lang="ts">
/**
 * 服务器目录浏览对话框（管理端注册工作空间 / 用户端选择工作空间共用）。
 * props.api(path) 返回 { path, parent, entries: {name,type,size}[] }。
 */
import { ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { FsEntryDto } from '@/api/types'

const props = defineProps<{
  modelValue: boolean
  title: string
  api: (path: string | null) => Promise<{ path: string; parent: string | null; entries: FsEntryDto[] }>
  /** 是否可选中当前目录（返回该路径） */
  selectable?: boolean
  selectLabel?: string
}>()
const emit = defineEmits<{ 'update:modelValue': [boolean]; select: [path: string] }>()

const { t } = useI18n()
const currentPath = ref('')
const parentPath = ref<string | null>(null)
const entries = ref<FsEntryDto[]>([])
const loading = ref(false)
const error = ref('')

async function open(path: string | null) {
  loading.value = true
  error.value = ''
  try {
    const r = await props.api(path)
    currentPath.value = r.path
    parentPath.value = r.parent
    entries.value = r.entries
  } catch (err) {
    error.value = String((err as Error)?.message ?? err)
  } finally {
    loading.value = false
  }
}

watch(
  () => props.modelValue,
  (v) => {
    if (v) void open(null)
  },
)

function enterDir(name: string) {
  void open(currentPath.value ? `${currentPath.value.replace(/[\\/]$/, '')}\\${name}` : name)
}

function goParent() {
  if (parentPath.value !== null) void open(parentPath.value)
}

function pick() {
  if (!currentPath.value) return
  emit('select', currentPath.value)
  emit('update:modelValue', false)
}
</script>

<template>
  <el-dialog :model-value="modelValue" :title="title" width="560px" :close-on-click-modal="false" @update:model-value="(v: boolean) => emit('update:modelValue', v)">
    <div class="fs-bar">
      <el-button size="small" :disabled="parentPath === null" @click="goParent">⬆ {{ t('workspace.fsParent') }}</el-button>
      <span class="fs-path" :title="currentPath">{{ currentPath || t('workspace.fsRoot') }}</span>
    </div>
    <div class="fs-body" v-loading="loading">
      <p v-if="error" class="fs-error">{{ error }}</p>
      <div v-else-if="entries.length === 0" class="fs-empty">{{ t('workspace.fsEmpty') }}</div>
      <div v-else class="fs-list">
        <div v-for="e in entries" :key="e.name" class="fs-item" @dblclick="e.type === 'dir' && enterDir(e.name)">
          <span class="fs-icon">{{ e.type === 'dir' ? '📁' : '📄' }}</span>
          <span class="fs-name" @click="e.type === 'dir' ? enterDir(e.name) : undefined">{{ e.name }}</span>
          <span class="fs-size">{{ e.type === 'file' && e.size > 0 ? `${(e.size / 1024).toFixed(1)} KB` : '' }}</span>
        </div>
      </div>
    </div>
    <template #footer>
      <el-button size="small" @click="emit('update:modelValue', false)">{{ t('common.cancel') }}</el-button>
      <el-button v-if="selectable" size="small" type="primary" :disabled="!currentPath" @click="pick">
        {{ selectLabel ?? t('workspace.fsSelect') }}
      </el-button>
    </template>
  </el-dialog>
</template>

<style scoped>
.fs-bar {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 8px;
}
.fs-path {
  flex: 1;
  font-family: ui-monospace, Consolas, monospace;
  font-size: 12px;
  color: var(--nc-text-dim, #888);
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  direction: rtl;
  text-align: left;
}
.fs-body {
  height: 320px;
  overflow-y: auto;
  border: 1px solid var(--nc-border, #333);
  border-radius: 6px;
  padding: 4px;
}
.fs-list {
  display: flex;
  flex-direction: column;
}
.fs-item {
  display: flex;
  align-items: center;
  gap: 8px;
  padding: 4px 8px;
  border-radius: 4px;
  cursor: default;
  font-size: 13px;
}
.fs-item:hover {
  background: color-mix(in srgb, var(--nc-primary, #6366f1) 12%, transparent);
}
.fs-name {
  flex: 1;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}
.fs-icon {
  width: 20px;
  text-align: center;
}
.fs-size {
  color: var(--nc-text-dim, #888);
  font-size: 12px;
}
.fs-empty,
.fs-error {
  padding: 24px;
  text-align: center;
  color: var(--nc-text-dim, #888);
  font-size: 13px;
}
</style>
