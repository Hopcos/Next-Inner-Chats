<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import type { ToolApprovalDto } from '@/api/types'
import { kernel } from '@/kernel'

const { t } = useI18n()

/** 当前账号只读：仅可查看后台，审批/驳回等写操作全部禁用 */
const ro = computed(() => kernel.auth.state.user?.isReadonly ?? false)

const rawList = ref<ToolApprovalDto[]>([])
const statusFilter = ref('')
const serverFilter = ref('')
const searchFilter = ref('')
const loading = ref(false)

// 分页状态
const currentPage = ref(1)
const pageSize = ref(15)

// 提取所有出现的 MCP Server 列表供分类过滤
const serverOptions = computed(() => {
  const set = new Set<string>()
  for (const item of rawList.value) {
    if (item.mcpServerName) set.add(item.mcpServerName)
  }
  return Array.from(set).sort()
})

// 分类汇总统计（全部、待处理、已批准、已拒绝、已过期）
const summary = computed(() => {
  const all = rawList.value
  let pending = 0
  let approved = 0
  let rejected = 0
  let expired = 0

  for (const item of all) {
    if (item.status === 'Pending') pending++
    else if (item.status === 'Approved') approved++
    else if (item.status === 'Rejected') rejected++
    else if (item.status === 'Expired') expired++
  }

  return {
    total: all.length,
    pending,
    approved,
    rejected,
    expired,
  }
})

// 根据搜索关键词、状态和 MCP 服务进行复合过滤
const filteredList = computed(() => {
  const q = searchFilter.value.trim().toLowerCase()
  const status = statusFilter.value
  const srv = serverFilter.value

  return rawList.value.filter(item => {
    if (status && item.status !== status) return false
    if (srv && item.mcpServerName !== srv) return false
    if (!q) return true

    return (
      (item.toolName && item.toolName.toLowerCase().includes(q)) ||
      (item.mcpServerName && item.mcpServerName.toLowerCase().includes(q)) ||
      (item.argumentsJson && item.argumentsJson.toLowerCase().includes(q))
    )
  })
})

// 分页切片数据
const pagedList = computed(() => {
  const start = (currentPage.value - 1) * pageSize.value
  return filteredList.value.slice(start, start + pageSize.value)
})

async function load() {
  loading.value = true
  try {
    // 拉取审批数据（拉取上限由后端安全控制）
    rawList.value = await http.get<ToolApprovalDto[]>('/api/admin/approvals?take=5000')
    currentPage.value = 1
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.approvals.loadFailed'), (e as { code?: string }).code)
  } finally {
    loading.value = false
  }
}

function onFilterChange() {
  currentPage.value = 1
}

function setQuickStatus(status: string) {
  statusFilter.value = statusFilter.value === status ? '' : status
  currentPage.value = 1
}

async function decide(row: ToolApprovalDto, approved: boolean) {
  try {
    await http.post(`/api/admin/approvals/${row.id}/decide`, { approved, reason: null })
    kernel.notify.success(approved ? t('admin.approvals.approved') : t('admin.approvals.rejected'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.approvals.operationFailed'), (e as { code?: string }).code)
  }
}

function prettyArgs(raw?: string): string {
  if (!raw) return ''
  try {
    return JSON.stringify(JSON.parse(raw), null, 2)
  } catch {
    return raw
  }
}

onMounted(load)
</script>

<template>
  <div class="approvals-view">
    <div class="head">
      <h3>{{ t('admin.approvals.title') }}</h3>
      <el-button size="small" @click="load">{{ t('common.refresh') }}</el-button>
    </div>

    <!-- 分类汇总卡片栏（可点击快速过滤） -->
    <div class="summary-cards">
      <div class="sum-card total" :class="{ active: !statusFilter }" @click="setQuickStatus('')">
        <div class="sum-label nc-dim">{{ t('admin.approvals.totalCount') }}</div>
        <div class="sum-val">{{ summary.total }}</div>
      </div>
      <div class="sum-card pending" :class="{ active: statusFilter === 'Pending' }" @click="setQuickStatus('Pending')">
        <div class="sum-label">{{ t('admin.approvals.statPending') }}</div>
        <div class="sum-val">{{ summary.pending }}</div>
      </div>
      <div class="sum-card approved" :class="{ active: statusFilter === 'Approved' }" @click="setQuickStatus('Approved')">
        <div class="sum-label">{{ t('admin.approvals.statApproved') }}</div>
        <div class="sum-val">{{ summary.approved }}</div>
      </div>
      <div class="sum-card rejected" :class="{ active: statusFilter === 'Rejected' }" @click="setQuickStatus('Rejected')">
        <div class="sum-label">{{ t('admin.approvals.statRejected') }}</div>
        <div class="sum-val">{{ summary.rejected }}</div>
      </div>
      <div class="sum-card expired" :class="{ active: statusFilter === 'Expired' }" @click="setQuickStatus('Expired')">
        <div class="sum-label">{{ t('admin.approvals.statExpired') }}</div>
        <div class="sum-val">{{ summary.expired }}</div>
      </div>
    </div>

    <!-- 查询与筛选工具栏 -->
    <div class="toolbar">
      <el-input
        v-model="searchFilter"
        :placeholder="t('admin.approvals.searchPlaceholder')"
        size="small"
        clearable
        class="search-input"
        @input="onFilterChange"
      >
        <template #prefix>🔍</template>
      </el-input>

      <el-select
        v-model="statusFilter"
        :placeholder="t('admin.approvals.allStatus')"
        size="small"
        clearable
        style="width: 130px"
        @change="onFilterChange"
      >
        <el-option :label="t('admin.approvals.pending')" value="Pending" />
        <el-option :label="t('admin.approvals.approved')" value="Approved" />
        <el-option :label="t('admin.approvals.rejected')" value="Rejected" />
        <el-option :label="t('admin.approvals.expired')" value="Expired" />
      </el-select>

      <el-select
        v-if="serverOptions.length > 0"
        v-model="serverFilter"
        :placeholder="t('admin.approvals.allMcp')"
        size="small"
        clearable
        style="width: 170px"
        @change="onFilterChange"
      >
        <el-option v-for="srv in serverOptions" :key="srv" :label="srv" :value="srv" />
      </el-select>
    </div>

    <!-- 表格与分页 -->
    <el-card class="table-card" shadow="never">
      <el-table :data="pagedList" v-loading="loading" size="small" stripe>
        <el-table-column prop="createdAt" :label="t('admin.approvals.time')" width="165">
          <template #default="{ row }">{{ new Date(row.createdAt).toLocaleString(undefined) }}</template>
        </el-table-column>
        <el-table-column :label="t('admin.approvals.tool')" min-width="190">
          <template #default="{ row }">
            <span class="nc-mono tool-name">{{ row.mcpServerName }}.{{ row.toolName }}</span>
          </template>
        </el-table-column>
        <el-table-column :label="t('admin.approvals.args')" min-width="220">
          <template #default="{ row }">
            <el-tooltip :content="prettyArgs(row.argumentsJson)" placement="top" :show-after="300">
              <span class="nc-mono nc-dim args-text">{{ (row.argumentsJson ?? '').slice(0, 80) }}</span>
            </el-tooltip>
          </template>
        </el-table-column>
        <el-table-column :label="t('common.status')" width="100">
          <template #default="{ row }">
            <el-tag :type="row.status === 'Pending' ? 'warning' : row.status === 'Approved' ? 'success' : row.status === 'Rejected' ? 'danger' : 'info'" size="small">
              {{ row.status }}
            </el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="t('common.actions')" width="140" fixed="right">
          <template #default="{ row }">
            <template v-if="row.status === 'Pending'">
              <el-button size="small" type="success" plain :disabled="ro" @click="decide(row, true)">{{ t('admin.approvals.approve') }}</el-button>
              <el-button size="small" type="danger" plain :disabled="ro" @click="decide(row, false)">{{ t('admin.approvals.reject') }}</el-button>
            </template>
            <span v-else class="nc-dim">{{ t('common.placeholderDash') }}</span>
          </template>
        </el-table-column>
      </el-table>

      <div class="pagination-bar" v-if="filteredList.length > 0">
        <el-pagination
          v-model:current-page="currentPage"
          v-model:page-size="pageSize"
          :page-sizes="[10, 15, 30, 50]"
          :total="filteredList.length"
          size="small"
          layout="total, sizes, prev, pager, next, jumper"
        />
      </div>
    </el-card>
  </div>
</template>

<style scoped>
.approvals-view {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.head {
  display: flex;
  justify-content: space-between;
  align-items: center;
}

.head h3 {
  margin: 0;
}

/* 分类汇总卡片 */
.summary-cards {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(130px, 1fr));
  gap: 10px;
}

.sum-card {
  background: var(--nc-surface);
  border: 1px solid var(--nc-border);
  border-radius: 10px;
  padding: 10px 14px;
  cursor: pointer;
  transition: all 0.18s ease;
}

.sum-card:hover {
  border-color: var(--el-color-primary, #409eff);
  transform: translateY(-1px);
}

.sum-card.active {
  border-color: var(--el-color-primary, #409eff);
  background: var(--el-color-primary-light-9, rgba(64, 158, 255, 0.12));
}

.sum-label {
  font-size: 12px;
  margin-bottom: 4px;
}

.sum-val {
  font-size: 18px;
  font-weight: 700;
}

.sum-card.pending .sum-label { color: #e6a23c; }
.sum-card.approved .sum-label { color: #67c23a; }
.sum-card.rejected .sum-label { color: #f56c6c; }
.sum-card.expired .sum-label { color: #909399; }

.toolbar {
  display: flex;
  align-items: center;
  gap: 8px;
  flex-wrap: wrap;
}

.search-input {
  max-width: 280px;
}

.table-card {
  background: var(--nc-surface);
  border: 1px solid var(--nc-border);
  border-radius: 12px;
}

.tool-name {
  font-weight: 500;
}

.args-text {
  cursor: help;
}

.pagination-bar {
  display: flex;
  justify-content: flex-end;
  margin-top: 12px;
  padding-top: 6px;
}
</style>
