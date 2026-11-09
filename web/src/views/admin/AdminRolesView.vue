<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import type { LlmProviderDto, McpServerDto, PromptDto, RoleDto, SkillDto } from '@/api/types'
import { kernel } from '@/kernel'

const { t } = useI18n()

/** 当前账号只读：仅可查看后台，写操作全部禁用 */
const ro = computed(() => kernel.auth.state.user?.isReadonly ?? false)

const roles = ref<RoleDto[]>([])
const allMcps = ref<McpServerDto[]>([])
const allPrompts = ref<PromptDto[]>([])
const allSkills = ref<SkillDto[]>([])
const allProviders = ref<LlmProviderDto[]>([])
const loading = ref(false)
const bindDialog = ref(false)
const activeRole = ref<RoleDto | null>(null)
const activeTab = ref('mcp')
const filterText = ref('')

const bindings = reactive({ mcpServerIds: [] as string[], promptIds: [] as string[], skillIds: [] as string[], modelIds: [] as string[] })

// 过滤后的列表
const filteredMcps = computed(() => {
  const kw = filterText.value.trim().toLowerCase()
  if (!kw) return allMcps.value
  return allMcps.value.filter(m => m.name.toLowerCase().includes(kw) || (m.description && m.description.toLowerCase().includes(kw)))
})

const filteredPrompts = computed(() => {
  const kw = filterText.value.trim().toLowerCase()
  if (!kw) return allPrompts.value
  return allPrompts.value.filter(p => p.name.toLowerCase().includes(kw) || (p.description && p.description.toLowerCase().includes(kw)))
})

const filteredSkills = computed(() => {
  const kw = filterText.value.trim().toLowerCase()
  if (!kw) return allSkills.value
  return allSkills.value.filter(s => s.name.toLowerCase().includes(kw) || s.metaToolName.toLowerCase().includes(kw) || (s.description && s.description.toLowerCase().includes(kw)))
})

const filteredProviders = computed(() => {
  const kw = filterText.value.trim().toLowerCase()
  if (!kw) return allProviders.value
  return allProviders.value.map(pr => {
    const models = pr.models.filter(m => m.name.toLowerCase().includes(kw) || pr.name.toLowerCase().includes(kw))
    return { ...pr, models }
  }).filter(pr => pr.models.length > 0)
})

const totalAvailableModels = computed(() => {
  return allProviders.value.reduce((acc, pr) => acc + pr.models.filter(m => m.enabled).length, 0)
})

function selectAllCurrentTab() {
  if (activeTab.value === 'mcp') {
    const ids = new Set(bindings.mcpServerIds)
    filteredMcps.value.forEach(m => ids.add(m.id))
    bindings.mcpServerIds = Array.from(ids)
  } else if (activeTab.value === 'prompts') {
    const ids = new Set(bindings.promptIds)
    filteredPrompts.value.forEach(p => ids.add(p.id))
    bindings.promptIds = Array.from(ids)
  } else if (activeTab.value === 'skills') {
    const ids = new Set(bindings.skillIds)
    filteredSkills.value.forEach(s => ids.add(s.id))
    bindings.skillIds = Array.from(ids)
  } else if (activeTab.value === 'models') {
    const ids = new Set(bindings.modelIds)
    filteredProviders.value.forEach(pr => pr.models.filter(m => m.enabled).forEach(m => ids.add(m.id)))
    bindings.modelIds = Array.from(ids)
  }
}

function clearCurrentTab() {
  if (activeTab.value === 'mcp') {
    if (!filterText.value.trim()) {
      bindings.mcpServerIds = []
    } else {
      const exclude = new Set(filteredMcps.value.map(m => m.id))
      bindings.mcpServerIds = bindings.mcpServerIds.filter(id => !exclude.has(id))
    }
  } else if (activeTab.value === 'prompts') {
    if (!filterText.value.trim()) {
      bindings.promptIds = []
    } else {
      const exclude = new Set(filteredPrompts.value.map(p => p.id))
      bindings.promptIds = bindings.promptIds.filter(id => !exclude.has(id))
    }
  } else if (activeTab.value === 'skills') {
    if (!filterText.value.trim()) {
      bindings.skillIds = []
    } else {
      const exclude = new Set(filteredSkills.value.map(s => s.id))
      bindings.skillIds = bindings.skillIds.filter(id => !exclude.has(id))
    }
  } else if (activeTab.value === 'models') {
    if (!filterText.value.trim()) {
      bindings.modelIds = []
    } else {
      const exclude = new Set<string>()
      filteredProviders.value.forEach(pr => pr.models.forEach(m => exclude.add(m.id)))
      bindings.modelIds = bindings.modelIds.filter(id => !exclude.has(id))
    }
  }
}

async function createRole() {
  try {
    const { value: name } = await ElMessageBox.prompt(t('admin.roles.roleNamePrompt'), t('admin.roles.addTitle'), {
      inputPlaceholder: t('admin.roles.namePlaceholder'),
      confirmButtonText: t('admin.roles.next'),
    })
    const { value: code } = await ElMessageBox.prompt(t('admin.roles.roleCodePrompt'), t('admin.roles.addTitle'), {
      inputPlaceholder: t('admin.roles.codePlaceholder'),
      confirmButtonText: t('admin.roles.createBtn'),
    })
    if (!name || !code) {
      kernel.notify.warning(t('admin.roles.nameAndCodeRequired'))
      return
    }
    await http.post('/api/admin/roles', { name, code, description: undefined })
    kernel.notify.success(t('admin.roles.roleCreated'))
    await load()
  } catch {
    /* 用户取消 */
  }
}

async function load() {
  loading.value = true
  try {
    const [r, m, p, s, l] = await Promise.all([
      http.get<RoleDto[]>('/api/admin/roles'),
      http.get<McpServerDto[]>('/api/admin/mcp-servers'),
      http.get<PromptDto[]>('/api/admin/prompts'),
      http.get<SkillDto[]>('/api/admin/skills'),
      http.get<LlmProviderDto[]>('/api/admin/llm-providers'),
    ])
    roles.value = r
    allMcps.value = m
    allPrompts.value = p
    allSkills.value = s
    allProviders.value = l
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.roles.loadFailed'), (e as { code?: string }).code)
  } finally {
    loading.value = false
  }
}

function openBindings(row: RoleDto) {
  activeRole.value = row
  bindings.mcpServerIds = [...row.mcpServerIds]
  bindings.promptIds = [...row.promptIds]
  bindings.skillIds = [...row.skillIds]
  bindings.modelIds = [...row.modelIds]
  activeTab.value = 'mcp'
  filterText.value = ''
  bindDialog.value = true
}

async function saveBindings() {
  const role = activeRole.value
  if (!role) return
  try {
    await http.put(`/api/admin/roles/${role.id}/bindings`, bindings)
    bindDialog.value = false
    kernel.notify.success(t('admin.roles.bindingsSaved'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.roles.saveFailed'), (e as { code?: string }).code)
  }
}

async function remove(row: RoleDto) {
  if (row.isSystem) {
    kernel.notify.warning(t('admin.roles.systemRoleProtected'))
    return
  }
  try {
    await ElMessageBox.confirm(t('admin.roles.deleteConfirm', { name: row.name }), t('common.delete'), { type: 'warning' })
  } catch {
    return
  }
  await http.delete(`/api/admin/roles/${row.id}`)
  await load()
}

onMounted(load)
</script>

<template>
  <div>
    <div class="head">
      <h3>{{ t('admin.roles.title') }}</h3>
      <el-button type="primary" :disabled="ro" @click="createRole">{{ t('admin.roles.create') }}</el-button>
    </div>

    <el-table :data="roles" v-loading="loading" size="small" stripe border>
      <el-table-column prop="name" :label="t('common.name')" width="140" min-width="110" resizable show-overflow-tooltip />
      <el-table-column prop="code" :label="t('admin.roles.code')" width="120" min-width="90" resizable>
        <template #default="{ row }"><span class="nc-mono">{{ row.code }}</span></template>
      </el-table-column>
      <el-table-column prop="description" :label="t('common.description')" min-width="180" resizable show-overflow-tooltip />
      <el-table-column :label="t('admin.roles.bindings')" width="240" min-width="200" resizable>
        <template #default="{ row }">
          <el-tag size="small" type="warning">MCP {{ row.mcpServerIds.length }}</el-tag>
          <el-tag size="small" type="primary" style="margin-left: 4px">Prompt {{ row.promptIds.length }}</el-tag>
          <el-tag size="small" type="success" style="margin-left: 4px">SKILL {{ row.skillIds.length }}</el-tag>
          <el-tag size="small" type="info" style="margin-left: 4px">LLM {{ row.modelIds.length }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('admin.roles.system')" width="80" min-width="70" resizable>
        <template #default="{ row }">
          <el-tag v-if="row.isSystem" size="small">{{ t('admin.roles.builtin') }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('common.actions')" min-width="160" resizable>
        <template #default="{ row }">
          <el-button size="small" type="primary" plain :disabled="ro" @click="openBindings(row)">{{ t('admin.roles.bind') }}</el-button>
          <el-button size="small" text type="danger" :disabled="ro" @click="remove(row)">{{ t('common.delete') }}</el-button>
        </template>
      </el-table-column>
    </el-table>

    <!-- 优化后的角色绑定弹窗：分类清晰，支持各分类Tab切换、搜索、全选/清空与统计Badge -->
    <el-dialog
      v-model="bindDialog"
      :title="t('admin.roles.bindingsTitle', { name: activeRole?.name ?? '' })"
      width="780px"
      top="7vh"
      class="role-bind-dialog"
      destroy-on-close
    >
      <div class="bind-wrapper">
        <div class="bind-toolbar">
          <el-input
            v-model="filterText"
            size="small"
            clearable
            :placeholder="t('admin.roles.searchPlaceholder')"
            class="search-input"
          >
            <template #prefix>🔍</template>
          </el-input>
          <div class="batch-btns">
            <el-button size="small" text type="primary" @click="selectAllCurrentTab">
              {{ t('admin.roles.selectAll') }}
            </el-button>
            <el-button size="small" text @click="clearCurrentTab">
              {{ t('admin.roles.selectNone') }}
            </el-button>
          </div>
        </div>

        <el-tabs v-model="activeTab" type="border-card" class="bind-tabs">
          <!-- MCP 服务 Tab -->
          <el-tab-pane name="mcp">
            <template #label>
              <span class="tab-label">
                <span>🔌 {{ t('admin.roles.tabMcp') }}</span>
                <el-badge
                  :value="bindings.mcpServerIds.length"
                  :max="999"
                  :type="bindings.mcpServerIds.length > 0 ? 'warning' : 'info'"
                  class="tab-badge"
                />
              </span>
            </template>
            <div class="tab-pane-content">
              <div class="summary-line">
                {{ t('admin.roles.totalSelected', { selected: bindings.mcpServerIds.length, total: allMcps.length }) }}
              </div>
              <el-scrollbar max-height="380px">
                <el-checkbox-group v-model="bindings.mcpServerIds" class="grid-group">
                  <div
                    v-for="m in filteredMcps"
                    :key="m.id"
                    class="bind-item-card"
                    :class="{ selected: bindings.mcpServerIds.includes(m.id) }"
                  >
                    <el-checkbox :value="m.id" class="bind-checkbox">
                      <div class="item-title">
                        <span class="name">{{ m.name }}</span>
                        <el-tag size="small" effect="plain" type="warning" class="tag">
                          {{ m.toolCount }} 工具
                        </el-tag>
                      </div>
                      <div v-if="m.description" class="item-desc" :title="m.description">
                        {{ m.description }}
                      </div>
                    </el-checkbox>
                  </div>
                  <el-empty v-if="filteredMcps.length === 0" :description="t('admin.roles.emptyList')" :image-size="60" />
                </el-checkbox-group>
              </el-scrollbar>
            </div>
          </el-tab-pane>

          <!-- Prompt 提示词 Tab -->
          <el-tab-pane name="prompts">
            <template #label>
              <span class="tab-label">
                <span>💬 {{ t('admin.roles.tabPrompts') }}</span>
                <el-badge
                  :value="bindings.promptIds.length"
                  :max="999"
                  :type="bindings.promptIds.length > 0 ? 'primary' : 'info'"
                  class="tab-badge"
                />
              </span>
            </template>
            <div class="tab-pane-content">
              <div class="summary-line">
                {{ t('admin.roles.totalSelected', { selected: bindings.promptIds.length, total: allPrompts.length }) }}
              </div>
              <el-scrollbar max-height="380px">
                <el-checkbox-group v-model="bindings.promptIds" class="grid-group">
                  <div
                    v-for="p in filteredPrompts"
                    :key="p.id"
                    class="bind-item-card"
                    :class="{ selected: bindings.promptIds.includes(p.id) }"
                  >
                    <el-checkbox :value="p.id" class="bind-checkbox">
                      <div class="item-title">
                        <span class="name">{{ p.name }}</span>
                        <el-tag v-if="p.enabled" size="small" type="success" effect="plain" class="tag">启用</el-tag>
                        <el-tag v-else size="small" type="info" effect="plain" class="tag">未启用</el-tag>
                      </div>
                      <div v-if="p.description || p.summary" class="item-desc" :title="p.description || p.summary">
                        {{ p.description || p.summary }}
                      </div>
                    </el-checkbox>
                  </div>
                  <el-empty v-if="filteredPrompts.length === 0" :description="t('admin.roles.emptyList')" :image-size="60" />
                </el-checkbox-group>
              </el-scrollbar>
            </div>
          </el-tab-pane>

          <!-- SKILL 技能 Tab -->
          <el-tab-pane name="skills">
            <template #label>
              <span class="tab-label">
                <span>⚡ {{ t('admin.roles.tabSkills') }}</span>
                <el-badge
                  :value="bindings.skillIds.length"
                  :max="999"
                  :type="bindings.skillIds.length > 0 ? 'success' : 'info'"
                  class="tab-badge"
                />
              </span>
            </template>
            <div class="tab-pane-content">
              <div class="summary-line">
                {{ t('admin.roles.totalSelected', { selected: bindings.skillIds.length, total: allSkills.length }) }}
              </div>
              <el-scrollbar max-height="380px">
                <el-checkbox-group v-model="bindings.skillIds" class="grid-group">
                  <div
                    v-for="s in filteredSkills"
                    :key="s.id"
                    class="bind-item-card"
                    :class="{ selected: bindings.skillIds.includes(s.id) }"
                  >
                    <el-checkbox :value="s.id" class="bind-checkbox">
                      <div class="item-title">
                        <span class="name">{{ s.name }}</span>
                        <span class="nc-mono skill-meta">({{ s.metaToolName }})</span>
                      </div>
                      <div v-if="s.description || s.summary" class="item-desc" :title="s.description || s.summary">
                        {{ s.description || s.summary }}
                      </div>
                    </el-checkbox>
                  </div>
                  <el-empty v-if="filteredSkills.length === 0" :description="t('admin.roles.emptyList')" :image-size="60" />
                </el-checkbox-group>
              </el-scrollbar>
            </div>
          </el-tab-pane>

          <!-- LLM 模型 Tab -->
          <el-tab-pane name="models">
            <template #label>
              <span class="tab-label">
                <span>🧠 {{ t('admin.roles.tabModels') }}</span>
                <el-badge
                  :value="bindings.modelIds.length"
                  :max="999"
                  :type="bindings.modelIds.length > 0 ? 'danger' : 'info'"
                  class="tab-badge"
                />
              </span>
            </template>
            <div class="tab-pane-content">
              <div class="model-tip-banner">
                💡 {{ t('admin.roles.modelUnboundTip') }}
              </div>
              <div class="summary-line">
                {{ t('admin.roles.totalSelected', { selected: bindings.modelIds.length, total: totalAvailableModels }) }}
              </div>
              <el-scrollbar max-height="350px">
                <el-checkbox-group v-model="bindings.modelIds" class="models-group">
                  <div v-for="pr in filteredProviders" :key="pr.id" class="provider-block">
                    <div class="prov-header">
                      <span class="prov-badge">{{ pr.kind || 'LLM' }}</span>
                      <span class="prov-name">{{ pr.name }}</span>
                      <span class="prov-count">({{ pr.models.length }} 模型)</span>
                    </div>
                    <div class="models-grid">
                      <div
                        v-for="m in pr.models"
                        :key="m.id"
                        class="bind-item-card model-card"
                        :class="{ selected: bindings.modelIds.includes(m.id), disabled: !m.enabled }"
                      >
                        <el-checkbox :value="m.id" :disabled="!m.enabled" class="bind-checkbox">
                          <div class="item-title">
                            <span class="name">{{ m.name }}</span>
                            <el-tag v-if="m.isVision" size="small" type="success" effect="plain" class="tag">Vision</el-tag>
                            <el-tag v-if="m.contextWindow" size="small" type="info" effect="plain" class="tag">{{ Math.round(m.contextWindow / 1024) }}k</el-tag>
                          </div>
                          <div class="item-desc" :title="pr.name">
                            {{ pr.name }}
                          </div>
                        </el-checkbox>
                      </div>
                    </div>
                  </div>
                  <el-empty v-if="filteredProviders.length === 0" :description="t('admin.roles.emptyList')" :image-size="60" />
                </el-checkbox-group>
              </el-scrollbar>
            </div>
          </el-tab-pane>
        </el-tabs>
      </div>

      <template #footer>
        <div class="dialog-footer">
          <div class="footer-stats">
            <span class="stat-pill">MCP <b>{{ bindings.mcpServerIds.length }}</b></span>
            <span class="stat-pill">Prompt <b>{{ bindings.promptIds.length }}</b></span>
            <span class="stat-pill">Skill <b>{{ bindings.skillIds.length }}</b></span>
            <span class="stat-pill">LLM <b>{{ bindings.modelIds.length }}</b></span>
          </div>
          <div class="footer-actions">
            <el-button @click="bindDialog = false">{{ t('common.cancel') }}</el-button>
            <el-button type="primary" :disabled="ro" @click="saveBindings">{{ t('admin.roles.saveBindings') }}</el-button>
          </div>
        </div>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 14px;
}

.bind-wrapper {
  display: flex;
  flex-direction: column;
  gap: 10px;
}

.bind-toolbar {
  display: flex;
  justify-content: space-between;
  align-items: center;
  gap: 12px;
  padding: 0 2px;
}

.search-input {
  max-width: 260px;
}

.batch-btns {
  display: flex;
  gap: 4px;
}

.bind-tabs {
  border-radius: 6px;
  overflow: hidden;
  border-color: var(--el-border-color-lighter, #dcdfe6);
}

.tab-label {
  display: inline-flex;
  align-items: center;
  gap: 6px;
  font-size: 13px;
  color: var(--el-text-color-primary, #303133);
}

.tab-badge :deep(.el-badge__content) {
  transform: translateY(-1px);
}

.tab-pane-content {
  padding: 4px 2px;
}

.summary-line {
  font-size: 12px;
  color: var(--el-text-color-secondary, #909399);
  margin-bottom: 8px;
  padding-left: 2px;
}

.model-tip-banner {
  background: var(--el-color-primary-light-9, rgba(64, 158, 255, 0.12));
  color: var(--el-color-primary, #409eff);
  border: 1px solid var(--el-color-primary-light-7, rgba(64, 158, 255, 0.25));
  padding: 6px 12px;
  border-radius: 4px;
  font-size: 12px;
  line-height: 1.5;
  margin-bottom: 8px;
}

.grid-group {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 8px;
}

.bind-item-card {
  border: 1px solid var(--el-border-color-light, #e4e7ed);
  border-radius: 6px;
  padding: 8px 10px;
  background: var(--el-bg-color-overlay, #ffffff);
  transition: all 0.2s ease;
  min-height: 48px;
  display: flex;
  align-items: center;
}

.bind-item-card:hover {
  border-color: var(--el-color-primary-light-3, #79bbff);
  background: var(--el-fill-color-light, #f5f7fa);
}

.bind-item-card.selected {
  border-color: var(--el-color-primary, #409eff);
  background: var(--el-color-primary-light-9, rgba(64, 158, 255, 0.12));
}

.bind-item-card.disabled {
  opacity: 0.5;
  background: var(--el-fill-color, #e6e8eb);
  cursor: not-allowed;
}

.bind-checkbox {
  width: 100%;
  margin-right: 0 !important;
  white-space: normal;
  display: flex;
  align-items: flex-start;
}

.bind-checkbox :deep(.el-checkbox__label) {
  flex: 1;
  min-width: 0;
  padding-left: 8px;
  line-height: 1.4;
  color: var(--el-text-color-primary, #303133);
}

.item-title {
  display: flex;
  align-items: center;
  gap: 6px;
  flex-wrap: wrap;
}

.item-title .name {
  font-weight: 500;
  color: var(--el-text-color-primary, #303133);
  font-size: 13px;
  word-break: break-all;
}

.skill-meta {
  font-size: 11px;
  color: var(--el-text-color-secondary, #909399);
}

.item-desc {
  font-size: 11px;
  color: var(--el-text-color-secondary, #909399);
  margin-top: 2px;
  overflow: hidden;
  text-overflow: ellipsis;
  white-space: nowrap;
  max-width: 280px;
}

/* 模型列表专用布局 */
.models-group {
  display: flex;
  flex-direction: column;
  gap: 12px;
}

.provider-block {
  border: 1px solid var(--el-border-color-lighter, #ebeef5);
  border-radius: 6px;
  padding: 8px 10px;
  background: var(--el-fill-color-blank, #fafafa);
}

.prov-header {
  display: flex;
  align-items: center;
  gap: 6px;
  margin-bottom: 8px;
}

.prov-badge {
  font-size: 10px;
  font-weight: 600;
  text-transform: uppercase;
  background: var(--el-fill-color, #f4f4f5);
  color: var(--el-text-color-secondary, #909399);
  padding: 1px 5px;
  border-radius: 3px;
}

.prov-name {
  font-size: 13px;
  font-weight: 600;
  color: var(--el-text-color-primary, #303133);
}

.prov-count {
  font-size: 12px;
  color: var(--el-text-color-secondary, #909399);
}

.models-grid {
  display: grid;
  grid-template-columns: repeat(2, 1fr);
  gap: 8px;
}

/* 底部状态条与按钮 */
.dialog-footer {
  display: flex;
  justify-content: space-between;
  align-items: center;
  width: 100%;
}

.footer-stats {
  display: flex;
  gap: 8px;
}

.stat-pill {
  font-size: 12px;
  color: var(--el-text-color-regular, #606266);
  background: var(--el-fill-color-light, #f0f2f5);
  border: 1px solid var(--el-border-color-lighter, #e4e7ed);
  padding: 2px 8px;
  border-radius: 12px;
}

.stat-pill b {
  color: var(--el-color-primary, #409eff);
  margin-left: 2px;
}

.footer-actions {
  display: flex;
  gap: 8px;
}
</style>
