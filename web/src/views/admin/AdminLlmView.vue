<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import type { LlmModelDto, LlmProviderDto } from '@/api/types'
import { kernel } from '@/kernel'

const { t } = useI18n()

/** 当前账号只读：仅可查看后台，写操作全部禁用 */
const ro = computed(() => kernel.auth.state.user?.isReadonly ?? false)

const list = ref<LlmProviderDto[]>([])
const loading = ref(false)
const dialogOpen = ref(false)
const editingId = ref<string | null>(null)

// 供应商基础信息（地址 / 密钥等）；模型由“获取模型”自动带出后逐模型配置
const form = reactive({
  name: '',
  kind: 'OpenAiCompatible',
  baseUrl: '',
  apiKey: '',
  timeoutSeconds: 120,
  enabled: true,
  priority: 100,
  thinkingParam: 'None',
})

const thinkingParamOptions = ['None', 'DeepSeek', 'Qwen', 'OpenAIEffort']
function thinkingParamLabel(v?: string): string {
  const key = {
    None: 'admin.llm.thinkParamNone',
    DeepSeek: 'admin.llm.thinkParamDeepSeek',
    Qwen: 'admin.llm.thinkParamQwen',
    OpenAIEffort: 'admin.llm.thinkParamOpenAi',
  } as Record<string, string>
  return (v && key[v]) ? t(key[v]) : (v ?? 'None')
}

const modelDialogOpen = ref(false)
const modelEditingId = ref<string | null>(null)
const modelProviderId = ref<string>('')
const fetchingModels = ref(false)
const modelForm = reactive({
  name: '',
  enabled: true,
  isVision: false,
  contextWindow: 128000,
  priceInPer1K: 0,
  priceOutPer1K: 0,
  priority: 1,
})

async function load() {
  loading.value = true
  try {
    list.value = await http.get<LlmProviderDto[]>('/api/admin/llm-providers')
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.llm.loadFailed'), (e as { code?: string }).code)
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingId.value = null
  Object.assign(form, { name: '', kind: 'OpenAiCompatible', baseUrl: '', apiKey: '', timeoutSeconds: 120, enabled: true, priority: 100, thinkingParam: 'None' })
  dialogOpen.value = true
}

function openEdit(row: LlmProviderDto) {
  editingId.value = row.id
  Object.assign(form, {
    name: row.name, kind: row.kind, baseUrl: row.baseUrl ?? '', apiKey: '',
    timeoutSeconds: row.timeoutSeconds, enabled: row.enabled, priority: row.priority,
    thinkingParam: row.thinkingParam ?? 'None',
  })
  dialogOpen.value = true
}

async function save() {
  const body = { ...form, apiKey: form.apiKey || undefined }
  try {
    if (editingId.value) {
      await http.put(`/api/admin/llm-providers/${editingId.value}`, body)
    } else {
      await http.post('/api/admin/llm-providers', body)
    }
    dialogOpen.value = false
    kernel.notify.success(t('admin.llm.saved'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.llm.saveFailed'), (e as { code?: string }).code)
  }
}

/// 获取模型（自动带出）：新建时尚未保存 → 先创建供应商，再拉取模型列表
async function fetchModels() {
  if (fetchingModels.value) return
  fetchingModels.value = true
  try {
    if (!editingId.value) {
      const id = await http.post<string>(`/api/admin/llm-providers`, { ...form, apiKey: form.apiKey || undefined })
      editingId.value = id
      form.apiKey = ''
    }
    const res = await http.post<{ added: string[] }>(`/api/admin/llm-providers/${editingId.value}/fetch-models`, {})
    kernel.notify.success(t('admin.llm.modelsFetched', { added: res.added.length }))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.llm.fetchModelsFailed'), (e as { code?: string }).code)
  } finally {
    fetchingModels.value = false
  }
}

/// 表格行内“获取模型”：直接对已存在供应商拉取模型
async function fetchModelsFor(row: LlmProviderDto) {
  editingId.value = row.id
  await fetchModels()
}

async function remove(row: LlmProviderDto) {
  try {
    await ElMessageBox.confirm(t('admin.llm.deleteConfirm', { name: row.name }), t('common.delete'), { type: 'warning' })
  } catch {
    return
  }
  try {
    await http.delete(`/api/admin/llm-providers/${row.id}`)
    kernel.notify.success(t('admin.llm.deleted'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.llm.deleteFailed'), (e as { code?: string }).code)
  }
}

async function ping(row: LlmProviderDto) {
  try {
    const res = await http.post<{ latencyMs: number }>(`/api/admin/llm-providers/${row.id}/ping`, {})
    kernel.notify.success(t('admin.llm.pingOk', { ms: res.latencyMs }))
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.llm.unreachable'), (e as { code?: string }).code)
  }
}

function openModelCreate(provider: LlmProviderDto) {
  modelProviderId.value = provider.id
  modelEditingId.value = null
  Object.assign(modelForm, { name: '', enabled: true, isVision: false, contextWindow: 128000, priceInPer1K: 0, priceOutPer1K: 0, priority: (provider.models.length + 1) })
  modelDialogOpen.value = true
}

function openModelEdit(model: LlmModelDto) {
  modelProviderId.value = model.id // 仅占位（编辑走 /models/{id}）
  modelEditingId.value = model.id
  Object.assign(modelForm, {
    name: model.name, enabled: model.enabled, isVision: model.isVision, contextWindow: model.contextWindow,
    priceInPer1K: model.priceInPer1K, priceOutPer1K: model.priceOutPer1K, priority: model.priority,
  })
  modelDialogOpen.value = true
}

async function saveModel() {
  try {
    const body = { ...modelForm }
    if (modelEditingId.value) {
      await http.put(`/api/admin/llm-providers/models/${modelEditingId.value}`, body)
    } else {
      await http.post(`/api/admin/llm-providers/${modelProviderId.value}/models`, body)
    }
    modelDialogOpen.value = false
    kernel.notify.success(t('admin.llm.modelSaved'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.llm.modelSaveFailed'), (e as { code?: string }).code)
  }
}

async function removeModel(model: LlmModelDto) {
  try {
    await ElMessageBox.confirm(t('admin.llm.modelDeleteConfirm', { name: model.name }), t('common.delete'), { type: 'warning' })
  } catch {
    return
  }
  try {
    await http.delete(`/api/admin/llm-providers/models/${model.id}`)
    kernel.notify.success(t('admin.llm.modelDeleted'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.llm.deleteFailed'), (e as { code?: string }).code)
  }
}

onMounted(load)
</script>

<template>
  <div>
    <div class="head">
      <h3>{{ t('admin.llm.title') }}</h3>
      <el-button type="primary" @click="openCreate">{{ t('admin.llm.create') }}</el-button>
    </div>

    <el-table :data="list" v-loading="loading" size="small" stripe>
      <el-table-column type="expand">
        <template #default="{ row }">
          <div class="expand">
            <div class="sec-title">
              {{ t('admin.llm.models', { count: row.models.length }) }}
              <el-button size="small" text type="primary" :disabled="ro" @click="openModelCreate(row)">＋ {{ t('admin.llm.addModel') }}</el-button>
            </div>
            <div v-if="row.models.length === 0" class="nc-dim">{{ t('admin.llm.noModels') }}</div>
            <el-table :data="row.models" size="small">
              <el-table-column prop="name" :label="t('common.name')" width="170" />
              <el-table-column :label="t('admin.llm.isVision')" width="80">
                <template #default="{ row: m }">
                  <el-tag :type="m.isVision ? 'success' : 'info'" size="small">{{ m.isVision ? t('common.yes') : t('common.no') }}</el-tag>
                </template>
              </el-table-column>
              <el-table-column prop="contextWindow" :label="t('admin.llm.colContext')" width="110" />
              <el-table-column prop="priceInPer1K" :label="t('admin.llm.priceIn')" width="130" />
              <el-table-column prop="priceOutPer1K" :label="t('admin.llm.priceOut')" width="130" />
              <el-table-column prop="priority" :label="t('admin.llm.colPriority')" width="70" />
              <el-table-column :label="t('common.enabled')" width="80">
                <template #default="{ row: m }">
                  <el-tag :type="m.enabled ? 'success' : 'info'" size="small">{{ m.enabled ? t('common.enabled') : t('common.disabled') }}</el-tag>
                </template>
              </el-table-column>
              <el-table-column :label="t('common.actions')" width="150">
                <template #default="{ row: m, $index }">
                  <el-button size="small" text :disabled="ro || row.models.length <= 1" @click="removeModel(m)">{{ t('common.delete') }}</el-button>
                  <el-button size="small" text :disabled="ro" @click="openModelEdit(m)">{{ t('common.edit') }}</el-button>
                  <span v-if="$index === 0" class="nc-dim primary-tag" :title="t('admin.llm.defaultModelHint')">{{ t('admin.llm.defaultModel') }}</span>
                </template>
              </el-table-column>
            </el-table>
          </div>
        </template>
      </el-table-column>
      <el-table-column prop="name" :label="t('common.name')" width="150" />
      <el-table-column prop="kind" :label="t('admin.llm.colKind')" width="120" />
      <el-table-column :label="t('admin.llm.colModels')" width="100">
        <template #default="{ row }">{{ row.models.length }} <span class="nc-dim">models</span></template>
      </el-table-column>
      <el-table-column :label="t('admin.llm.baseUrl')" min-width="180">
        <template #default="{ row }"><span class="nc-mono nc-dim">{{ row.baseUrl || t('common.placeholderDash') }}</span></template>
      </el-table-column>
      <el-table-column :label="t('admin.llm.colKey')" width="110">
        <template #default="{ row }"><span class="nc-mono nc-dim">{{ row.apiKeyMasked || t('admin.llm.notConfigured') }}</span></template>
      </el-table-column>
      <el-table-column :label="t('common.enabled')" width="70">
        <template #default="{ row }">
          <el-tag :type="row.enabled ? 'success' : 'info'" size="small">{{ row.enabled ? t('common.enabled') : t('common.disabled') }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('admin.llm.colHealth')" width="80">
        <template #default="{ row }">
          <el-tag :type="row.isHealthy ? 'success' : 'danger'" size="small">{{ row.isHealthy ? t('admin.llm.healthy') : t('admin.llm.circuitBroken') }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('common.actions')" width="330" fixed="right">
        <template #default="{ row }">
          <div class="row-actions">
            <el-button size="small" text type="primary" :disabled="ro" @click="fetchModelsFor(row)">{{ t('admin.llm.fetchModels') }}</el-button>
            <el-button size="small" text :disabled="ro" @click="ping(row)">{{ t('common.ping') }}</el-button>
            <el-button size="small" text :disabled="ro" @click="openEdit(row)">{{ t('common.edit') }}</el-button>
            <el-button size="small" text type="danger" :disabled="ro" @click="remove(row)">{{ t('common.delete') }}</el-button>
          </div>
        </template>
      </el-table-column>
    </el-table>

    <!-- 供应商基础信息（地址 / 密钥等） + 获取模型 -->
    <el-dialog v-model="dialogOpen" :title="editingId ? t('admin.llm.editServer') : t('admin.llm.addServer')" width="520px">
      <el-form label-width="110px" label-position="left">
        <el-form-item :label="t('common.name')" required><el-input v-model="form.name" /></el-form-item>
        <el-form-item :label="t('admin.llm.kind')">
          <el-select v-model="form.kind">
            <el-option :label="t('admin.llm.kindOpenAi')" value="OpenAiCompatible" />
            <el-option :label="t('admin.llm.kindMock')" value="Mock" />
          </el-select>
        </el-form-item>
        <el-form-item :label="t('admin.llm.baseUrl')"><el-input v-model="form.baseUrl" placeholder="https://api.openai.com/v1" /></el-form-item>
        <el-form-item :label="t('admin.llm.apiKey')">
          <el-input v-model="form.apiKey" type="password" show-password :placeholder="t('admin.llm.apiKeyPlaceholder')" />
        </el-form-item>
        <el-form-item :label="t('admin.llm.timeoutSeconds')"><el-input-number v-model="form.timeoutSeconds" :min="5" :max="600" /></el-form-item>
        <el-form-item :label="t('admin.llm.priority')"><el-input-number v-model="form.priority" :min="1" :max="1000" /></el-form-item>
        <el-form-item :label="t('admin.llm.thinkParam')">
          <el-select v-model="form.thinkingParam" style="width: 100%">
            <el-option v-for="m in thinkingParamOptions" :key="m" :value="m" :label="thinkingParamLabel(m)" />
          </el-select>
        </el-form-item>
        <el-form-item :label="t('common.enabled')"><el-switch v-model="form.enabled" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogOpen = false">{{ t('common.cancel') }}</el-button>
        <el-button :loading="fetchingModels" type="primary" plain :disabled="ro" @click="fetchModels">{{ t('admin.llm.fetchModels') }}</el-button>
        <el-button type="primary" :disabled="ro" @click="save">{{ t('common.save') }}</el-button>
      </template>
    </el-dialog>

    <!-- 模型配置（视觉 / 上下文 / 成本 / 启用） -->
    <el-dialog v-model="modelDialogOpen" :title="modelEditingId ? t('admin.llm.editModel') : t('admin.llm.addModel')" width="460px">
      <el-form label-width="110px" label-position="left">
        <el-form-item :label="t('common.name')" required><el-input v-model="modelForm.name" /></el-form-item>
        <el-form-item :label="t('admin.llm.isVision')"><el-switch v-model="modelForm.isVision" /></el-form-item>
        <el-form-item :label="t('admin.llm.contextWindow')"><el-input-number v-model="modelForm.contextWindow" :min="1024" :step="1024" /></el-form-item>
        <el-form-item :label="t('admin.llm.priceIn')"><el-input-number v-model="modelForm.priceInPer1K" :min="0" :precision="6" :step="0.000001" /></el-form-item>
        <el-form-item :label="t('admin.llm.priceOut')"><el-input-number v-model="modelForm.priceOutPer1K" :min="0" :precision="6" :step="0.000001" /></el-form-item>
        <el-form-item :label="t('admin.llm.colPriority')"><el-input-number v-model="modelForm.priority" :min="1" :max="1000" /></el-form-item>
        <el-form-item :label="t('common.enabled')"><el-switch v-model="modelForm.enabled" /></el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="modelDialogOpen = false">{{ t('common.cancel') }}</el-button>
        <el-button type="primary" :disabled="ro" @click="saveModel">{{ t('common.save') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.row-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  white-space: nowrap;
}

.head {
  display: flex;
  justify-content: space-between;
  align-items: center;
  margin-bottom: 14px;
}

.expand {
  padding: 8px 20px 12px;
}

.sec-title {
  font-size: 12.5px;
  opacity: 0.75;
  margin-bottom: 8px;
  display: flex;
  align-items: center;
  gap: 8px;
}

.primary-tag {
  margin-left: 4px;
}
</style>
