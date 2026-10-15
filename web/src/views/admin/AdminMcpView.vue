<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import type { McpServerDto } from '@/api/types'
import { kernel } from '@/kernel'

const { t } = useI18n()

/** 当前账号只读：仅可查看后台，写操作全部禁用 */
const ro = computed(() => kernel.auth.state.user?.isReadonly ?? false)

const list = ref<McpServerDto[]>([])
const loading = ref(false)
const fetchingId = ref<string | null>(null)
const dialogOpen = ref(false)
const editingId = ref<string | null>(null)
const expandedServer = ref<string | null>(null)

const form = reactive({
  name: '',
  transport: 'Http',
  endpoint: '',
  headersJson: '',
  enabled: true,
  isVision: false,
  timeoutSeconds: 60,
  stdioCommand: '',
  stdioArgsJson: '',
  instructions: '',
})

async function load() {
  loading.value = true
  try {
    list.value = await http.get<McpServerDto[]>('/api/admin/mcp-servers')
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.mcp.loadFailed'), (e as { code?: string }).code)
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingId.value = null
  Object.assign(form, { name: '', transport: 'Http', endpoint: '', headersJson: '', enabled: true, isVision: false, timeoutSeconds: 60, stdioCommand: '', stdioArgsJson: '', instructions: '' })
  dialogOpen.value = true
}

function openEdit(row: McpServerDto) {
  editingId.value = row.id
  Object.assign(form, {
    name: row.name, transport: row.transport, endpoint: row.endpoint ?? '', headersJson: '',
    enabled: row.enabled, isVision: row.isVision, timeoutSeconds: row.timeoutSeconds, stdioCommand: row.stdioCommand ?? '', stdioArgsJson: row.stdioArgsJson ?? '',
    instructions: row.instructions ?? '',
  })
  dialogOpen.value = true
}

async function save() {
  if (!headersJsonValid()) {
    kernel.notify.error(t('admin.mcp.headersInvalid'))
    return
  }
  const body = {
    ...form,
    headersJson: form.headersJson || undefined,
    endpoint: form.endpoint || undefined,
    stdioCommand: form.stdioCommand || undefined,
    stdioArgsJson: form.stdioArgsJson || undefined,
    instructions: form.instructions || undefined,
  }
  try {
    if (editingId.value) {
      await http.put(`/api/admin/mcp-servers/${editingId.value}`, body)
    } else {
      await http.post('/api/admin/mcp-servers', body)
    }
    dialogOpen.value = false
    kernel.notify.success(t('admin.mcp.saved'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.mcp.saveFailed'), (e as { code?: string }).code)
  }
}

async function remove(row: McpServerDto) {
  try {
    await ElMessageBox.confirm(t('admin.mcp.deleteConfirm', { name: row.name }), t('common.delete'), { type: 'warning' })
  } catch {
    return
  }
  await http.delete(`/api/admin/mcp-servers/${row.id}`)
  kernel.notify.success(t('admin.mcp.deleted'))
  await load()
}

/** “获取”：自动带出 description / tools / prompts / resources */
async function fetchCatalog(row: McpServerDto) {
  fetchingId.value = row.id
  try {
    await http.post(`/api/admin/mcp-servers/${row.id}/fetch`, {})
    kernel.notify.success(t('admin.mcp.catalogUpdated'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.mcp.fetchFailed'), (e as { code?: string }).code)
  } finally {
    fetchingId.value = null
  }
}

async function ping(row: McpServerDto) {
  try {
    const res = await http.post<{ latencyMs: number }>(`/api/admin/mcp-servers/${row.id}/ping`, {})
    kernel.notify.success(t('admin.mcp.pingOk', { ms: res.latencyMs }))
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.mcp.unreachable'), (e as { code?: string }).code)
  }
}

async function toggleItem(itemId: string, enabled: boolean) {
  await http.put(`/api/admin/mcp-servers/items/${itemId}/enabled`, { enabled })
  await load()
  kernel.notify.success(enabled ? t('admin.mcp.enabledMsg') : t('admin.mcp.disabledMsg'))
}

async function toggleServer(row: McpServerDto, enabled: boolean) {
  await http.put(`/api/admin/mcp-servers/${row.id}`, {
    name: row.name, transport: row.transport, endpoint: row.endpoint, headersJson: undefined,
    enabled, isVision: row.isVision, timeoutSeconds: row.timeoutSeconds,
  })
  await load()
}

/** 请求头 JSON 合法：空白视为未填；否则必须是 JSON 对象 */
function headersJsonValid(): boolean {
  const v = form.headersJson.trim()
  if (!v) return true
  try {
    const o = JSON.parse(v)
    return o !== null && typeof o === 'object' && !Array.isArray(o)
  } catch {
    return false
  }
}

/** dialog 内“获取”：先按表单内容落库（新建 POST / 编辑 PUT），再连接拉取目录。
 *  之前编辑模式直接用旧配置连接，弹窗中新填的请求头/端点根本没入库，导致带不进请求（401）。 */
const dialogFetching = ref(false)

async function fetchInDialog() {
  if (!form.name.trim() || (form.transport === 'Http' && !form.endpoint.trim())) {
    kernel.notify.warning(t('admin.mcp.fetchNeedBase'))
    return
  }
  if (!headersJsonValid()) {
    kernel.notify.error(t('admin.mcp.headersInvalid'))
    return
  }
  dialogFetching.value = true
  try {
    let id = editingId.value
    const body = {
      name: form.name, transport: form.transport,
      endpoint: form.endpoint || undefined,
      headersJson: form.headersJson || undefined,
      stdioCommand: form.stdioCommand || undefined,
      stdioArgsJson: form.stdioArgsJson || undefined,
      instructions: form.instructions || undefined,
      enabled: form.enabled, isVision: form.isVision, timeoutSeconds: form.timeoutSeconds,
    }
    if (id) {
      await http.put(`/api/admin/mcp-servers/${id}`, body)
    } else {
      const created = await http.post<{ id?: string } | string>('/api/admin/mcp-servers', body)
      id = typeof created === 'string' ? created : (created as { id: string }).id
      editingId.value = id
    }
    const res = await http.post<{ ok: boolean; instructions?: string | null }>(`/api/admin/mcp-servers/${id}/fetch`, {})
    if (res.instructions) {
      form.instructions = res.instructions
      kernel.notify.success(t('admin.mcp.instructionsFetched'))
    } else {
      kernel.notify.info(t('admin.mcp.instructionsEmpty'))
    }
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.mcp.fetchFailed'), (e as { code?: string }).code)
  } finally {
    dialogFetching.value = false
  }
}

onMounted(load)
</script>

<template>
  <div>
    <div class="head">
      <h3>{{ t('admin.mcp.title') }}</h3>
      <el-button type="primary" :disabled="ro" @click="openCreate">{{ t('admin.mcp.create') }}</el-button>
    </div>

    <el-table :data="list" v-loading="loading" size="small" stripe>
      <el-table-column type="expand">
        <template #default="{ row }">
          <div class="expand">
            <div class="sec-title">{{ t('admin.mcp.catalogSummary', { tools: row.toolCount, prompts: row.promptCount, resources: row.resourceCount }) }}</div>
            <el-table :data="row.items" size="small">
              <el-table-column :label="t('admin.mcp.kind')" width="100">
                <template #default="{ row: it }">
                  <el-tag size="small" :type="it.kind === 'Tool' ? 'primary' : it.kind === 'Prompt' ? 'warning' : 'success'">
                    {{ it.kind }}
                  </el-tag>
                </template>
              </el-table-column>
              <el-table-column prop="name" :label="t('common.name')" width="180" />
              <el-table-column prop="description" :label="t('common.description')" show-overflow-tooltip />
              <el-table-column :label="t('common.enabled')" width="90">
                <template #default="{ row: it }">
                  <el-switch :model-value="it.enabled" size="small" :disabled="ro" @change="(v: boolean) => toggleItem(it.id, v)" />
                </template>
              </el-table-column>
            </el-table>
          </div>
        </template>
      </el-table-column>
      <el-table-column prop="name" :label="t('common.name')" width="150" />
      <el-table-column prop="transport" :label="t('admin.mcp.transport')" width="90" />
      <el-table-column :label="t('admin.mcp.endpoint')" min-width="180">
        <template #default="{ row }"><span class="nc-mono nc-dim">{{ row.endpoint || row.stdioCommand || t('common.placeholderDash') }}</span></template>
      </el-table-column>
      <el-table-column :label="t('admin.mcp.headers')" width="120">
        <template #default="{ row }"><span class="nc-mono nc-dim">{{ row.headersMasked || t('common.placeholderDash') }}</span></template>
      </el-table-column>
      <el-table-column prop="toolCount" :label="t('admin.mcp.tools')" width="60" />
      <el-table-column :label="t('common.enabled')" width="80">
        <template #default="{ row }">
          <el-switch :model-value="row.enabled" size="small" :disabled="ro" @change="(v: boolean) => toggleServer(row, v)" />
        </template>
      </el-table-column>
      <el-table-column :label="t('admin.mcp.isVision')" width="90">
        <template #default="{ row }">
          <el-tag :type="row.isVision ? 'success' : 'info'" size="small">{{ row.isVision ? t('common.yes') : t('common.no') }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('common.actions')" width="330" fixed="right">
        <template #default="{ row }">
          <div class="row-actions">
            <el-button size="small" type="primary" plain :loading="fetchingId === row.id" :disabled="ro" @click="fetchCatalog(row)">{{ t('admin.mcp.fetch') }}</el-button>
            <el-button size="small" text :disabled="ro" @click="ping(row)">{{ t('common.ping') }}</el-button>
            <el-button size="small" text :disabled="ro" @click="openEdit(row)">{{ t('common.edit') }}</el-button>
            <el-button size="small" text type="danger" :disabled="ro" @click="remove(row)">{{ t('common.delete') }}</el-button>
          </div>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="dialogOpen" :title="editingId ? t('admin.mcp.editServer') : t('admin.mcp.addServer')" width="560px">
      <el-form label-width="110px" label-position="left">
        <el-form-item :label="t('common.name')" required><el-input v-model="form.name" /></el-form-item>
        <el-form-item :label="t('admin.mcp.transport')">
          <el-select v-model="form.transport">
            <el-option :label="t('admin.mcp.transportStreamable')" value="Http" />
            <el-option :label="t('admin.mcp.transportStdio')" value="Stdio" />
          </el-select>
        </el-form-item>
        <el-form-item v-if="form.transport === 'Http'" :label="t('admin.mcp.endpoint')" required>
          <el-input v-model="form.endpoint" placeholder="http://localhost:5300/mcp" />
        </el-form-item>
        <el-form-item v-if="form.transport === 'Http'" :label="t('admin.mcp.headersJson')">
          <el-input v-model="form.headersJson" type="textarea" :rows="3" placeholder='{"Authorization": "Bearer xxx"}' />
        </el-form-item>
        <el-form-item v-if="form.transport === 'Stdio'" :label="t('admin.mcp.command')">
          <el-input v-model="form.stdioCommand" placeholder="node /path/to/server.js" />
        </el-form-item>
        <el-form-item v-if="form.transport === 'Stdio'" :label="t('admin.mcp.argsJson')">
          <el-input v-model="form.stdioArgsJson" type="textarea" :rows="2" placeholder='["--flag"]' />
        </el-form-item>
        <el-form-item :label="t('admin.mcp.timeoutSeconds')"><el-input-number v-model="form.timeoutSeconds" :min="5" :max="600" /></el-form-item>
        <el-form-item :label="t('admin.mcp.isVision')">
          <el-switch v-model="form.isVision" />
        </el-form-item>
        <el-form-item :label="t('common.enabled')"><el-switch v-model="form.enabled" /></el-form-item>
        <el-form-item :label="t('admin.mcp.instructions')">
          <div class="instr-wrap">
            <el-input v-model="form.instructions" type="textarea" :rows="4" :placeholder="t('admin.mcp.instructionsPlaceholder')" />
            <el-button class="instr-fetch" size="small" type="primary" plain :loading="dialogFetching" :disabled="ro" @click="fetchInDialog">
              {{ t('admin.mcp.fetch') }}
            </el-button>
          </div>
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button @click="dialogOpen = false">{{ t('common.cancel') }}</el-button>
        <el-button type="primary" :disabled="ro" @click="save">{{ t('common.save') }}</el-button>
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

.expand {
  padding: 8px 20px 12px;
}

.sec-title {
  font-size: 12.5px;
  opacity: 0.75;
  margin-bottom: 8px;
}

.instr-wrap {
  display: flex;
  gap: 8px;
  width: 100%;
}

.instr-wrap .el-input {
  flex: 1;
}

.instr-fetch {
  align-self: flex-start;
  margin-top: 2px;
  white-space: nowrap;
}

.row-actions {
  display: flex;
  align-items: center;
  gap: 4px;
  white-space: nowrap;
}
</style>
