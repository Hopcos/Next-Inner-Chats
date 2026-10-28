<script setup lang="ts">
/**
 * Schema Registry Explorer：模式注册表浏览工具。
 * 对接 kafka-producer-mcp-server REST 网关（/api/environments、/api/schemas、/api/schemas/{schemaId}）。
 * 端点地址与授权头来自管理端「工具 → API Endpoint / 授权头」（AppTool.BaseUrl / HeadersJson），
 * 前端经同源代理 /api/ext/tool/schema-registry-explorer/** 访问。
 * 上部：环境、关键字、所有版本（请求 /api/schemas）；
 * 下部左右分栏（SplitPane 拖动调整）：左侧块状展示 Schema 列表（schemaId、subject、version），
 * 点击左侧项触发调用 /api/schemas/{schemaId} 并在右侧以 JSON 格式化展示。
 */
import { computed, onMounted, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import { kernel } from '@/kernel'
import { copyText } from '@/utils/clipboard'
import SplitPane from '../components/SplitPane.vue'
import { highlightJson } from './jsonHighlight'
import { prettyJson } from '../kafka-explorer/prettyJson'

const { t } = useI18n()
const TOOL_KEY = 'schema-registry-explorer'

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
const endpoint = '/api/ext/tool/schema-registry-explorer'

/** 上游统一信封：{ success, tool, data, error, durationMs } */
interface Envelope<T = unknown> {
  success: boolean
  data?: T
  error?: string | null
  durationMs?: number
}

async function getEnv<T>(path: string): Promise<Envelope<T>> {
  const r = await http.get<Envelope<T>>(`${endpoint}${path}`)
  if (!r?.success) throw new Error(r?.error ?? 'request failed')
  return r
}

// ---------------- 基本条件 ----------------
interface EnvironmentItem {
  name: string
  hasSchemaRegistry: boolean
  toolDescription?: string
}

interface SchemaSummaryItem {
  subject: string
  version: number
  schemaId: number
  schemaType?: string
  schemaName?: string
}

const envs = ref<EnvironmentItem[]>([])
const loadingEnvs = ref(false)
const environment = ref('')
const keyword = ref('')
const allVersions = ref(false)

const curEnv = computed(() => envs.value.find((e) => e.name === environment.value))
const schemaRegistryDisabled = computed(() => !!curEnv.value && !curEnv.value.hasSchemaRegistry)

async function loadEnvs() {
  if (!cfgOk.value) return
  loadingEnvs.value = true
  try {
    const r = await getEnv<{ environments: EnvironmentItem[] }>('/api/environments')
    const list = r.data?.environments ?? []
    envs.value = list
    // 优先选中具备 Schema Registry 的环境（如 dm-prod），否则选第一个
    const withSr = list.find((e) => e.hasSchemaRegistry)
    if (withSr) {
      environment.value = withSr.name
    } else if (list.length) {
      environment.value = list[0].name
    }
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('tools.schemaRegistry.loadEnvsFailed'), (e as { code?: string }).code)
  } finally {
    loadingEnvs.value = false
  }
}

// ---------------- 列表查询（/api/schemas） ----------------
const schemas = ref<SchemaSummaryItem[]>([])
const loadingSchemas = ref(false)
const schemaCount = ref(0)
const totalSubjects = ref<number | null>(null)
const matchedSubjects = ref<number | null>(null)
const truncated = ref(false)
const listErr = ref<string | null>(null)
let querySeq = 0

async function querySchemas() {
  if (!cfgOk.value || !environment.value) return
  if (schemaRegistryDisabled.value) {
    schemas.value = []
    schemaCount.value = 0
    selectedId.value = null
    detailData.value = null
    listErr.value = t('tools.schemaRegistry.noRegistryEnv')
    return
  }
  const seq = ++querySeq
  loadingSchemas.value = true
  listErr.value = null
  try {
    const params = new URLSearchParams()
    params.set('environment', environment.value)
    if (keyword.value.trim()) params.set('keyword', keyword.value.trim())
    if (allVersions.value) params.set('allVersions', 'true')
    const r = await getEnv<{
      schemas: SchemaSummaryItem[]
      schemaCount: number
      totalSubjects?: number
      matchedSubjects?: number
      truncated?: boolean
    }>(`/api/schemas?${params.toString()}`)

    if (seq !== querySeq) return
    schemas.value = r.data?.schemas ?? []
    schemaCount.value = r.data?.schemaCount ?? schemas.value.length
    totalSubjects.value = r.data?.totalSubjects ?? null
    matchedSubjects.value = r.data?.matchedSubjects ?? null
    truncated.value = !!r.data?.truncated

    // 默认保持选中；如果当前选中的不在新列表，则重置
    if (selectedId.value != null && !schemas.value.some((s) => s.schemaId === selectedId.value)) {
      selectedId.value = null
      detailData.value = null
      detailPretty.value = ''
      detailHtml.value = ''
    }
    // 如果无选中项且有结果，自动加载第一项
    if (selectedId.value == null && schemas.value.length > 0) {
      void selectSchema(schemas.value[0])
    }
  } catch (e) {
    if (seq === querySeq) {
      listErr.value = (e as { message?: string }).message ?? t('tools.schemaRegistry.queryFailed')
      schemas.value = []
      schemaCount.value = 0
    }
  } finally {
    if (seq === querySeq) loadingSchemas.value = false
  }
}

// ---------------- 详情查询（/api/schemas/{schemaId}） ----------------
interface SchemaDetail {
  environment: string
  schemaId: number
  schemaType?: string
  schemaName?: string
  fields?: { name: string; type: string; nullable: boolean; hasDefault: boolean }[]
  schema?: string | unknown
  [key: string]: unknown
}

const selectedId = ref<number | null>(null)
const selectedSubject = ref<string>('')
const loadingDetail = ref(false)
const detailErr = ref<string | null>(null)
const detailData = ref<SchemaDetail | null>(null)
const detailPretty = ref('')
const detailHtml = ref('')
const viewMode = ref<'formatted' | 'raw'>('formatted')
let detailSeq = 0

async function selectSchema(item: SchemaSummaryItem) {
  selectedId.value = item.schemaId
  selectedSubject.value = item.subject
  detailErr.value = null
  loadingDetail.value = true
  const seq = ++detailSeq
  try {
    const r = await getEnv<SchemaDetail>(
      `/api/schemas/${item.schemaId}?environment=${encodeURIComponent(environment.value)}`,
    )
    if (seq !== detailSeq) return
    const d = r.data ?? null
    detailData.value = d
    if (d) {
      // 若返回的 d.schema 是 JSON 字符串，反序列化成对象统一展示，避免嵌套字符串转义
      let displayTarget: unknown = d
      if (typeof d.schema === 'string') {
        try {
          const parsedSchema = JSON.parse(d.schema)
          displayTarget = { ...d, schema: parsedSchema }
        } catch {
          displayTarget = d
        }
      }
      const rawText = JSON.stringify(displayTarget, null, 2)
      detailPretty.value = prettyJson(rawText)
      detailHtml.value = highlightJson(detailPretty.value)
    } else {
      detailPretty.value = ''
      detailHtml.value = ''
    }
  } catch (e) {
    if (seq === detailSeq) {
      detailErr.value = (e as { message?: string }).message ?? t('tools.schemaRegistry.detailFailed')
      detailData.value = null
      detailPretty.value = ''
      detailHtml.value = ''
    }
  } finally {
    if (seq === detailSeq) loadingDetail.value = false
  }
}

async function onCopy() {
  if (!detailPretty.value) return
  const ok = await copyText(detailPretty.value)
  if (ok) kernel.notify.success(t('tools.common.copied'))
  else kernel.notify.error(t('tools.common.copyFailed'))
}

watch(environment, () => {
  void querySchemas()
})

onMounted(async () => {
  await loadBaseUrl()
  if (cfgOk.value) {
    await loadEnvs()
    await querySchemas()
  }
})
</script>

<template>
  <div class="sre-page">
    <!-- 未配置提示 -->
    <div v-if="!cfgOk" class="sre-card sre-tip">
      <div class="sre-tip-title">{{ t('tools.schemaRegistry.notConfigured') }}</div>
      <div class="sre-tip-desc">{{ t('tools.schemaRegistry.notConfiguredDesc') }}</div>
    </div>

    <!-- 上部：基本条件 -->
    <div v-else class="sre-card sre-form">
      <div class="sre-row">
        <label class="sre-field">
          <span class="sre-label">{{ t('tools.schemaRegistry.environment') }}</span>
          <el-select
            v-model="environment"
            size="small"
            class="sre-ctrl sre-env-select"
            :placeholder="t('tools.schemaRegistry.environment')"
            :loading="loadingEnvs"
          >
            <el-option
              v-for="e in envs"
              :key="e.name"
              :label="e.name + (e.hasSchemaRegistry ? '' : ` (${t('tools.schemaRegistry.noRegistryShort')})`)"
              :value="e.name"
            />
          </el-select>
        </label>

        <label class="sre-field sre-keyword-field">
          <span class="sre-label">{{ t('tools.schemaRegistry.keyword') }}</span>
          <el-input
            v-model="keyword"
            size="small"
            class="sre-ctrl"
            clearable
            :placeholder="t('tools.schemaRegistry.keywordPh')"
            @keyup.enter="querySchemas"
          />
        </label>

        <label class="sre-field sre-checkbox-field">
          <span class="sre-label">&nbsp;</span>
          <el-checkbox v-model="allVersions" size="small" class="sre-checkbox">
            {{ t('tools.schemaRegistry.allVersions') }}
          </el-checkbox>
        </label>

        <div class="sre-actions">
          <el-button
            type="primary"
            size="small"
            :loading="loadingSchemas"
            :disabled="!environment || schemaRegistryDisabled"
            @click="querySchemas"
          >
            {{ t('tools.schemaRegistry.query') }}
          </el-button>
        </div>
      </div>

      <div v-if="schemaRegistryDisabled" class="sre-warn">
        {{ t('tools.schemaRegistry.noRegistryEnv') }}
      </div>
    </div>

    <!-- 下部：左右可拖拽分栏 -->
    <div v-if="cfgOk" class="sre-main">
      <SplitPane
        :left-title="t('tools.schemaRegistry.schemasListTitle', { count: schemaCount })"
        :right-title="selectedId ? t('tools.schemaRegistry.detailTitle', { id: selectedId, subject: selectedSubject }) : t('tools.schemaRegistry.detailEmptyTitle')"
      >
        <!-- 左侧：Schema 块列表 -->
        <template #left>
          <div class="sre-left-wrap">
            <div v-if="totalSubjects != null || matchedSubjects != null" class="sre-list-meta">
              <span v-if="matchedSubjects != null">{{ t('tools.schemaRegistry.matched') }}: {{ matchedSubjects }}</span>
              <span v-if="totalSubjects != null">{{ t('tools.schemaRegistry.total') }}: {{ totalSubjects }}</span>
              <span v-if="truncated" class="sre-badge-warn">{{ t('tools.schemaRegistry.truncated') }}</span>
            </div>

            <div v-if="loadingSchemas" class="sre-state-hint">
              {{ t('tools.schemaRegistry.loading') }}
            </div>
            <div v-else-if="listErr" class="sre-state-err">
              {{ listErr }}
            </div>
            <div v-else-if="!schemas.length" class="sre-state-hint">
              {{ t('tools.schemaRegistry.empty') }}
            </div>

            <div v-else class="sre-items-container nc-scroll">
              <div
                v-for="s in schemas"
                :key="`${s.schemaId}-${s.subject}-${s.version}`"
                class="sre-item-card"
                :class="{ active: s.schemaId === selectedId }"
                @click="selectSchema(s)"
              >
                <div class="sre-item-header">
                  <span class="sre-item-id">#{{ s.schemaId }}</span>
                  <span class="sre-item-version">v{{ s.version }}</span>
                  <span v-if="s.schemaType" class="sre-item-type">{{ s.schemaType }}</span>
                </div>
                <div class="sre-item-subject" :title="s.subject">{{ s.subject }}</div>
                <div v-if="s.schemaName" class="sre-item-name" :title="s.schemaName">{{ s.schemaName }}</div>
              </div>
            </div>
          </div>
        </template>

        <!-- 右侧：选中的 Schema JSON 详细 -->
        <template #right>
          <div class="sre-right-wrap">
            <div class="sre-right-bar">
              <div class="sre-right-info">
                <span v-if="selectedId" class="sre-tag">ID: {{ selectedId }}</span>
                <span v-if="detailData?.schemaType" class="sre-tag">{{ detailData.schemaType }}</span>
                <span v-if="detailData?.fields?.length" class="sre-tag">{{ detailData.fields.length }} {{ t('tools.schemaRegistry.fieldsCount') }}</span>
              </div>
              <div class="sre-right-actions">
                <el-radio-group v-model="viewMode" size="small">
                  <el-radio-button value="formatted">{{ t('tools.schemaRegistry.formatted') }}</el-radio-button>
                  <el-radio-button value="raw">{{ t('tools.schemaRegistry.raw') }}</el-radio-button>
                </el-radio-group>
                <el-button size="small" :disabled="!detailPretty" @click="onCopy">
                  {{ t('tools.schemaRegistry.copyJson') }}
                </el-button>
              </div>
            </div>

            <div v-if="loadingDetail" class="sre-state-hint sre-detail-state">
              {{ t('tools.schemaRegistry.loadingDetail') }}
            </div>
            <div v-else-if="detailErr" class="sre-state-err sre-detail-state">
              {{ detailErr }}
            </div>
            <div v-else-if="!selectedId" class="sre-state-hint sre-detail-state">
              {{ t('tools.schemaRegistry.selectPrompt') }}
            </div>
            <div v-else class="sre-detail-body nc-scroll">
              <pre v-if="viewMode === 'formatted'" class="sre-json-pre mono" v-html="detailHtml" />
              <pre v-else class="sre-raw-pre mono">{{ detailPretty }}</pre>
            </div>
          </div>
        </template>
      </SplitPane>
    </div>
  </div>
</template>

<style scoped>
.sre-page {
  flex: 1;
  width: 100%;
  height: 100%;
  min-height: 0;
  display: flex;
  flex-direction: column;
  gap: 14px;
  padding: 18px 22px 20px;
  overflow: hidden;
  box-sizing: border-box;
}

.sre-card {
  background: var(--nc-surface-solid);
  border: 1px solid var(--nc-border-strong);
  border-radius: 12px;
  padding: 14px 16px;
}

.sre-tip {
  color: var(--nc-text-dim);
}
.sre-tip-title {
  font-size: 14px;
  font-weight: 700;
  color: var(--nc-text);
  margin-bottom: 6px;
}
.sre-tip-desc {
  font-size: 13px;
  line-height: 1.6;
}

.sre-form {
  flex-shrink: 0;
}

.sre-row {
  display: flex;
  flex-wrap: wrap;
  gap: 12px 18px;
  align-items: flex-end;
}

.sre-field {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 140px;
}

.sre-keyword-field {
  width: 520px;
}

.sre-env-select {
  width: 180px;
}

.sre-checkbox-field {
  min-width: auto;
  justify-content: flex-end;
}

.sre-checkbox {
  height: 32px;
  display: flex;
  align-items: center;
  color: var(--nc-text-dim);
}

.sre-label {
  font-size: 12px;
  color: var(--nc-text-dim);
  font-weight: 600;
}

.sre-ctrl {
  width: 100%;
}

.sre-actions {
  display: flex;
  align-items: flex-end;
  padding-bottom: 2px;
}

.sre-warn {
  margin-top: 10px;
  font-size: 12px;
  color: var(--nc-danger, #ff4d6d);
}

.sre-main {
  flex: 1;
  min-height: 0;
  width: 100%;
  display: flex;
}

/* 左侧列表 */
.sre-left-wrap {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  width: 100%;
  overflow: hidden;
}

.sre-list-meta {
  display: flex;
  align-items: center;
  gap: 12px;
  padding: 8px 12px;
  border-bottom: 1px solid var(--nc-border);
  font-size: 11.5px;
  color: var(--nc-text-dim);
  background: var(--nc-surface-2);
}

.sre-badge-warn {
  color: #f59e0b;
  font-weight: 600;
}

.sre-items-container {
  flex: 1;
  min-height: 0;
  overflow-y: auto;
  padding: 10px 12px;
  display: flex;
  flex-direction: column;
  gap: 8px;
}

.sre-item-card {
  border: 1px solid var(--nc-border);
  background: var(--nc-surface-2);
  border-radius: 8px;
  padding: 9px 12px;
  cursor: pointer;
  transition: all 0.15s ease;
  display: flex;
  flex-direction: column;
  gap: 4px;
}

.sre-item-card:hover {
  border-color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary, #00f0ff) 8%, var(--nc-surface-2));
}

.sre-item-card.active {
  border-color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary, #00f0ff) 14%, var(--nc-surface-solid));
  box-shadow: inset 3px 0 0 var(--nc-primary);
}

.sre-item-header {
  display: flex;
  align-items: center;
  gap: 8px;
  font-size: 12px;
}

.sre-item-id {
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
  font-weight: 700;
  color: var(--nc-primary);
}

.sre-item-version {
  font-size: 11px;
  font-weight: 600;
  background: color-mix(in srgb, var(--nc-primary, #00f0ff) 15%, transparent);
  color: var(--nc-primary);
  padding: 0 6px;
  border-radius: 4px;
}

.sre-item-type {
  font-size: 10.5px;
  color: var(--nc-text-faint);
  margin-left: auto;
}

.sre-item-subject {
  font-size: 13px;
  font-weight: 600;
  color: var(--nc-text);
  word-break: break-all;
  line-height: 1.35;
}

.sre-item-name {
  font-size: 11px;
  color: var(--nc-text-dim);
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
  word-break: break-all;
}

/* 右侧详情 */
.sre-right-wrap {
  display: flex;
  flex-direction: column;
  height: 100%;
  min-height: 0;
  width: 100%;
  overflow: hidden;
}

.sre-right-bar {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 6px 12px;
  border-bottom: 1px solid var(--nc-border);
  background: var(--nc-surface-2);
  flex-shrink: 0;
}

.sre-right-info {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.sre-tag {
  font-size: 11px;
  padding: 1px 7px;
  border-radius: 4px;
  background: var(--nc-surface-solid);
  border: 1px solid var(--nc-border);
  color: var(--nc-text-dim);
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
}

.sre-right-actions {
  display: flex;
  align-items: center;
  gap: 8px;
}

.sre-detail-body {
  flex: 1;
  min-height: 0;
  overflow: auto;
  padding: 14px 16px;
  background: var(--nc-code-bg, rgba(5, 2, 14, 0.85));
}

.sre-json-pre,
.sre-raw-pre {
  margin: 0;
  font-size: 12.5px;
  line-height: 1.65;
  white-space: pre-wrap;
  word-break: break-all;
  color: var(--nc-text);
}

.mono {
  font-family: 'JetBrains Mono', 'Cascadia Code', Consolas, Menlo, monospace;
}

.sre-state-hint,
.sre-state-err {
  padding: 24px 16px;
  text-align: center;
  font-size: 13px;
  color: var(--nc-text-dim);
}

.sre-state-err {
  color: var(--nc-danger, #ff4d6d);
}

.sre-detail-state {
  margin: auto;
}

/* 高亮样式（与现有工具配色完全对齐） */
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
</style>
