<script setup lang="ts">
/**
 * Kafka Producer：向 Kafka 主题发送一条消息（kafka-producer-mcp-server REST 网关）。
 * 布局与 Kafka Explorer 一致：上部发送条件（environment/topic/partition/format/schemaId），
 * 中部 key/value 编辑区（JSON 校验与格式化），下部发送记录（本次会话；点击 key/value 弹窗 JSON 格式化）。
 * format=avro 时可按主题检索 Schema Registry（<topic>-key / <topic>-value）自动带出 schemaId，并查看字段。
 * 端点地址与授权头来自管理端「工具 → API Endpoint / 授权头」（AppTool.BaseUrl / HeadersJson），
 * 前端经同源代理 /api/ext/tool/kafka-producer/** 访问；发送为 POST，仅该路径在后端白名单内放行。
 * 发送不可撤销：每次发送前必须二次确认（显示环境/主题/分区）。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessageBox } from 'element-plus'
import { http, tokenStore } from '@/api/http'
import { getLang } from '@/i18n'
import { kernel } from '@/kernel'
import { copyText } from '@/utils/clipboard'
import { highlightJson } from './jsonHighlight'

const { t } = useI18n()
const TOOL_KEY = 'kafka-producer'

/** Key / Value 文本框随内容自动增高，达到上限后出现内部滚动条；页面整体可滚动，不挤压下方发送记录 */
const KEY_MAX_ROWS = 12
const DATA_MAX_ROWS = 28

/** 列表 cell 默认截断长度（完整内容在弹窗中查看） */
const TRUNCATE_LEN = 120
function truncate(text: string): string {
  return text.length > TRUNCATE_LEN ? `${text.slice(0, TRUNCATE_LEN)}…` : text
}

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
const endpoint = '/api/ext/tool/kafka-producer'

/** 上游统一信封：{ success, tool, data, error, durationMs } */
interface Envelope<T = unknown> {
  success: boolean
  data?: T
  error?: string | null
  durationMs?: number
}

/**
 * 读取类请求：沿用 http.get（成功时 success=true；失败抛出由调用方提示）。
 */
async function getEnv<T>(path: string): Promise<Envelope<T>> {
  const r = await http.get<Envelope<T>>(`${endpoint}${path}`)
  if (!r?.success) throw new Error(r?.error ?? 'request failed')
  return r
}

/**
 * 发送请求：上游失败时返回 非 200 + 信封（含具体原因，如 Unknown environment / topic does not exist），
 * http 封装会把非 2xx 吞成通用 HTTP_ERROR，因此这里直接 fetch 以保留真实失败原因。
 */
async function postProduce(path: string, body: unknown): Promise<{ ok: boolean; status: number; env: Envelope<ProduceResult> | null; raw: string }> {
  const token = tokenStore.get()
  const res = await fetch(`${endpoint}${path}`, {
    method: 'POST',
    headers: {
      'Content-Type': 'application/json',
      'X-Lang': getLang(),
      ...(token ? { Authorization: `Bearer ${token}` } : {}),
    },
    body: JSON.stringify(body),
  })
  const raw = await res.text()
  let env: Envelope<ProduceResult> | null = null
  try {
    env = raw ? (JSON.parse(raw) as Envelope<ProduceResult>) : null
  } catch {
    env = null
  }
  return { ok: res.ok, status: res.status, env, raw }
}

// ---------------- 发送条件 ----------------
interface PartitionInfo {
  id: number
  leader?: number
  replicaCount?: number
  inSyncReplicaCount?: number
}
interface SchemaItem {
  subject: string
  version: number
  schemaId: number
  schemaType?: string
  schemaName?: string
}
interface SchemaField {
  name: string
  type: string
  nullable: boolean
  hasDefault: boolean
}

const envs = ref<{ name: string; hasSchemaRegistry: boolean; toolDescription?: string }[]>([])
const topics = ref<string[]>([])
const partitions = ref<PartitionInfo[]>([])
const loadingMeta = ref(false)

const environment = ref('')
const topic = ref('')
const partition = ref<number | null>(null) // null = 由 Kafka 默认分区器决定
const format = ref<'json' | 'avro'>('json')
const keySchemaId = ref<number | null>(null)
const dataSchemaId = ref<number | null>(null)

const keyText = ref('')
const dataText = ref('')

const curEnv = computed(() => envs.value.find((e) => e.name === environment.value))
const avroDisabled = computed(() => !!curEnv.value && !curEnv.value.hasSchemaRegistry)

async function loadEnvs() {
  if (!cfgOk.value) return
  loadingMeta.value = true
  try {
    const r = await getEnv<{ environments: { name: string; hasSchemaRegistry: boolean; toolDescription?: string }[] }>('/api/environments')
    const list = r.data?.environments ?? []
    envs.value = list
    const names = list.map((e) => e.name)
    if (names.length && !names.includes(environment.value)) environment.value = names[0]
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('tools.kafkaProducer.metaLoadFailed'), (e as { code?: string }).code)
  } finally {
    loadingMeta.value = false
  }
}

async function loadTopics() {
  if (!cfgOk.value || !environment.value) {
    topics.value = []
    partitions.value = []
    return
  }
  loadingMeta.value = true
  try {
    const r = await getEnv<{ topics: { name: string }[] }>(`/api/topics?environment=${encodeURIComponent(environment.value)}`)
    const names = (r.data?.topics ?? []).map((x) => x.name)
    topics.value = names
    partition.value = null
    // 发送是写操作：不自动选中主题，避免未经确认就对默认主题误发（与 Explorer 的只读查询不同）
    if (!names.includes(topic.value)) topic.value = ''
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('tools.kafkaProducer.metaLoadFailed'), (e as { code?: string }).code)
    topics.value = []
  } finally {
    loadingMeta.value = false
  }
}

async function loadPartitions() {
  if (!cfgOk.value || !environment.value || !topic.value) {
    partitions.value = []
    return
  }
  try {
    const r = await getEnv<{ partitions: PartitionInfo[] }>(
      `/api/topics/${encodeURIComponent(topic.value)}/partitions?environment=${encodeURIComponent(environment.value)}`,
    )
    partitions.value = r.data?.partitions ?? []
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('tools.kafkaProducer.metaLoadFailed'), (e as { code?: string }).code)
    partitions.value = []
  }
}

// ---------------- Avro：按主题检索 Schema Registry，自动带出 schemaId ----------------
const schemas = ref<SchemaItem[]>([])
const schemaLoading = ref(false)
const schemaErr = ref('')
/** 见过的 schema 缓存（id → 条目）：搜索结果被替换后，已选项仍能显示 "#id · subject"，而不是裸数字 */
const schemaSeen = new Map<number, SchemaItem>()
/** 请求序号：丢弃过期响应（用户连续输入搜索词时，只采用最后一次） */
let schemaSeq = 0

async function fetchSchemas(keyword: string): Promise<SchemaItem[]> {
  const r = await getEnv<{ schemas: SchemaItem[] }>(
    `/api/schemas?environment=${encodeURIComponent(environment.value)}&keyword=${encodeURIComponent(keyword)}`,
  )
  const list = r.data?.schemas ?? []
  for (const s of list) schemaSeen.set(s.schemaId, s)
  return list
}

/** 下拉当前输入的搜索词（纯数字时提供“直接使用该 ID”的选项） */
const schemaQuery = ref('')

/** 下拉选项：当前结果 + 已选项（保持可见）+ 输入的纯数字 ID（Registry 里查不到时也允许直接使用），按 schemaId 去重 */
const schemaOptions = computed<SchemaItem[]>(() => {
  const map = new Map<number, SchemaItem>()
  for (const s of schemas.value) if (!map.has(s.schemaId)) map.set(s.schemaId, s)
  const extra: number[] = []
  for (const id of [keySchemaId.value, dataSchemaId.value]) if (id != null) extra.push(id)
  const q = schemaQuery.value.trim()
  if (/^\d+$/.test(q) && Number(q) > 0) extra.push(Number(q))
  for (const id of extra) {
    if (!map.has(id)) map.set(id, schemaSeen.get(id) ?? { subject: '', version: 0, schemaId: id })
  }
  return [...map.values()]
})
function schemaLabel(s: SchemaItem): string {
  if (!s.subject) return `#${s.schemaId}`
  return `#${s.schemaId} · ${s.subject}${s.version ? ` (v${s.version})` : ''}`
}

/** 下拉值转 schemaId：非正整数视为未选择 */
function toId(v: unknown): number | null {
  if (v === '' || v == null) return null
  const n = Number(v)
  return Number.isInteger(n) && n > 0 ? n : null
}

/** 按当前主题检索 Schema Registry，并自动带出 <topic>-key / <topic>-value 的 schemaId（部分环境主体形如 -ce.xxx，需人工选择） */
async function loadSchemas() {
  schemas.value = []
  schemaErr.value = ''
  if (format.value !== 'avro' || !cfgOk.value || !environment.value || !topic.value || avroDisabled.value) return
  const seq = ++schemaSeq
  schemaLoading.value = true
  try {
    const list = await fetchSchemas(topic.value)
    if (seq !== schemaSeq) return
    schemas.value = list
    const tp = topic.value.toLowerCase()
    const keyItem = list.find((s) => s.subject.toLowerCase() === `${tp}-key`)
    const valItem = list.find((s) => s.subject.toLowerCase() === `${tp}-value`)
    if (keyItem) keySchemaId.value = keyItem.schemaId
    if (valItem) dataSchemaId.value = valItem.schemaId
    if (!list.length) schemaErr.value = t('tools.kafkaProducer.schemaNone')
  } catch (e) {
    if (seq === schemaSeq) schemaErr.value = (e as { message?: string }).message ?? t('tools.kafkaProducer.metaLoadFailed')
  } finally {
    if (seq === schemaSeq) schemaLoading.value = false
  }
}

/** 下拉里输入搜索词：防抖后按关键词查 Schema Registry；清空则回到按主题检索的结果 */
let schemaTimer: ReturnType<typeof setTimeout> | undefined
function onSchemaSearch(query: string) {
  if (schemaTimer) clearTimeout(schemaTimer)
  schemaQuery.value = query
  schemaTimer = setTimeout(async () => {
    const q = query.trim()
    if (!cfgOk.value || !environment.value || avroDisabled.value) return
    if (!q) {
      const keepKey = keySchemaId.value
      const keepData = dataSchemaId.value
      await loadSchemas()
      // 回到默认列表时不能覆盖用户已手选的值
      if (keepKey != null) keySchemaId.value = keepKey
      if (keepData != null) dataSchemaId.value = keepData
      return
    }
    const seq = ++schemaSeq
    schemaLoading.value = true
    schemaErr.value = ''
    try {
      const list = await fetchSchemas(q)
      if (seq === schemaSeq) schemas.value = list
    } catch (e) {
      if (seq === schemaSeq) schemaErr.value = (e as { message?: string }).message ?? t('tools.kafkaProducer.metaLoadFailed')
    } finally {
      if (seq === schemaSeq) schemaLoading.value = false
    }
  }, 300)
}

// 字段预览弹窗（get_schema）
const schemaDlg = ref(false)
const schemaDlgTitle = ref('')
const schemaFields = ref<SchemaField[]>([])
const schemaText = ref('')
const schemaDlgLoading = ref(false)

async function viewSchema(id: number | null) {
  if (id == null || !environment.value) return
  schemaDlgTitle.value = `schema #${id}`
  schemaFields.value = []
  schemaText.value = ''
  schemaDlg.value = true
  schemaDlgLoading.value = true
  try {
    const r = await getEnv<{ schemaName?: string; fields?: SchemaField[]; schemaText?: string; schema?: string }>(
      `/api/schemas/${id}?environment=${encodeURIComponent(environment.value)}`,
    )
    schemaDlgTitle.value = `schema #${id} · ${r.data?.schemaName ?? ''}`
    schemaFields.value = r.data?.fields ?? []
    schemaText.value = r.data?.schemaText ?? r.data?.schema ?? ''
  } catch (e) {
    schemaText.value = (e as { message?: string }).message ?? t('tools.kafkaProducer.metaLoadFailed')
  } finally {
    schemaDlgLoading.value = false
  }
}

/** 按字段生成 JSON 骨架，写入 value 编辑区（便于快速构造 Avro 载荷） */
function fillSkeleton() {
  const obj: Record<string, unknown> = {}
  for (const f of schemaFields.value) {
    const ty = f.type.toLowerCase()
    obj[f.name] = f.nullable ? null : ty.includes('int') || ty.includes('long') || ty.includes('double') || ty.includes('float') ? 0 : ty.includes('bool') ? false : ''
  }
  dataText.value = JSON.stringify(obj, null, 2)
  schemaDlg.value = false
}

watch(environment, () => {
  topic.value = ''
  keySchemaId.value = null
  dataSchemaId.value = null
  if (avroDisabled.value) format.value = 'json'
  void loadTopics()
})
watch(topic, () => {
  partition.value = null
  keySchemaId.value = null
  dataSchemaId.value = null
  void loadPartitions()
  void loadSchemas()
})
watch(format, () => {
  void loadSchemas()
})

// ---------------- 载荷校验 / 格式化 ----------------
/** 返回 null = 合法 JSON（或为空）；否则返回错误提示 */
function jsonError(text: string): string | null {
  if (!text.trim()) return null
  try {
    JSON.parse(text)
    return null
  } catch (e) {
    return (e as Error).message
  }
}
const keyJsonErr = computed(() => jsonError(keyText.value))
const dataJsonErr = computed(() => jsonError(dataText.value))

function prettify(which: 'key' | 'data') {
  const src = which === 'key' ? keyText : dataText
  try {
    src.value = JSON.stringify(JSON.parse(src.value), null, 2)
  } catch {
    kernel.notify.warning(t('tools.kafkaProducer.notJson'))
  }
}
function minify(which: 'key' | 'data') {
  const src = which === 'key' ? keyText : dataText
  try {
    src.value = JSON.stringify(JSON.parse(src.value))
  } catch {
    kernel.notify.warning(t('tools.kafkaProducer.notJson'))
  }
}

// ---------------- 发送 ----------------
interface ProduceResult {
  partition?: number
  offset?: number
  topic?: string
  environment?: string
  timestampUtc?: string
  [k: string]: unknown
}
interface SendRecord {
  at: string
  environment: string
  topic: string
  format: string
  ok: boolean
  partition?: number
  offset?: number
  key: string
  value: string
  message: string
  ms?: number
}

const records = ref<SendRecord[]>([])
const sending = ref(false)

const canSend = computed(() => {
  if (!cfgOk.value || !environment.value || !topic.value || !dataText.value.trim()) return false
  if (format.value === 'avro' && (keySchemaId.value == null || dataSchemaId.value == null)) return false
  return true
})

function nowText(): string {
  const d = new Date()
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${p(d.getMonth() + 1)}-${p(d.getDate())} ${p(d.getHours())}:${p(d.getMinutes())}:${p(d.getSeconds())}`
}

async function send() {
  if (!environment.value || !topic.value) {
    kernel.notify.warning(t('tools.kafkaProducer.requiredTip'))
    return
  }
  if (!dataText.value.trim()) {
    kernel.notify.warning(t('tools.kafkaProducer.dataRequired'))
    return
  }
  if (format.value === 'avro') {
    if (keySchemaId.value == null || dataSchemaId.value == null) {
      kernel.notify.warning(t('tools.kafkaProducer.schemaRequired'))
      return
    }
    // Avro 载荷必须是合法 JSON，否则服务端转换必然失败，提前拦截
    if (keyJsonErr.value || dataJsonErr.value) {
      kernel.notify.warning(t('tools.kafkaProducer.notJson'))
      return
    }
  }

  // 写操作不可撤销：二次确认并明确展示目标
  try {
    await ElMessageBox.confirm(
      t('tools.kafkaProducer.confirmBody', {
        env: environment.value,
        topic: topic.value,
        partition: partition.value != null ? String(partition.value) : t('tools.kafkaProducer.autoPartition'),
        format: format.value,
      }),
      t('tools.kafkaProducer.confirmTitle'),
      { type: 'warning', confirmButtonText: t('tools.kafkaProducer.send'), cancelButtonText: t('common.cancel'), customClass: 'kp-confirm' },
    )
  } catch {
    return
  }

  const q = new URLSearchParams()
  q.set('environment', environment.value)
  if (partition.value != null) q.set('partition', String(partition.value))
  q.set('format', format.value)
  if (format.value === 'avro') {
    q.set('keySchemaId', String(keySchemaId.value))
    q.set('dataSchemaId', String(dataSchemaId.value))
  }
  // 上游约定：body 仅 { jsonKey, jsonData }，均为字符串（json 格式原样 UTF-8 发送，avro 为待转换的 JSON 文档）
  const body = { jsonKey: keyText.value, jsonData: dataText.value }

  sending.value = true
  const rec: SendRecord = {
    at: nowText(),
    environment: environment.value,
    topic: topic.value,
    format: format.value,
    ok: false,
    key: keyText.value,
    value: dataText.value,
    message: '',
  }
  try {
    const r = await postProduce(`/api/topics/${encodeURIComponent(topic.value)}/messages?${q.toString()}`, body)
    rec.ms = r.env?.durationMs
    if (r.ok && r.env?.success) {
      rec.ok = true
      rec.partition = r.env.data?.partition
      rec.offset = r.env.data?.offset
      rec.message = t('tools.kafkaProducer.sentOk', { p: rec.partition ?? '-', o: rec.offset ?? '-' })
      kernel.notify.success(rec.message)
    } else {
      // 上游失败：优先展示上游给出的精确原因；代理自身错误（{code,message}）次之
      const proxy = (() => {
        try {
          return JSON.parse(r.raw) as { message?: string }
        } catch {
          return null
        }
      })()
      rec.message = r.env?.error || proxy?.message || `HTTP ${r.status}`
      kernel.notify.error(rec.message)
    }
  } catch (e) {
    rec.message = (e as { message?: string }).message ?? t('tools.kafkaProducer.sendFailed')
    kernel.notify.error(rec.message)
  } finally {
    sending.value = false
    records.value.unshift(rec)
  }
}

function resetPayload() {
  keyText.value = ''
  dataText.value = ''
}

// ---------------- 详情弹窗（key/value：JSON 格式化，失败原样） ----------------
const dialog = ref(false)
const dialogTitle = ref('')
const dialogRaw = ref('')
const dialogPretty = ref('')
const dialogHtml = ref('')
const dialogIsJson = ref(false)

function openDetail(which: 'key' | 'value', rec: SendRecord) {
  const text = which === 'key' ? rec.key : rec.value
  dialogTitle.value = `${which} · ${rec.topic}`
  dialogRaw.value = text
  try {
    const parsed: unknown = JSON.parse(text)
    dialogPretty.value = JSON.stringify(parsed, null, 2)
    dialogHtml.value = highlightJson(dialogPretty.value)
    dialogIsJson.value = true
  } catch {
    dialogPretty.value = text
    dialogHtml.value = ''
    dialogIsJson.value = false
  }
  dialog.value = true
}

/** 载入历史记录到编辑区（便于微调后重发） */
function reuse(rec: SendRecord) {
  keyText.value = rec.key
  dataText.value = rec.value
}

async function onCopy() {
  const text = dialogIsJson.value ? dialogPretty.value : dialogRaw.value
  const ok = await copyText(text)
  if (ok) kernel.notify.success(t('tools.common.copied'))
  else kernel.notify.error(t('tools.common.copyFailed'))
}

onMounted(async () => {
  await loadBaseUrl()
  if (cfgOk.value) await loadEnvs()
})
</script>

<template>
  <div class="kp-page">
    <!-- 上部：发送条件 -->
    <div class="kp-card kp-form">
      <div class="kp-row">
        <label class="kp-field">
          <span class="kp-label">{{ t('tools.kafkaProducer.environment') }} *</span>
          <el-select v-model="environment" size="small" class="kp-ctrl" :placeholder="t('tools.kafkaProducer.environment')" :loading="loadingMeta">
            <el-option v-for="e in envs" :key="e.name" :label="e.name" :value="e.name" />
          </el-select>
        </label>

        <label class="kp-field">
          <span class="kp-label">{{ t('tools.kafkaProducer.topic') }} *</span>
          <el-select v-model="topic" size="small" class="kp-ctrl kp-wide" filterable :placeholder="t('tools.kafkaProducer.topic')" :loading="loadingMeta">
            <el-option v-for="tp in topics" :key="tp" :label="tp" :value="tp" />
          </el-select>
        </label>

        <label class="kp-field">
          <span class="kp-label">{{ t('tools.kafkaProducer.partition') }}</span>
          <el-select v-model="partition" size="small" class="kp-ctrl" clearable :placeholder="t('tools.kafkaProducer.autoPartition')">
            <el-option v-for="p in partitions" :key="p.id" :label="`${p.id} · leader ${p.leader ?? '-'}`" :value="p.id" />
          </el-select>
        </label>

        <label class="kp-field">
          <span class="kp-label">{{ t('tools.kafkaProducer.format') }}</span>
          <el-select v-model="format" size="small" class="kp-ctrl">
            <el-option label="json" value="json" />
            <el-option label="avro" value="avro" :disabled="avroDisabled" />
          </el-select>
        </label>

        <template v-if="format === 'avro'">
          <label class="kp-field">
            <span class="kp-label">{{ t('tools.kafkaProducer.keySchemaId') }} *</span>
            <div class="kp-inline">
              <el-select
                :model-value="keySchemaId"
                size="small"
                class="kp-ctrl kp-schema-sel"
                filterable
                remote
                remote-show-suffix
                default-first-option
                clearable
                :remote-method="onSchemaSearch"
                :loading="schemaLoading"
                :placeholder="t('tools.kafkaProducer.schemaPh')"
                popper-class="kp-schema-popper"
                @update:model-value="(v: unknown) => (keySchemaId = toId(v))"
                @visible-change="(open: boolean) => open && !schemas.length && loadSchemas()"
              >
                <el-option v-for="s in schemaOptions" :key="s.schemaId" :label="schemaLabel(s)" :value="s.schemaId" />
              </el-select>
              <el-button size="small" text :disabled="keySchemaId == null" @click="viewSchema(keySchemaId)">{{ t('tools.kafkaProducer.fields') }}</el-button>
            </div>
          </label>
          <label class="kp-field">
            <span class="kp-label">{{ t('tools.kafkaProducer.dataSchemaId') }} *</span>
            <div class="kp-inline">
              <el-select
                :model-value="dataSchemaId"
                size="small"
                class="kp-ctrl kp-schema-sel"
                filterable
                remote
                remote-show-suffix
                default-first-option
                clearable
                :remote-method="onSchemaSearch"
                :loading="schemaLoading"
                :placeholder="t('tools.kafkaProducer.schemaPh')"
                popper-class="kp-schema-popper"
                @update:model-value="(v: unknown) => (dataSchemaId = toId(v))"
                @visible-change="(open: boolean) => open && !schemas.length && loadSchemas()"
              >
                <el-option v-for="s in schemaOptions" :key="s.schemaId" :label="schemaLabel(s)" :value="s.schemaId" />
              </el-select>
              <el-button size="small" text :disabled="dataSchemaId == null" @click="viewSchema(dataSchemaId)">{{ t('tools.kafkaProducer.fields') }}</el-button>
            </div>
          </label>
        </template>

        <div class="kp-actions">
          <el-button type="primary" size="small" :loading="sending" :disabled="!canSend" @click="send">
            {{ t('tools.kafkaProducer.send') }}
          </el-button>
          <el-button size="small" :disabled="!keyText && !dataText" @click="resetPayload">{{ t('tools.kafkaProducer.clear') }}</el-button>
        </div>
      </div>

      <div v-if="format === 'avro' && (schemaLoading || schemas.length || schemaErr)" class="kp-schema-hint">
        <span v-if="schemaLoading">{{ t('tools.kafkaProducer.schemaLoading') }}</span>
        <span v-else-if="schemaErr">{{ schemaErr }}</span>
        <template v-else>
          <span class="kp-schema-title">{{ t('tools.kafkaProducer.schemaMatches') }}</span>
          <button
            v-for="s in schemas"
            :key="`${s.subject}-${s.version}`"
            type="button"
            class="kp-chip"
            :title="`${s.schemaName ?? ''} · v${s.version}`"
            @click="viewSchema(s.schemaId)"
          >
            {{ s.subject }} · #{{ s.schemaId }}
          </button>
        </template>
      </div>
    </div>

    <!-- 中部：key / value 编辑区 -->
    <div class="kp-card kp-editors">
      <div class="kp-editor">
        <div class="kp-editor-head">
          <span class="kp-label">{{ t('tools.kafkaProducer.key') }}</span>
          <span v-if="keyJsonErr" class="kp-warn" :title="keyJsonErr">{{ t('tools.kafkaProducer.notJsonShort') }}</span>
          <span class="kp-spacer" />
          <el-button size="small" text :disabled="!keyText || !!keyJsonErr" @click="prettify('key')">{{ t('tools.kafkaProducer.format_') }}</el-button>
          <el-button size="small" text :disabled="!keyText || !!keyJsonErr" @click="minify('key')">{{ t('tools.kafkaProducer.minify') }}</el-button>
        </div>
        <el-input
          v-model="keyText"
          type="textarea"
          :autosize="{ minRows: 2, maxRows: KEY_MAX_ROWS }"
          resize="none"
          class="kp-text"
          :placeholder="t('tools.kafkaProducer.keyPh')"
        />
      </div>

      <div class="kp-editor kp-editor-grow">
        <div class="kp-editor-head">
          <span class="kp-label">{{ t('tools.kafkaProducer.value') }} *</span>
          <span v-if="dataJsonErr" class="kp-warn" :title="dataJsonErr">{{ t('tools.kafkaProducer.notJsonShort') }}</span>
          <span class="kp-spacer" />
          <el-button size="small" text :disabled="!dataText || !!dataJsonErr" @click="prettify('data')">{{ t('tools.kafkaProducer.format_') }}</el-button>
          <el-button size="small" text :disabled="!dataText || !!dataJsonErr" @click="minify('data')">{{ t('tools.kafkaProducer.minify') }}</el-button>
        </div>
        <el-input
          v-model="dataText"
          type="textarea"
          :autosize="{ minRows: 6, maxRows: DATA_MAX_ROWS }"
          resize="none"
          class="kp-text"
          :placeholder="t('tools.kafkaProducer.valuePh')"
        />
      </div>
    </div>

    <!-- 下部：发送记录 -->
    <div class="kp-card kp-list">
      <div class="kp-meta">
        <span class="kp-meta-item">{{ t('tools.kafkaProducer.history') }} ({{ records.length }})</span>
        <span v-if="!cfgOk" class="kp-warn">{{ t('tools.kafkaProducer.noEndpoint') }}</span>
      </div>

      <div v-if="!records.length" class="kp-empty">{{ t('tools.kafkaProducer.historyEmpty') }}</div>

      <div v-else class="kp-table-fill">
        <el-table :data="records" size="small" border class="kp-rtable" height="100%">
          <el-table-column :label="t('tools.kafkaProducer.colTime')" width="160" show-overflow-tooltip>
            <template #default="{ row }"><span class="kp-ts">{{ row.at }}</span></template>
          </el-table-column>
          <el-table-column :label="t('tools.kafkaProducer.colTarget')" min-width="200">
            <template #default="{ row }">
              <div class="kp-target">{{ row.environment }} / {{ row.topic }}</div>
              <div class="kp-sub">{{ row.format }}<template v-if="row.ms != null"> · {{ row.ms }} ms</template></div>
            </template>
          </el-table-column>
          <el-table-column :label="t('tools.kafkaProducer.colResult')" min-width="150">
            <template #default="{ row }">
              <el-tag size="small" :type="row.ok ? 'success' : 'danger'">{{ row.ok ? t('tools.kafkaProducer.ok') : t('tools.kafkaProducer.fail') }}</el-tag>
              <div v-if="!row.ok" class="kp-fail-msg">{{ row.message }}</div>
            </template>
          </el-table-column>
          <el-table-column :label="t('tools.kafkaProducer.colPartition')" width="90" align="right">
            <template #default="{ row }">{{ row.partition ?? '-' }}</template>
          </el-table-column>
          <el-table-column :label="t('tools.kafkaProducer.colOffset')" width="100" align="right">
            <template #default="{ row }">{{ row.offset ?? '-' }}</template>
          </el-table-column>
          <el-table-column :label="t('tools.kafkaProducer.colKey')" min-width="180">
            <template #default="{ row }">
              <button type="button" class="kp-cell" :title="t('tools.kafkaProducer.viewJson')" @click="openDetail('key', row)">
                {{ row.key ? truncate(row.key) : '∅' }}
              </button>
            </template>
          </el-table-column>
          <el-table-column :label="t('tools.kafkaProducer.colValue')" min-width="320">
            <template #default="{ row }">
              <button type="button" class="kp-cell" :title="t('tools.kafkaProducer.viewJson')" @click="openDetail('value', row)">
                {{ truncate(row.value) }}
              </button>
            </template>
          </el-table-column>
          <el-table-column width="80" :resizable="false">
            <template #default="{ row }">
              <el-button size="small" text @click="reuse(row)">{{ t('tools.kafkaProducer.reuse') }}</el-button>
            </template>
          </el-table-column>
        </el-table>
      </div>
    </div>
    <!-- 弹窗：key/value JSON 格式化展示 -->
    <el-dialog v-model="dialog" :title="dialogTitle" width="720px" append-to-body destroy-on-close class="kp-dlg">
      <div class="kp-dlg-body">
        <div v-if="dialogIsJson" class="kp-json-pre" v-html="dialogHtml" />
        <pre v-else class="kp-raw-pre">{{ dialogRaw }}</pre>
      </div>
      <template #footer>
        <el-button :disabled="!dialog" @click="dialog = false">{{ t('common.close') }}</el-button>
        <el-button type="primary" :disabled="!dialog" @click="onCopy">
          {{ dialogIsJson ? t('tools.kafkaProducer.copyFormatted') : t('tools.common.copy') }}
        </el-button>
      </template>
    </el-dialog>

    <!-- 弹窗：Avro schema 字段 -->
    <el-dialog v-model="schemaDlg" :title="schemaDlgTitle" width="720px" append-to-body destroy-on-close class="kp-dlg">
      <div v-loading="schemaDlgLoading" class="kp-dlg-body">
        <table v-if="schemaFields.length" class="kp-table">
          <thead>
            <tr>
              <th class="kp-th">{{ t('tools.kafkaProducer.fName') }}</th>
              <th class="kp-th">{{ t('tools.kafkaProducer.fType') }}</th>
              <th class="kp-th">{{ t('tools.kafkaProducer.fNullable') }}</th>
              <th class="kp-th">{{ t('tools.kafkaProducer.fDefault') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="f in schemaFields" :key="f.name" class="kp-tr">
              <td class="kp-td">{{ f.name }}</td>
              <td class="kp-td">{{ f.type }}</td>
              <td class="kp-td">{{ f.nullable ? 'yes' : 'no' }}</td>
              <td class="kp-td">{{ f.hasDefault ? 'yes' : 'no' }}</td>
            </tr>
          </tbody>
        </table>
        <pre v-else-if="schemaText" class="kp-raw-pre">{{ schemaText }}</pre>
      </div>
      <template #footer>
        <el-button @click="schemaDlg = false">{{ t('common.close') }}</el-button>
        <el-button type="primary" :disabled="!schemaFields.length" @click="fillSkeleton">{{ t('tools.kafkaProducer.fillSkeleton') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.kp-page {
  flex: 1;
  height: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 18px 22px 20px;
  overflow-x: hidden;
  overflow-y: auto;
}

.kp-card {
  background: #ffffff;
  border: 1px solid #e2e8f0;
  border-radius: 12px;
  padding: 14px 16px;
}

.kp-form {
  flex-shrink: 0;
}

.kp-row {
  display: flex;
  flex-wrap: wrap;
  gap: 12px 18px;
  align-items: flex-end;
}

.kp-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 118px;
}

.kp-label {
  font-size: 12px;
  color: #64748b;
  font-weight: 600;
}

.kp-ctrl {
  width: 100%;
}

.kp-wide {
  min-width: 240px;
}

.kp-inline {
  display: flex;
  align-items: center;
  gap: 2px;
}

/* Schema ID 下拉：与主题下拉同款（可输入搜索），宽度容纳 "#id · subject" */
.kp-schema-sel {
  width: 260px;
}

.kp-actions {
  display: flex;
  align-items: flex-end;
  gap: 8px;
  padding-bottom: 2px;
}

.kp-schema-hint {
  display: flex;
  flex-wrap: wrap;
  align-items: center;
  gap: 6px 8px;
  margin-top: 10px;
  padding-top: 10px;
  border-top: 1px dashed #e2e8f0;
  font-size: 12px;
  color: #64748b;
}

.kp-schema-title {
  font-weight: 600;
}

.kp-chip {
  border: 1px solid color-mix(in srgb, var(--nc-primary, #4e7cff) 40%, transparent);
  background: color-mix(in srgb, var(--nc-primary, #4e7cff) 8%, transparent);
  color: var(--nc-primary, #4e7cff);
  border-radius: 999px;
  padding: 1px 10px;
  font-size: 11.5px;
  cursor: pointer;
}

.kp-chip:hover {
  background: color-mix(in srgb, var(--nc-primary, #4e7cff) 16%, transparent);
}

/* 编辑区：key 固定高，value 占满剩余（与发送记录按 flex 比例分配） */
.kp-editors {
  flex: 0 0 auto;
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.kp-editor-head {
  display: flex;
  align-items: center;
  gap: 8px;
  margin-bottom: 4px;
}

.kp-spacer {
  flex: 1;
}

.kp-warn {
  font-size: 11.5px;
  color: #b45309;
  background: color-mix(in srgb, #f59e0b 14%, transparent);
  border-radius: 4px;
  padding: 0 6px;
}

.kp-text :deep(textarea) {
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
  font-size: 12.5px;
  line-height: 1.55;
}

.kp-list {
  flex: 1 0 300px;
  min-height: 300px;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  padding: 10px 12px 12px;
}

.kp-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  padding: 2px 2px 10px;
  border-bottom: 1px solid #eef2f7;
  margin-bottom: 10px;
  font-size: 12px;
  color: #64748b;
}

.kp-meta-item {
  white-space: nowrap;
  font-weight: 600;
}

/* 发送记录：el-table border 模式，表头分隔线可拖动调整列宽 */
.kp-table-fill {
  flex: 1;
  min-height: 0;
}

.kp-rtable {
  font-size: 12.5px;
  line-height: 1.55;
}

.kp-rtable :deep(th.el-table__cell) {
  background: #f8fafc;
  color: #64748b;
  font-size: 12px;
  font-weight: 700;
}

.kp-rtable :deep(.cell) {
  word-break: break-all;
}

.kp-ts {
  font-variant-numeric: tabular-nums;
  color: #64748b;
}

.kp-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12.5px;
  line-height: 1.55;
}

.kp-th {
  position: sticky;
  top: 0;
  z-index: 1;
  background: #f8fafc;
  text-align: left;
  font-size: 12px;
  font-weight: 700;
  color: #64748b;
  padding: 7px 10px;
  border-bottom: 1px solid #eef2f7;
  white-space: nowrap;
}

.kp-th.num {
  text-align: right;
}

.kp-tr:hover .kp-td {
  background: #f8fafc;
}

.kp-td {
  padding: 5px 10px;
  border-bottom: 1px solid #f1f5f9;
  vertical-align: top;
  max-width: 460px;
}

.kp-td.num {
  text-align: right;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
  color: #334155;
}

.kp-td.ts {
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
  color: #64748b;
}

.kp-td.kv {
  max-width: 420px;
  min-width: 140px;
}

.kp-target {
  font-weight: 600;
  color: #334155;
  word-break: break-all;
}

.kp-sub {
  font-size: 11.5px;
  color: #94a3b8;
}

.kp-fail-msg {
  margin-top: 3px;
  font-size: 11.5px;
  color: #b91c1c;
  word-break: break-word;
}

.kp-cell {
  display: block;
  width: 100%;
  border: 0;
  background: transparent;
  text-align: left;
  font: inherit;
  color: inherit;
  white-space: pre-wrap;
  word-break: break-all;
  cursor: pointer;
  padding: 0;
}

.kp-cell:hover {
  color: var(--nc-primary, #4e7cff);
  text-decoration: underline;
}

.kp-empty {
  padding: 24px 10px;
  text-align: center;
  color: #94a3b8;
  font-size: 12.5px;
}

/* 弹窗 JSON 高亮配色（与 Kafka Explorer / json-formatter 一致） */
.kp-json-pre {
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

.kp-raw-pre {
  margin: 0;
  padding: 14px 16px;
  background: #f8fafc;
  color: #334155;
  border: 1px solid #e2e8f0;
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
  color: #94a3b8;
}

:deep(.kp-dlg .el-dialog__title) {
  font-size: 14px;
  font-weight: 700;
}
</style>
