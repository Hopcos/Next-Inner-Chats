<script setup lang="ts">
import { computed, onUnmounted, ref, watch } from 'vue'
import { ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'
import type { ChatSessionDto } from '@/api/types'
import NcIcon from '@/components/ui/NcIcon.vue'

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
        <span class="logo" aria-hidden="true"><NcIcon name="sparkles" :size="15" :stroke="2" /></span>
        <span class="brand-name">Next Chats</span>
        <el-tooltip :content="t('chat.collapseSidebar')" placement="right">
          <button class="nc-icon-btn collapse-top" type="button" :aria-label="t('chat.collapseSidebar')" @click="kernel.session.toggleSidebar()">
            <NcIcon name="panel-left" :size="18" />
          </button>
        </el-tooltip>
      </div>

      <button class="new-chat nc-focus" type="button" @click="kernel.session.create().catch(() => {})">
        <NcIcon name="square-pen" :size="16" />
        <span>{{ t('chat.newSession') }}</span>
      </button>

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
                <NcIcon class="pin-badge" name="pin" :size="12" />
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
              <button class="item-menu" type="button" :aria-label="t('chat.sessionOps')" @click.stop><NcIcon name="more" :size="16" :stroke="2" /></button>
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
            <button class="item-menu" type="button" :aria-label="t('chat.sessionOps')" @click.stop><NcIcon name="more" :size="16" :stroke="2" /></button>
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
            <el-button size="small" text @click="void kernel.session.loadAll().catch(() => {})">{{ t('common.refresh') }}</el-button>
          </div>
        </div>
      </div>
    </div>

    <div v-if="kernel.session.state.sidebarCollapsed" class="collapsed-head">
      <button class="nc-icon-btn" type="button" :aria-label="t('chat.expandSidebar')" @click="kernel.session.toggleSidebar()"><NcIcon name="panel-left" :size="18" /></button>
    </div>

  </aside>
</template>

<style scoped>
.sidebar {
  width: 264px;
  min-width: 264px;
  display: flex;
  flex-direction: column;
  border-right: 1px solid var(--nc-border);
  background: var(--nc-bg-elev);
  transition: width 0.2s, min-width 0.2s;
}

/* 收起：完全隐藏（入口移到顶栏左侧的“展开侧栏”按钮，ChatGPT 式） */
.sidebar.collapsed {
  width: 0;
  min-width: 0;
  border-right: none;
  overflow: hidden;
}

/* 关键：让列表层的直接父级成为 flex 容器，.list 的 flex:1 + overflow 才能真正生效，
   列表超高时收缩自身并出现滚动条 */
.sidebar-body {
  display: flex;
  flex-direction: column;
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.brand {
  height: 52px;
  padding: 0 8px 0 14px;
  display: flex;
  align-items: center;
  gap: 9px;
  flex-shrink: 0;
}

.logo {
  width: 26px;
  height: 26px;
  border-radius: 8px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  color: var(--nc-primary-contrast);
  background: linear-gradient(135deg, var(--nc-primary), color-mix(in srgb, var(--nc-primary) 55%, #6366f1));
}

.brand-name {
  font-size: 15px;
  font-weight: 650;
  letter-spacing: -0.01em;
}

.collapse-top {
  margin-left: auto;
}

.collapsed-head {
  display: none;
}

/* 新会话：整行按钮（主操作，置于列表顶部） */
.new-chat {
  display: flex;
  align-items: center;
  gap: 10px;
  margin: 4px 10px 8px;
  height: 38px;
  padding: 0 12px;
  border-radius: 10px;
  border: 1px solid var(--nc-border);
  background: var(--nc-surface-solid);
  color: var(--nc-text);
  font-size: 13.5px;
  font-weight: 500;
  cursor: pointer;
  box-shadow: var(--nc-shadow-sm);
  transition: background 0.15s, border-color 0.15s;
}

.new-chat:hover {
  border-color: var(--nc-border-strong);
  background: color-mix(in srgb, var(--nc-surface-solid) 85%, var(--nc-text) 4%);
}

.new-chat :deep(.nc-icon) {
  color: var(--nc-primary);
}

/* ---------- 会话搜索框 ---------- */
.search-box {
  position: relative;
  margin: 0 10px 6px;
  display: flex;
  align-items: center;
}

.search-icon {
  position: absolute;
  left: 11px;
  color: var(--nc-text-faint);
  pointer-events: none;
}

.search-input {
  width: 100%;
  height: 34px;
  padding: 0 28px 0 32px;
  border-radius: 10px;
  border: 1px solid transparent;
  background: var(--nc-surface-2);
  color: var(--nc-text);
  font-size: 13px;
  outline: none;
  transition: border-color 0.15s, box-shadow 0.15s, background 0.15s;
}

.search-input:hover {
  background: var(--nc-surface-3);
}

.search-input:focus {
  background: var(--nc-surface-solid);
  border-color: color-mix(in srgb, var(--nc-primary) 60%, transparent);
  box-shadow: 0 0 0 3px var(--nc-primary-soft);
}

.search-input::placeholder {
  color: var(--nc-text-faint);
}

.search-clear {
  position: absolute;
  right: 7px;
  width: 20px;
  height: 20px;
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
  color: var(--nc-text);
  background: var(--nc-surface-3);
}

/* ---------- 会话列表 ---------- */
.list {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 4px 8px 12px;
}

.item {
  position: relative;
  display: flex;
  align-items: center;
  gap: 4px;
  padding: 8px 6px 8px 12px;
  border-radius: 10px;
  margin-bottom: 1px;
  cursor: pointer;
  color: var(--nc-text);
  transition: background 0.12s;
}

.item-main {
  flex: 1;
  min-width: 0;
}

.item-menu {
  flex-shrink: 0;
  opacity: 0;
  width: 26px;
  height: 26px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border: none;
  border-radius: 7px;
  background: transparent;
  color: var(--nc-text-dim);
  cursor: pointer;
  transition: opacity 0.12s, background 0.12s;
}

.item-menu:hover {
  background: var(--nc-surface-3);
  color: var(--nc-text);
}

.item-menu:focus-visible {
  opacity: 1;
  outline: 2px solid color-mix(in srgb, var(--nc-primary) 70%, transparent);
}

.item:hover .item-menu,
.item.active .item-menu {
  opacity: 1;
}

.pin-badge {
  margin-right: 5px;
  vertical-align: -1px;
  color: var(--nc-text-faint);
}

/* 工作空间绑定标识：已绑定会话在标题前的专属 icon */
.ws-badge {
  display: inline-block;
  vertical-align: -2px;
  margin-right: 5px;
  flex-shrink: 0;
  color: var(--nc-primary);
  opacity: 0.9;
}

.item:hover {
  background: var(--nc-surface-2);
}

.item.active {
  background: var(--nc-surface-3);
}

/* 选中态：左侧主题色指示条（不再整块高饱和填充） */
.item.active::before {
  content: '';
  position: absolute;
  left: 2px;
  top: 10px;
  bottom: 10px;
  width: 3px;
  border-radius: 3px;
  background: var(--nc-primary);
}

.item-title {
  font-size: 13.5px;
  line-height: 1.35;
  overflow: hidden;
  white-space: nowrap;
  text-overflow: ellipsis;
}

.item.active .item-title {
  font-weight: 550;
}

.item-meta {
  font-size: 11.5px;
  line-height: 1.3;
  margin-top: 2px;
  color: var(--nc-text-faint);
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
</style>
