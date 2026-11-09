<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import type { UsageTotals } from '@/api/types'
import { kernel } from '@/kernel'

const { t } = useI18n()

interface UsageByDayRow { day: string; tokens: number; cost: number; requests: number }
interface UsageResp { totals: UsageTotals; byDay: UsageByDayRow[] }
interface UserUsageRow {
  userId: string
  userName: string
  requests: number
  tokens: number
  cost: number
  lastRequestAt: string
  lastLoginAt?: string | null
}

const usage = ref<UsageResp | null>(null)
const users = ref<UserUsageRow[]>([])
const loading = ref(false)

// 时间范围：1d(1天), 3d(3天), 7d(7天), 1m(1个月), 3m(3个月), 6m(6个月), 1y(1年), 3y(3年), custom(自定义)
const selectedRange = ref('7d')
const customDates = ref<[Date, Date] | null>(null)

// 趋势视图：chart(折线图) / table(数据表)
const trendView = ref<'chart' | 'table'>('chart')
// 选中指标：all(全部对比), tokens, cost, requests
const selectedMetric = ref<'all' | 'tokens' | 'cost' | 'requests'>('all')

// 数据表分页状态
const tablePage = ref(1)
const tablePageSize = ref(10)

const pagedByDay = computed(() => {
  const rows = usage.value?.byDay ?? []
  const start = (tablePage.value - 1) * tablePageSize.value
  return rows.slice(start, start + tablePageSize.value)
})

const rangeOptions = [
  { value: '1d', labelKey: 'admin.metrics.range1d', days: 1 },
  { value: '3d', labelKey: 'admin.metrics.range3d', days: 3 },
  { value: '7d', labelKey: 'admin.metrics.range7d', days: 7 },
  { value: '1m', labelKey: 'admin.metrics.range1m', days: 30 },
  { value: '3m', labelKey: 'admin.metrics.range3m', days: 90 },
  { value: '6m', labelKey: 'admin.metrics.range6m', days: 180 },
  { value: '1y', labelKey: 'admin.metrics.range1y', days: 365 },
  { value: '3y', labelKey: 'admin.metrics.range3y', days: 1095 },
  { value: 'custom', labelKey: 'admin.metrics.customRange', days: 0 },
]

function getTimeWindow(): { from: number; to: number } {
  const now = Date.now()
  if (selectedRange.value === 'custom') {
    if (customDates.value && customDates.value[0] && customDates.value[1]) {
      const from = new Date(customDates.value[0]).setHours(0, 0, 0, 0)
      const to = new Date(customDates.value[1]).setHours(23, 59, 59, 999)
      return { from, to }
    }
    return { from: now - 7 * 86400000, to: now + 86400000 }
  }

  const opt = rangeOptions.find(o => o.value === selectedRange.value)
  const days = opt ? opt.days : 7
  return {
    from: now - days * 86400000,
    to: now + 86400000,
  }
}

async function load() {
  loading.value = true
  try {
    const { from, to } = getTimeWindow()
    const [u, us] = await Promise.all([
      http.get<UsageResp>(`/api/admin/metrics/usage?from=${from}&to=${to}&take=10000`),
      http.get<UserUsageRow[]>(`/api/admin/metrics/usage/users?from=${from}&to=${to}&take=10000`),
    ])
    usage.value = u
    users.value = us
    tablePage.value = 1
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.metrics.loadFailed'), (e as { code?: string }).code)
  } finally {
    loading.value = false
  }
}

function onRangeChange() {
  if (selectedRange.value !== 'custom') {
    load()
  }
}

function onCustomDateChange() {
  if (customDates.value && customDates.value[0] && customDates.value[1]) {
    load()
  }
}

function fmtTime(v?: string | null) {
  return v ? new Date(v).toLocaleString() : '—'
}

const cards = [
  { key: 'requests' as const, labelKey: 'admin.metrics.requests', fmt: (v: number) => `${v}` },
  { key: 'totalTokens' as const, labelKey: 'admin.metrics.totalTokens', fmt: (v: number) => v.toLocaleString() },
  { key: 'promptTokens' as const, labelKey: 'admin.metrics.promptTokens', fmt: (v: number) => v.toLocaleString() },
  { key: 'completionTokens' as const, labelKey: 'admin.metrics.completionTokens', fmt: (v: number) => v.toLocaleString() },
  { key: 'cost' as const, labelKey: 'admin.metrics.cost', fmt: (v: number) => v.toFixed(4) },
  { key: 'avgTtftMs' as const, labelKey: 'admin.metrics.avgTtftMs', fmt: (v: number) => `${v}` },
  { key: 'avgTotalMs' as const, labelKey: 'admin.metrics.avgTotalMs', fmt: (v: number) => `${v}` },
  { key: 'toolCalls' as const, labelKey: 'admin.metrics.toolCalls', fmt: (v: number) => `${v}` },
  { key: 'toolErrors' as const, labelKey: 'admin.metrics.toolErrors', fmt: (v: number) => `${v}` },
  { key: 'approvals' as const, labelKey: 'admin.metrics.approvals', fmt: (v: number) => `${v}` },
]

// =================== SVG 折线图计算逻辑 ===================
const hoveredIndex = ref<number | null>(null)

interface ChartPoint {
  x: number
  yTokens: number
  yCost: number
  yRequests: number
  data: UsageByDayRow
}

const chartData = computed(() => {
  const rows = usage.value?.byDay ?? []
  if (rows.length === 0) return null

  const width = 1000
  const height = 300
  const padding = { top: 30, right: 30, bottom: 40, left: 60 }
  const plotW = width - padding.left - padding.right
  const plotH = height - padding.top - padding.bottom

  const maxTokens = Math.max(...rows.map(r => r.tokens), 1)
  const maxCost = Math.max(...rows.map(r => r.cost), 0.01)
  const maxRequests = Math.max(...rows.map(r => r.requests), 1)

  const n = rows.length
  const stepX = n > 1 ? plotW / (n - 1) : plotW / 2

  const points: ChartPoint[] = rows.map((r, i) => {
    const x = n > 1 ? padding.left + i * stepX : padding.left + plotW / 2
    const yTokens = padding.top + plotH - (r.tokens / maxTokens) * plotH
    const yCost = padding.top + plotH - (r.cost / maxCost) * plotH
    const yRequests = padding.top + plotH - (r.requests / maxRequests) * plotH
    return { x, yTokens, yCost, yRequests, data: r }
  })

  function makePolyline(pts: { x: number; y: number }[]): string {
    return pts.map(p => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' ')
  }

  function makeArea(pts: { x: number; y: number }[]): string {
    if (pts.length === 0) return ''
    const bottomY = padding.top + plotH
    const firstX = pts[0].x.toFixed(1)
    const lastX = pts[pts.length - 1].x.toFixed(1)
    const line = pts.map(p => `${p.x.toFixed(1)},${p.y.toFixed(1)}`).join(' ')
    return `M ${firstX},${bottomY} L ${line} L ${lastX},${bottomY} Z`
  }

  const polyTokens = makePolyline(points.map(p => ({ x: p.x, y: p.yTokens })))
  const polyCost = makePolyline(points.map(p => ({ x: p.x, y: p.yCost })))
  const polyRequests = makePolyline(points.map(p => ({ x: p.x, y: p.yRequests })))

  const areaTokens = makeArea(points.map(p => ({ x: p.x, y: p.yTokens })))
  const areaCost = makeArea(points.map(p => ({ x: p.x, y: p.yCost })))
  const areaRequests = makeArea(points.map(p => ({ x: p.x, y: p.yRequests })))

  // 辅助网格线（横向 4 条）
  const gridLines = [0, 0.25, 0.5, 0.75, 1].map(ratio => {
    const y = padding.top + plotH - ratio * plotH
    return {
      y,
      valTokens: Math.round(maxTokens * ratio),
      valCost: (maxCost * ratio).toFixed(2),
      valRequests: Math.round(maxRequests * ratio),
    }
  })

  // X 轴标签抽样（防止过多重叠）
  const maxLabels = 10
  const labelStep = Math.max(1, Math.ceil(n / maxLabels))
  const xLabels = points.filter((_, idx) => idx % labelStep === 0 || idx === n - 1)

  return {
    width,
    height,
    padding,
    plotW,
    plotH,
    points,
    polyTokens,
    polyCost,
    polyRequests,
    areaTokens,
    areaCost,
    areaRequests,
    gridLines,
    xLabels,
    maxTokens,
    maxCost,
    maxRequests,
  }
})

onMounted(load)
</script>

<template>
  <div v-loading="loading">
    <div class="head">
      <div class="title-wrap">
        <h3>{{ t('admin.metrics.title') }}</h3>
      </div>
      <div class="toolbar">
        <el-radio-group v-model="selectedRange" size="small" @change="onRangeChange">
          <el-radio-button v-for="opt in rangeOptions" :key="opt.value" :value="opt.value">
            {{ t(opt.labelKey) }}
          </el-radio-button>
        </el-radio-group>

        <el-date-picker
          v-if="selectedRange === 'custom'"
          v-model="customDates"
          type="daterange"
          size="small"
          range-separator="~"
          :start-placeholder="t('admin.metrics.startDate')"
          :end-placeholder="t('admin.metrics.endDate')"
          @change="onCustomDateChange"
          style="width: 240px"
        />

        <el-button size="small" @click="load">{{ t('common.refresh') }}</el-button>
      </div>
    </div>

    <template v-if="usage">
      <div class="cards">
        <div v-for="c in cards" :key="c.key" class="stat-card">
          <div class="stat-label nc-dim">{{ t(c.labelKey) }}</div>
          <div class="stat-value">{{ c.fmt(usage.totals[c.key as keyof UsageTotals] as number) }}</div>
        </div>
      </div>

      <!-- 按日趋势卡片：支持折线图与表格切换 -->
      <el-card class="table-card" shadow="never">
        <template #header>
          <div class="card-header">
            <span class="card-title">{{ t('admin.metrics.dayTrend') }}</span>
            <div class="header-actions">
              <!-- 指标筛选（折线图模式下生效） -->
              <el-radio-group v-if="trendView === 'chart'" v-model="selectedMetric" size="small">
                <el-radio-button value="all">{{ t('common.all') }}</el-radio-button>
                <el-radio-button value="tokens">Tokens</el-radio-button>
                <el-radio-button value="cost">{{ t('admin.metrics.cost') }}</el-radio-button>
                <el-radio-button value="requests">{{ t('admin.metrics.requests') }}</el-radio-button>
              </el-radio-group>

              <!-- 视图切换：折线图 / 数据表 -->
              <el-radio-group v-model="trendView" size="small">
                <el-radio-button value="chart">{{ t('admin.metrics.chartView') }}</el-radio-button>
                <el-radio-button value="table">{{ t('admin.metrics.tableView') }}</el-radio-button>
              </el-radio-group>
            </div>
          </div>
        </template>

        <!-- 折线图展示 -->
        <div v-if="trendView === 'chart'" class="chart-wrapper">
          <template v-if="chartData && chartData.points.length > 0">
            <!-- 图例栏 -->
            <div class="chart-legend">
              <span v-if="selectedMetric === 'all' || selectedMetric === 'tokens'" class="legend-item tokens">
                <span class="legend-dot" /> Tokens
              </span>
              <span v-if="selectedMetric === 'all' || selectedMetric === 'cost'" class="legend-item cost">
                <span class="legend-dot" /> {{ t('admin.metrics.cost') }} ($)
              </span>
              <span v-if="selectedMetric === 'all' || selectedMetric === 'requests'" class="legend-item requests">
                <span class="legend-dot" /> {{ t('admin.metrics.requests') }}
              </span>
            </div>

            <!-- 响应式 SVG 画布 -->
            <div class="svg-container">
              <svg :viewBox="`0 0 ${chartData.width} ${chartData.height}`" class="metrics-svg" preserveAspectRatio="none">
                <defs>
                  <linearGradient id="grad-tokens" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0%" stop-color="#409eff" stop-opacity="0.28" />
                    <stop offset="100%" stop-color="#409eff" stop-opacity="0.01" />
                  </linearGradient>
                  <linearGradient id="grad-cost" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0%" stop-color="#e6a23c" stop-opacity="0.28" />
                    <stop offset="100%" stop-color="#e6a23c" stop-opacity="0.01" />
                  </linearGradient>
                  <linearGradient id="grad-requests" x1="0" y1="0" x2="0" y2="1">
                    <stop offset="0%" stop-color="#67c23a" stop-opacity="0.28" />
                    <stop offset="100%" stop-color="#67c23a" stop-opacity="0.01" />
                  </linearGradient>
                </defs>

                <!-- 水平网格线 -->
                <g class="grid-lines">
                  <line
                    v-for="(gl, idx) in chartData.gridLines"
                    :key="idx"
                    :x1="chartData.padding.left"
                    :y1="gl.y"
                    :x2="chartData.width - chartData.padding.right"
                    :y2="gl.y"
                    stroke="var(--el-border-color-lighter, #e4e7ed)"
                    stroke-dasharray="3 3"
                    stroke-width="1"
                  />
                </g>

                <!-- 渐变面积填充 -->
                <path
                  v-if="selectedMetric === 'tokens'"
                  :d="chartData.areaTokens"
                  fill="url(#grad-tokens)"
                />
                <path
                  v-if="selectedMetric === 'cost'"
                  :d="chartData.areaCost"
                  fill="url(#grad-cost)"
                />
                <path
                  v-if="selectedMetric === 'requests'"
                  :d="chartData.areaRequests"
                  fill="url(#grad-requests)"
                />

                <!-- 折线 (Tokens) -->
                <polyline
                  v-if="selectedMetric === 'all' || selectedMetric === 'tokens'"
                  :points="chartData.polyTokens"
                  fill="none"
                  stroke="#409eff"
                  stroke-width="2.2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                />

                <!-- 折线 (Cost) -->
                <polyline
                  v-if="selectedMetric === 'all' || selectedMetric === 'cost'"
                  :points="chartData.polyCost"
                  fill="none"
                  stroke="#e6a23c"
                  stroke-width="2.2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                />

                <!-- 折线 (Requests) -->
                <polyline
                  v-if="selectedMetric === 'all' || selectedMetric === 'requests'"
                  :points="chartData.polyRequests"
                  fill="none"
                  stroke="#67c23a"
                  stroke-width="2.2"
                  stroke-linecap="round"
                  stroke-linejoin="round"
                />

                <!-- 数据点交互层 -->
                <g v-for="(p, idx) in chartData.points" :key="idx" class="point-group">
                  <!-- 悬停指示竖线 -->
                  <line
                    v-if="hoveredIndex === idx"
                    :x1="p.x"
                    :y1="chartData.padding.top"
                    :x2="p.x"
                    :y2="chartData.padding.top + chartData.plotH"
                    stroke="var(--el-text-color-secondary, #909399)"
                    stroke-width="1"
                    stroke-dasharray="2 2"
                  />

                  <!-- Tokens 点 -->
                  <circle
                    v-if="selectedMetric === 'all' || selectedMetric === 'tokens'"
                    :cx="p.x"
                    :cy="p.yTokens"
                    :r="hoveredIndex === idx ? 5 : 3.5"
                    fill="#409eff"
                    stroke="var(--el-bg-color-overlay, #ffffff)"
                    stroke-width="1.8"
                  />

                  <!-- Cost 点 -->
                  <circle
                    v-if="selectedMetric === 'all' || selectedMetric === 'cost'"
                    :cx="p.x"
                    :cy="p.yCost"
                    :r="hoveredIndex === idx ? 5 : 3.5"
                    fill="#e6a23c"
                    stroke="var(--el-bg-color-overlay, #ffffff)"
                    stroke-width="1.8"
                  />

                  <!-- Requests 点 -->
                  <circle
                    v-if="selectedMetric === 'all' || selectedMetric === 'requests'"
                    :cx="p.x"
                    :cy="p.yRequests"
                    :r="hoveredIndex === idx ? 5 : 3.5"
                    fill="#67c23a"
                    stroke="var(--el-bg-color-overlay, #ffffff)"
                    stroke-width="1.8"
                  />

                  <!-- 悬停感应透明热区 -->
                  <rect
                    :x="p.x - 12"
                    :y="chartData.padding.top"
                    width="24"
                    :height="chartData.plotH"
                    fill="transparent"
                    style="cursor: pointer;"
                    @mouseenter="hoveredIndex = idx"
                    @mouseleave="hoveredIndex = null"
                  />
                </g>

                <!-- X 轴日期刻度 -->
                <g class="x-axis-labels">
                  <text
                    v-for="(p, idx) in chartData.xLabels"
                    :key="idx"
                    :x="p.x"
                    :y="chartData.height - 12"
                    text-anchor="middle"
                    fill="var(--el-text-color-secondary, #909399)"
                    font-size="11"
                  >
                    {{ p.data.day }}
                  </text>
                </g>
              </svg>

              <!-- 悬停浮层 Tooltip -->
              <div
                v-if="hoveredIndex !== null && chartData.points[hoveredIndex]"
                class="chart-tooltip"
                :style="{
                  left: `${(chartData.points[hoveredIndex].x / chartData.width) * 100}%`,
                  top: '12px'
                }"
              >
                <div class="tt-date">{{ chartData.points[hoveredIndex].data.day }}</div>
                <div class="tt-row tokens">
                  <span>Tokens:</span>
                  <b>{{ chartData.points[hoveredIndex].data.tokens.toLocaleString() }}</b>
                </div>
                <div class="tt-row cost">
                  <span>Cost:</span>
                  <b>${{ chartData.points[hoveredIndex].data.cost.toFixed(4) }}</b>
                </div>
                <div class="tt-row requests">
                  <span>Requests:</span>
                  <b>{{ chartData.points[hoveredIndex].data.requests.toLocaleString() }}</b>
                </div>
              </div>
            </div>
          </template>

          <div v-else class="empty-chart">
            <span class="nc-dim">{{ t('admin.metrics.noData') }}</span>
          </div>
        </div>

        <!-- 数据表格展示（支持分页） -->
        <div v-else class="table-wrapper">
          <el-table :data="pagedByDay" size="small" border>
            <el-table-column prop="day" :label="t('admin.metrics.day')" width="140" min-width="110" resizable />
            <el-table-column :label="t('admin.metrics.requests')" width="120" min-width="90" resizable>
              <template #default="{ row }">{{ row.requests }}</template>
            </el-table-column>
            <el-table-column :label="t('admin.metrics.tokens')" min-width="140" resizable>
              <template #default="{ row }">{{ row.tokens.toLocaleString() }}</template>
            </el-table-column>
            <el-table-column :label="t('admin.metrics.cost')" min-width="120" resizable>
              <template #default="{ row }">{{ row.cost.toFixed(4) }}</template>
            </el-table-column>
          </el-table>

          <div v-if="(usage.byDay?.length ?? 0) > tablePageSize" class="table-pagination">
            <el-pagination
              v-model:current-page="tablePage"
              v-model:page-size="tablePageSize"
              :page-sizes="[7, 10, 15, 30]"
              :total="usage.byDay.length"
              size="small"
              layout="total, sizes, prev, pager, next"
            />
          </div>
        </div>
      </el-card>

      <!-- 用户使用情况卡片 -->
      <el-card class="table-card" shadow="never">
        <template #header>{{ t('admin.metrics.userUsage') }}</template>
        <el-table :data="users" size="small" border>
          <el-table-column prop="userName" :label="t('admin.metrics.userName')" min-width="160" resizable show-overflow-tooltip />
          <el-table-column :label="t('admin.metrics.requests')" width="110" min-width="90" resizable>
            <template #default="{ row }">{{ row.requests }}</template>
          </el-table-column>
          <el-table-column :label="t('admin.metrics.tokens')" width="130" min-width="100" resizable>
            <template #default="{ row }">{{ row.tokens.toLocaleString() }}</template>
          </el-table-column>
          <el-table-column :label="t('admin.metrics.cost')" width="130" min-width="100" resizable>
            <template #default="{ row }">${{ row.cost.toFixed(4) }}</template>
          </el-table-column>
          <el-table-column :label="t('admin.metrics.lastRequest')" width="180" min-width="140" resizable>
            <template #default="{ row }">{{ fmtTime(row.lastRequestAt) }}</template>
          </el-table-column>
          <el-table-column :label="t('admin.metrics.lastLogin')" min-width="180" resizable>
            <template #default="{ row }">{{ fmtTime(row.lastLoginAt) }}</template>
          </el-table-column>
        </el-table>
      </el-card>
    </template>
  </div>
</template>

<style scoped>
.head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  flex-wrap: wrap;
  gap: 12px;
}

.title-wrap h3 {
  margin: 0;
}

.toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.cards {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(150px, 1fr));
  gap: 12px;
  margin: 16px 0;
}

.stat-card {
  background: var(--nc-surface);
  border: 1px solid var(--nc-border);
  border-radius: 12px;
  padding: 14px;
  backdrop-filter: blur(8px);
}

.stat-label {
  font-size: 12px;
}

.stat-value {
  font-size: 20px;
  font-weight: 700;
  margin-top: 4px;
}

.table-card {
  background: var(--nc-surface);
  border: 1px solid var(--nc-border);
  border-radius: 12px;
}

.table-card + .table-card {
  margin-top: 16px;
}

.card-header {
  display: flex;
  justify-content: space-between;
  align-items: center;
  flex-wrap: wrap;
  gap: 10px;
}

.card-title {
  font-weight: 600;
  font-size: 14px;
}

.header-actions {
  display: flex;
  align-items: center;
  gap: 10px;
  flex-wrap: wrap;
}

/* 折线图样式 */
.chart-wrapper {
  padding: 4px 0 10px;
}

.chart-legend {
  display: flex;
  gap: 16px;
  margin-bottom: 8px;
  font-size: 12px;
  color: var(--el-text-color-regular, #606266);
}

.legend-item {
  display: inline-flex;
  align-items: center;
  gap: 6px;
}

.legend-dot {
  width: 10px;
  height: 10px;
  border-radius: 50%;
}

.legend-item.tokens .legend-dot { background: #409eff; }
.legend-item.cost .legend-dot { background: #e6a23c; }
.legend-item.requests .legend-dot { background: #67c23a; }

.svg-container {
  position: relative;
  width: 100%;
  height: 280px;
}

.metrics-svg {
  width: 100%;
  height: 100%;
  overflow: visible;
}

.chart-tooltip {
  position: absolute;
  transform: translateX(-50%);
  background: var(--el-bg-color-overlay, #ffffff);
  border: 1px solid var(--el-border-color-light, #e4e7ed);
  border-radius: 8px;
  padding: 8px 12px;
  box-shadow: 0 4px 12px rgba(0, 0, 0, 0.15);
  font-size: 12px;
  pointer-events: none;
  z-index: 10;
  min-width: 140px;
}

.tt-date {
  font-weight: 600;
  margin-bottom: 4px;
  color: var(--el-text-color-primary, #303133);
  border-bottom: 1px solid var(--el-border-color-lighter, #ebeef5);
  padding-bottom: 3px;
}

.tt-row {
  display: flex;
  justify-content: space-between;
  gap: 12px;
  margin-top: 2px;
}

.tt-row.tokens b { color: #409eff; }
.tt-row.cost b { color: #e6a23c; }
.tt-row.requests b { color: #67c23a; }

.empty-chart {
  display: flex;
  justify-content: center;
  align-items: center;
  height: 200px;
}

.table-wrapper {
  display: flex;
  flex-direction: column;
}

.table-pagination {
  display: flex;
  justify-content: flex-end;
  margin-top: 12px;
  padding: 0 4px;
}
</style>
