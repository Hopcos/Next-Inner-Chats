<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref } from 'vue'
import { useRouter } from 'vue-router'
import { ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'
import { http } from '@/api/http'
import type { UiMessage } from '@/kernel/plugins'
import { getLang, setLang, type AppLang } from '@/i18n'
import SessionSidebar from '@/components/chat/SessionSidebar.vue'
import MessageList from '@/components/chat/MessageList.vue'
import ChatInputBar from '@/components/chat/ChatInputBar.vue'
import ChatSettingsDrawer from '@/components/chat/ChatSettingsDrawer.vue'
import TeamConfigDrawer from '@/components/chat/TeamConfigDrawer.vue'
import MusicPlayer from '@/components/chat/MusicPlayer.vue'
import NcIcon from '@/components/ui/NcIcon.vue'

const router = useRouter()
const { t } = useI18n()
const drawerOpen = ref(false)
const appearanceOpen = ref(false)
const teamDrawerOpen = ref(false)
const editingTitle = ref(false)
const titleDraft = ref('')
let fallbackTimer: number | undefined

const current = computed(() => kernel.session.current)
const user = computed(() => kernel.auth.state.user)
/** 当前会话团队模式标识（顶栏徽标 + 发送通道切换） */
const currentTeamMode = computed(() => !!kernel.session.current?.teamMode)
const messages = computed(() => kernel.chat.messagesOf(kernel.session.state.currentId))
/** 全量话题索引（话题导航条渲染） */
const topics = computed(() => kernel.chat.state.topics[kernel.session.state.currentId ?? ''] ?? [])
/** 会话是否还有更早消息（向上翻页） */
const hasMoreBefore = computed(() => kernel.chat.state.moreBefore[kernel.session.state.currentId ?? ''] === true)
const loadingOlder = computed(() => kernel.chat.isOlderLoading(kernel.session.state.currentId ?? ''))

const lang = computed<AppLang>(() => getLang())
const langOptions = [
  { value: 'en' as AppLang, label: 'English' },
  { value: 'zh' as AppLang, label: '中文' },
]

// ---------- 工作空间选择（编码会话） ----------
const currentWorkspaceId = computed(() => kernel.session.current?.workspaceId ?? null)
const wsLevelLabel = (level: number) =>
  level >= 40
    ? t('chat.workspaceAuto')
    : level >= 30
      ? t('chat.workspaceFull')
      : level >= 20
        ? t('chat.workspaceWrite')
        : t('chat.workspaceReadOnly')

/** 顶部工作空间触发器文案：绑定后必须展示工作空间名称（自绘控件，不依赖 el-select 内部 label 解析） */
const boundWorkspaceLabel = computed(() => {
  const id = currentWorkspaceId.value
  if (!id) return t('chat.workspaceNone')
  const w = kernel.session.state.workspaces.find((x) => x.id === id)
  if (!w) return t('chat.workspaceNone')
  return `${w.name} · ${wsLevelLabel(w.level)}`
})

async function onWorkspaceChange(id: string | null | undefined) {
  const cur = kernel.session.current
  if (!cur) return
  const target = id ?? null // 归一化：clear 图标会 emit undefined/'' → 一律视为 null（解绑）
  // 同值幂等：@change 与 clear 双事件下避免重复处理（重复走确认/重复请求）
  if (target === (cur.workspaceId ?? null)) return
  // 解绑工作空间需弹窗确认（避免误触回到普通聊天、丢失工作空间上下文）
  if (target === null && cur.workspaceId) {
    const confirmed = await ElMessageBox.confirm(t('chat.workspaceUnbindConfirm'), t('common.confirm'), { type: 'warning', confirmButtonText: t('common.confirm'), cancelButtonText: t('common.cancel') })
      .then(() => true)
      .catch(() => false)
    if (!confirmed) return
  }
  try {
    const bound = await kernel.session.setWorkspace(cur.id, target)
    kernel.notify.success(target ? t('chat.workspaceBound', { name: kernel.session.state.workspaces.find((w) => w.id === target)?.name ?? '' }) : t('chat.workspaceUnbound'))
    if (bound) void kernel.session.loadWorkspaces().catch(() => {})
  } catch (err) {
    kernel.notify.error(t('chat.workspaceBindFailed'))
    console.error('[chat] setWorkspace failed:', err)
  }
}

onMounted(() => {
  // 聊天页专属 Element Plus 令牌映射（base.css 中 html.nc-chat 作用域）
  document.documentElement.classList.add('nc-chat')
  void kernel.session.loadWorkspaces().catch(() => {})
  void (async () => {
    await kernel.session.loadAll().catch(() => {})
    // 首次使用引导：没有任何会话时自动创建一个（否则输入问题后发送会因无可归属会话而不显示）
    if (kernel.session.state.sessions.length === 0) {
      await kernel.session.create().catch(() => {})
    }
  })()
  // 自愈兜底：若首屏加载后会话列表仍为空，稍后自动重试，避免“刷新后侧栏消失”
  fallbackTimer = window.setTimeout(() => {
    if (kernel.session.state.sessions.length === 0 && !kernel.session.state.loading) {
      void kernel.session.loadAll().catch(() => {})
    }
  }, 1500)
})

onUnmounted(() => {
  window.clearTimeout(fallbackTimer)
  document.documentElement.classList.remove('nc-chat')
})

async function openSettings() {
  drawerOpen.value = true
  // 先拉取服务端记忆，再刷新目录：避免“孤儿校准”基于过期本地值误清用户选择
  await kernel.settings.pullFromServer().catch(() => {})
  void kernel.catalog.load().catch(() => {})
}

/** 顶部标题左侧「新建会话」图标按钮：创建新会话并进入 */
async function onNewSession() {
  await kernel.session.create().catch(() => {})
}

function startRename() {
  if (!current.value) return
  titleDraft.value = current.value.title
  editingTitle.value = true
}

async function commitRename() {
  editingTitle.value = false
  const title = titleDraft.value.trim()
  if (current.value && title) {
    await kernel.session
      .rename(current.value.id, title)
      .catch((e) => kernel.notify.error((e as { message?: string }).message ?? t('chat.renameFailed'), (e as { code?: string }).code))
  }
}

async function onDeleteSession() {
  if (!current.value) return
  try {
    await ElMessageBox.confirm(t('chat.deleteSessionConfirm', { title: current.value.title }), t('chat.deleteSessionTitle'), { type: 'warning' })
  } catch {
    return
  }
  await kernel.session.remove(current.value.id)
}

function onRegenerate(messageId: string) {
  void kernel.chat.regenerate(messageId)
}

async function onRemoveMessage(msg: { id: string; content?: string; role?: string }) {
  const title = (msg.content || t('chat.untitled')).replace(/\s+/g, ' ').slice(0, 30)
  try {
    await ElMessageBox.confirm(t('chat.deleteMessageConfirm', { title }), t('chat.deleteMessageTitle'), { type: 'warning' })
  } catch {
    return
  }
  await kernel.chat.deleteFrom(msg.id)
}

// ---------------- 收藏：当前“提问 + 回答”一起收藏（去重提示由后端 409 提供） ----------------
interface FavoriteDto {
  id: string
  title: string
  questionText?: string | null
  answerText?: string | null
}

async function onFavoriteMessage(msg: UiMessage) {
  const list = messages.value
  const idx = list.findIndex((m) => m.id === msg.id)
  if (idx < 0) return

  let q: UiMessage | undefined
  let a: UiMessage | undefined
  if (msg.role === 'user') {
    // 提问：取本条；回答：其后第一条已完成（有正文）的 assistant 消息
    q = msg
    a = list.slice(idx + 1).find((m) => m.role === 'assistant' && m.content?.trim())
  } else {
    // 回答：本条；提问：其前最近的一条 user 消息
    a = msg
    for (let i = idx - 1; i >= 0; i--) {
      if (list[i].role === 'user') {
        q = list[i]
        break
      }
    }
  }

  if (!a?.content?.trim() || !q?.content?.trim()) {
    kernel.notify.warning(t('chat.favoriteNeedAnswer'))
    return
  }

  try {
    await http.post<FavoriteDto>('/api/chat/favorites', {
      questionMessageId: q.id,
      question: q.content.trim(),
      answer: a.content.trim(),
    })
    kernel.notify.success(t('chat.favoriteSaved'))
  } catch (e) {
    const err = e as { code?: string; message?: string }
    if (err.code === 'FAVORITE_DUPLICATED') {
      kernel.notify.warning(t('chat.favoriteDuplicated'))
    } else {
      kernel.notify.error(err.message ?? t('chat.favoriteFailed'), err.code)
    }
  }
}

async function logout() {
  await kernel.auth.logout()
  void router.push('/login')
}

const themeOptions = [
  { value: 'aurora', labelKey: 'chat.themeAurora' },
  { value: 'dawn', labelKey: 'chat.themeDawn' },
  { value: 'midnight', labelKey: 'chat.themeMidnight' },
  { value: 'cyber', labelKey: 'chat.themeCyber' },
]

/** 沉浸式工具栏：以独立新标签页进入工具主页（与聊天页隔离，互不干扰状态） */
function openToolsHub() {
  window.open('/tools', '_blank')
}
</script>

<template>
  <div class="chat-layout">
    <SessionSidebar />

    <main class="chat-main">
      <header class="topbar">
        <div class="title-area">
          <!-- 侧栏收起时：顶栏左侧补出“展开侧栏 / 新会话”入口（ChatGPT 式） -->
          <template v-if="kernel.session.state.sidebarCollapsed">
            <el-tooltip :content="t('chat.expandSidebar')" placement="bottom">
              <button class="nc-icon-btn" type="button" :aria-label="t('chat.expandSidebar')" @click="kernel.session.toggleSidebar()">
                <NcIcon name="panel-left" :size="18" />
              </button>
            </el-tooltip>
          </template>
          <el-tooltip :content="t('chat.newSession')" placement="bottom">
            <button class="nc-icon-btn new-session-btn" type="button" :aria-label="t('chat.newSession')" @click="onNewSession">
              <NcIcon name="square-pen" :size="18" />
            </button>
          </el-tooltip>
          <template v-if="editingTitle">
            <el-input v-model="titleDraft" size="small" style="width: 280px" @keyup.enter="commitRename" @blur="commitRename" />
          </template>
          <template v-else>
            <h2 class="session-title" @dblclick="startRename">
              {{ current?.title ?? t('common.appName') }}
              <span v-if="currentTeamMode" class="team-chip" :title="t('chat.teamConfig')"><NcIcon name="users" :size="12" /> {{ t('chat.teamBadge') }}</span>
            </h2>
          </template>
          <!-- 工作空间选择（编码会话；仅当用户角色被授予 ≥1 个工作空间时显示）。
               自绘 dropdown 触发器：绑定后触发器恒以正常文字展示“工作空间名 · 级别”，
               不依赖 el-select 的 label 解析/占位样式，保证“绑定后名称必可见”。 -->
          <div v-if="kernel.session.state.workspaces.length > 0" class="ws-picker">
            <el-dropdown trigger="click" @command="(v: string | null) => onWorkspaceChange(v)">
              <button class="ws-picker-btn" :class="{ bound: !!currentWorkspaceId }" type="button" :aria-label="t('chat.workspaceSelect')">
                <NcIcon class="ws-icon" name="briefcase" :size="13" />
                <span class="ws-label">{{ boundWorkspaceLabel }}</span>
                <NcIcon class="ws-caret" name="chevron-down" :size="12" :stroke="2" />
              </button>
              <template #dropdown>
                <el-dropdown-menu class="ws-menu">
                  <el-dropdown-item :command="null" :class="{ 'is-active': !currentWorkspaceId }">
                    <span class="ws-menu-item"><span class="ws-none-dot" />{{ t('chat.workspaceNone') }}</span>
                  </el-dropdown-item>
                  <el-dropdown-item v-for="w in kernel.session.state.workspaces" :key="w.id" :command="w.id" :class="{ 'is-active': w.id === currentWorkspaceId }">
                    <span class="ws-menu-item"><NcIcon name="briefcase" :size="14" style="margin-right: 6px" />{{ w.name }} · {{ wsLevelLabel(w.level) }}</span>
                  </el-dropdown-item>
                </el-dropdown-menu>
              </template>
            </el-dropdown>
          </div>
        </div>

        <div class="actions">
          <div class="icon-actions">
            <el-tooltip :content="t('chat.favorites')" placement="bottom">
              <button class="toolbar-entry nc-icon-btn" type="button" :aria-label="t('chat.favorites')" @click="router.push('/favorites')">
                <NcIcon name="star" :size="18" />
              </button>
            </el-tooltip>

            <el-tooltip :content="t('chat.teamConfig')" placement="bottom">
              <button class="toolbar-entry nc-icon-btn" type="button" :class="{ active: currentTeamMode || teamDrawerOpen }" :aria-label="t('chat.teamConfig')" @click="teamDrawerOpen = true">
                <NcIcon name="users" :size="18" />
              </button>
            </el-tooltip>

            <el-tooltip :content="t('chat.settings')" placement="bottom">
              <button class="toolbar-entry nc-icon-btn" type="button" :class="{ active: drawerOpen }" :aria-label="t('chat.settings')" @click="openSettings">
                <NcIcon name="sliders" :size="18" />
              </button>
            </el-tooltip>

            <el-tooltip :content="t('tools.hubTitle')" placement="bottom">
              <button class="toolbar-entry nc-icon-btn" type="button" :aria-label="t('tools.hubTitle')" @click="openToolsHub">
                <NcIcon name="grid" :size="18" />
              </button>
            </el-tooltip>

            <!-- 外观：主题 / 语言 / 3D 背景收进一个弹出面板，顶栏只保留高频入口 -->
            <el-popover v-model:visible="appearanceOpen" trigger="click" placement="bottom-end" :width="264" popper-class="nc-appearance-pop">
              <template #reference>
                <button class="toolbar-entry nc-icon-btn appearance-btn" type="button" :class="{ active: appearanceOpen }" :aria-label="t('chat.appearance')">
                  <NcIcon name="palette" :size="18" />
                </button>
              </template>
              <div class="ap">
                <div class="ap-label">{{ t('chat.theme') }}</div>
                <div class="ap-themes" role="radiogroup" :aria-label="t('chat.theme')">
                  <button
                    v-for="tOpt in themeOptions"
                    :key="tOpt.value"
                    type="button"
                    role="radio"
                    class="ap-theme nc-focus"
                    :class="[tOpt.value, { on: kernel.theme.state.theme === tOpt.value }]"
                    :aria-checked="kernel.theme.state.theme === tOpt.value"
                    @click="kernel.theme.set(tOpt.value as never)"
                  >
                    <span class="ap-swatch"><i /><b /></span>
                    <span class="ap-name">{{ t(tOpt.labelKey) }}</span>
                  </button>
                </div>
                <div class="ap-label">{{ t('chat.langLabel') }}</div>
                <div class="ap-seg" role="radiogroup" :aria-label="t('chat.langLabel')">
                  <button
                    v-for="l in langOptions"
                    :key="l.value"
                    type="button"
                    role="radio"
                    class="nc-focus"
                    :class="{ on: lang === l.value }"
                    :aria-checked="lang === l.value"
                    @click="setLang(l.value)"
                  >{{ l.label }}</button>
                </div>
                <div class="ap-row">
                  <span>{{ t('chat.threeD') }}</span>
                  <el-switch :model-value="kernel.settings.state.threeEnabled" size="small" @change="(v: boolean) => kernel.settings.toggleThree(v)" />
                </div>
              </div>
            </el-popover>
          </div>

          <span class="actions-sep" aria-hidden="true" />

          <el-dropdown trigger="click" placement="bottom-end">
            <button class="avatar-btn nc-focus" type="button" :aria-label="user?.displayName ?? user?.username ?? ''">
              <el-avatar :size="28" class="avatar">{{ (user?.displayName ?? user?.username ?? '?').slice(0, 1).toUpperCase() }}</el-avatar>
            </button>
            <template #dropdown>
              <div class="user-card">
                <div class="user-name">{{ user?.displayName ?? user?.username }}</div>
                <div v-if="user?.displayName && user?.username" class="user-sub">@{{ user.username }}</div>
              </div>
              <el-dropdown-menu>
                <el-dropdown-item @click="router.push('/settings')"><NcIcon name="user" :size="15" />{{ t('chat.personalCatalog') }}</el-dropdown-item>
                <el-dropdown-item v-if="user?.isAdmin" @click="router.push('/admin')"><NcIcon name="shield" :size="15" />{{ t('common.admin') }}</el-dropdown-item>
                <el-dropdown-item divided @click="logout"><NcIcon name="log-out" :size="15" />{{ t('common.logout') }}</el-dropdown-item>
              </el-dropdown-menu>
            </template>
          </el-dropdown>
        </div>
      </header>

      <!-- 顶部音乐播放器：多公开源自动切换，独立细条不挤压消息区 -->
      <MusicPlayer />

      <!-- :key 绑定会话 id：切换会话时整个列表重挂载 → 首屏强制滚到底部 -->
      <MessageList
        :key="kernel.session.state.currentId ?? 'none'"
        :session-id="kernel.session.state.currentId"
        :messages="messages"
        :topics="topics"
        :has-more-before="hasMoreBefore"
        :loading-older="loadingOlder"
        @regenerate="onRegenerate"
        @remove="onRemoveMessage"
        @favorite="onFavoriteMessage"
        @need-older="() => { const sid = kernel.session.state.currentId; if (sid) void kernel.chat.loadOlder(sid) }"
        @jump-topic="(id: string) => { const sid = kernel.session.state.currentId; if (sid) void kernel.chat.ensureLoadedUntil(sid, id) }"
      />
      <ChatInputBar />

      <ChatSettingsDrawer v-model="drawerOpen" />
      <TeamConfigDrawer v-model="teamDrawerOpen" :session-id="kernel.session.state.currentId" />
    </main>
  </div>
</template>

<style scoped>
.chat-layout {
  display: flex;
  height: 100%;
}

.chat-main {
  flex: 1;
  min-width: 0;
  display: flex;
  flex-direction: column;
}

.topbar {
  height: 52px;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 14px 0 12px;
  border-bottom: 1px solid var(--nc-border);
  background: color-mix(in srgb, var(--nc-bg) 82%, transparent);
  backdrop-filter: saturate(160%) blur(12px);
}

/* 左侧标题区：允许被压缩（flex 默认 min-width:auto 不收缩，超长标题会把右侧 actions
   整体挤出视口 → 头像悬停/点击失效）。min-width:0 + 省略号保证任何窗口宽度下右侧恒在视口内 */
.title-area {
  min-width: 0;
  flex: 0 1 auto;
  display: flex;
  align-items: center;
  gap: 4px;
  overflow: hidden;
  margin-right: 12px;
}

.session-title {
  margin: 0 0 0 6px;
  font-size: 15px;
  font-weight: 600;
  letter-spacing: -0.005em;
  cursor: text;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  padding: 4px 8px;
  border-radius: 8px;
  transition: background 0.15s;
}

.session-title:hover {
  background: var(--nc-surface-2);
}

/* 顶部「新建会话」：幽灵图标按钮（与其它顶栏图标同形态），悬停才出底色 */
.new-session-btn:active {
  transform: scale(0.94);
}

/* 团队模式徽标（紧贴会话标题） */
.team-chip {
  display: inline-flex;
  align-items: center;
  gap: 4px;
  margin-left: 8px;
  font-size: 11px;
  font-weight: 600;
  vertical-align: 2px;
  color: var(--nc-primary);
  padding: 1px 8px;
  border-radius: 999px;
  background: var(--nc-primary-soft);
}

.ws-picker {
  margin-left: 6px;
  flex-shrink: 1;
  max-width: 260px;
  min-width: 0;
}

/* 顶部工作空间触发器按钮：绑定后以主题色高亮，名称恒可见（防 el-select 占位样式导致的“名称不展示”） */
.ws-picker-btn {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  width: 100%;
  max-width: 260px;
  height: 28px;
  padding: 0 10px;
  border-radius: 999px;
  border: 1px solid var(--nc-border);
  background: transparent;
  color: var(--nc-text-dim);
  cursor: pointer;
  font-size: 12.5px;
  transition: all 0.15s;
  box-sizing: border-box;
}

.ws-picker-btn:hover {
  background: var(--nc-surface-2);
  color: var(--nc-text);
}

.ws-picker-btn.bound {
  color: var(--nc-primary);
  border-color: transparent;
  background: var(--nc-primary-soft);
}

.ws-icon,
.ws-caret {
  flex-shrink: 0;
}

.ws-label {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  flex: 1;
  min-width: 0;
  text-align: left;
}

.ws-none-dot {
  display: inline-block;
  width: 7px;
  height: 7px;
  border-radius: 50%;
  border: 1px solid currentColor;
  opacity: 0.6;
  margin-right: 6px;
}

.ws-menu-item {
  display: inline-flex;
  align-items: center;
}

:deep(.ws-menu .el-dropdown-menu__item.is-active) {
  color: var(--nc-primary);
  font-weight: 600;
}

/* 右侧操作区：永不收缩（头像最右永远完整可见可点），并提升层级防被任何覆盖层遮挡 */
.actions {
  position: relative;
  z-index: 10;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  gap: 6px;
}

.icon-actions {
  display: inline-flex;
  align-items: center;
  gap: 2px;
}

.actions-sep {
  width: 1px;
  height: 20px;
  margin: 0 6px;
  background: var(--nc-border);
}

.avatar-btn {
  display: inline-flex;
  padding: 0;
  border: none;
  background: transparent;
  border-radius: 50%;
  cursor: pointer;
}

.avatar {
  background: linear-gradient(135deg, var(--nc-primary), color-mix(in srgb, var(--nc-primary) 55%, #6366f1));
  color: var(--nc-primary-contrast);
  font-weight: 700;
  font-size: 13px;
  transition: box-shadow 0.15s;
}

.avatar-btn:hover .avatar {
  box-shadow: 0 0 0 3px var(--nc-primary-soft);
}

.user-card {
  padding: 10px 16px 8px;
  border-bottom: 1px solid var(--nc-border);
  min-width: 200px;
}

.user-name {
  font-weight: 600;
  font-size: 13.5px;
  color: var(--nc-text);
}

.user-sub {
  font-size: 12px;
  color: var(--nc-text-faint);
  margin-top: 2px;
}

/* 窄窗口自适应：保证头像始终落在视口内（次要入口按宽度分级隐藏） */
@media (max-width: 900px) {
  .icon-actions .toolbar-entry:nth-child(3),
  .icon-actions .toolbar-entry:nth-child(4) {
    display: none;
  }
}

@media (max-width: 560px) {
  /* 极端窄窗（手机横屏/极小窗口）：隐藏整个图标组，只留头像，保证头像不溢出 */
  .icon-actions,
  .actions-sep {
    display: none;
  }
}
</style>

<!-- 外观面板（popper teleport 到 body，需非 scoped） -->
<style>
.nc-appearance-pop.el-popover.el-popper {
  padding: 14px;
}

.nc-appearance-pop .ap-label {
  font-size: 11.5px;
  font-weight: 600;
  letter-spacing: 0.02em;
  color: var(--nc-text-faint);
  margin: 0 0 8px;
}

.nc-appearance-pop .ap-label:not(:first-child) {
  margin-top: 14px;
}

.nc-appearance-pop .ap-themes {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 8px;
}

.nc-appearance-pop .ap-theme {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: 6px;
  padding: 6px;
  border-radius: 10px;
  border: 1px solid var(--nc-border);
  background: transparent;
  color: var(--nc-text-dim);
  cursor: pointer;
  font-size: 12px;
  transition: border-color 0.15s, color 0.15s;
}

.nc-appearance-pop .ap-theme:hover {
  border-color: var(--nc-border-strong);
  color: var(--nc-text);
}

.nc-appearance-pop .ap-theme.on {
  border-color: var(--nc-primary);
  box-shadow: 0 0 0 1px var(--nc-primary);
  color: var(--nc-text);
}

.nc-appearance-pop .ap-swatch {
  position: relative;
  height: 34px;
  border-radius: 6px;
  overflow: hidden;
  display: block;
}

.nc-appearance-pop .ap-swatch i {
  position: absolute;
  inset: 0 62% 0 0;
}

.nc-appearance-pop .ap-swatch b {
  position: absolute;
  right: 8px;
  bottom: 8px;
  width: 14px;
  height: 14px;
  border-radius: 50%;
}

.nc-appearance-pop .ap-theme.aurora .ap-swatch { background: #0b1220; }
.nc-appearance-pop .ap-theme.aurora .ap-swatch i { background: #0e1628; border-right: 1px solid rgba(148, 163, 184, 0.18); }
.nc-appearance-pop .ap-theme.aurora .ap-swatch b { background: #2dd4bf; }
.nc-appearance-pop .ap-theme.dawn .ap-swatch { background: #f8f9fb; box-shadow: inset 0 0 0 1px rgba(15, 23, 42, 0.08); }
.nc-appearance-pop .ap-theme.dawn .ap-swatch i { background: #eceff4; border-right: 1px solid rgba(15, 23, 42, 0.08); }
.nc-appearance-pop .ap-theme.dawn .ap-swatch b { background: #4f46e5; }
.nc-appearance-pop .ap-theme.midnight .ap-swatch { background: #0b0b0d; }
.nc-appearance-pop .ap-theme.midnight .ap-swatch i { background: #141417; border-right: 1px solid rgba(255, 255, 255, 0.08); }
.nc-appearance-pop .ap-theme.midnight .ap-swatch b { background: #a78bfa; }
.nc-appearance-pop .ap-theme.cyber .ap-swatch {
  background:
    linear-gradient(rgba(255, 43, 214, 0.55) 1px, transparent 1px) 0 18px / 100% 5px,
    linear-gradient(to bottom, #07040f 50%, #1a0630);
}
.nc-appearance-pop .ap-theme.cyber .ap-swatch i { background: rgba(12, 7, 26, 0.9); border-right: 1px solid #00f0ff; box-shadow: 1px 0 6px rgba(0, 240, 255, 0.6); }
.nc-appearance-pop .ap-theme.cyber .ap-swatch b { background: linear-gradient(135deg, #00f0ff, #ff2bd6); border-radius: 2px; box-shadow: 0 0 8px #ff2bd6; }

.nc-appearance-pop .ap-seg {
  display: grid;
  grid-template-columns: 1fr 1fr;
  padding: 3px;
  gap: 3px;
  border-radius: 9px;
  background: var(--nc-surface-2);
}

.nc-appearance-pop .ap-seg button {
  height: 28px;
  border: none;
  border-radius: 7px;
  background: transparent;
  color: var(--nc-text-dim);
  font-size: 12.5px;
  cursor: pointer;
}

.nc-appearance-pop .ap-seg button.on {
  background: var(--nc-surface-solid);
  color: var(--nc-text);
  font-weight: 600;
  box-shadow: var(--nc-shadow-sm);
}

.nc-appearance-pop .ap-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-top: 14px;
  padding-top: 12px;
  border-top: 1px solid var(--nc-border);
  font-size: 13px;
  color: var(--nc-text);
}
</style>
