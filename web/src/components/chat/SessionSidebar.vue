<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'
import type { ChatSessionDto } from '@/api/types'

const { t } = useI18n()
// 必须用 computed：loadAll/remove 会“替换” sessions 数组引用，
// 直接解构常量会持有旧数组 → 刷新后侧栏永远为空（重挂载才恢复）
const sessions = computed(() => kernel.session.state.sessions)

// ---------------- 会话搜索（名称模糊 → 各话题问题模糊） ----------------
const query = ref('')
const filtered = ref<ChatSessionDto[] | null>(null)
const matchedTopicBySession = ref(new Map<string, string>())
let debounceTimer: number | undefined

const searchActive = computed(() => query.value.trim().length > 0)

/** 归一化：小写 + 空白折叠（中英文都可直接子串匹配） */
function normalize(s: string): string {
  return s.toLowerCase().replace(/\s+/g, ' ').trim()
}

/** 模糊匹配：整词包含；多词（空格分隔）时每词都必须包含（AND） */
function fuzzyMatch(text: string, q: string): boolean {
  const tokens = normalize(q).split(' ').filter(Boolean)
  if (tokens.length === 0) return true
  const target = normalize(text)
  return tokens.every((tok) => target.includes(tok))
}

async function runSearch(q: string) {
  if (!q) {
    filtered.value = null
    matchedTopicBySession.value = new Map()
    return
  }
  const term = q.trim()
  const out: ChatSessionDto[] = []
  const matchedTopic = new Map<string, string>()
  for (const s of sessions.value) {
    // 第一优先：会话名称模糊匹配（命中即展示，无需拉话题）
    if (fuzzyMatch(s.title, term)) {
      out.push(s)
      continue
    }
    // 第二优先：会话内各话题（user 提问）模糊匹配（按需懒加载话题索引）
    try {
      const topics = await kernel.chat.loadTopics(s.id)
      const hit = topics.find((tp) => fuzzyMatch(tp.title, term))
      if (hit) {
        out.push(s)
        matchedTopic.set(s.id, hit.title)
      }
    } catch {
      /* 单会话话题拉取失败：跳过该会话，不阻塞其余匹配 */
    }
  }
  filtered.value = out
  matchedTopicBySession.value = matchedTopic
}

watch(query, () => {
  window.clearTimeout(debounceTimer)
  debounceTimer = window.setTimeout(() => {
    void runSearch(query.value)
  }, 250)
})

onUnmounted(() => window.clearTimeout(debounceTimer))

// ---------------- 展示列表（搜索时用过滤结果，否则全量） ----------------
const listAll = computed(() => filtered.value ?? sessions.value)
const pinnedSessions = computed(() => listAll.value.filter((s) => s.isPinned))
const normalSessions = computed(() => listAll.value.filter((s) => !s.isPinned))
const noResult = computed(() => searchActive.value && listAll.value.length === 0)
const emptyAll = computed(() => !searchActive.value && sessions.value.length === 0)

/** 会话更新时间（搜索按话题命中时改为展示命中话题，让用户知道为什么匹配） */
function metaText(s: ChatSessionDto): string {
  const hit = matchedTopicBySession.value.get(s.id)
  if (hit) return t('chat.matchByTopic', { title: hit })
  return new Date(s.updatedAt).toLocaleString(undefined, { month: 'numeric', day: 'numeric', hour: '2-digit', minute: '2-digit' })
}

/** 工作空间绑定标识：已绑定会话显示专属图标，tooltip 展示工作空间名称 */
const workspaceNameOf = (id?: string | null): string => {
  if (!id) return ''
  return kernel.session.state.workspaces.find((w) => w.id === id)?.name ?? t('chat.boundWorkspace')
}

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

      <div class="search-box">
        <svg class="search-icon" viewBox="0 0 24 24" width="14" height="14" fill="none" stroke="currentColor" stroke-width="2" stroke-linecap="round" aria-hidden="true">
          <circle cx="11" cy="11" r="7" />
          <path d="m20 20-3.2-3.2" />
        </svg>
        <input
          v-model="query"
          class="search-input"
          type="text"
          :placeholder="t('chat.searchSessions')"
          :aria-label="t('chat.searchSessions')"
        />
        <button v-if="query" class="search-clear" :aria-label="t('common.clear')" @click="query = ''">×</button>
      </div>

      <div class="list nc-scroll">
        <template v-if="pinnedSessions.length > 0">
          <div
            v-for="s in pinnedSessions"
            :key="s.id"
            class="item"
            :class="{ active: s.id === kernel.session.state.currentId }"
            @click="select(s.id)"
          >
            <div class="item-main">
              <div class="item-title">
                <span class="pin-badge">📌</span>
                <el-tooltip v-if="s.workspaceId" :content="workspaceNameOf(s.workspaceId)" placement="top" :show-after="250">
                  <svg class="ws-badge" viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                    <rect x="2.5" y="7" width="19" height="12" rx="2" />
                    <path d="M8.5 7V5.5a2 2 0 0 1 2-2h3a2 2 0 0 1 2 2V7" />
                    <path d="M2.5 12h19" />
                  </svg>
                </el-tooltip>
                {{ s.title || t('chat.untitled') }}
              </div>
              <div class="item-meta nc-dim">{{ metaText(s) }}</div>
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
            <div class="item-title">
              <el-tooltip v-if="s.workspaceId" :content="workspaceNameOf(s.workspaceId)" placement="top" :show-after="250">
                <svg class="ws-badge" viewBox="0 0 24 24" width="13" height="13" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                  <rect x="2.5" y="7" width="19" height="12" rx="2" />
                  <path d="M8.5 7V5.5a2 2 0 0 1 2-2h3a2 2 0 0 1 2 2V7" />
                  <path d="M2.5 12h19" />
                </svg>
              </el-tooltip>
              {{ s.title || t('chat.untitled') }}
            </div>
            <div class="item-meta nc-dim">{{ metaText(s) }}</div>
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

        <div v-if="noResult" class="empty nc-dim">
          {{ t('chat.searchNoResult') }}
        </div>

        <div v-if="emptyAll" class="empty nc-dim">
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
  padding: 18px 18px 8px;
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

/* ---------- 会话搜索框 ---------- */
.search-box {
  position: relative;
  margin: 0 12px 8px;
  display: flex;
  align-items: center;
}

.search-icon {
  position: absolute;
  left: 10px;
  color: var(--nc-text-dim);
  pointer-events: none;
}

.search-input {
  width: 100%;
  height: 30px;
  padding: 0 28px 0 30px;
  border-radius: 9px;
  border: 1px solid var(--nc-border);
  background: color-mix(in srgb, var(--nc-surface) 80%, var(--nc-bg, #000) 20%);
  color: var(--nc-text);
  font-size: 12.5px;
  outline: none;
  transition: border-color 0.15s, box-shadow 0.15s;
}

.search-input:focus {
  border-color: var(--nc-primary);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--nc-primary) 15%, transparent);
}

.search-input::placeholder {
  color: var(--nc-text-dim);
}

.search-clear {
  position: absolute;
  right: 6px;
  width: 18px;
  height: 18px;
  border: none;
  border-radius: 50%;
  background: transparent;
  color: var(--nc-text-dim);
  font-size: 14px;
  line-height: 1;
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  justify-content: center;
}

.search-clear:hover {
  color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 12%, transparent);
}

/* ---------- 会话列表 ---------- */
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

.pin-badge {
  margin-right: 4px;
  font-size: 11px;
}

/* 工作空间绑定标识：已绑定会话在标题前的专属 icon */
.ws-badge {
  display: inline-block;
  vertical-align: -2px;
  margin-right: 4px;
  flex-shrink: 0;
  color: var(--nc-primary);
  opacity: 0.9;
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
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
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
