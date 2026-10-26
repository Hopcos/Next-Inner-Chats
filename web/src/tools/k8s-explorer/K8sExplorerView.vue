<script setup lang="ts">
/**
 * K8S Explorer：浏览 Kubernetes Pod（k8s-mcp-server REST 网关，只读）。
 * 上部查询条件（environment / 状态 / 名称过滤），与 Kafka Explorer 一致保持联动：
 *   environment 下拉来自 /api/environments；切换 environment 级联重载 Pod 列表；
 *   状态下拉选项由当前环境已加载 Pod 的去重状态实时派生（随数据联动），选择后客户端过滤。
 * 下部结果表格列对齐 k8s-monitor 的 Pods 页：NAME / READY / STATUS / RESTARTS / AGE / IP / NODE / CPU / MEMORY / IMAGE。
 * 点击 Pod 行弹出文件浏览器：/files 列目录 → /files/content 查看文本内容。
 * 端点地址来自管理端「工具 → API Endpoint」（AppTool.BaseUrl），授权头由管理端「工具 → 授权头」配置，
 * 后端 /api/ext/tool/k8s-explorer/** 同源代理转发时自动附加；前端经该代理访问。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { downloadBlob, http } from '@/api/http'
import { kernel } from '@/kernel'
import { copyText } from '@/utils/clipboard'
import { highlightJson } from './jsonHighlight'

const { t } = useI18n()
const TOOL_KEY = 'k8s-explorer'

// ---------------- 端点配置（后台 AppTools.BaseUrl + 授权头，经后端代理访问） ----------------
async function loadBaseUrl() {
  try {
    const list = await http.get<{ key: string; baseUrl?: string | null }[]>('/api/me/tools')
    const me = list.find((x) => x.key === TOOL_KEY)
    if (me?.baseUrl) cfgOk.value = true
  } catch {
    /* 忽略：下方按未配置提示 */
  }
}

const cfgOk = ref(false)
const endpoint = '/api/ext/tool/k8s-explorer'

// ---------------- 数据模型（swagger v1 结构） ----------------
interface EnvironmentInfo {
  name: string
  cluster: string
  namespace: string
  toolDescription?: string | null
}
interface PodDetails {
  name: string
  ready: string
  status: string
  restarts: number
  age: string
  ip: string
  node: string
  cpuCurrent: string
  cpuLimit: string
  memoryCurrent: string
  memoryLimit: string
  imageUrl: string
}
interface FileEntry {
  name: string
  isDir: boolean
  size: number
}

// ---------------- 查询条件 ----------------
const envs = ref<string[]>([])
const envMeta = ref<Record<string, EnvironmentInfo>>({})
const loadingMeta = ref(false)

const environment = ref('')
const statusFilter = ref('') // '' = 全部；选项由当前数据派生
const nameFilter = ref('')
const loading = ref(false)
const pods = ref<PodDetails[]>([])
const err = ref('')

/** 状态标签配色（对齐 k8s-monitor PodsPage 语义，使用 Element Plus tag 合法值） */
const STATUS_COLORS: Record<string, string> = {
  Running: 'success',
  Pending: 'warning',
  Succeeded: 'primary',
  Failed: 'danger',
  Unknown: 'info',
  CrashLoopBackOff: 'danger',
  ImagePullBackOff: 'warning',
  OOMKilled: 'danger',
  Error: 'danger',
}
const statusType = (s: string) => STATUS_COLORS[s] || 'info'

/** 状态下拉：当前环境已加载 Pod 的去重状态（与数据联动，同 Kafka 的级联下拉） */
const statusOptions = computed(() => [...new Set(pods.value.map((p) => p.status).filter(Boolean))].sort())
const filteredPods = computed(() =>
  statusFilter.value ? pods.value.filter((p) => p.status === statusFilter.value) : pods.value,
)

async function loadEnvs() {
  if (!cfgOk.value) return
  loadingMeta.value = true
  try {
    const r = await http.get<any>(`${endpoint}/api/environments`)
    const list: EnvironmentInfo[] = Array.isArray(r) ? r : r?.environments ?? []
    const names = list.map((e) => e.name)
    envMeta.value = Object.fromEntries(list.map((e) => [e.name, e]))
    envs.value = names
    if (names.length && !names.includes(environment.value)) environment.value = names[0]
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('tools.k8s.metaLoadFailed'), (e as { code?: string }).code)
  } finally {
    loadingMeta.value = false
  }
}

async function loadPods() {
  if (!cfgOk.value || !environment.value) {
    pods.value = []
    return
  }
  loading.value = true
  err.value = ''
  try {
    const q = nameFilter.value.trim() ? `?nameFilter=${encodeURIComponent(nameFilter.value.trim())}` : ''
    const r = await http.get<any>(`${endpoint}/api/environments/${encodeURIComponent(environment.value)}/pods/detailed${q}`)
    pods.value = Array.isArray(r) ? r : r?.pods ?? []
    // 状态筛选随新数据联动：若原选值已不存在则回到「全部」
    if (statusFilter.value && !pods.value.some((p) => p.status === statusFilter.value)) statusFilter.value = ''
    if (!pods.value.length) err.value = t('tools.k8s.empty')
  } catch (e) {
    pods.value = []
    err.value = (e as { message?: string }).message ?? t('tools.k8s.queryFailed')
  } finally {
    loading.value = false
  }
}

// 联动：环境切换（或环境可用后自动选中）→ 重载 Pod 列表
watch(environment, () => {
  if (environment.value) loadPods()
})

watch(nameFilter, () => {
  // 名称过滤为服务端过滤：输入停顿后自动查询，保持下游联动
  debounceName()
})

let nameTimer: ReturnType<typeof setTimeout> | null = null
function debounceName() {
  if (nameTimer) clearTimeout(nameTimer)
  nameTimer = setTimeout(() => {
    if (environment.value) loadPods()
  }, 400)
}

// ---------------- Pod 文件浏览器（/files → /files/content） ----------------
const fbOpen = ref(false)
const fbPod = ref('')
const fbPath = ref('/')
const fbParent = ref('')
const fbEntries = ref<FileEntry[]>([])
const fbLoading = ref(false)
const fbErr = ref('')
const fbDirCount = computed(() => fbEntries.value.filter((e) => e.isDir).length)

function openFileBrowser(pod: PodDetails) {
  fbPod.value = pod.name
  fbPath.value = '/'
  fbParent.value = ''
  fbEntries.value = []
  fbErr.value = ''
  fbOpen.value = true
  loadDir('/')
}

async function loadDir(path: string) {
  if (!fbPod.value) return
  fbLoading.value = true
  fbErr.value = ''
  try {
    const clean = path === '/' ? '' : path.replace(/^\/+|\/+$/g, '')
    const r = await http.get<any>(
      `${endpoint}/api/environments/${encodeURIComponent(environment.value)}/pods/${encodeURIComponent(fbPod.value)}/files${clean ? `?path=${encodeURIComponent(clean)}` : ''}`,
    )
    const d = Array.isArray(r) ? { path: '', parentPath: '', entries: r } : r
    fbPath.value = d.path ?? path
    fbParent.value = d.parentPath ?? ''
    fbEntries.value = Array.isArray(d.entries) ? d.entries : []
  } catch (e) {
    fbErr.value = (e as { message?: string }).message ?? t('tools.k8s.filesLoadFailed')
  } finally {
    fbLoading.value = false
  }
}

/** 点击条目：目录进入下一层；文件弹窗查看文本内容 */
function onEntryClick(entry: FileEntry) {
  if (entry.isDir) {
    loadDir(fbPath.value === '/' ? `/${entry.name}` : `${fbPath.value}/${entry.name}`)
  } else {
    viewFile(entry.name)
  }
}

// ---------------- 文件内容查看（/files/content，JSON 高亮展示） ----------------
const contentOpen = ref(false)
const contentMeta = ref('')
const contentIsJson = ref(false)
const contentPretty = ref('')
const contentHtml = ref('')
const contentRaw = ref('')
const contentErr = ref('')
/** 当前弹窗查看的文件完整路径（下载按钮使用） */
const contentPath = ref('')

async function viewFile(name: string) {
  contentOpen.value = true
  contentPath.value = fbPath.value === '/' ? `/${name}` : `${fbPath.value}/${name}`
  contentMeta.value = `${fbPod.value} · ${fbPath.value === '/' ? '/' : fbPath.value}/${name}`
  contentHtml.value = ''
  contentPretty.value = ''
  contentRaw.value = ''
  contentErr.value = ''
  const p = fbPath.value === '/' ? `/${name}` : `${fbPath.value}/${name}`
  try {
    const r = await http.get<any>(
      `${endpoint}/api/environments/${encodeURIComponent(environment.value)}/pods/${encodeURIComponent(fbPod.value)}/files/content?path=${encodeURIComponent(p)}&maxBytes=200000`,
    )
    const c = Array.isArray(r) ? { content: (r as unknown as string) } : r
    if (c.exists === false || (!c.content && c.error)) {
      contentErr.value = c.error || t('tools.k8s.contentEmpty')
      return
    }
    const text: string = c.content ?? ''
    contentRaw.value = text
    try {
      const parsed: unknown = JSON.parse(text)
      contentPretty.value = JSON.stringify(parsed, null, 2)
      contentHtml.value = highlightJson(contentPretty.value)
      contentIsJson.value = true
    } catch {
      contentPretty.value = ''
      contentHtml.value = ''
      contentIsJson.value = false
    }
  } catch (e) {
    contentErr.value = (e as { message?: string }).message ?? t('tools.k8s.contentLoadFailed')
  }
}

/** 弹窗复制：JSON 可解析时复制格式化后的展示文本（所见即所得），否则复制原文 */
async function onCopyContent() {
  const text = contentIsJson.value ? contentPretty.value : contentRaw.value
  const ok = await copyText(text)
  if (ok) kernel.notify.success(t('tools.common.copied'))
  else kernel.notify.error(t('tools.common.copyFailed'))
}

// ---------------- 文件下载（/files/download，二进制原样透传） ----------------
const downloadingPath = ref('')

/** 下载 Pod 内文件：以 Blob 取回后触发浏览器保存（带 JWT，不能直接用 <a href>）；文件名取响应头，缺省用路径末段 */
async function downloadFile(path: string) {
  if (!fbPod.value || !environment.value || downloadingPath.value) return
  downloadingPath.value = path
  try {
    const url = `${endpoint}/api/environments/${encodeURIComponent(environment.value)}/pods/${encodeURIComponent(fbPod.value)}/files/download?path=${encodeURIComponent(path)}`
    const { blob, filename } = await downloadBlob(url)
    const name = filename || path.split('/').filter(Boolean).pop() || 'download'
    const href = URL.createObjectURL(blob)
    const a = document.createElement('a')
    a.href = href
    a.download = name
    document.body.appendChild(a)
    a.click()
    a.remove()
    setTimeout(() => URL.revokeObjectURL(href), 10_000)
    kernel.notify.success(t('tools.k8s.downloadStarted', { name }))
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('tools.k8s.downloadFailed'), (e as { code?: string }).code)
  } finally {
    downloadingPath.value = ''
  }
}

function entryPath(name: string): string {
  return fbPath.value === '/' ? `/${name}` : `${fbPath.value}/${name}`
}

// ---------------- 展示辅助 ----------------
function fmtSize(n: number | undefined | null): string {
  if (n == null) return ''
  if (n < 1024) return `${n} B`
  if (n < 1024 * 1024) return `${(n / 1024).toFixed(1)} KB`
  return `${(n / 1024 / 1024).toFixed(1)} MB`
}

function cpuText(cur: string, limit: string): string {
  if (!cur && !limit) return '-'
  return limit ? `${cur || '?'}/${limit}` : cur
}

/** 当前环境信息（cluster/namespace） */
const envInfo = computed(() => (environment.value ? envMeta.value[environment.value] : null))

onMounted(async () => {
  await loadBaseUrl()
  if (cfgOk.value) await loadEnvs()
})
</script>

<template>
  <div class="k8s-page">
    <!-- 上部：查询条件（与数据联动） -->
    <div class="k8s-card k8s-form">
      <div class="k8s-row">
        <label class="k8s-field">
          <span class="k8s-label">{{ t('tools.k8s.environment') }}</span>
          <el-select v-model="environment" size="small" class="k8s-ctrl" :placeholder="t('tools.k8s.environment')" :loading="loadingMeta">
            <el-option v-for="e in envs" :key="e" :label="e" :value="e" />
          </el-select>
        </label>

        <label class="k8s-field">
          <span class="k8s-label">{{ t('tools.k8s.status') }}</span>
          <el-select v-model="statusFilter" size="small" class="k8s-ctrl" clearable :placeholder="t('tools.k8s.statusAll')">
            <el-option v-for="s in statusOptions" :key="s" :label="s" :value="s" />
          </el-select>
        </label>

        <label class="k8s-field k8s-wide">
          <span class="k8s-label">{{ t('tools.k8s.nameFilter') }}</span>
          <el-input v-model="nameFilter" size="small" class="k8s-ctrl" clearable :placeholder="t('tools.k8s.nameFilterPh')" />
        </label>

        <div class="k8s-actions">
          <el-button type="primary" size="small" :loading="loading" :disabled="!environment" @click="loadPods">
            {{ t('tools.k8s.refresh') }}
          </el-button>
        </div>
      </div>
    </div>

    <!-- 下部：Pod 列表 -->
    <div class="k8s-card k8s-list">
      <div v-if="envInfo || pods.length" class="k8s-meta">
        <span v-if="envInfo" class="k8s-meta-item">cluster={{ envInfo.cluster || '-' }}</span>
        <span v-if="envInfo" class="k8s-meta-item">namespace={{ envInfo.namespace || '-' }}</span>
        <span class="k8s-meta-item">pods={{ filteredPods.length }}</span>
        <span v-if="statusFilter" class="k8s-meta-item">status={{ statusFilter }}</span>
      </div>

      <div v-if="err && !filteredPods.length" class="k8s-empty">{{ err }}</div>
      <div v-else-if="!cfgOk" class="k8s-empty">{{ t('tools.k8s.noEndpoint') }}</div>
      <div v-else-if="!environment && !loading" class="k8s-empty">{{ t('tools.k8s.pickEnv') }}</div>

      <div v-if="filteredPods.length" class="k8s-table-fill">
        <el-table
          :data="filteredPods"
          size="small"
          border
          class="k8s-table"
          height="100%"
          @row-click="(row: PodDetails) => openFileBrowser(row)"
        >
          <el-table-column :label="t('tools.k8s.colName')" prop="name" min-width="220" show-overflow-tooltip>
            <template #default="{ row }">
              <button type="button" class="k8s-cell" :title="t('tools.k8s.browseFiles')" @click.stop="openFileBrowser(row)">{{ row.name }}</button>
            </template>
          </el-table-column>
          <el-table-column :label="t('tools.k8s.colReady')" prop="ready" min-width="70" />
          <el-table-column :label="t('tools.k8s.colStatus')" prop="status" min-width="110">
            <template #default="{ row }">
              <el-tag size="small" :type="statusType(row.status)">{{ row.status }}</el-tag>
            </template>
          </el-table-column>
          <el-table-column :label="t('tools.k8s.colRestarts')" prop="restarts" align="right" min-width="80" />
          <el-table-column :label="t('tools.k8s.colAge')" prop="age" min-width="90" />
          <el-table-column :label="t('tools.k8s.colIp')" prop="ip" min-width="130" show-overflow-tooltip />
          <el-table-column :label="t('tools.k8s.colNode')" prop="node" min-width="130" show-overflow-tooltip />
          <el-table-column :label="t('tools.k8s.colCpu')" align="right" min-width="100">
            <template #default="{ row }">
              {{ row.cpuCurrent || row.cpuLimit ? cpuText(row.cpuCurrent, row.cpuLimit) : '-' }}
            </template>
          </el-table-column>
          <el-table-column :label="t('tools.k8s.colMemory')" align="right" min-width="100">
            <template #default="{ row }">
              {{ row.memoryCurrent || row.memoryLimit ? cpuText(row.memoryCurrent, row.memoryLimit) : '-' }}
            </template>
          </el-table-column>
          <el-table-column :label="t('tools.k8s.colImage')" prop="imageUrl" min-width="340" show-overflow-tooltip class-name="k8s-col-img" />
        </el-table>
      </div>
    </div>

    <!-- 弹窗：Pod 文件浏览器 -->
    <el-dialog v-model="fbOpen" :title="`${t('tools.k8s.filesTitle')} · ${fbPod}`" width="720px" append-to-body destroy-on-close class="k8s-dlg" @closed="fbOpen = false">
      <div class="k8s-fb-nav">
        <el-button size="small" :disabled="!fbParent" @click="loadDir(fbParent)">{{ t('tools.k8s.parentDir') }}</el-button>
        <span class="k8s-fb-path">{{ fbPath }}</span>
        <span class="k8s-fb-count">{{ fbEntries.length }} · {{ fbDirCount }} dir</span>
        <el-button size="small" :loading="fbLoading" @click="loadDir(fbPath)">{{ t('tools.k8s.refresh') }}</el-button>
      </div>
      <div v-if="fbErr" class="k8s-empty">{{ fbErr }}</div>
      <div v-else-if="!fbEntries.length && !fbLoading" class="k8s-empty">{{ t('tools.k8s.filesEmpty') }}</div>
      <div v-else class="k8s-fb-list k8s-scroll">
        <table class="k8s-table">
          <thead>
            <tr>
              <th class="k8s-th name">{{ t('tools.k8s.colName') }}</th>
              <th class="k8s-th">{{ t('tools.k8s.colType') }}</th>
              <th class="k8s-th num">{{ t('tools.k8s.colSize') }}</th>
              <th class="k8s-th act" />
            </tr>
          </thead>
          <tbody>
            <tr v-for="en in fbEntries" :key="en.name" class="k8s-tr">
              <td class="k8s-td name">
                <button type="button" class="k8s-cell" :class="{ dir: en.isDir }" @click="onEntryClick(en)">
                  {{ en.isDir ? '📁' : '📄' }} {{ en.name }}
                </button>
              </td>
              <td class="k8s-td">{{ en.isDir ? 'dir' : 'file' }}</td>
              <td class="k8s-td num">{{ fmtSize(en.size) }}</td>
              <td class="k8s-td act">
                <button
                  v-if="!en.isDir"
                  type="button"
                  class="k8s-dl"
                  :title="t('tools.k8s.downloadFile')"
                  :disabled="!!downloadingPath"
                  @click="downloadFile(entryPath(en.name))"
                >
                  {{ downloadingPath === entryPath(en.name) ? '…' : '⬇' }}
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </el-dialog>

    <!-- 弹窗：文件内容查看（JSON 高亮，失败原样） -->
    <el-dialog v-model="contentOpen" :title="contentMeta" width="760px" append-to-body destroy-on-close class="k8s-dlg">
      <div v-if="contentErr" class="k8s-empty">{{ contentErr }}</div>
      <template v-else>
        <div v-if="contentIsJson" class="k8s-json-pre" v-html="contentHtml" />
        <pre v-else class="k8s-raw-pre">{{ contentRaw }}</pre>
      </template>
      <template #footer>
        <el-button :disabled="!contentOpen" @click="contentOpen = false">{{ t('common.close') }}</el-button>
        <el-button :disabled="!contentOpen || !contentPath" :loading="downloadingPath === contentPath" @click="downloadFile(contentPath)">
          {{ t('tools.k8s.download') }}
        </el-button>
        <el-button type="primary" :disabled="!contentOpen" @click="onCopyContent">
          {{ contentIsJson ? t('tools.k8s.copyFormatted') : t('tools.common.copy') }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.k8s-page {
  flex: 1;
  height: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 18px 22px 20px;
  overflow: hidden;
}

.k8s-card {
  background: var(--nc-surface-solid);
  border: 1px solid var(--nc-border-strong);
  border-radius: 12px;
  padding: 14px 16px;
}

.k8s-form {
  flex-shrink: 0;
}

.k8s-row {
  display: flex;
  flex-wrap: wrap;
  gap: 12px 18px;
  align-items: flex-end;
}

.k8s-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 130px;
}

.k8s-label {
  font-size: 12px;
  color: var(--nc-text-dim);
  font-weight: 600;
}

.k8s-ctrl {
  width: 100%;
}

.k8s-wide {
  min-width: 220px;
}

.k8s-actions {
  display: flex;
  align-items: flex-end;
  padding-bottom: 2px;
}

.k8s-list {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  padding: 10px 12px 12px;
}

/* Pod 结果表格：flex 撑满剩余高度，表格内部滚动 + 固定表头；border 开启列宽拖动 */
.k8s-table-fill {
  flex: 1;
  min-height: 0;
  overflow: hidden;
}

.k8s-table.el-table {
  flex: 1;
  min-height: 0;
}

.k8s-table.el-table :deep(.el-table__row) {
  cursor: pointer;
}

/* 名称/镜像单元格：列宽可拖，内容超长省略 */
.k8s-table.el-table :deep(.k8s-col-img .cell),
.k8s-table.el-table :deep(.cell) {
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.k8s-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  padding: 2px 2px 10px;
  border-bottom: 1px solid var(--nc-border);
  margin-bottom: 10px;
  font-size: 12px;
  color: var(--nc-text-dim);
}

.k8s-meta-item {
  white-space: nowrap;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
}

.k8s-scroll {
  flex: 1;
  min-height: 0;
  overflow: auto;
}

.k8s-empty {
  padding: 26px 10px;
  text-align: center;
  color: var(--nc-text-faint);
  font-size: 13px;
}

.k8s-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12.5px;
  line-height: 1.55;
}

.k8s-th {
  position: sticky;
  top: 0;
  z-index: 1;
  background: var(--nc-surface-2);
  text-align: left;
  font-size: 12px;
  font-weight: 700;
  color: var(--nc-text-dim);
  padding: 7px 10px;
  border-bottom: 1px solid var(--nc-border);
  white-space: nowrap;
}

.k8s-th.num {
  text-align: right;
}

.k8s-th.name {
  min-width: 200px;
}

.k8s-th.img {
  min-width: 160px;
}

.k8s-tr:hover .k8s-td {
  background: var(--nc-surface-2);
  cursor: pointer;
}

.k8s-td {
  padding: 7px 10px;
  border-bottom: 1px solid var(--nc-border);
  white-space: nowrap;
  color: var(--nc-text);
}

.k8s-td.num {
  text-align: right;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
}

.k8s-td.node,
.k8s-td.img {
  max-width: 180px;
  overflow: hidden;
  text-overflow: ellipsis;
}

.k8s-cell {
  border: none;
  background: transparent;
  padding: 0;
  color: var(--nc-primary);
  font-size: 12.5px;
  font-weight: 600;
  cursor: pointer;
  max-width: 100%;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  text-align: left;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
}

.k8s-cell:hover {
  text-decoration: underline;
}

.k8s-cell.dir {
  color: color-mix(in srgb, #7c3aed 55%, var(--nc-text));
}

/* 下载列：窄列、居中；按钮用主题令牌，深浅主题下都清晰 */
.k8s-th.act,
.k8s-td.act {
  width: 44px;
  text-align: center;
  padding-left: 4px;
  padding-right: 4px;
}

.k8s-dl {
  border: 1px solid var(--nc-border-strong);
  background: transparent;
  color: var(--nc-primary);
  border-radius: 6px;
  width: 26px;
  height: 24px;
  line-height: 1;
  font-size: 13px;
  cursor: pointer;
  transition: background 0.15s, border-color 0.15s;
}

.k8s-dl:hover:not(:disabled) {
  background: color-mix(in srgb, var(--nc-primary) 14%, transparent);
  border-color: var(--nc-primary);
}

.k8s-dl:disabled {
  opacity: 0.5;
  cursor: progress;
}

/* 文件浏览器 */
.k8s-fb-nav {
  display: flex;
  align-items: center;
  gap: 10px;
  margin-bottom: 10px;
}

.k8s-fb-path {
  flex: 1;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
  font-size: 12.5px;
  color: var(--nc-text-dim);
  background: var(--nc-surface-2);
  border-radius: 6px;
  padding: 4px 10px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
}

.k8s-fb-count {
  font-size: 12px;
  color: var(--nc-text-faint);
  white-space: nowrap;
}

.k8s-fb-list {
  max-height: 52vh;
  border: 1px solid var(--nc-border);
  border-radius: 8px;
  overflow: auto;
}

/* 内容弹窗 */
.k8s-json-pre {
  margin: 0;
  padding: 14px 16px;
  background: #0f172a;
  color: #e2e8f0;
  border-radius: 10px;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
  font-size: 12.5px;
  line-height: 1.65;
  white-space: pre;
  overflow: auto;
  max-height: 60vh;
  min-height: 120px;
}

.k8s-raw-pre {
  margin: 0;
  padding: 14px 16px;
  background: var(--nc-surface-2);
  color: var(--nc-text);
  border: 1px solid var(--nc-border-strong);
  border-radius: 10px;
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
  font-size: 12.5px;
  line-height: 1.65;
  white-space: pre-wrap;
  word-break: break-all;
  overflow: auto;
  max-height: 60vh;
  min-height: 120px;
}

:deep(.jq-key) {
  color: #7dd3fc;
}

:deep(.jq-str) {
  color: #86efac;
}

:deep(.jq-num) {
  color: #fca5a5;
}

:deep(.jq-bool) {
  color: #c4b5fd;
}

:deep(.jq-punc) {
  color: var(--nc-text-faint);
}

:deep(.k8s-dlg .el-dialog__title) {
  font-size: 14px;
  font-weight: 700;
}
</style>
