<script setup lang="ts">
/** 管理端：工作空间（服务器目录）注册 + 角色授权（级别绑定）。管理端 API 见 AdminWorkspacesController。 */
import { onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElMessage, ElMessageBox } from 'element-plus'
import { http } from '@/api/http'
import FsBrowserDialog from '@/components/fs/FsBrowserDialog.vue'
import type { FsEntryDto } from '@/api/types'

const { t } = useI18n()

interface WsBinding {
  roleId: string
  roleName: string
  level: number
}
interface WsRow {
  id: string
  name: string
  rootPath: string
  description?: string | null
  enabled: boolean
  createdAt: string
  updatedAt: string
  bindings: WsBinding[]
}

const rows = ref<WsRow[]>([])
const loading = ref(false)

async function load() {
  loading.value = true
  try {
    rows.value = await http.get<WsRow[]>('/api/admin/workspaces')
  } finally {
    loading.value = false
  }
}
onMounted(load)

// ---------- 新建/编辑 ----------
const editVisible = ref(false)
const editing = ref<WsRow | null>(null)
const form = ref({ name: '', rootPath: '', description: '', enabled: true })
const saveBusy = ref(false)
const browseVisible = ref(false)

function openCreate() {
  editing.value = null
  form.value = { name: '', rootPath: '', description: '', enabled: true }
  editVisible.value = true
}
function openEdit(r: WsRow) {
  editing.value = r
  form.value = { name: r.name, rootPath: r.rootPath, description: r.description ?? '', enabled: r.enabled }
  editVisible.value = true
}

async function save() {
  if (!form.value.rootPath.trim()) {
    ElMessage.warning(t('workspace.pathRequired'))
    return
  }
  saveBusy.value = true
  try {
    if (editing.value) {
      await http.put(`/api/admin/workspaces/${editing.value.id}`, {
        name: form.value.name || null,
        rootPath: form.value.rootPath || null,
        description: form.value.description ?? null,
        enabled: form.value.enabled,
      })
      ElMessage.success(t('workspace.saved'))
    } else {
      await http.post('/api/admin/workspaces', {
        name: form.value.name,
        rootPath: form.value.rootPath,
        description: form.value.description || null,
        enabled: form.value.enabled,
      })
      ElMessage.success(t('workspace.created'))
    }
    editVisible.value = false
    await load()
  } catch (err) {
    const msg = (err as { body?: { message?: string } })?.body?.message
    ElMessage.error(msg ?? String(err))
  } finally {
    saveBusy.value = false
  }
}

async function remove(r: WsRow) {
  try {
    await ElMessageBox.confirm(t('workspace.deleteConfirm', { name: r.name }), t('common.confirm'), { type: 'warning' })
    await http.delete(`/api/admin/workspaces/${r.id}`)
    await load()
    ElMessage.success(t('workspace.deleted'))
  } catch {
    /* 取消 */
  }
}

// ---------- 角色绑定（级别） ----------
const bindVisible = ref(false)
const bindTarget = ref<WsRow | null>(null)
const bindForm = ref<Record<string, number>>({})
const roles = ref<{ id: string; name: string }[]>([])

async function openBind(r: WsRow) {
  bindTarget.value = r
  bindForm.value = {}
  for (const b of r.bindings) bindForm.value[b.roleId] = b.level
  try {
    roles.value = await http.get<{ id: string; name: string; code: string }[]>('/api/admin/roles')
  } catch {
    /* 角色加载失败仍可打开 */
  }
  bindVisible.value = true
}

async function saveBind() {
  const target = bindTarget.value
  if (!target) return
  const bindings = Object.entries(bindForm.value)
    .filter(([, level]) => level > 0)
    .map(([roleId, level]) => ({ roleId, level }))
  try {
    await http.put(`/api/admin/workspaces/${target.id}/bindings`, { bindings })
    ElMessage.success(t('workspace.bindSaved'))
    bindVisible.value = false
    await load()
  } catch (err) {
    ElMessage.error(String(err))
  }
}

const fsBrowseApi = async (path: string | null) => {
  const r = await http.get<{ path: string; parent: string | null; entries: FsEntryDto[] }>(
    `/api/admin/workspaces/browse${path ? '?path=' + encodeURIComponent(path) : ''}`,
  )
  return r
}

function onFsSelect(p: string) {
  form.value.rootPath = p
}
</script>

<template>
  <div class="ws-admin">
    <div class="ws-head">
      <h3>{{ t('workspace.title') }}</h3>
      <el-button size="small" type="primary" @click="openCreate">＋ {{ t('workspace.create') }}</el-button>
    </div>

    <el-table :data="rows" v-loading="loading" size="small" stripe>
      <el-table-column prop="name" :label="t('workspace.name')" min-width="140" />
      <el-table-column prop="rootPath" :label="t('workspace.rootPath')" min-width="260" show-overflow-tooltip />
      <el-table-column :label="t('workspace.roleBindings')" min-width="220">
        <template #default="{ row }">
          <el-tag v-for="b in row.bindings" :key="b.roleId" size="small" class="ws-tag" :type="b.level >= 30 ? 'danger' : b.level >= 20 ? 'warning' : 'info'">
            {{ b.roleName }}: {{ b.level >= 40 ? t('workspace.levelAuto') : b.level >= 30 ? t('workspace.levelFull') : b.level >= 20 ? t('workspace.levelWrite') : t('workspace.levelRead') }}
          </el-tag>
          <span v-if="row.bindings.length === 0" class="ws-none-bind">{{ t('workspace.noBindings') }}</span>
        </template>
      </el-table-column>
      <el-table-column :label="t('workspace.enabled')" width="90">
        <template #default="{ row }">
          <el-tag size="small" :type="row.enabled ? 'success' : 'info'">{{ row.enabled ? t('common.yes') : t('common.no') }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('common.actions')" width="215" fixed="right">
        <template #default="{ row }">
          <div style="display: flex; align-items: center; gap: 4px; white-space: nowrap;">
            <el-button size="small" text @click="openEdit(row)">{{ t('common.edit') }}</el-button>
            <el-button size="small" text @click="openBind(row)">{{ t('workspace.bindRoles') }}</el-button>
            <el-button size="small" text type="danger" @click="remove(row)">{{ t('common.delete') }}</el-button>
          </div>
        </template>
      </el-table-column>
    </el-table>

    <!-- 新建/编辑 -->
    <el-dialog :model-value="editVisible" :title="editing ? t('workspace.edit') : t('workspace.create')" width="520px" @update:model-value="(v: boolean) => (editVisible = v)">
      <el-form label-position="top" size="small">
        <el-form-item :label="t('workspace.name')">
          <el-input v-model="form.name" :placeholder="t('workspace.namePlaceholder')" />
        </el-form-item>
        <el-form-item :label="t('workspace.rootPath')" required>
          <div class="ws-path-row">
            <el-input v-model="form.rootPath" placeholder="D:\workspaces\repo" />
            <el-button @click="browseVisible = true">📂 {{ t('workspace.browseServer') }}</el-button>
          </div>
          <div class="ws-hint">{{ t('workspace.pathHint') }}</div>
        </el-form-item>
        <el-form-item :label="t('workspace.description')">
          <el-input v-model="form.description" type="textarea" :rows="2" />
        </el-form-item>
        <el-form-item>
          <el-switch v-model="form.enabled" active-text="Enabled" />
        </el-form-item>
      </el-form>
      <template #footer>
        <el-button size="small" @click="editVisible = false">{{ t('common.cancel') }}</el-button>
        <el-button size="small" type="primary" :loading="saveBusy" @click="save">{{ t('common.save') }}</el-button>
      </template>
    </el-dialog>

    <!-- 角色绑定（级别） -->
    <el-dialog :model-value="bindVisible" :title="`${t('workspace.bindRoles')} — ${bindTarget?.name ?? ''}`" width="460px" @update:model-value="(v: boolean) => (bindVisible = v)">
      <div class="bind-grid">
        <div v-for="r in roles" :key="r.id" class="bind-row">
          <span class="bind-role">{{ r.name }}</span>
          <el-select v-model="bindForm[r.id]" size="small" style="width: 180px" :placeholder="t('workspace.noBind')">
            <el-option :value="0" :label="t('workspace.noBind')" />
            <el-option :value="10" :label="t('workspace.levelRead')" />
            <el-option :value="20" :label="t('workspace.levelWrite')" />
            <el-option :value="30" :label="t('workspace.levelFull')" />
            <el-option :value="40" :label="t('workspace.levelAuto')" />
          </el-select>
        </div>
      </div>
      <template #footer>
        <el-button size="small" @click="bindVisible = false">{{ t('common.cancel') }}</el-button>
        <el-button size="small" type="primary" @click="saveBind">{{ t('common.save') }}</el-button>
      </template>
    </el-dialog>

    <FsBrowserDialog
      v-model="browseVisible"
      :title="t('workspace.browseServer')"
      :selectable="true"
      :select-label="t('workspace.fsSelect')"
      :api="fsBrowseApi"
      @select="onFsSelect"
    />
  </div>
</template>

<style scoped>
.ws-head {
  display: flex;
  align-items: center;
  justify-content: space-between;
  margin-bottom: 12px;
}
.ws-tag {
  margin-right: 6px;
}
.ws-none-bind {
  font-size: 12px;
  color: var(--nc-text-dim, #888);
}
.ws-path-row {
  display: flex;
  gap: 8px;
  width: 100%;
}
.ws-hint {
  font-size: 12px;
  color: var(--nc-text-dim, #888);
  margin-top: 4px;
}
.bind-grid {
  display: flex;
  flex-direction: column;
  gap: 8px;
  max-height: 50vh;
  overflow-y: auto;
}
.bind-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
}
.bind-role {
  font-size: 13px;
}
</style>
