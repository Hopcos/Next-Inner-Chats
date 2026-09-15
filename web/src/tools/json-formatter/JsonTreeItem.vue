<script setup lang="ts">
/** JSON 折叠树的行（单行渲染；行序列与行号由父组件扁平化生成） */
import { computed } from 'vue'

export interface JsonNode {
  /** 唯一 id（Vue key） */
  id: number
  /** 键名（数组项/根节点为空串） */
  key: string
  type: 'object' | 'array' | 'scalar'
  /** 标量原文（字符串带引号；数字/bool/null 直接） */
  raw: string
  children: JsonNode[]
  collapsed: boolean
  /** 是否为父级中最后一项（决定是否输出行尾逗号） */
  last: boolean
}

const props = defineProps<{
  /** 数据行节点；close 行为 null */
  node?: JsonNode | null
  /** 结束行类型（节点行的 close 为 null） */
  close?: 'object' | 'array' | null
  depth: number
  /** 行号（可见行序，1 起） */
  line: number
  /** 本行是否父级最后项（决定行尾逗号） */
  last: boolean
  /** 每级缩进像素（跟随"缩进"设置：2空格=14 / 4空格与Tab=28） */
  indentPx: number
}>()

/** 可折叠节点（对象/数组且有子项）；标量与空容器返回 null 不渲染三角 */
const toggleNode = computed(() => {
  const n = props.node
  if (n && (n.type === 'object' || n.type === 'array') && n.children.length > 0) return n
  return null
})

const scalarClass = computed(() => {
  const r = props.node?.raw ?? ''
  if (r.startsWith('"')) return 'jq-str'
  if (r === 'true' || r === 'false' || r === 'null') return 'jq-bool'
  return 'jq-num'
})

/** 键名文本：始终带 JSON 引号并正确转义（保证与合法 JSON 一致） */
const keyText = computed(() => (props.node?.key ? JSON.stringify(props.node.key) : ''))

function toggle() {
  if (props.node) props.node.collapsed = !props.node.collapsed
}
</script>

<template>
  <div class="jt-row">
    <!-- 行号栏：左侧固定（横向滚动不随数据滚走），内含 [数字行号][折叠三角] -->
    <div class="jt-ln">
      <span class="jt-num">{{ line }}</span>
      <span class="jt-fold">
        <button v-if="toggleNode" class="jt-toggle" :class="{ collapsed: toggleNode.collapsed }" :aria-label="toggleNode.collapsed ? 'expand' : 'collapse'" @click="toggle">
          <svg viewBox="0 0 10 10" width="9" height="9" aria-hidden="true"><path d="M2 1.5 7.5 5 2 8.5Z" fill="currentColor" /></svg>
        </button>
      </span>
    </div>

    <!-- 数据区：缩进从 0 起，宽度跟随"缩进"设置，小三角不参与缩进 -->
    <div class="jt-data" :style="{ paddingLeft: depth * indentPx + 'px' }">
      <template v-if="node">
        <template v-if="node.key">
          <span class="jq-key">{{ keyText }}</span><span class="jq-punc">: </span>
        </template>
        <template v-if="node.type === 'scalar'">
          <span :class="scalarClass">{{ node.raw }}</span><span class="jq-punc">{{ last ? '' : ',' }}</span>
        </template>
        <template v-else>
          <span class="jq-punc">{{ node.collapsed ? (node.type === 'object' ? '{…}' : '[…]') : (node.type === 'object' ? '{' : '[') }}</span>
          <span class="jq-punc">{{ last ? '' : ',' }}</span>
        </template>
      </template>
      <template v-else>
        <span class="jq-punc">{{ close === 'array' ? ']' : '}' }}</span><span class="jq-punc">{{ last ? '' : ',' }}</span>
      </template>
    </div>
  </div>
</template>

<style scoped>
.jt-row {
  display: flex;
  align-items: stretch;
  line-height: 1.6;
  white-space: pre;
}

.jt-data {
  flex: 1 1 auto;
  min-width: 0;
  overflow: hidden;
  text-overflow: clip;
}

/* 行号栏：贴左固定；内含 [数字][折叠三角]，不进入数据区 */
.jt-ln {
  position: sticky;
  left: 0;
  z-index: 1;
  flex-shrink: 0;
  display: flex;
  align-items: center;
  width: 74px;
  height: 100%;
  background: #ffffff;
  border-right: 1px solid #eef1f5;
  user-select: none;
}

.jt-num {
  flex: 1;
  text-align: right;
  padding-right: 6px;
  color: var(--nc-text-dim, #8a94a6);
  font-size: 12px;
}

/* 折叠三角：行号数字右侧、行号栏内 */
.jt-fold {
  flex-shrink: 0;
  width: 20px;
  display: flex;
  align-items: center;
  justify-content: center;
}

.jt-toggle {
  width: 16px;
  height: 16px;
  padding: 0;
  border: 0;
  background: transparent;
  color: var(--nc-text-dim, #8a94a6);
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  border-radius: 4px;
  line-height: 1;
}

.jt-toggle svg {
  transition: transform 0.12s;
}

.jt-toggle.collapsed svg {
  transform: rotate(-90deg);
}

.jt-toggle:hover {
  background: color-mix(in srgb, var(--nc-primary) 14%, transparent);
  color: var(--nc-primary);
}

/* JSON 语法高亮（与编辑页配色一致） */
.jq-key {
  color: #0550ae;
}

.jq-str {
  color: #22863a;
}

.jq-num {
  color: #953800;
}

.jq-bool {
  color: #cf222e;
}

.jq-punc {
  color: #57606a;
}
</style>
