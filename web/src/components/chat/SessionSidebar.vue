<script setup lang="ts">
import { computed } from 'vue'
import { ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'

const { t } = useI18n()
// 必须用 computed：loadAll/remove 会“替换” sessions 数组引用，
// 直接解构常量会持有旧数组 → 刷新后侧栏永远为空（重挂载才恢复）
const sessions = computed(() => kernel.session.state.sessions)
// 置顶区：置顶会话（后端/本地排序已置顶优先）；其余为普通区
const pinnedSessions = computed(() => sessions.value.filter((s) => s.isPinned))
const normalSessions = computed(() => sessions.value.filter((s) => !s.isPinned))

function select(id: string) {
  if (id === kernel.session.state.currentId) return
  kernel.session.select(id)
  void kernel.chat.loadHistory(id)
}

async function remove(id: string, title: string) {
  try {
    await ElMessageBox.confirm(t('chat.deleteSessionConfirm', { title }), t('chat.deleteSessionTitle'), {
      type: 'warning',
      confirmButtonText: t('common.delete'),
    })
  } catch {
    return
  }
  await kernel.session.remove(id)
}

async function rename(id: string, title: string) {
  let next = title
  try {
    const res = await ElMessageBox.prompt(t('chat.renameSessionPrompt'), t('chat.rename'), {
      inputValue: title,
      confirmButtonText: t('common.confirm'),
      cancelButtonText: t('common.cancel'),
      inputValidator: (v: string) => (v && v.trim().length > 0) || t('chat.renameSessionEmpty'),
    })
    next = (res.value ?? '').trim()
  } catch {
    return
  }
  if (next && next !== title) await kernel.session.rename(id, next)
}

async function onMenuCommand(command: string, s: { id: string; title: string; isPinned?: boolean }) {
  if (command === 'rename') await rename(s.id, s.title)
  else if (command === 'pin') await kernel.session.pin(s.id, !s.isPinned)
  else if (command === 'delete') await remove(s.id, s.title)
}
</script>

<template>
  <aside class="sidebar" :class="{ collapsed: kernel.session.state.sidebarCollapsed }">
    <div v-if="!kernel.session.state.sidebarCollapsed" class="sidebar-body">
      <div class="brand">
        <span class="dot" /> Next <strong>Chats</strong>
        <el-button class="collapse-top" size="small" text :title="t('chat.collapseSidebar')" @click="kernel.session.toggleSidebar()">⮜</el-button>
      </div>
      <div class="list nc-scroll">
        <template v-if="pinnedSessions.length > 0">
          <div class="group-label">{{ t('chat.pinnedSection') }}</div>
          <div
            v-for="s in pinnedSessions"
            :key="s.id"
            class="item"
            :class="{ active: s.id === kernel.session.state.currentId }"
            @click="select(s.id)"
          >
            <div class="item-main">
              <div class="item-title"><span class="pin-badge">📌</span>{{ s.title || t('chat.untitled') }}</div>
              <div class="item-meta nc-dim">{{ new Date(s.updatedAt).toLocaleString(undefined, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }) }}</div>
            </div>
            <el-dropdown trigger="click" @command="(cmd: string) => onMenuCommand(cmd, s)" @click.stop>
              <el-button class="item-menu" size="small" text :aria-label="t('chat.sessionOps')">⋮</el-button>
              <template #dropdown>
                <el-dropdown-menu>
                  <el-dropdown-item command="rename">{{ t('chat.rename') }}</el-dropdown-item>
                  <el-dropdown-item command="pin">{{ t('chat.unpin') }}</el-dropdown-item>
                  <el-dropdown-item command="delete" divided>{{ t('common.delete') }}</el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </div>
        </template>

        <div v-for="s in normalSessions" :key="s.id" class="item" :class="{ active: s.id === kernel.session.state.currentId }" @click="select(s.id)">
          <div class="item-main">
            <div class="item-title">{{ s.title || t('chat.untitled') }}</div>
            <div class="item-meta nc-dim">{{ new Date(s.updatedAt).toLocaleString(undefined, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' }) }}</div>
          </div>
          <el-dropdown trigger="click" @command="(cmd: string) => onMenuCommand(cmd, s)" @click.stop>
            <el-button class="item-menu" size="small" text :aria-label="t('chat.sessionOps')">⋮</el-button>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item command="rename">{{ t('chat.rename') }}</el-dropdown-item>
                <el-dropdown-item command="pin">{{ t('chat.pin') }}</el-dropdown-item>
                <el-dropdown-item command="delete" divided>{{ t('common.delete') }}</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>

        <div v-if="sessions.length === 0" class="empty nc-dim">
          {{ t('chat.emptySessions') }}
          <div class="empty-retry">
            <el-button size="small" text @click="void kernel.session.loadAll().catch(() => {})">🔄 {{ t('common.refresh') }}</el-button>
          </div>
        </div>
      </div>
    </div>

    <div v-if="kernel.session.state.sidebarCollapsed" class="collapsed-head">
      <el-button size="small" text :title="t('chat.expandSidebar')" @click="kernel.session.toggleSidebar()">⮞</el-button>
    </div>

    <div class="footer">
      <el-button size="small" text @click="kernel.session.toggleSidebar()">
        {{ kernel.session.state.sidebarCollapsed ? '⮞' : '⮜' }}
      </el-button>
    </div>
  </aside>
</template>

<style scoped>
.sidebar {
  width: 260px;
  min-width: 260px;
  display: flex;
  flex-direction: column;
  border-right: 1px solid var(--nc-border);
  background: var(--nc-surface);
  backdrop-filter: blur(10px);
  transition: width 0.2s;
}

.sidebar.collapsed {
  width: 48px;
  min-width: 48px;
}

/* 关键：让列表层的直接父级成为 flex 容器，.list 的 flex:1 + overflow 才能真正生效，
   列表超高时收缩自身并出现滚动条，底部 footer（＋新会话）始终可见 */
.sidebar-body {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.brand {
  padding: 18px 18px 12px;
  font-size: 18px;
  display: flex;
  align-items: center;
  gap: 8px;
}

.collapse-top {
  margin-left: auto;
  color: var(--nc-text-dim);
  font-size: 14px;
}

.collapsed-head {
  display: flex;
  justify-content: center;
  padding: 14px 0 4px;
}

.dot {
  width: 9px;
  height: 9px;
  border-radius: 50%;
  background: var(--nc-primary);
  box-shadow: 0 0 10px var(--nc-primary);
}

.list {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 6px 10px;
}

.item {
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 10px 12px;
  border-radius: 10px;
  margin-bottom: 4px;
  cursor: pointer;
  transition: background 0.15s;
}

.item-main {
  flex: 1;
  min-width: 0;
}

.item-menu {
  flex-shrink: 0;
  opacity: 0;
  transition: opacity 0.15s;
  color: var(--nc-text-dim);
  padding: 4px 6px;
  font-size: 15px;
  line-height: 1;
  letter-spacing: 1px;
}

.item:hover .item-menu,
.item.active .item-menu {
  opacity: 1;
}

.group-label {
  font-size: 11px;
  font-weight: 600;
  color: var(--nc-text-dim);
  padding: 10px 12px 4px;
  letter-spacing: 0.5px;
}

.pin-badge {
  margin-right: 4px;
  font-size: 11px;
}

.item:hover {
  background: rgba(148, 163, 184, 0.12);
}

.item.active {
  background: color-mix(in srgb, var(--nc-primary) 22%, transparent);
  border: 1px solid color-mix(in srgb, var(--nc-primary) 45%, transparent);
}

.item-title {
  font-size: 13.5px;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.item-meta {
  font-size: 11px;
  margin-top: 2px;
}

.empty {
  text-align: center;
  padding: 20px;
  font-size: 12.5px;
}

.empty-retry {
  margin-top: 8px;
}

.footer {
  padding: 10px;
  display: flex;
  gap: 6px;
  justify-content: center;
  border-top: 1px solid var(--nc-border);
}
</style>
