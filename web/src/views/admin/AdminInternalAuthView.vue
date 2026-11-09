<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import type { InternalAuthProviderDto, InternalAuthSuccessRuleDto, RoleDto } from '@/api/types'
import { kernel } from '@/kernel'

const { t } = useI18n()

/** 当前账号只读：仅可查看后台，写操作全部禁用 */
const ro = computed(() => kernel.auth.state.user?.isReadonly ?? false)

const providers = ref<InternalAuthProviderDto[]>([])
const allRoles = ref<RoleDto[]>([])
const loading = ref(false)
const dialogOpen = ref(false)
const editingId = ref<string | null>(null)

const emptyProvider = () => ({
  name: '',
  api: '',
  httpMethod: 'POST',
  requestFormat: 'BodyJson',
  usernameField: 'username',
  passwordField: 'password',
  enabled: true,
  timeoutSeconds: 15,
  successRules: [] as InternalAuthSuccessRuleDto[],
  defaultRoleIds: [] as string[],
})

const form = reactive(emptyProvider())

async function load() {
  loading.value = true
  try {
    const [p, r] = await Promise.all([
      http.get<InternalAuthProviderDto[]>('/api/admin/internal-auth'),
      http.get<RoleDto[]>('/api/admin/roles'),
    ])
    providers.value = p
    allRoles.value = r
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.internalAuth.loadFailed'), (e as { code?: string }).code)
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingId.value = null
  Object.assign(form, emptyProvider())
  dialogOpen.value = true
}

function openEdit(row: InternalAuthProviderDto) {
  editingId.value = row.id
  Object.assign(form, {
    name: row.name,
    api: row.api,
    httpMethod: row.httpMethod,
    requestFormat: row.requestFormat,
    usernameField: row.usernameField,
    passwordField: row.passwordField,
    enabled: row.enabled,
    timeoutSeconds: row.timeoutSeconds,
    successRules: JSON.parse(JSON.stringify(row.successRules)) as InternalAuthSuccessRuleDto[],
    defaultRoleIds: [...row.defaultRoleIds],
  })
  dialogOpen.value = true
}

function addRule() {
  form.successRules.push({ field: '', operator: 'NotEmpty', expectedValue: '' })
}

function removeRule(index: number) {
  form.successRules.splice(index, 1)
}

async function save() {
  if (!form.name.trim() || !form.api.trim()) {
    kernel.notify.warning(t('admin.internalAuth.nameApiRequired'))
    return
  }
  if (form.successRules.length === 0) {
    kernel.notify.warning(t('admin.internalAuth.ruleRequired'))
    return
  }
  const rules = form.successRules.filter((r) => r.field.trim())
  const body = {
    name: form.name.trim(),
    api: form.api.trim(),
    httpMethod: form.httpMethod,
    requestFormat: form.requestFormat,
    usernameField: form.usernameField.trim() || 'username',
    passwordField: form.passwordField.trim() || 'password',
    enabled: form.enabled,
    timeoutSeconds: form.timeoutSeconds,
    successRules: rules.map((r) => ({ field: r.field.trim(), operator: r.operator, expectedValue: r.expectedValue || undefined })),
    defaultRoleIds: form.defaultRoleIds,
  }
  try {
    if (editingId.value) {
      await http.put(`/api/admin/internal-auth/${editingId.value}`, body)
    } else {
      await http.post('/api/admin/internal-auth', body)
    }
    dialogOpen.value = false
    kernel.notify.success(t('admin.internalAuth.saved'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.internalAuth.saveFailed'), (e as { code?: string }).code)
  }
}

async function remove(row: InternalAuthProviderDto) {
  try {
    await ElMessageBox.confirm(t('admin.internalAuth.deleteConfirm', { name: row.name }), t('common.delete'), { type: 'warning' })
  } catch {
    return
  }
  try {
    await http.delete(`/api/admin/internal-auth/${row.id}`)
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.internalAuth.deleteFailed'), (e as { code?: string }).code)
  }
}

async function testProvider(row: InternalAuthProviderDto) {
  try {
    const { value: username } = await ElMessageBox.prompt(t('admin.internalAuth.testUserPrompt'), `${t('admin.internalAuth.testTitle')}（${row.name}）`, {
      inputPlaceholder: t('admin.internalAuth.testUserPlaceholder'),
      confirmButtonText: t('admin.internalAuth.testBtn'),
    })
    if (!username) return
    const { value: password } = await ElMessageBox.prompt(t('admin.internalAuth.testPwdPrompt'), `${t('admin.internalAuth.testTitle')}（${row.name}）`, {
      inputPlaceholder: t('admin.internalAuth.testPwdPlaceholder'),
      confirmButtonText: t('admin.internalAuth.testBtn'),
    })
    if (password == null) return
    try {
      const res = await http.post<{ token: string }>('/api/auth/login', {
        username,
        password,
        authType: row.name,
      })
      kernel.notify.success(t('admin.internalAuth.testOk'))
      void res
    } catch (e) {
      kernel.notify.error((e as { message?: string }).message ?? t('admin.internalAuth.testFailed'), (e as { code?: string }).code)
    }
  } catch {
    /* 用户取消 */
  }
}

onMounted(load)
</script>

<template>
  <div>
    <div class="head">
      <h3>{{ t('admin.internalAuth.title') }}</h3>
      <el-button type="primary" :disabled="ro" @click="openCreate">{{ t('admin.internalAuth.create') }}</el-button>
    </div>

    <el-table :data="providers" v-loading="loading" size="small" stripe border>
      <el-table-column prop="name" :label="t('admin.internalAuth.colName')" width="110" min-width="90" resizable>
        <template #default="{ row }"><span class="nc-mono">{{ row.name }}</span></template>
      </el-table-column>
      <el-table-column prop="api" :label="t('admin.internalAuth.colApi')" min-width="220" resizable show-overflow-tooltip />
      <el-table-column prop="httpMethod" :label="t('admin.internalAuth.colMethod')" width="90" min-width="80" resizable />
      <el-table-column :label="t('admin.internalAuth.colRules')" width="100" min-width="80" resizable>
        <template #default="{ row }">
          <el-tag size="small" type="warning">{{ row.successRules.length }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('admin.internalAuth.colRoles')" min-width="150" resizable>
        <template #default="{ row }">
          <el-tag v-for="rid in row.defaultRoleIds" :key="rid" size="small" style="margin: 1px">
            {{ allRoles.find((r) => r.id === rid)?.name ?? rid.slice(0, 8) }}
          </el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('common.status')" width="80" min-width="70" resizable>
        <template #default="{ row }">
          <el-tag :type="row.enabled ? 'success' : 'info'" size="small">{{ row.enabled ? t('admin.internalAuth.enabled') : t('admin.internalAuth.disabled') }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('common.actions')" min-width="200" resizable>
        <template #default="{ row }">
          <el-button size="small" type="primary" plain :disabled="ro" @click="testProvider(row)">{{ t('admin.internalAuth.test') }}</el-button>
          <el-button size="small" text :disabled="ro" @click="openEdit(row)">{{ t('common.edit') }}</el-button>
          <el-button size="small" text type="danger" :disabled="ro" @click="remove(row)">{{ t('common.delete') }}</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="dialogOpen" :title="editingId ? t('admin.internalAuth.editTitle') : t('admin.internalAuth.addTitle')" width="640px">
      <el-form label-width="120px" label-position="left">
        <el-form-item :label="t('admin.internalAuth.name')" required>
          <el-input v-model="form.name" :placeholder="t('admin.internalAuth.namePlaceholder')" />
        </el-form-item>
        <el-form-item :label="t('admin.internalAuth.api')" required>
          <el-input v-model="form.api" :placeholder="t('admin.internalAuth.apiPlaceholder')" />
        </el-form-item>
        <el-form-item :label="t('admin.internalAuth.httpMethod')">
          <el-select v-model="form.httpMethod" style="width: 120px">
            <el-option v-for="m in ['POST', 'GET', 'PUT', 'PATCH']" :key="m" :label="m" :value="m" />
          </el-select>
          <span class="muted" style="margin-left: 12px">{{ t('admin.internalAuth.requestFormat', { fmt: form.requestFormat.replace('BodyJson', 'body(application/json)') }) }}</span>
        </el-form-item>
        <el-form-item :label="t('admin.internalAuth.credFields')">
          <div class="fields-row">
            <el-input v-model="form.usernameField" :placeholder="t('admin.internalAuth.usernameFieldPlaceholder')" style="width: 150px" />
            <span class="muted">{{ t('admin.internalAuth.and') }}</span>
            <el-input v-model="form.passwordField" :placeholder="t('admin.internalAuth.passwordFieldPlaceholder')" style="width: 150px" />
          </div>
        </el-form-item>
        <el-form-item :label="t('admin.internalAuth.successRules')" required>
          <div class="rules">
            <div v-for="(rule, i) in form.successRules" :key="i" class="rule-row">
              <el-input v-model="rule.field" :placeholder="t('admin.internalAuth.ruleFieldPlaceholder')" style="width: 170px" />
              <el-select v-model="rule.operator" style="width: 130px">
                <el-option :label="t('admin.internalAuth.opNotEmpty')" value="NotEmpty" />
                <el-option :label="t('admin.internalAuth.opEquals')" value="Equals" />
              </el-select>
              <el-input
                v-if="rule.operator === 'Equals'"
                v-model="rule.expectedValue"
                :placeholder="t('admin.internalAuth.expectedValuePlaceholder')"
                style="width: 160px"
              />
              <span v-else class="muted" style="width: 160px">{{ t('admin.internalAuth.notEmptyHint') }}</span>
              <el-button size="small" text type="danger" :disabled="ro" @click="removeRule(i)">✕</el-button>
            </div>
            <el-button size="small" plain :disabled="ro" @click="addRule">＋ {{ t('admin.internalAuth.addRule') }}</el-button>
          </div>
        </el-form-item>
        <el-form-item :label="t('admin.internalAuth.defaultRoles')">
          <el-select v-model="form.defaultRoleIds" multiple style="width: 100%">
            <el-option v-for="r in allRoles" :key="r.id" :label="r.name" :value="r.id" />
          </el-select>
        </el-form-item>
        <el-form-item :label="t('admin.internalAuth.timeout')">
          <el-input-number v-model="form.timeoutSeconds" :min="1" :max="120" />
        </el-form-item>
        <el-form-item :label="t('common.status')">
          <el-switch v-model="form.enabled" />
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

.fields-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.rules {
  display: flex;
  flex-direction: column;
  gap: 6px;
  width: 100%;
}

.rule-row {
  display: flex;
  align-items: center;
  gap: 8px;
}

.muted {
  opacity: 0.65;
  font-size: 12px;
}
</style>
