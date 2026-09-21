<script setup lang="ts">
/**
 * 管理端 · 沉浸式工具栏维护：
 * - 工具唯一标识：从前端已注册的 Cordis 工具插件清单中选择（不可手输，杜绝脏 key）
 * - 图标：系统内置图标库（key 存库，前端渲染）
 * - 名称/描述/启用/允许角色（角色多选；未绑定 = 仅管理员可见）
 */
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import { kernel } from '@/kernel'
import type { RoleDto } from '@/api/types'
import { listToolPlugins } from '@/tools/registry'
import { BUILTIN_ICONS } from '@/tools/icons'
import ToolIcon from '@/tools/ToolIcon.vue'

const { t } = useI18n()

/** 当前账号只读：仅可查看后台，写操作全部禁用 */
const ro = computed(() => kernel.auth.state.user?.isReadonly ?? false)

interface AdminToolDto {
  id: string
  toolKey: string
  name: string
  icon: string
  description?: string | null
  baseUrl?: string | null
  enabled: boolean
  createdAt: string
  updatedAt: string
  roleIds: string[]
  roleNames: string[]
}

const tools = ref<AdminToolDto[]>([])
const allRoles = ref<RoleDto[]>([])
const loading = ref(false)
const dialogOpen = ref(false)
const saving = ref(false)
const editingId = ref<string | null>(null)

/** 插件清单（来自 Cordis 注册中心：装载 kernel 后即全量） */
const pluginDefs = computed(() => listToolPlugins())
const pluginName = (key: string, defName: string, nameKey?: string) => (nameKey ? t(nameKey) : defName || key)

/** 新建时已被占用的 key 禁选（唯一标识不可重复注册） */
function keyDisabled(key: string) {
  return tools.value.some((x) => x.toolKey === key && x.id !== editingId.value)
}

const emptyForm = () => ({
  toolKey: '',
  icon: 'toolbox',
  name: '',
  description: '',
  baseUrl: '',
  enabled: true,
  roleIds: [] as string[],
})
const form = reactive(emptyForm())

async function load() {
  loading.value = true
  try {
    const [list, roles] = await Promise.all([
      http.get<AdminToolDto[]>('/api/admin/tools'),
      http.get<RoleDto[]>('/api/admin/roles'),
    ])
    tools.value = list
    allRoles.value = roles
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.tools.loadFailed'), (e as { code?: string }).code)
  } finally {
    loading.value = false
  }
}

onMounted(load)

function openCreate() {
  editingId.value = null
  Object.assign(form, emptyForm())
  dialogOpen.value = true
}

function openEdit(row: AdminToolDto) {
  editingId.value = row.id
  Object.assign(form, {
    toolKey: row.toolKey,
    icon: row.icon,
    name: row.name,
    description: row.description ?? '',
    baseUrl: row.baseUrl ?? '',
    enabled: row.enabled,
    roleIds: [...row.roleIds],
  })
  dialogOpen.value = true
}

/** 选择插件标识时自动带出默认名称/图标/描述（仍可修改） */
function onKeyChange(key: string) {
  const def = pluginDefs.value.find((d) => d.key === key)
  if (!def) return
  form.icon = def.defaultIcon
  form.name = pluginName(def.key, def.defaultName, def.nameKey)
  if (def.descriptionKey) form.description = t(def.descriptionKey)
}

/** 编辑态改图标时同步预览 */
async function onSave() {
  if (!form.toolKey || !form.name.trim()) {
    kernel.notify.warning(t('admin.tools.fieldsRequired'))
    return
  }
  saving.value = true
  try {
    const body = {
      toolKey: form.toolKey,
      name: form.name.trim(),
      icon: form.icon,
      description: form.description.trim() || null,
      baseUrl: form.baseUrl.trim() || null,
      enabled: form.enabled,
      roleIds: form.roleIds,
    }
    if (editingId.value) await http.put(`/api/admin/tools/${editingId.value}`, body)
    else await http.post('/api/admin/tools', body)
    kernel.notify.success(t('admin.tools.saved'))
    dialogOpen.value = false
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.tools.saveFailed'), (e as { code?: string }).code)
  } finally {
    saving.value = false
  }
}

async function onDelete(row: AdminToolDto) {
  try {
    await ElMessageBox.confirm(t('admin.tools.deleteConfirm', { name: row.name }), t('admin.tools.deleteTitle'), { type: 'warning' })
  } catch {
    return
  }
  try {
    await http.delete(`/api/admin/tools/${row.id}`)
    kernel.notify.success(t('admin.tools.deleted'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.tools.deleteFailed'), (e as { code?: string }).code)
  }
}
</script>

<template>
  <div>
    <div class="head">
      <h2 class="nc-page-title">{{ t('admin.tools.title') }}</h2>
      <el-button type="primary" size="small" :disabled="ro" @click="openCreate">{{ t('admin.tools.create') }}</el-button>
    </div>
    <p class="tip">{{ t('admin.tools.tip') }}</p>

    <el-table v-loading="loading" :data="tools" size="small" class="tbl">
      <el-table-column width="52">
        <template #default="{ row }">
          <span class="cell-ico"><ToolIcon :icon="row.icon" :size="18" /></span>
        </template>
      </el-table-column>
      <el-table-column prop="toolKey" :label="t('admin.tools.toolKey')" width="130">
        <template #default="{ row }"><code class="key">{{ row.toolKey }}</code></template>
      </el-table-column>
      <el-table-column prop="name" :label="t('admin.tools.name')" width="150" />
      <el-table-column prop="description" :label="t('admin.tools.description')" min-width="200" show-overflow-tooltip />
      <el-table-column prop="baseUrl" :label="t('admin.tools.endpoint')" min-width="180" show-overflow-tooltip />
      <el-table-column :label="t('admin.tools.roles')" width="200">
        <template #default="{ row }">
          <el-tag v-for="rn in row.roleNames" :key="rn" size="small" class="role-tag">{{ rn }}</el-tag>
          <span v-if="!row.roleNames.length" class="admin-only">{{ t('admin.tools.adminOnly') }}</span>
        </template>
      </el-table-column>
      <el-table-column :label="t('admin.tools.enabled')" width="80">
        <template #default="{ row }">
          <el-tag :type="row.enabled ? 'success' : 'info'" size="small">{{ row.enabled ? 'ON' : 'OFF' }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('common.actions')" width="130">
        <template #default="{ row }">
          <el-button size="small" text type="primary" :disabled="ro" @click="openEdit(row)">{{ t('common.edit') }}</el-button>
          <el-button size="small" text type="danger" :disabled="ro" @click="onDelete(row)">{{ t('common.delete') }}</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="dialogOpen" :title="editingId ? t('admin.tools.editTitle') : t('admin.tools.create')" width="560px" append-to-body>
      <el-form label-width="110px" label-position="left">
        <el-form-item :label="t('admin.tools.toolKey')">
          <el-select v-model="form.toolKey" style="width: 100%" :disabled="!!editingId" @change="onKeyChange">
            <el-option
              v-for="def in pluginDefs"
              :key="def.key"
              :value="def.key"
              :label="`${def.key} — ${pluginName(def.key, def.defaultName, def.nameKey)}`"
              :disabled="keyDisabled(def.key)"
            />
          </el-select>
          <div class="field-tip">{{ t('admin.tools.toolKeyTip') }}</div>
        </el-form-item>

        <el-form-item :label="t('admin.tools.icon')">
          <div class="icon-picker">
            <button
              v-for="ic in BUILTIN_ICONS"
              :key="ic.key"
              type="button"
              class="icon-opt"
              :class="{ on: form.icon === ic.key }"
              :title="ic.key"
              @click="form.icon = ic.key"
            >
              <ToolIcon :icon="ic.key" :size="20" />
            </button>
          </div>
        </el-form-item>

        <el-form-item :label="t('admin.tools.name')">
          <el-input v-model="form.name" maxlength="64" />
        </el-form-item>

        <el-form-item :label="t('admin.tools.description')">
          <el-input v-model="form.description" type="textarea" :rows="2" maxlength="256" show-word-limit />
        </el-form-item>

        <el-form-item :label="t('admin.tools.endpoint')">
          <el-input v-model="form.baseUrl" maxlength="512" :placeholder="`https://host:port/`" clearable />
          <div class="field-tip">{{ t('admin.tools.endpointTip') }}</div>
        </el-form-item>

        <el-form-item :label="t('admin.tools.roles')">
          <el-select v-model="form.roleIds" multiple style="width: 100%">
            <el-option v-for="r in allRoles" :key="r.id" :label="`${r.name}（${r.code}）`" :value="r.id" />
          </el-select>
          <div class="field-tip">{{ t('admin.tools.rolesTip') }}</div>
        </el-form-item>

        <el-form-item :label="t('admin.tools.enabled')">
          <el-switch v-model="form.enabled" />
        </el-form-item>
      </el-form>

      <template #footer>
        <el-button @click="dialogOpen = false">{{ t('common.cancel') }}</el-button>
        <el-button type="primary" :loading="saving" :disabled="ro" @click="onSave">{{ t('common.save') }}</el-button>
      </template>
    </el-dialog>
  </div>
</template>

<style scoped>
.head {
  display: flex;
  align-items: center;
  justify-content: space-between;
}

.tip {
  color: var(--nc-text-dim);
  font-size: 12.5px;
  margin: 6px 0 14px;
}

.tbl {
  width: 100%;
}

.cell-ico {
  width: 32px;
  height: 32px;
  border-radius: 9px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 12%, transparent);
}

.key {
  font-size: 12px;
  background: rgba(148, 163, 184, 0.14);
  padding: 2px 6px;
  border-radius: 5px;
}

.role-tag {
  margin-right: 4px;
  margin-bottom: 2px;
}

.admin-only {
  font-size: 12px;
  color: var(--nc-text-dim);
  font-style: italic;
}

.field-tip {
  font-size: 12px;
  color: var(--nc-text-dim);
  line-height: 1.5;
  margin-top: 4px;
}

.icon-picker {
  display: grid;
  grid-template-columns: repeat(6, 40px);
  gap: 8px;
}

.icon-opt {
  width: 40px;
  height: 40px;
  border-radius: 10px;
  border: 1px solid var(--nc-border);
  background: transparent;
  color: var(--nc-text-dim);
  cursor: pointer;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  transition: all 0.15s;
}

.icon-opt:hover {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
}

.icon-opt.on {
  color: #fff;
  background: var(--nc-primary);
  border-color: var(--nc-primary);
}
</style>
