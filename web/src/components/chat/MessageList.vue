<script setup lang="ts">
import { nextTick, onMounted, onUnmounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import type { UiMessage } from '@/kernel/plugins'
import MessageItem from '@/components/chat/MessageItem.vue'
import TopicRail from '@/components/chat/TopicRail.vue'

const { t } = useI18n()
const props = defineProps<{
  messages: UiMessage[]
  sessionId?: string | null
  /** 全量话题索引（话题导航条用） */
  topics: { id: string; title: string }[]
  /** 会话是否还有更早的未加载消息 */
  hasMoreBefore: boolean
  /** 正在向上加载 */
  loadingOlder: boolean
}>()
const emit = defineEmits<{
  regenerate: [messageId: string]
  remove: [message: UiMessage]
  favorite: [message: UiMessage]
  /** 滚动接近顶部：请求加载更早消息 */
  'need-older': []
  /** 点击话题导航条中的未加载话题：请求加载到该话题 */
  'jump-topic': [topicId: string]
}>()

const scroller = ref<HTMLDivElement | null>(null)
const stickToBottom = ref(true)
/** 用户主动离开底部 → 终止首屏贴底强制滚动（防止打中断续的平滑跳转/上翻加载） */
let forceScrollCancel = false

/**
 * 无论以任何方式进入会话（首次 / 切换 / 刷新 / 历史加载完成）都无条件滚动到最底部。
 * 用 rAF 循环持续以最新 scrollHeight 定位，直到连续 5 帧贴底且高度不再变化才结束；
 * 不设帧数上限（防止长列表渲染耗帧导致提前停在半路），仅 10s 时间死兜底防死循环。
 */
async function scrollToBottomForce() {
  const el = scroller.value
  if (!el) return
  forceScrollCancel = false
  el.style.scrollBehavior = 'auto'
  const deadline = performance.now() + 10000
  let lastHeight = -1
  let stableFrames = 0
  const step = () => {
    const e = scroller.value
    if (!e || forceScrollCancel) {
      if (e) e.style.scrollBehavior = ''
      return
    }
    const height = e.scrollHeight
    const gap = height - e.scrollTop - e.clientHeight
    if (gap <= 2 && height === lastHeight) {
      stableFrames++
      if (stableFrames >= 5 || performance.now() > deadline) {
        e.style.scrollBehavior = ''
        return
      }
    } else {
      stableFrames = 0
      lastHeight = height
    }
    e.scrollTo({ top: height, behavior: 'auto' })
    requestAnimationFrame(step)
  }
  step()
}

let lastNeedOlderAt = 0
function onScroll() {
  const el = scroller.value
  if (!el) return
  stickToBottom.value = el.scrollHeight - el.scrollTop - el.clientHeight < 80
  // 接近顶部 → 请求加载更早消息（150ms 节流；并发由 chat.loadOlder 内部去重）
  if (el.scrollTop < 90 && props.hasMoreBefore) {
    const now = performance.now()
    if (now - lastNeedOlderAt > 150) {
      lastNeedOlderAt = now
      emit('need-older')
    }
  }
}

// 仅用户直接输入（滚轮/触摸/键盘）终止首屏贴底强制滚动；
// force 自身的 scrollTo 只产生 scroll 事件，不会误触发（否则贴底循环第一帧即被自身事件打断）
const cancelForceScroll = () => {
  forceScrollCancel = true
}

// 会话切换（key 重挂载的首个场景）即触发强制滚动；历史/新消息到达（空→非空）再次触发
let lastMessageCount = 0
watch(
  () => props.messages.length,
  async (count) => {
    const grewFromEmpty = lastMessageCount === 0 && count > 0
    lastMessageCount = count
    if (grewFromEmpty) await scrollToBottomForce()
  },
  { immediate: true },
)

watch(
  () => props.sessionId,
  () => {
    // 切换会话（或首次进入）：无条件强制滚动到底部（不管之前位置/缓存）
    void scrollToBottomForce()
  },
  { immediate: true },
)

// 向上翻页（更早消息前插）后保持滚动位置 —— 锚点恢复法：
// 前插前（pre flush，DOM 未变）捕获视口顶部锚点及其相对位置；前插渲染稳定后，
// 把视口重新定位到该锚点（矩形实时读取，不依赖 scrollHeight，对分批/延迟布局免疫）。
let prevFirstId: string | null = null
let anchorId: string | null = null
let anchorVis = 0
/** 补偿单飞锁：连续滚动触发多轮前插时，只允许一个补偿流程在跑 */
let compensating = false
let pendingRestore = false

// pre flush：消息列表即将变化 → 若发生前插（更早消息引入），在本帧 DOM 变化前捕获视口锚点
watch(
  () => props.messages,
  (list, prev) => {
    const el = scroller.value
    const first = list[0]?.id ?? null
    const prevFirst = prev?.[0]?.id ?? null
    if (el && prevFirst !== null && first !== null && first !== prevFirst && !pendingRestore) {
      const scTop = el.getBoundingClientRect().top
      const limit = el.clientHeight * 0.5
      let best: HTMLElement | null = null
      let bestVis = 0
      for (const a of el.querySelectorAll<HTMLElement>('[id^="topic-"]')) {
        const vis = a.getBoundingClientRect().top - scTop
        if (vis <= limit) {
          best = a
          bestVis = vis
        }
      }
      if (best) {
        anchorId = best.id.slice(6)
        anchorVis = bestVis
      } else {
        anchorId = null
      }
      pendingRestore = true
    }
  },
  { flush: 'pre' },
)

// post flush：DOM 已更新 → 渲染稳定后按锚点恢复视口位置
watch(
  () => props.messages,
  async (list) => {
    const el = scroller.value
    const first = list[0]?.id ?? null
    if (el && pendingRestore && !compensating) {
      compensating = true
      try {
        // 等渲染稳定（锚点数达标 + 两帧）：锚点矩形此时已是可靠布局
        const wantAnchors = list.filter((m) => m.role === 'user').length
        for (let i = 0; i < 40; i++) {
          if (el.querySelectorAll('[id^="topic-"]').length >= wantAnchors) break
          await new Promise<void>((res) => requestAnimationFrame(() => res()))
        }
        await new Promise<void>((res) => requestAnimationFrame(() => res()))
        await new Promise<void>((res) => requestAnimationFrame(() => res()))
        if (anchorId) {
          const anchor = document.getElementById('topic-' + anchorId)
          if (anchor) {
            const scTop = el.getBoundingClientRect().top
            // 锚点内容位置 = 视口内位置 + 当前 scrollTop；恢复目标 = 内容位置 - 原视口内位置
            el.style.scrollBehavior = 'auto'
            el.scrollTop = anchor.getBoundingClientRect().top - scTop + el.scrollTop - anchorVis
            el.style.scrollBehavior = ''
          }
        }
        anchorId = null
        pendingRestore = false
      } finally {
        compensating = false
      }
    }
    prevFirstId = first
  },
  { flush: 'post' },
)

// 打字机揭示阶段只改 MessageItem 内部文本，不触发上方 watch；内容区变高且贴底时持续跟随
let observer: MutationObserver | null = null
let scrollRaf = 0

onMounted(() => {
  const el = scroller.value
  if (el) {
    el.addEventListener('wheel', cancelForceScroll, { passive: true })
    el.addEventListener('touchstart', cancelForceScroll, { passive: true })
    el.addEventListener('keydown', cancelForceScroll)
  }
  // 兜底贴底：immediate watch 在 setup 同步执行时 scroller 尚为 null（直接 return）。
  // 切回已缓存会话（messages 非空且之后不再变化）时，这里保证挂载后一定贴底；
  // 首次加载（空→非空）仍由 grewFromEmpty watch 触发。
  if (props.messages.length > 0) void scrollToBottomForce()
  observer = new MutationObserver(() => {
    if (scrollRaf) return
    scrollRaf = requestAnimationFrame(() => {
      scrollRaf = 0
      const el = scroller.value
      if (el && stickToBottom.value) el.scrollTop = el.scrollHeight
    })
  })
  if (scroller.value) {
    observer.observe(scroller.value, { childList: true, subtree: true, characterData: true, attributes: true })
  }
})

onUnmounted(() => {
  const el = scroller.value
  el?.removeEventListener('wheel', cancelForceScroll)
  el?.removeEventListener('touchstart', cancelForceScroll)
  el?.removeEventListener('keydown', cancelForceScroll)
  observer?.disconnect()
  if (scrollRaf) cancelAnimationFrame(scrollRaf)
})
</script>

<template>
  <div class="msg-wrap">
    <div ref="scroller" class="msg-list nc-scroll" @scroll="onScroll">
      <div v-if="messages.length === 0" class="welcome">
        <div class="hero">🚀 Next Chats</div>
        <p class="nc-dim">{{ t('chat.welcomeSlogan') }}</p>
        <p class="hint nc-dim">{{ t('chat.welcomeHint') }}</p>
      </div>

      <!-- 向上翻页占位（还有更早消息） -->
      <div v-if="messages.length > 0 && hasMoreBefore" class="older-hint nc-dim">
        <template v-if="loadingOlder">{{ t('chat.loadingOlder') }}</template>
        <template v-else>{{ t('chat.scrollUpForOlder') }}</template>
      </div>

      <MessageItem
        v-for="m in messages"
        :key="m.id"
        :message="m"
        @regenerate="(id: string) => emit('regenerate', id)"
        @remove="(msg: UiMessage) => emit('remove', msg)"
        @favorite="(msg: UiMessage) => emit('favorite', msg)"
      />
      <div style="height: 12px" />
    </div>
    <TopicRail :messages="messages" :topics="topics" :scroller="scroller" @jump-topic="(id: string) => emit('jump-topic', id)" />
  </div>
</template>

<style scoped>
.msg-wrap {
  position: relative;
  flex: 1;
  display: flex;
  min-height: 0;
}

.msg-list {
  flex: 1;
  overflow-y: auto;
  padding: 10px 4%;
  scroll-behavior: smooth;
  /* 回答框宽度 = 剩余窗口（聊天内容区）的 80%，随窗口大小动态缩放 */
  --nc-msg-w: 80%;
}

.older-hint {
  text-align: center;
  font-size: 12px;
  padding: 6px 0 10px;
  opacity: 0.8;
}

.welcome {
  text-align: center;
  margin-top: 16vh;
}

.hero {
  font-size: 42px;
  font-weight: 800;
  letter-spacing: 1px;
  background: linear-gradient(120deg, var(--nc-primary), #a78bfa);
  -webkit-background-clip: text;
  background-clip: text;
  color: transparent;
}

.hint {
  font-size: 12.5px;
}
</style>
