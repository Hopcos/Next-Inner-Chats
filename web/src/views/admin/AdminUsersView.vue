<script setup lang="ts">
import { computed, nextTick, onMounted, reactive, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import type { RoleDto, UserDto } from '@/api/types'
import { kernel } from '@/kernel'

const { t } = useI18n()

const users = ref<UserDto[]>([])
const tableRef = ref<{ sort: (prop: string, order: 'ascending' | 'descending') => void } | null>(null)
const roles = ref<RoleDto[]>([])
const loading = ref(false)
const dialogOpen = ref(false)
const editingId = ref<string | null>(null)

const form = reactive({
  username: '', displayName: '', email: '', password: '', status: 'Active', roleIds: [] as string[], isReadonly: false,
})

/** 当前登录账号自身（禁止将自己设为只读，防止后台无人可管理） */
const self = kernel.auth.state.user
const isEditingSelf = computed(() => editingId.value !== null && editingId.value === self?.id)

/** 当前登录账号只读：仅可查看，所有写操作禁用 */
const ro = computed(() => self?.isReadonly ?? false)

async function load() {
  loading.value = true
  try {
    const [u, r] = await Promise.all([
      http.get<UserDto[]>('/api/admin/users'),
      http.get<RoleDto[]>('/api/admin/roles'),
    ])
    users.value = u
    roles.value = r
    // 数据到达后按最后登录降序（初次渲染时数据可能为空，default-sort 不一定生效）
    await nextTick()
    tableRef.value?.sort('lastLoginAt', 'descending')
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.users.loadFailed'), (e as { code?: string }).code)
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingId.value = null
  Object.assign(form, { username: '', displayName: '', email: '', password: '', status: 'Active', roleIds: [], isReadonly: false })
  dialogOpen.value = true
}

function openEdit(row: UserDto) {
  editingId.value = row.id
  Object.assign(form, {
    username: row.username, displayName: row.displayName ?? '', email: row.email ?? '',
    password: '', status: row.status, roleIds: row.roles.map((r) => r.id),
    isReadonly: row.isReadonly ?? false,
  })
  dialogOpen.value = true
}

async function save() {
  try {
    const body = {
      username: form.username, displayName: form.displayName, email: form.email,
      password: form.password || undefined, status: form.status, roleIds: form.roleIds,
      isReadonly: form.isReadonly,
    }
    if (editingId.value) {
      await http.put(`/api/admin/users/${editingId.value}`, body)
    } else {
      await http.post('/api/admin/users', body)
    }
    dialogOpen.value = false
    kernel.notify.success(t('admin.users.saved'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.users.saveFailed'), (e as { code?: string }).code)
  }
}

async function remove(row: UserDto) {
  try {
    await ElMessageBox.confirm(t('admin.users.deleteConfirm', { username: row.username }), t('common.delete'), { type: 'warning' })
  } catch {
    return
  }
  await http.delete(`/api/admin/users/${row.id}`)
  await load()
}

/** 最后登录时间展示（与审计页一致使用本地时区） */
function fmtDateTime(iso: string): string {
  const d = new Date(iso)
  return Number.isNaN(d.getTime()) ? iso : d.toLocaleString(undefined)
}

/** 排序：从未登录（null）视为最早 */
function sortByLastLogin(a: UserDto, b: UserDto): number {
  const ta = a.lastLoginAt ? new Date(a.lastLoginAt).getTime() : 0
  const tb = b.lastLoginAt ? new Date(b.lastLoginAt).getTime() : 0
  return ta - tb
}

onMounted(load)
</script>

<template>
  <div>
    <div class="head">
      <h3>{{ t('admin.users.title') }}</h3>
      <el-button type="primary" :disabled="ro" :title="ro ? t('admin.readonlyBanner') : undefined" @click="openCreate">{{ t('admin.users.create') }}</el-button>
    </div>
    <el-table
      ref="tableRef"
      :data="users"
      v-loading="loading"
      size="small"
      stripe
      border
      :default-sort="{ prop: 'lastLoginAt', order: 'descending' }"
    >
      <el-table-column prop="username" :label="t('admin.users.username')" min-width="120" show-overflow-tooltip resizable />
      <el-table-column :label="t('admin.users.authType')" min-width="100" resizable>
        <template #default="{ row }">
          <el-tag size="small" :type="row.authType && row.authType !== 'default' ? 'warning' : 'info'">
            {{ row.authType && row.authType !== 'default' ? row.authType : t('admin.users.authTypeDefault') }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="displayName" :label="t('admin.users.displayName')" min-width="120" show-overflow-tooltip resizable />
      <el-table-column prop="email" :label="t('admin.users.email')" min-width="180" show-overflow-tooltip resizable />
      <el-table-column :label="t('admin.users.roles')" min-width="180" resizable>
        <template #default="{ row }">
          <el-tag v-for="r in row.roles" :key="r.id" size="small" :type="r.code === 'admin' ? 'danger' : 'primary'" style="margin: 1px">
            {{ r.name }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('common.status')" min-width="80" resizable>
        <template #default="{ row }">
          <el-tag :type="row.status === 'Active' ? 'success' : 'info'" size="small">{{ row.status }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('admin.users.readonly')" min-width="90" resizable>
        <template #default="{ row }">
          <el-tag v-if="row.isReadonly" type="warning" size="small">{{ t('common.yes') }}</el-tag>
          <span v-else class="nc-dim">{{ t('common.no') }}</span>
        </template>
      </el-table-column>
      <el-table-column
        prop="lastLoginAt"
        :label="t('admin.users.lastLogin')"
        min-width="170"
        sortable
        resizable
        :sort-method="sortByLastLogin"
      >
        <template #default="{ row }">
          <span v-if="row.lastLoginAt">{{ fmtDateTime(row.lastLoginAt) }}</span>
          <span v-else class="nc-dim">{{ t('admin.users.neverLoggedIn') }}</span>
        </template>
      </el-table-column>
      <el-table-column :label="t('common.actions')" min-width="140" resizable>
        <template #default="{ row }">
          <el-button size="small" text :disabled="ro" @click="openEdit(row)">{{ t('common.edit') }}</el-button>
          <el-button size="small" text type="danger" :disabled="ro" @click="remove(row)">{{ t('common.delete') }}</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="dialogOpen" :title="editingId ? t('admin.users.editTitle') : t('admin.users.addTitle')" width="480px">
      <el-form label-width="90px" label-position="left">
        <el-form-item :label="t('admin.users.username')" required><el-input v-model="form.username" /></el-form-item>
        <el-form-item :label="t('admin.users.displayName')"><el-input v-model="form.displayName" /></el-form-item>
        <el-form-item :label="t('admin.users.email')"><el-input v-model="form.email" /></el-form-item>
        <el-form-item :label="t('admin.users.password')">
          <el-input v-model="form.password" type="password" show-password :placeholder="editingId ? t('admin.users.passwordPlaceholderEdit') : t('admin.users.passwordPlaceholderNew')" />
        </el-form-item>
        <el-form-item :label="t('common.status')">
          <el-select v-model="form.status">
            <el-option :label="t('admin.users.statusActive')" value="Active" />
            <el-option :label="t('admin.users.statusDisabled')" value="Disabled" />
          </el-select>
        </el-form-item>
        <el-form-item :label="t('admin.users.roles')">
          <el-select v-model="form.roleIds" multiple>
            <el-option v-for="r in roles" :key="r.id" :label="r.name" :value="r.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="t('admin.users.readonly')">
          <div>
            <el-switch v-model="form.isReadonly" :disabled="isEditingSelf" :title="isEditingSelf ? t('admin.users.readonlySelfBlocked') : t('admin.users.readonlyTip')" />
            <div class="nc-dim" style="font-size: 12px; line-height: 1.5; margin-top: 2px">
              {{ isEditingSelf ? t('admin.users.readonlySelfBlocked') : t('admin.users.readonlyTip') }}
            </div>
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
</style>
