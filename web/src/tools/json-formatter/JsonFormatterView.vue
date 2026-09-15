<script setup lang="ts">
/** JSON Formatter 工具页（移植自 dev-tools，适配当前系统 UI） */
import { computed, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'
import { copyText } from '@/utils/clipboard'
import SplitPane from '../components/SplitPane.vue'
import JsonTreeItem, { type JsonNode } from './JsonTreeItem.vue'
import { LS, highlightJson, loadIndent, loadInput } from './config'

const { t } = useI18n()

const input = ref(loadInput())
const output = ref('')
const error = ref<string | null>(null)
/** 结果视图：tree=可折叠树（格式化）；minified=压缩单行文本 */
const viewMode = ref<'tree' | 'minified'>('tree')
const indent = ref<'2' | '4' | 'tab'>(String(loadIndent()) as '2' | '4')
if (indent.value !== '2' && indent.value !== '4') indent.value = '2'

/** 压缩视图：单行 JSON 语法高亮 */
const outputHtml = computed(() => highlightJson(output.value))

/** 每级缩进像素（树视图）：2空格=14 / 4空格与Tab=28（等宽字体下约 2/4 个字符宽） */
const indentPx = computed(() => (indent.value === '2' ? 14 : 28))

/** 折叠树（reactive：节点 collapsed 修改即时生效） */
const tree = ref<JsonNode[]>([])

/** 扁平可见行（含行号）：折叠中的子树不产出行；行号=可见行序 */
interface TreeRow {
  node: JsonNode | null
  close: 'object' | 'array' | null
  depth: number
  line: number
  last: boolean
}

const rows = computed<TreeRow[]>(() => {
  const out: TreeRow[] = []
  const walk = (nodes: JsonNode[], depth: number) => {
    for (const n of nodes) {
      out.push({ node: n, close: null, depth, line: 0, last: n.last })
      if (!n.collapsed && n.children.length) {
        walk(n.children, depth + 1)
        out.push({ node: null, close: n.type === 'scalar' ? null : n.type, depth, line: 0, last: n.last })
      }
    }
  }
  walk(tree.value, 0)
  out.forEach((r, i) => (r.line = i + 1))
  return out
})

let nodeSeq = 0
function buildNode(key: string, value: unknown, last: boolean): JsonNode {
  if (Array.isArray(value)) {
    return {
      id: nodeSeq++, key, type: 'array', raw: '',
      children: value.map((v, i) => buildNode('', v, i === value.length - 1)),
      collapsed: false, last,
    }
  }
  if (value !== null && typeof value === 'object') {
    const entries = Object.entries(value)
    return {
      id: nodeSeq++, key, type: 'object', raw: '',
      children: entries.map(([k, v], i) => buildNode(k, v, i === entries.length - 1)),
      collapsed: false, last,
    }
  }
  const raw = value === null ? 'null' : typeof value === 'string' ? JSON.stringify(value) : String(value)
  return { id: nodeSeq++, key, type: 'scalar', raw, children: [], collapsed: false, last }
}

function buildTree(parsed: unknown): JsonNode[] {
  nodeSeq = 0
  return [buildNode('', parsed, true)]
}

function indentWidth(): string | number {
  return indent.value === 'tab' ? '\t' : Number(indent.value)
}

function onIndentChange(v: string) {
  try {
    localStorage.setItem(LS.indent, v === 'tab' ? '0' : v)
  } catch {
    /* ignore */
  }
  format()
}

function format() {
  try {
    const parsed: unknown = JSON.parse(input.value)
    output.value = JSON.stringify(parsed, null, indentWidth())
    tree.value = buildTree(parsed)
    viewMode.value = 'tree'
    error.value = null
    try {
      localStorage.setItem(LS.input, input.value)
    } catch {
      /* ignore */
    }
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Invalid JSON'
    output.value = ''
    tree.value = []
  }
}

function minify() {
  try {
    const parsed: unknown = JSON.parse(input.value)
    output.value = JSON.stringify(parsed)
    tree.value = buildTree(parsed)
    viewMode.value = 'minified'
    error.value = null
  } catch (e) {
    error.value = e instanceof Error ? e.message : 'Invalid JSON'
    output.value = ''
    tree.value = []
  }
}

function setAllCollapsed(nodes: JsonNode[], collapsed: boolean) {
  for (const n of nodes) {
    n.collapsed = collapsed
    setAllCollapsed(n.children, collapsed)
  }
}

/** 全部展开（collapse open 语义：展开所有可折叠点） */
function expandAll() {
  setAllCollapsed(tree.value, false)
}

/** 全部折叠：根以下全部折叠；根保持展开以便查看 */
function collapseAll() {
  for (const root of tree.value) {
    setAllCollapsed(root.children, true)
  }
}

async function copyOutput() {
  if (!output.value) return
  const ok = await copyText(output.value)
  if (ok) kernel.notify.success(t('tools.common.copied'))
  else kernel.notify.error(t('tools.common.copyFailed'))
}
</script>

<template>
  <div class="jf-page">
    <div class="jf-bar">
      <div class="jf-bar-right">
        <el-radio-group v-model="indent" size="small" @change="(v: string) => onIndentChange(v)">
          <el-radio-button value="2">2 {{ t('tools.json.spaces') }}</el-radio-button>
          <el-radio-button value="4">4 {{ t('tools.json.spaces') }}</el-radio-button>
          <el-radio-button value="tab">Tab</el-radio-button>
        </el-radio-group>
        <span v-if="viewMode === 'tree' && tree.length > 0 && !error" class="jf-ops">
          <el-button size="small" text @click="expandAll">{{ t('tools.json.expandAll') }}</el-button>
          <el-button size="small" text @click="collapseAll">{{ t('tools.json.collapseAll') }}</el-button>
        </span>
        <el-button size="small" @click="minify">{{ t('tools.json.minify') }}</el-button>
        <el-button size="small" type="primary" @click="format">
          <svg class="jf-ico" viewBox="0 0 16 16" aria-hidden="true"><path d="M4 2.5 13 8l-9 5.5v-11Z" fill="none" stroke="currentColor" stroke-width="1.6" stroke-linejoin="round" /></svg>
          {{ t('tools.json.format') }}
        </el-button>
      </div>
    </div>

    <div class="jf-split">
      <SplitPane :left-title="t('tools.json.raw')" :right-title="t('tools.json.out')">
        <template #left>
          <textarea v-model="input" class="jf-area mono" spellcheck="false" :placeholder="t('tools.json.rawPlaceholder')" />
        </template>
        <template #right>
          <div v-if="error" class="jf-error">
            <svg viewBox="0 0 24 24" width="18" height="18" fill="none" stroke="currentColor" stroke-width="1.8" stroke-linecap="round" aria-hidden="true">
              <circle cx="12" cy="12" r="9" />
              <path d="M12 7.5V13" />
              <path d="M12 16.5v.01" />
            </svg>
            <pre class="jf-error-text">{{ error }}</pre>
          </div>
          <div v-else-if="viewMode === 'tree' && tree.length > 0" class="jf-tree mono nc-scroll">
            <JsonTreeItem v-for="r in rows" :key="r.node ? r.node.id + (r.close ? ':c' : '') : 'close-' + r.depth + '-' + r.line" :node="r.node" :close="r.close" :depth="r.depth" :line="r.line" :last="r.last" :indent-px="indentPx" />
          </div>
          <div v-else-if="viewMode === 'minified' && output" class="jf-out mono nc-scroll" v-html="outputHtml" />
          <div v-else class="jf-empty mono nc-dim">{{ t('tools.json.outPlaceholder') }}</div>
          <footer v-if="output && !error" class="jf-foot">
            <span class="jf-meta">{{ output.split('\n').length }} {{ t('tools.json.lines') }}</span>
            <button class="jf-copy" @click="copyOutput">{{ t('tools.json.copyOut') }}</button>
          </footer>
        </template>
      </SplitPane>
    </div>
  </div>
</template>

<style scoped>
.jf-page {
  flex: 1;
  height: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
  padding: 18px 22px 20px;
  gap: 14px;
  overflow: hidden;
}

.jf-bar {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

/* 全部展开/全部折叠：紧凑排布 */
.jf-ops {
  display: flex;
  gap: 0;
}

.jf-ops :deep(.el-button + .el-button) {
  margin-left: 0;
}

.jf-bar-right {
  margin-left: auto;
  display: flex;
  align-items: center;
  gap: 8px;
}

.jf-ico {
  width: 12px;
  height: 12px;
  margin-right: 4px;
  vertical-align: -1px;
}

.jf-split {
  flex: 1;
  min-height: 0;
}

.jf-area {
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

.jf-area::placeholder {
  color: #94a3b8;
  opacity: 0.8;
}

/* 结果区：可折叠树（展开视图） */
.jf-tree {
  flex: 1;
  overflow: auto;
  margin: 0;
  padding: 12px 14px;
  font-size: 13px;
  background: #ffffff;
  color: #334155;
  tab-size: 2;
}

/* 结果区：压缩视图（单行 JSON 语法高亮文本） */
.jf-out {
  flex: 1;
  overflow: auto;
  margin: 0;
  padding: 14px 16px;
  font-size: 13px;
  line-height: 1.65;
  background: #ffffff;
  color: #334155;
  white-space: pre;
  word-break: break-all;
}

.jf-out :deep(.jq-key) {
  color: #0550ae;
}

.jf-out :deep(.jq-str) {
  color: #22863a;
}

.jf-out :deep(.jq-num) {
  color: #953800;
}

.jf-out :deep(.jq-bool) {
  color: #cf222e;
}

.jf-out :deep(.jq-punc) {
  color: #57606a;
}

.jf-empty {
  flex: 1;
  display: flex;
  align-items: center;
  justify-content: center;
  font-size: 13px;
}

.jf-error {
  flex: 1;
  display: flex;
  gap: 10px;
  align-items: flex-start;
  padding: 16px;
  color: #b91c1c;
  background: #fef2f2;
  border: 1px solid #fee2e2;
  border-radius: 10px;
  margin: 12px;
}

.jf-error svg {
  flex-shrink: 0;
  margin-top: 2px;
}

.jf-error-text {
  margin: 0;
  white-space: pre-wrap;
  word-break: break-all;
  font-size: 12.5px;
  line-height: 1.6;
  font-family: Consolas, Menlo, monospace;
}

.jf-foot {
  display: flex;
  align-items: center;
  justify-content: space-between;
  padding: 6px 12px;
  border-top: 1px solid var(--nc-border);
}

.jf-meta {
  font-size: 12px;
  color: var(--nc-text-dim, #8a94a6);
}

.jf-copy {
  border: 0;
  background: transparent;
  color: var(--nc-primary);
  font-size: 12.5px;
  cursor: pointer;
  padding: 3px 8px;
  border-radius: 6px;
}

.jf-copy:hover {
  background: color-mix(in srgb, var(--nc-primary) 12%, transparent);
}
</style>
