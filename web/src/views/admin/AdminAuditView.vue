<script setup lang="ts">
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import type { AuditLogDto } from '@/api/types'
import { kernel } from '@/kernel'

const { t } = useI18n()

const rawList = ref<AuditLogDto[]>([])
const searchFilter = ref('')
const selectedCategories = ref<string[]>([])
const suspiciousOnly = ref(false)
const loading = ref(false)

// 分页状态
const currentPage = ref(1)
const pageSize = ref(15)

const actionTypes: Record<string, string> = {
  'LLM_PROVIDER.CREATE': 'primary', 'LLM_PROVIDER.UPDATE': 'primary', 'LLM_PROVIDER.DELETE': 'danger',
  'MCP.CREATE': 'warning', 'MCP.UPDATE': 'warning', 'MCP.DELETE': 'danger', 'MCP.FETCH': 'warning', 'MCP.ITEM_TOGGLE': 'warning',
  'PROMPT.CREATE': 'success', 'PROMPT.UPDATE': 'success', 'PROMPT.DELETE': 'danger',
  'SKILL.CREATE': 'success', 'SKILL.UPDATE': 'success', 'SKILL.DELETE': 'danger',
  'USER.CREATE': 'primary', 'USER.UPDATE': 'primary', 'USER.DELETE': 'danger',
  'ROLE.CREATE': 'primary', 'ROLE.UPDATE': 'primary', 'ROLE.DELETE': 'danger', 'ROLE.BINDINGS': 'primary',
  'APPROVAL.APPROVED': 'success', 'APPROVAL.REJECTED': 'danger',
  'CHAT.SEND': 'info', 'LOGIN.SUCCESS': 'success', 'LOGIN.FAILED': 'danger',
}

// 归类映射函数
function getCategory(action: string): string {
  if (action.startsWith('LLM_')) return 'llm'
  if (action.startsWith('MCP')) return 'mcp'
  if (action.startsWith('PROMPT')) return 'prompt'
  if (action.startsWith('SKILL')) return 'skill'
  if (action.startsWith('USER') || action.startsWith('ROLE')) return 'userRole'
  if (action.startsWith('APPROVAL')) return 'approval'
  if (action.startsWith('LOGIN') || action.startsWith('CHAT') || action.startsWith('SESSION') || action.startsWith('REFRESH') || action.startsWith('LOGOUT')) return 'authChat'
  return 'other'
}

// 分类汇总统计
const summary = computed(() => {
  const all = rawList.value
  let suspicious = 0
  let llm = 0
  let mcp = 0
  let prompt = 0
  let skill = 0
  let userRole = 0
  let approval = 0
  let authChat = 0
  let other = 0

  for (const item of all) {
    if (item.isSuspicious) suspicious++
    const cat = getCategory(item.action)
    if (cat === 'llm') llm++
    else if (cat === 'mcp') mcp++
    else if (cat === 'prompt') prompt++
    else if (cat === 'skill') skill++
    else if (cat === 'userRole') userRole++
    else if (cat === 'approval') approval++
    else if (cat === 'authChat') authChat++
    else other++
  }

  return {
    total: all.length,
    suspicious,
    llm,
    mcp,
    prompt,
    skill,
    userRole,
    approval,
    authChat,
    other,
  }
})

// 复合多维搜索与过滤
// 规则：
// 1. 若同时勾选了具体业务分类（cats.length > 0）与“仅看可疑”（suspOnly）：
//    在界面交互上，用户点击“可疑事件”卡片和“MCP/用户角色”卡片属于【联合多选】（并集逻辑），
//    即符合属于选中分类 OR 属于可疑事件的数据都应该展示，而不是求交集导致空结果。
// 2. 若仅勾选了“仅看可疑”，则只展示 isSuspicious 的日志；
// 3. 若仅勾选了业务分类，则展示对应分类的日志。
const filteredList = computed(() => {
  const q = searchFilter.value.trim().toLowerCase()
  const cats = selectedCategories.value
  const suspOnly = suspiciousOnly.value

  return rawList.value.filter(item => {
    // 类别与可疑状态联合判断
    if (suspOnly && cats.length > 0) {
      // 联合并集：满足可疑 OR 满足分类
      const matchSusp = item.isSuspicious
      const matchCat = cats.includes(getCategory(item.action))
      if (!matchSusp && !matchCat) return false
    } else if (suspOnly) {
      if (!item.isSuspicious) return false
    } else if (cats.length > 0) {
      if (!cats.includes(getCategory(item.action))) return false
    }

    if (!q) return true

    return (
      (item.action && item.action.toLowerCase().includes(q)) ||
      (item.target && item.target.toLowerCase().includes(q)) ||
      (item.traceId && item.traceId.toLowerCase().includes(q)) ||
      (item.detailJson && item.detailJson.toLowerCase().includes(q)) ||
      (item.userId && item.userId.toLowerCase().includes(q))
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
    rawList.value = await http.get<AuditLogDto[]>('/api/admin/audit?take=5000')
    currentPage.value = 1
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.audit.loadFailed'), (e as { code?: string }).code)
  } finally {
    loading.value = false
  }
}

function onFilterChange() {
  currentPage.value = 1
}

function toggleCategory(cat: string) {
  const idx = selectedCategories.value.indexOf(cat)
  if (idx >= 0) {
    selectedCategories.value.splice(idx, 1)
  } else {
    selectedCategories.value.push(cat)
  }
  currentPage.value = 1
}

function clearAllFilters() {
  selectedCategories.value = []
  suspiciousOnly.value = false
  currentPage.value = 1
}

function toggleQuickSuspicious() {
  suspiciousOnly.value = !suspiciousOnly.value
  currentPage.value = 1
}

onMounted(load)
</script>

<template>
  <div class="audit-view">
    <div class="head">
      <h3>{{ t('admin.audit.title') }}</h3>
      <el-button size="small" @click="load">{{ t('common.refresh') }}</el-button>
    </div>

    <!-- 分类汇总卡片栏（可点击多选组合过滤） -->
    <div class="summary-cards">
      <div class="sum-card total" :class="{ active: selectedCategories.length === 0 && !suspiciousOnly }" @click="clearAllFilters">
        <div class="sum-label nc-dim">{{ t('admin.audit.totalLogs') }}</div>
        <div class="sum-val">{{ summary.total }}</div>
      </div>
      <div class="sum-card suspicious" :class="{ active: suspiciousOnly }" @click="toggleQuickSuspicious">
        <div class="sum-label">{{ t('admin.audit.statSuspicious') }}</div>
        <div class="sum-val">{{ summary.suspicious }}</div>
      </div>
      <div class="sum-card mcp" :class="{ active: selectedCategories.includes('mcp') }" @click="toggleCategory('mcp')">
        <div class="sum-label">{{ t('admin.audit.catMcp') }}</div>
        <div class="sum-val">{{ summary.mcp }}</div>
      </div>
      <div class="sum-card user-role" :class="{ active: selectedCategories.includes('userRole') }" @click="toggleCategory('userRole')">
        <div class="sum-label">{{ t('admin.audit.catUserRole') }}</div>
        <div class="sum-val">{{ summary.userRole }}</div>
      </div>
      <div class="sum-card approval" :class="{ active: selectedCategories.includes('approval') }" @click="toggleCategory('approval')">
        <div class="sum-label">{{ t('admin.audit.catApproval') }}</div>
        <div class="sum-val">{{ summary.approval }}</div>
      </div>
      <div class="sum-card auth" :class="{ active: selectedCategories.includes('authChat') }" @click="toggleCategory('authChat')">
        <div class="sum-label">{{ t('admin.audit.catAuthChat') }}</div>
        <div class="sum-val">{{ summary.authChat }}</div>
      </div>
    </div>

    <!-- 查询与筛选工具栏 -->
    <div class="toolbar">
      <el-input
        v-model="searchFilter"
        :placeholder="t('admin.audit.searchPlaceholder')"
        size="small"
        clearable
        class="search-input"
        @input="onFilterChange"
      >
        <template #prefix>🔍</template>
      </el-input>

      <el-select
        v-model="selectedCategories"
        multiple
        collapse-tags
        collapse-tags-tooltip
        :placeholder="t('admin.audit.allCategories')"
        size="small"
        clearable
        style="min-width: 220px"
        @change="onFilterChange"
      >
        <el-option :label="t('admin.audit.catLlm')" value="llm" />
        <el-option :label="t('admin.audit.catMcp')" value="mcp" />
        <el-option :label="t('admin.audit.catPrompt')" value="prompt" />
        <el-option :label="t('admin.audit.catSkill')" value="skill" />
        <el-option :label="t('admin.audit.catUserRole')" value="userRole" />
        <el-option :label="t('admin.audit.catApproval')" value="approval" />
        <el-option :label="t('admin.audit.catAuthChat')" value="authChat" />
        <el-option :label="t('admin.audit.catOther')" value="other" />
      </el-select>

      <el-checkbox v-model="suspiciousOnly" :label="t('admin.audit.suspiciousOnly')" size="small" @change="onFilterChange" />
    </div>

    <!-- 表格与分页 -->
    <el-card class="table-card" shadow="never">
      <el-table :data="pagedList" v-loading="loading" size="small" stripe>
        <el-table-column prop="createdAt" :label="t('admin.audit.time')" width="165">
          <template #default="{ row }">{{ new Date(row.createdAt).toLocaleString(undefined) }}</template>
        </el-table-column>
        <el-table-column :label="t('admin.audit.action')" width="170">
          <template #default="{ row }">
            <el-tag :type="(actionTypes[row.action] as any) ?? 'info'" size="small">{{ row.action }}</el-tag>
          </template>
        </el-table-column>
        <el-table-column :label="t('admin.audit.traceId')" width="180">
          <template #default="{ row }"><span class="nc-mono nc-dim">{{ row.traceId }}</span></template>
        </el-table-column>
        <el-table-column prop="target" :label="t('admin.audit.target')" min-width="150" show-overflow-tooltip />
        <el-table-column :label="t('admin.audit.detail')" min-width="220" show-overflow-tooltip>
          <template #default="{ row }"><span class="nc-mono nc-dim">{{ row.detailJson ?? t('common.placeholderDash') }}</span></template>
        </el-table-column>
        <el-table-column :label="t('admin.audit.suspicious')" width="80" align="center">
          <template #default="{ row }">
            <el-tag v-if="row.isSuspicious" type="danger" size="small">{{ t('admin.audit.yes') }}</el-tag>
            <span v-else class="nc-dim">{{ t('admin.audit.no') }}</span>
          </template>
        </el-table-column>
      </el-table>

      <div class="pagination-bar" v-if="filteredList.length > 0">
        <el-pagination
          v-model:current-page="currentPage"
          v-model:page-size="pageSize"
          :page-sizes="[10, 15, 30, 50, 100]"
          :total="filteredList.length"
          size="small"
          layout="total, sizes, prev, pager, next, jumper"
        />
      </div>
    </el-card>
  </div>
</template>

<style scoped>
.audit-view {
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

/* 汇总卡片 */
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

.sum-card.suspicious .sum-label { color: #f56c6c; }
.sum-card.mcp .sum-label { color: #e6a23c; }
.sum-card.user-role .sum-label { color: #409eff; }
.sum-card.approval .sum-label { color: #67c23a; }
.sum-card.auth .sum-label { color: #909399; }

.toolbar {
  display: flex;
  align-items: center;
  gap: 12px;
  flex-wrap: wrap;
}

.search-input {
  max-width: 320px;
}

.table-card {
  background: var(--nc-surface);
  border: 1px solid var(--nc-border);
  border-radius: 12px;
}

.pagination-bar {
  display: flex;
  justify-content: flex-end;
  margin-top: 12px;
  padding-top: 6px;
}
</style>
