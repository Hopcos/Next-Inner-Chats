<script setup lang="ts">
/** Text Compare 工具页：行级 diff，逐行严格对齐双栏对比（仅增/删行着色，相同行无背景） */
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { diffLines } from 'diff'
import SplitPane from '../components/SplitPane.vue'
import { LS, loadSide, saveSide } from './config'

const { t } = useI18n()

const leftText = ref(loadSide(LS.left))
const rightText = ref(loadSide(LS.right))
const comparing = ref(false)

interface DiffPart {
  value: string
  added?: boolean
  removed?: boolean
}

type PairKind = 'same' | 'removed' | 'added'

interface DiffLinePair {
  left: string | null
  right: string | null
  leftNo: number
  rightNo: number
  kind: PairKind
}

/**
 * 规范化换行符：Windows(\r\n)/老 Mac(\r) 统一为 \n。
 * diff 按行比较时若一侧是 \r\n、另一侧是 \n，每一行都会因多出的 \r 被判为不同，
 * 导致"所有行永远不一致"——这是逐行对比最常见的陷阱。
 */
function normalizeLines(s: string): string {
  return s.replace(/\r\n?/g, '\n')
}

const diffResult = computed<DiffPart[] | null>(() => {
  if (!comparing.value) return null
  return diffLines(normalizeLines(leftText.value), normalizeLines(rightText.value))
})

/**
 * 把 diff 块拆成"行对"：每一对 = 左侧一行 ↔ 右侧一行（grid 同行两格，天然严格对齐）。
 * 相同行左右同显且无背景；删除行只显左（红）；新增行只显右（绿）。
 * leftNo/rightNo 为各自输入侧的真实行号，确保"第 N 行对第 N 行"透明可见。
 */
const diffPairs = computed<DiffLinePair[]>(() => {
  const parts = diffResult.value
  if (!parts) return []
  const pairs: DiffLinePair[] = []
  let ln = 0
  let rn = 0
  for (const p of parts) {
    const lines = p.value.replace(/\n$/, '').split('\n')
    if (p.added) {
      for (const line of lines) {
        rn++
        pairs.push({ left: null, right: line, leftNo: 0, rightNo: rn, kind: 'added' })
      }
    } else if (p.removed) {
      for (const line of lines) {
        ln++
        pairs.push({ left: line, right: null, leftNo: ln, rightNo: 0, kind: 'removed' })
      }
    } else {
      for (const line of lines) {
        ln++
        rn++
        pairs.push({ left: line, right: line, leftNo: ln, rightNo: rn, kind: 'same' })
      }
    }
  }
  return pairs
})

const stats = computed(() => ({
  added: diffPairs.value.filter((p) => p.kind === 'added').length,
  removed: diffPairs.value.filter((p) => p.kind === 'removed').length,
}))

function compare() {
  saveSide(LS.left, leftText.value)
  saveSide(LS.right, rightText.value)
  comparing.value = true
}

/** 返回编辑：仅退出对比视图，保留两侧内容（不清除） */
function backToEdit() {
  comparing.value = false
}
</script>

<template>
  <div class="tc-page">
    <div class="tc-bar">
      <el-button v-if="!comparing" size="small" type="primary" @click="compare">
        <svg class="tc-ico" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 2.5 13 8l-9 5.5v-11Z" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round" /></svg>
        {{ t('tools.compare.run') }}
      </el-button>
      <el-button v-else size="small" @click="backToEdit">
        <svg class="tc-ico" viewBox="0 0 16 16" aria-hidden="true"><path d="M3 13h10M5.5 13l-2-2 6.7-6.7a1.2 1.2 0 0 1 1.7 0l.3.3a1.2 1.2 0 0 1 0 1.7L5.5 13" fill="none" stroke="currentColor" stroke-width="1.4" stroke-linejoin="round" /></svg>
        {{ t('tools.compare.backToEdit') }}
      </el-button>
      <div v-if="comparing" class="tc-stats">
        <span v-if="stats.added === 0 && stats.removed === 0" class="tc-stat same">{{ t('tools.compare.identical') }}</span>
        <template v-else>
          <span class="tc-stat add">+{{ stats.added }} {{ t('tools.compare.added') }}</span>
          <span class="tc-stat del">−{{ stats.removed }} {{ t('tools.compare.removed') }}</span>
        </template>
      </div>
      <span v-if="comparing" class="tc-hint">{{ t('tools.compare.hint') }}</span>
    </div>

    <div class="tc-split">
      <!-- 编辑态：左右分栏输入 -->
      <SplitPane v-if="!comparing" :left-title="t('tools.compare.original')" :right-title="t('tools.compare.changed')">
        <template #left>
          <textarea v-model="leftText" class="tc-area mono" spellcheck="false" :placeholder="t('tools.compare.leftPlaceholder')" />
        </template>
        <template #right>
          <textarea v-model="rightText" class="tc-area mono" spellcheck="false" :placeholder="t('tools.compare.rightPlaceholder')" />
        </template>
      </SplitPane>

      <!-- 对比态：单一完整双列对照表（逐行严格对齐，同一行左右两格并排，行号一一对应） -->
      <div v-else class="tc-compare">
        <div class="tc-compare-head">
          <span class="tc-col-title">{{ t('tools.compare.original') }}</span>
          <span class="tc-col-title">{{ t('tools.compare.changed') }}</span>
        </div>
        <div class="tc-compare-body nc-scroll">
          <!-- 表格布局：行/列锁定，左右两格永远并排同一行，绝不被挤到下一行 -->
          <table v-if="diffPairs.length" class="tc-table">
            <tbody>
              <tr v-for="(pair, i) in diffPairs" :key="i">
                <!-- 相同行：无背景；不一致行：左边红色 / 右边绿色 -->
                <td class="tc-cell" :class="{ 'tc-removed': pair.kind === 'removed' }">
                  <span class="tc-ln" aria-hidden="true">{{ pair.leftNo || '' }}</span><span class="tc-text">{{ pair.left ?? '' }}</span>
                </td>
                <td class="tc-cell" :class="{ 'tc-added': pair.kind === 'added' }">
                  <span class="tc-ln" aria-hidden="true">{{ pair.rightNo || '' }}</span><span class="tc-text">{{ pair.right ?? '' }}</span>
                </td>
              </tr>
            </tbody>
          </table>
          <div v-else class="tc-none">{{ t('tools.compare.identical') }}</div>
        </div>
      </div>
    </div>
  </div>
</template>

<style scoped>
.tc-page {
  flex: 1;
  height: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
  padding: 18px 22px 20px;
  gap: 14px;
  overflow: hidden;
}

.tc-bar {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.tc-ico {
  width: 12px;
  height: 12px;
  margin-right: 4px;
  vertical-align: -1px;
}

.tc-stats {
  display: flex;
  gap: 8px;
}

.tc-stat {
  font-size: 12px;
  border-radius: 999px;
  padding: 2px 10px;
  font-weight: 600;
}

.tc-stat.add {
  color: #16a34a;
  background: color-mix(in srgb, #22c55e 14%, transparent);
}

.tc-stat.same {
  color: #16a34a;
  background: color-mix(in srgb, #22c55e 14%, transparent);
}

.tc-stat.del {
  color: #dc2626;
  background: color-mix(in srgb, #ef4444 12%, transparent);
}

.tc-hint {
  margin-left: auto;
  font-size: 12px;
  color: var(--nc-text-dim, #8a94a6);
}

/* 必须 display:flex column：对比态 .tc-compare 直接挂在 .tc-split 下，若保持 block 容器，
   其 flex:1/min-height:0 全部失效、高度=内容高度(大表格 3 万 px)，被 .tc-page 的 overflow:hidden
   裁掉且无滚动条。flex column 让对比体可收缩并在 .tc-compare-body 内滚动。 */
.tc-split {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
}

.tc-area {
  flex: 1;
  width: 100%;
  border: 0;
  outline: none;
  resize: none;
  background: #ffffff;
  color: #1e293b;
  padding: 14px 16px;
  font-size: 13px;
  line-height: 1.65;
}

.mono {
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
}

.tc-area::placeholder {
  color: #94a3b8;
  opacity: 0.8;
}

.tc-compare {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  background: #ffffff;
  border: 1px solid #e2e8f0;
  border-radius: 12px;
  overflow: hidden;
}

.tc-compare-head {
  display: grid;
  grid-template-columns: 1fr 1fr;
  border-bottom: 1px solid #eef2f7;
  background: #f8fafc;
  padding-left: 10px;
  padding-right: 10px;
}

.tc-col-title {
  padding: 8px 8px;
  font-size: 12.5px;
  font-weight: 700;
  color: #64748b;
  letter-spacing: 0.02em;
}

/* 对照表：table 布局，行/列锁定，左右两格永远并排（不存在被挤到下一行的可能） */
/* min-height:0 必须：flex 子项默认 min-height:auto，行数超大时会撑开自身而不是收缩滚动，
   结果是内容被父级 overflow:hidden 裁掉、滚动条不出现（对比结果"超窗且两侧拖不动"）。 */
.tc-compare-body {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 10px;
  font-size: 12.5px;
  line-height: 1.7;
  color: #334155;
}

.tc-table {
  width: 100%;
  table-layout: fixed;
  border-collapse: separate;
  border-spacing: 0;
}

.tc-table td {
  vertical-align: top;
  padding: 1px 10px;
  border-radius: 4px;
  min-width: 0;
}

/* 左列与右列之间留出 26px 空隙：左右内容彻底分开，无论背景色/折行都不会粘连或错觉错位 */
.tc-table td:first-child {
  padding-right: 26px;
}

.tc-table td:last-child {
  padding-left: 12px;
}

/* 行号：各自输入侧的真实行号，右对齐灰色小字 */
.tc-ln {
  display: inline-block;
  min-width: 34px;
  margin-right: 10px;
  text-align: right;
  color: #cbd5e1;
  font-size: 11.5px;
  line-height: inherit;
  user-select: none;
  font-variant-numeric: tabular-nums;
}

.tc-text {
  white-space: pre-wrap;
  word-break: break-all;
  overflow-wrap: anywhere;
}

/* 相同行：无背景色标识（仅文本呈现） */
/* 不一致行：左红右绿 */
.tc-added {
  background: color-mix(in srgb, #22c55e 18%, transparent);
  color: #14532d;
}

.tc-removed {
  background: color-mix(in srgb, #ef4444 16%, transparent);
  color: #7f1d1d;
}

.tc-none {
  padding: 20px;
  text-align: center;
  color: var(--nc-text-dim, #8a94a6);
  font-size: 12.5px;
}
</style>
