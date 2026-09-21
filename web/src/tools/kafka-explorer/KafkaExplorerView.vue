<script setup lang="ts">
/**
 * Kafka Explorer：浏览 Kafka 主题消息（kafka-mcp-server REST 网关）。
 * 上部查询条件（environment/topic/partition/deserializer/order/count/top/keyword/minOffset/maxOffset），
 * 下部结果列表（timestampUtc/partition/offset/key/value）；点击 key 或 value 弹窗 JSON 格式化展示，
 * 解析失败则原样展示。
 * 端点地址来自管理端「工具 → API Endpoint」配置（AppTool.BaseUrl）；前端经同源代理
 * /api/ext/tool/kafka-explorer/** 访问，由后端按该配置转发（外部端点通常无 CORS，直连会被浏览器拦截）。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import { kernel } from '@/kernel'
import { copyText } from '@/utils/clipboard'
import { highlightJson } from './jsonHighlight'

const { t } = useI18n()
const TOOL_KEY = 'kafka-explorer'

/** 列表 cell 默认截断长度（完整内容在弹窗中查看） */
const TRUNCATE_LEN = 200
function truncate(text: string): string {
  return text.length > TRUNCATE_LEN ? `${text.slice(0, TRUNCATE_LEN)}…` : text
}

// ---------------- 端点配置（后台 AppTools.BaseUrl，经后端代理访问） ----------------
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
const endpoint = '/api/ext/tool/kafka-explorer'

// ---------------- 查询条件 ----------------
const envs = ref<string[]>([])
const topics = ref<string[]>([])
const partitions = ref<{ id: number; low: number; high: number; messageCount: number }[]>([])
const loadingMeta = ref(false)

const environment = ref('')
const topic = ref('')
const partition = ref<number | null>(null) // null = 全部（consume_topic）
const deserializer = ref('string')
const order = ref('Newest')
const count = ref(20)
const top = ref<number | null>(null)
const keyword = ref('')
const minOffset = ref<number | null>(null)
const maxOffset = ref<number | null>(null)

async function loadEnvs() {
  if (!cfgOk.value) return
  loadingMeta.value = true
  try {
    const r = await http.get<{ success: boolean; data?: { environments: { name: string }[] }; error?: string | null }>(
      `${endpoint}/api/environments`,
    )
    if (!r?.success) throw new Error(r?.error ?? 'list_environments failed')
    const names = (r.data?.environments ?? []).map((e) => e.name)
    envs.value = names
    if (names.length && !names.includes(environment.value)) environment.value = names[0]
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('tools.kafka.metaLoadFailed'), (e as { code?: string }).code)
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
  try {
    const r = await http.get<{ success: boolean; data?: { topics: { name: string }[] }; error?: string | null }>(
      `${endpoint}/api/topics?environment=${encodeURIComponent(environment.value)}`,
    )
    if (!r?.success) throw new Error(r?.error ?? 'list_topics failed')
    const names = (r.data?.topics ?? []).map((x) => x.name)
    topics.value = names
    partition.value = null
    if (names.length && !names.includes(topic.value)) topic.value = names[0]
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('tools.kafka.metaLoadFailed'), (e as { code?: string }).code)
    topics.value = []
  }
}

async function loadPartitions() {
  if (!cfgOk.value || !environment.value || !topic.value) {
    partitions.value = []
    return
  }
  try {
    const r = await http.get<{ success: boolean; data?: { partitions: { id: number; low: number; high: number; messageCount: number }[] }; error?: string | null }>(
      `${endpoint}/api/topics/${encodeURIComponent(topic.value)}/partitions?environment=${encodeURIComponent(environment.value)}`,
    )
    if (!r?.success) throw new Error(r?.error ?? 'list_partitions failed')
    partitions.value = (r.data?.partitions ?? []).map((p) => ({
      id: p.id,
      low: p.low,
      high: p.high,
      messageCount: p.messageCount,
    }))
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('tools.kafka.metaLoadFailed'), (e as { code?: string }).code)
    partitions.value = []
  }
}

watch(environment, () => {
  topic.value = ''
  void loadTopics()
})
watch(topic, () => {
  partition.value = null
  void loadPartitions()
})

// ---------------- 查询与结果 ----------------
interface KfRecord {
  partition: number
  offset: number
  timestampUtc: string
  key: string
  value: string
  valueDeserializer?: string
  decodeError?: boolean
  decodeErrorDetail?: string | null
}

const records = ref<KfRecord[]>([])
const loading = ref(false)
const err = ref<string | null>(null)
const meta = ref<Record<string, unknown> | null>(null)

const canQuery = computed(() => cfgOk.value && !!environment.value && !!topic.value)

async function query() {
  err.value = null
  if (!endpoint) {
    err.value = t('tools.kafka.noEndpoint')
    return
  }
  if (!environment.value || !topic.value) {
    kernel.notify.warning(t('tools.kafka.requiredTip'))
    return
  }
  loading.value = true
  records.value = []
  meta.value = null
  try {
    const q = new URLSearchParams()
    q.set('environment', environment.value)
    if (count.value != null && Number.isFinite(Number(count.value))) q.set('count', String(count.value))
    if (top.value != null && Number.isFinite(Number(top.value))) q.set('top', String(top.value))
    if (keyword.value.trim()) q.set('keyword', keyword.value.trim())
    if (minOffset.value != null && Number.isFinite(Number(minOffset.value))) q.set('minOffset', String(minOffset.value))
    if (maxOffset.value != null && Number.isFinite(Number(maxOffset.value))) q.set('maxOffset', String(maxOffset.value))
    q.set('order', order.value)
    q.set('deserializer', deserializer.value)

    // 有 partition → consume_partition；否则 consume_topic
    const path =
      partition.value != null
        ? `/api/topics/${encodeURIComponent(topic.value)}/partitions/${partition.value}/consume`
        : `/api/topics/${encodeURIComponent(topic.value)}/consume`
    const r = await http.get<{ success: boolean; data?: { records?: KfRecord[] } & Record<string, unknown>; error?: string | null }>(
      `${endpoint}${path}?${q.toString()}`,
    )
    if (!r?.success) {
      err.value = r?.error ?? t('tools.kafka.queryFailed')
      return
    }
    const { records: recs, ...rest } = r.data ?? {}
    records.value = (recs ?? []).map((rec) => ({
      partition: rec.partition,
      offset: rec.offset,
      timestampUtc: rec.timestampUtc,
      key: rec.key ?? '',
      value: rec.value ?? '',
      valueDeserializer: rec.valueDeserializer,
      decodeError: rec.decodeError,
      decodeErrorDetail: rec.decodeErrorDetail,
    }))
    meta.value = rest
    if (!records.value.length) err.value = t('tools.kafka.empty')
  } catch (e) {
    err.value = (e as { message?: string }).message ?? t('tools.kafka.queryFailed')
  } finally {
    loading.value = false
  }
}

// ---------------- 详情弹窗（key/value：JSON 格式化，失败原样） ----------------
const dialog = ref(false)
const dialogTitle = ref('')
const dialogRaw = ref('')
const dialogPretty = ref('')
const dialogHtml = ref('')
const dialogIsJson = ref(false)

function openDetail(which: 'key' | 'value', rec: KfRecord) {
  const text = which === 'key' ? rec.key : rec.value
  dialogTitle.value = `${which} · p${rec.partition}/o${rec.offset}`
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

/** 弹窗复制：JSON 可解析时复制格式化后的展示文本（所见即所得），否则复制原文 */
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
  <div class="kf-page">
    <!-- 上部：查询条件 -->
    <div class="kf-card kf-form">
      <div class="kf-row">
        <label class="kf-field">
          <span class="kf-label">{{ t('tools.kafka.environment') }}</span>
          <el-select v-model="environment" size="small" class="kf-ctrl" :placeholder="t('tools.kafka.environment')" :loading="loadingMeta">
            <el-option v-for="e in envs" :key="e" :label="e" :value="e" />
          </el-select>
        </label>

        <label class="kf-field">
          <span class="kf-label">{{ t('tools.kafka.topic') }} *</span>
          <el-select v-model="topic" size="small" class="kf-ctrl kf-wide" filterable :placeholder="t('tools.kafka.topic')" :loading="loadingMeta">
            <el-option v-for="tp in topics" :key="tp" :label="tp" :value="tp" />
          </el-select>
        </label>

        <label class="kf-field">
          <span class="kf-label">{{ t('tools.kafka.partition') }}</span>
          <el-select v-model="partition" size="small" class="kf-ctrl" clearable :placeholder="t('tools.kafka.allPartitions')">
            <el-option
              v-for="p in partitions"
              :key="p.id"
              :label="`${p.id} · ${p.low}..${p.high} (${p.messageCount})`"
              :value="p.id"
            />
          </el-select>
        </label>

        <label class="kf-field">
          <span class="kf-label">{{ t('tools.kafka.deserializer') }}</span>
          <el-select v-model="deserializer" size="small" class="kf-ctrl">
            <el-option v-for="d in ['string', 'avro']" :key="d" :label="d" :value="d" />
          </el-select>
        </label>

        <label class="kf-field">
          <span class="kf-label">{{ t('tools.kafka.order') }}</span>
          <el-select v-model="order" size="small" class="kf-ctrl">
            <el-option v-for="o in ['Newest', 'Oldest']" :key="o" :label="o" :value="o" />
          </el-select>
        </label>

        <label class="kf-field">
          <span class="kf-label">{{ t('tools.kafka.count') }} (M)</span>
          <el-input-number v-model="count" size="small" class="kf-ctrl" :min="1" :max="2000" :controls="false" />
        </label>

        <label class="kf-field">
          <span class="kf-label">{{ t('tools.kafka.top') }} (N)</span>
          <el-input-number v-model="top" size="small" class="kf-ctrl" :min="1" :max="200" :controls="false" placeholder="-" />
        </label>

        <label class="kf-field">
          <span class="kf-label">{{ t('tools.kafka.keyword') }}</span>
          <el-input v-model="keyword" size="small" class="kf-ctrl" clearable :placeholder="t('tools.kafka.keywordPh')" />
        </label>

        <label class="kf-field">
          <span class="kf-label">{{ t('tools.kafka.minOffset') }}</span>
          <el-input-number v-model="minOffset" size="small" class="kf-ctrl" :min="0" :controls="false" placeholder="-" />
        </label>

        <label class="kf-field">
          <span class="kf-label">{{ t('tools.kafka.maxOffset') }}</span>
          <el-input-number v-model="maxOffset" size="small" class="kf-ctrl" :min="0" :controls="false" placeholder="-" />
        </label>

        <div class="kf-actions">
          <el-button type="primary" size="small" :loading="loading" :disabled="!canQuery" @click="query">
            {{ t('tools.kafka.query') }}
          </el-button>
        </div>
      </div>
    </div>

    <!-- 下部：结果列表 -->
    <div class="kf-card kf-list">
      <div v-if="meta" class="kf-meta">
        <span class="kf-meta-item">tool={{ meta.tool }}</span>
        <span class="kf-meta-item">read={{ meta.read }}</span>
        <span class="kf-meta-item">matched={{ meta.matched }}</span>
        <span class="kf-meta-item">returned={{ meta.top }}</span>
        <span class="kf-meta-item">partitions={{ meta.partitions }}</span>
        <span class="kf-meta-item">{{ meta.tookMs }} ms</span>
        <span v-if="meta.partition != null" class="kf-meta-item">partition={{ meta.partition }}</span>
      </div>

      <div v-if="err && !records.length" class="kf-empty">{{ err }}</div>

      <div v-if="records.length" class="kf-table-wrap kf-scroll">
        <table class="kf-table">
          <thead>
            <tr>
              <th class="kf-th ts">{{ t('tools.kafka.colTimestamp') }}</th>
              <th class="kf-th num">{{ t('tools.kafka.colPartition') }}</th>
              <th class="kf-th num">{{ t('tools.kafka.colOffset') }}</th>
              <th class="kf-th">{{ t('tools.kafka.colKey') }}</th>
              <th class="kf-th">{{ t('tools.kafka.colValue') }}</th>
            </tr>
          </thead>
          <tbody>
            <tr v-for="(rec, i) in records" :key="i" class="kf-tr">
              <td class="kf-td ts">{{ rec.timestampUtc }}</td>
              <td class="kf-td num">{{ rec.partition }}</td>
              <td class="kf-td num">{{ rec.offset }}</td>
              <td class="kf-td kv">
                <button type="button" class="kf-cell" :title="t('tools.kafka.viewJson')" @click="openDetail('key', rec)">
                  {{ rec.key ? truncate(rec.key) : '∅' }}
                </button>
              </td>
              <td class="kf-td kv">
                <button type="button" class="kf-cell" :class="{ err: rec.decodeError }" :title="t('tools.kafka.viewJson')" @click="openDetail('value', rec)">
                  <span v-if="rec.decodeError" class="kf-dec-err">{{ t('tools.kafka.decodeError') }}</span>
                  {{ truncate(rec.value) }}
                </button>
              </td>
            </tr>
          </tbody>
        </table>
      </div>
    </div>

    <!-- 弹窗：key/value JSON 格式化展示 -->
    <el-dialog v-model="dialog" :title="dialogTitle" width="720px" append-to-body destroy-on-close class="kf-dlg">
      <div class="kf-dlg-body">
        <div v-if="dialogIsJson" class="kf-json-pre" v-html="dialogHtml" />
        <pre v-else class="kf-raw-pre">{{ dialogRaw }}</pre>
      </div>
      <template #footer>
        <el-button :disabled="!dialog" @click="dialog = false">{{ t('common.close') }}</el-button>
        <el-button type="primary" :disabled="!dialog" @click="onCopy">
          {{ dialogIsJson ? t('tools.kafka.copyFormatted') : t('tools.common.copy') }}
        </el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.kf-page {
  flex: 1;
  height: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 18px 22px 20px;
  overflow: hidden;
}

.kf-card {
  background: #ffffff;
  border: 1px solid #e2e8f0;
  border-radius: 12px;
  padding: 14px 16px;
}

.kf-form {
  flex-shrink: 0;
}

.kf-row {
  display: flex;
  flex-wrap: wrap;
  gap: 12px 18px;
  align-items: flex-end;
}

.kf-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 118px;
}

.kf-label {
  font-size: 12px;
  color: #64748b;
  font-weight: 600;
}

.kf-ctrl {
  width: 100%;
}

.kf-wide {
  min-width: 220px;
}

.kf-actions {
  display: flex;
  align-items: flex-end;
  padding-bottom: 2px;
}

.kf-list {
  flex: 1;
  min-height: 0;
  display: flex;
  flex-direction: column;
  overflow: hidden;
  padding: 10px 12px 12px;
}

.kf-meta {
  display: flex;
  flex-wrap: wrap;
  gap: 14px;
  padding: 2px 2px 10px;
  border-bottom: 1px solid #eef2f7;
  margin-bottom: 10px;
  font-size: 12px;
  color: #64748b;
}

.kf-meta-item {
  white-space: nowrap;
}

.kf-scroll {
  flex: 1;
  min-height: 0;
  overflow: auto;
}

.kf-table-wrap {
}

.kf-table {
  width: 100%;
  border-collapse: collapse;
  font-size: 12.5px;
  line-height: 1.55;
}

.kf-th {
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

.kf-th.num {
  text-align: right;
}

.kf-tr:hover .kf-td {
  background: #f8fafc;
}

.kf-td {
  padding: 5px 10px;
  border-bottom: 1px solid #f1f5f9;
  vertical-align: top;
  max-width: 460px;
}

.kf-td.num {
  text-align: right;
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
  color: #334155;
}

.kf-td.ts {
  white-space: nowrap;
  font-variant-numeric: tabular-nums;
  color: #64748b;
}

.kf-td.kv {
  max-width: 560px;
  min-width: 140px;
}

.kf-cell {
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

.kf-cell:hover {
  color: var(--nc-primary, #4e7cff);
  text-decoration: underline;
}

.kf-cell.err {
  color: #b91c1c;
}

.kf-dec-err {
  display: inline-block;
  font-size: 11px;
  color: #b91c1c;
  background: color-mix(in srgb, #ef4444 12%, transparent);
  border-radius: 4px;
  padding: 0 5px;
  margin-right: 6px;
  font-weight: 600;
}

.kf-empty {
  padding: 24px 10px;
  text-align: center;
  color: #94a3b8;
  font-size: 12.5px;
}

/* 弹窗 JSON 高亮配色（与 json-formatter 一致） */
.kf-json-pre {
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

.kf-raw-pre {
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

:deep(.kf-dlg .el-dialog__title) {
  font-size: 14px;
  font-weight: 700;
}
</style>
