<script setup lang="ts">
import { computed, onMounted, onUnmounted, ref, watch } from 'vue'
import type { UiMessage } from '@/kernel/plugins'

/**
 * 话题导航条：消息区左侧垂直竖轨，一个话题（user 提问）一根横线。
 * 话题列表来自全量索引（props.topics），与当前已加载窗口解耦：
 *  - hover 显示话题标题（限字数）
 *  - 点击平滑滚动到对应话题；若该话题尚未加载（更早的消息），请求父组件补加载后自动跳转
 *  - 随滚动高亮当前话题（仅已加载到 DOM 的话题参与计算）
 */
const props = defineProps<{
  messages: UiMessage[]
  topics: { id: string; title: string }[]
  scroller: HTMLElement | null
}>()
const emit = defineEmits<{ 'jump-topic': [topicId: string] }>()

const activeIndex = ref(-1)
let pendingJumpId: string | null = null

function titleOf(tp: { id: string; title: string }): string {
  const s = (tp.title || '').replace(/\s+/g, ' ').trim()
  return s.length > 46 ? s.slice(0, 46) + '…' : s
}

/** 横条宽度：话题越多每条越短（14~44px），整体保持紧凑 */
function lineWidth(i: number): string {
  const n = props.topics.length
  const w = Math.max(14, Math.min(44, Math.round(320 / Math.max(1, n))))
  return w + 'px'
}

function scrollToTopic(topicId: string) {
  const sc = getScroller()
  if (!sc) return
  const el = document.getElementById('topic-' + topicId)
  if (!el) return
  // 只滚动消息列表（scroller）：scrollIntoView 会连带滚动侧栏/页面等所有祖先滚动容器（引发布局错乱）
  const top = el.getBoundingClientRect().top - sc.getBoundingClientRect().top + sc.scrollTop - 12
  sc.scrollTo({ top, behavior: 'smooth' })
}

// 滚动容器：优先用已绑定的 props.scroller；首帧（prop 尚为 null）兜底 DOM 查询
function getScroller(): HTMLElement | null {
  return boundScroller ?? document.querySelector<HTMLElement>('.msg-list')
}

function jump(i: number) {
  const tp = props.topics[i]
  if (!tp) return
  activeIndex.value = i // 立即高亮目标话题（滚动随后跟上）
  if (document.getElementById('topic-' + tp.id)) {
    scrollToTopic(tp.id)
  } else {
    // 话题尚未加载（更早消息）→ 请求补齐，DOM 出现后自动跳转
    pendingJumpId = tp.id
    emit('jump-topic', tp.id)
  }
}

// 补齐加载完成后：目标话题锚点出现 → 执行跳转。
// 注意：补齐加载同时会触发 MessageList 的锚点恢复补偿（把视口钉回原处）；
// 若在其完成前发起 smooth 滚动，会被恢复的 scrollTop 赋值打断而停在半途。
// 因此：等目标锚点出现后，再多等 ~45 帧（恢复流程上限约 40+2 帧）再跳转。
watch(
  () => props.messages.map((m) => m.id).join(','),
  async () => {
    if (!pendingJumpId) return
    const id = pendingJumpId
    let appeared = false
    for (let tries = 0; tries < 80; tries++) {
      if (document.getElementById('topic-' + id)) { appeared = true; break }
      await new Promise<void>((r) => requestAnimationFrame(() => r()))
    }
    if (!appeared) {
      pendingJumpId = null
      return
    }
    for (let w = 0; w < 45; w++) await new Promise<void>((r) => requestAnimationFrame(() => r()))
    pendingJumpId = null
    scrollToTopic(id)
  },
  { flush: 'post' },
)

let raf = 0
function onScroll() {
  if (raf) return
  raf = requestAnimationFrame(() => {
    raf = 0
    const sc = getScroller()
    if (!sc) return
    // 当前话题 = 视口 1/3 高度以上、最近的 user 提问（已加载到 DOM 的；位置一律按 scroller 内坐标计算）
    const scTop = sc.getBoundingClientRect().top
    const mid = sc.scrollTop + sc.clientHeight * 0.35
    let idx = -1
    for (let i = 0; i < props.topics.length; i++) {
      const el = document.getElementById('topic-' + props.topics[i].id)
      if (!el) continue
      const relTop = el.getBoundingClientRect().top - scTop + sc.scrollTop
      if (relTop <= mid) idx = i
    }
    activeIndex.value = idx
  })
}

// 话题数量变化（尾部流式追加 / 向上翻页前插）后按当前视口重算高亮，不强制位移
watch(
  () => props.topics.length,
  () => onScroll(),
  { immediate: true },
)

// 滚动监听绑定：props.scroller 在首帧渲染时为 null（父 ref 赋值晚于子组件 props 快照），
// 挂载后静态 addEventListener 会永久漏绑 → 用 watch 等父 re-render 后 prop 变为元素时再挂。
let boundScroller: HTMLElement | null = null
function bindScroller(sc: HTMLElement | null) {
  if (boundScroller === sc) return
  boundScroller?.removeEventListener('scroll', onScroll)
  boundScroller = sc ?? null
  sc?.addEventListener('scroll', onScroll, { passive: true })
}
watch(() => props.scroller, (sc) => bindScroller(sc), { immediate: true })

onUnmounted(() => {
  bindScroller(null)
  if (raf) cancelAnimationFrame(raf)
})
</script>

<template>
  <div v-if="topics.length > 1" class="topic-rail" aria-hidden="true">
    <div v-for="(tp, i) in topics" :key="tp.id" class="rail-line-wrap" @click="jump(i)">
      <el-tooltip :content="titleOf(tp)" placement="left" :show-after="300" :offset="8">
        <div
          class="rail-line"
          :class="{ active: i === activeIndex }"
          :style="{ '--rail-i': i, '--rail-w': lineWidth(i), animationDelay: -0.16 * i + 's' }"
        />
      </el-tooltip>
    </div>
  </div>
</template>

<style scoped>
.topic-rail {
  position: absolute;
  right: 6px;
  top: 50%;
  transform: translateY(-50%);
  /* 固定高度（但以包含块高度为上限）→ 垂直居中时永不向上穿出消息区、盖住顶栏头像；
     overflow → 滚动条必然出现，底部话题始终可达 */
  height: min(78vh, 640px, calc(100% - 10px));
  display: flex;
  flex-direction: column;
  gap: 10px;
  z-index: 6;
  padding: 6px 2px;
  pointer-events: auto;
  overflow-y: auto;
  scrollbar-width: thin;
}

/* 首尾弹性 spacer：话题少时竖轨内容垂直居中；话题很多时 spacer 收缩为 0，顶部/底部全程可滚动 */
.topic-rail::before,
.topic-rail::after {
  content: '';
  flex: 1 1 auto;
  min-height: 6px;
  flex-shrink: 1;
}

.topic-rail::-webkit-scrollbar {
  width: 3px;
}

.topic-rail::-webkit-scrollbar-thumb {
  background: var(--nc-border);
  border-radius: 2px;
}

.rail-line-wrap {
  cursor: pointer;
  line-height: 0;
  /* 加宽热区：横条最宽 44px，热区留足横向余量，方便 hover/点击 */
  width: 46px;
  /* 固定轨道高度：跳动动画（scaleY）不会挤压/推乱相邻话题的布局 */
  height: 14px;
  display: flex;
  align-items: center;
  justify-content: center;
}

/* 横条：水平条，宽度随话题数缩放（--rail-w 内联注入），hover 加长；高度做"音乐波纹"式节律跳动 */
.rail-line {
  flex-shrink: 0;
  height: 3px;
  width: var(--rail-w, 20px);
  border-radius: 2px;
  background: var(--nc-border);
  transform-origin: center;
  /* 跳动：3px → 约 13px 再回落；ease-in-out 对称波形，类似均衡器电平 */
  animation: rail-wave 1.6s ease-in-out infinite;
  /* 相位按行号错开（负 delay 立即铺开）→ 多条横线形成沿轨道自上而下流动的波纹 */
  animation-delay: calc(var(--rail-i, 0) * -0.16s);
  transition: width 0.18s ease, background 0.15s, box-shadow 0.15s;
}

@keyframes rail-wave {
  0%,
  100% {
    transform: scaleY(1);
  }
  50% {
    transform: scaleY(4.4);
  }
}

/* 用户系统偏好减少动态效果时，停止跳动（保留高亮/宽窄功能） */
@media (prefers-reduced-motion: reduce) {
  .rail-line {
    animation: none;
  }
}

/* hover（整行热区）：横条加长，更易看清与选中 */
.rail-line-wrap:hover .rail-line {
  width: min(calc(var(--rail-w, 20px) * 1.7), 46px);
  background: color-mix(in srgb, var(--nc-primary) 55%, transparent);
}

.rail-line.active {
  background: var(--nc-primary);
  box-shadow: 0 0 6px color-mix(in srgb, var(--nc-primary) 60%, transparent);
}

.rail-line-wrap:hover .rail-line.active {
  box-shadow: 0 0 8px color-mix(in srgb, var(--nc-primary) 70%, transparent);
}
</style>
