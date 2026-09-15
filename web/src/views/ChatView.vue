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
import MusicPlayer from '@/components/chat/MusicPlayer.vue'

const router = useRouter()
const { t } = useI18n()
const drawerOpen = ref(false)
const editingTitle = ref(false)
const titleDraft = ref('')
let fallbackTimer: number | undefined

const current = computed(() => kernel.session.current)
const user = computed(() => kernel.auth.state.user)
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

onMounted(() => {
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
})

async function onNewSession() {
  await kernel.session.create()
}

async function openSettings() {
  drawerOpen.value = true
  // 先拉取服务端记忆，再刷新目录：避免“孤儿校准”基于过期本地值误清用户选择
  await kernel.settings.pullFromServer().catch(() => {})
  void kernel.catalog.load().catch(() => {})
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
          <template v-if="editingTitle">
            <el-input v-model="titleDraft" size="small" style="width: 280px" @keyup.enter="commitRename" @blur="commitRename" />
          </template>
          <template v-else>
            <h2 class="session-title" @dblclick="startRename">{{ current?.title ?? t('common.appName') }}</h2>
          </template>
        </div>

        <div class="actions">
          <div class="icon-actions">
            <el-tooltip :content="t('chat.newSession')" placement="bottom">
              <button class="toolbar-entry" :aria-label="t('chat.newSession')" @click="onNewSession">
                <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                  <rect x="2.5" y="5.5" width="19" height="13" rx="2" />
                  <path d="M12 9v6" />
                  <path d="M9 12h6" />
                </svg>
              </button>
            </el-tooltip>

            <el-tooltip :content="t('chat.favorites')" placement="bottom">
              <button class="toolbar-entry" :aria-label="t('chat.favorites')" @click="router.push('/favorites')">
                <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                  <path d="m12 3.5 2.6 5.3 5.9.85-4.25 4.15 1 5.85L12 16.85 6.75 19.6l1-5.85L3.5 9.65l5.9-.85L12 3.5Z" />
                </svg>
              </button>
            </el-tooltip>

            <el-tooltip :content="t('chat.settings')" placement="bottom">
              <button class="toolbar-entry" :aria-label="t('chat.settings')" @click="openSettings">
                <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                  <circle cx="12" cy="12" r="3" />
                  <path d="M12 3.5v2.3M12 18.2v2.3M3.5 12h2.3M18.2 12h2.3M6 6l1.6 1.6M16.4 16.4 18 18M6 18l1.6-1.6M16.4 7.6 18 6" />
                </svg>
              </button>
            </el-tooltip>

            <el-tooltip :content="t('tools.hubTitle')" placement="bottom">
              <button class="toolbar-entry" :aria-label="t('tools.hubTitle')" @click="openToolsHub">
                <svg viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.7" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
                  <path d="M3 8h18v11a1 1 0 0 1-1 1H4a1 1 0 0 1-1-1V8Z" />
                  <path d="M8 8V6a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2" />
                  <path d="M3 12h18" />
                  <path d="M12 13.4v2.2" />
                </svg>
              </button>
            </el-tooltip>
          </div>

          <el-tooltip :content="t('chat.threeD')" placement="bottom">
            <el-switch
              :model-value="kernel.settings.state.threeEnabled"
              style="--el-switch-on-color: var(--nc-primary)"
              @change="(v: boolean) => kernel.settings.toggleThree(v)"
            />
          </el-tooltip>

          <el-select
            :model-value="kernel.theme.state.theme"
            size="small"
            class="theme-picker"
            style="width: 96px"
            @change="(v: string) => kernel.theme.set(v as never)"
          >
            <el-option v-for="tOpt in themeOptions" :key="tOpt.value" :label="t(tOpt.labelKey)" :value="tOpt.value" />
          </el-select>

          <el-select :model-value="lang" size="small" style="width: 96px" @change="(v: AppLang) => setLang(v)">
            <el-option v-for="l in langOptions" :key="l.value" :label="l.label" :value="l.value" />
          </el-select>

          <el-dropdown trigger="click">
            <el-avatar :size="26" class="avatar">{{ (user?.displayName ?? user?.username ?? '?').slice(0, 1) }}</el-avatar>
            <template #dropdown>
              <el-dropdown-menu>
                <el-dropdown-item @click="router.push('/settings')">{{ t('chat.personalCatalog') }}</el-dropdown-item>
                <el-dropdown-item v-if="user?.isAdmin" @click="router.push('/admin')">{{ t('common.admin') }}</el-dropdown-item>
                <el-dropdown-item divided @click="logout">{{ t('common.logout') }}</el-dropdown-item>
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
  height: 56px;
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 0 20px;
  border-bottom: 1px solid var(--nc-border);
  background: var(--nc-surface);
  backdrop-filter: blur(10px);
}

/* 左侧标题区：允许被压缩（flex 默认 min-width:auto 不收缩，超长标题会把右侧 actions
   整体挤出视口 → 头像悬停/点击失效）。min-width:0 + 省略号保证任何窗口宽度下右侧恒在视口内 */
.title-area {
  min-width: 0;
  flex: 0 1 auto;
  display: flex;
  align-items: center;
  overflow: hidden;
  margin-right: 12px;
}

.session-title {
  margin: 0;
  font-size: 16px;
  font-weight: 600;
  cursor: pointer;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

/* 右侧操作区：永不收缩（头像最右永远完整可见可点），并提升层级防被任何覆盖层遮挡 */
.actions {
  position: relative;
  z-index: 10;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  gap: 12px;
}

.avatar {
  cursor: pointer;
  background: var(--nc-primary);
  color: #04121f;
  font-weight: 700;
}

/* 四个图标按钮统一容器：固定间距，宽度/圆角/描边完全一致 */
.icon-actions {
  display: inline-flex;
  align-items: center;
  gap: 10px;
}

.toolbar-entry {
  width: 30px;
  height: 30px;
  border-radius: 9px;
  border: 1px solid var(--nc-border);
  background: var(--nc-surface);
  color: var(--nc-text-dim, #94a3b8);
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  flex-shrink: 0;
  transition: all 0.15s;
}

.toolbar-entry:hover {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
  box-shadow: 0 0 0 3px color-mix(in srgb, var(--nc-primary) 15%, transparent);
}

/* 窄窗口自适应：actions 固定总宽约 790px 时，最右的头像下拉会被推出视口右侧，
   导致整块区域悬停/点击失效（指针不变、点了没反应；缩小页面后视口变宽才恢复）。
   这里按宽度分级压缩次要控件，保证头像始终落在视口内。 */
@media (max-width: 1020px) {
  .actions {
    gap: 8px;
  }

  .icon-actions {
    gap: 6px;
  }

  .toolbar-entry {
    width: 28px;
    height: 28px;
  }

  /* 3D 背景开关属锦上添花，窄屏优先隐藏（:deep 命中 el-switch 根元素） */
  :deep(.actions .el-switch) {
    display: none;
  }
}

@media (max-width: 900px) {
  /* 900 档：除收窄选择器外，隐藏主题选择器与 3/4 号图标（设置/工具台），
     否则 820~880px 窗口下头像（原 x≈854）仍溢出视口无法点击 */
  :deep(.actions .el-select) {
    width: 76px !important;
  }

  :deep(.actions .el-select.theme-picker) {
    display: none;
  }

  .icon-actions .toolbar-entry:nth-child(3),
  .icon-actions .toolbar-entry:nth-child(4) {
    display: none;
  }

  .actions {
    gap: 6px;
  }
}

@media (max-width: 780px) {
  :deep(.actions .el-select) {
    width: 70px !important;
  }
}

@media (max-width: 560px) {
  /* 极端窄窗（手机横屏/极小窗口）：隐藏整个图标组，只留语言选择与头像，保证头像不溢出 */
  .icon-actions {
    display: none;
  }
}
</style>
