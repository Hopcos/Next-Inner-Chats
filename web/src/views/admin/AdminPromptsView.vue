<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import type { PromptDto } from '@/api/types'
import { kernel } from '@/kernel'

const { t } = useI18n()

/** 当前账号只读：仅可查看后台，写操作全部禁用 */
const ro = computed(() => kernel.auth.state.user?.isReadonly ?? false)

// 模板语法字面量（避开 Vue 模板编译器对 {{ }} 的插值解析）
const syntaxVar = '{{' + 'var' + '}} / #if / #each'
const syntaxFull = '{{' + 'var' + '}} / #if / #each / #section'

const list = ref<PromptDto[]>([])
const loading = ref(false)
const dialogOpen = ref(false)
const editingId = ref<string | null>(null)

const form = reactive({ name: '', description: '', summary: '', content: '', enabled: true, tags: '' })

async function load() {
  loading.value = true
  try {
    list.value = await http.get<PromptDto[]>('/api/admin/prompts')
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.prompts.loadFailed'), (e as { code?: string }).code)
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingId.value = null
  Object.assign(form, { name: '', description: '', summary: '', content: '', enabled: true, tags: '' })
  dialogOpen.value = true
}

function openEdit(row: PromptDto) {
  editingId.value = row.id
  Object.assign(form, {
    name: row.name, description: row.description ?? '', summary: row.summary ?? '',
    content: row.content, enabled: row.enabled, tags: (row.tags ?? []).join(','),
  })
  dialogOpen.value = true
}

async function save() {
  try {
    const body = {
      name: form.name, description: form.description, summary: form.summary, content: form.content,
      enabled: form.enabled,
      tags: form.tags ? form.tags.split(',').map((s) => s.trim()).filter(Boolean) : [],
    }
    if (editingId.value) {
      await http.put(`/api/admin/prompts/${editingId.value}`, body)
    } else {
      await http.post('/api/admin/prompts', body)
    }
    dialogOpen.value = false
    kernel.notify.success(t('admin.prompts.saved'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.prompts.saveFailed'), (e as { code?: string }).code)
  }
}

async function remove(row: PromptDto) {
  try {
    await ElMessageBox.confirm(t('admin.prompts.deleteConfirm', { name: row.name }), t('common.delete'), { type: 'warning' })
  } catch {
    return
  }
  await http.delete(`/api/admin/prompts/${row.id}`)
  await load()
}

onMounted(load)
</script>

<template>
  <div>
    <div class="head">
      <h3>{{ t('admin.prompts.title', { syntax: syntaxVar }) }}</h3>
      <el-button type="primary" :disabled="ro" @click="openCreate">{{ t('admin.prompts.create') }}</el-button>
    </div>
    <el-table :data="list" v-loading="loading" size="small" stripe border>
      <el-table-column prop="name" :label="t('common.name')" width="160" min-width="120" resizable show-overflow-tooltip />
      <el-table-column prop="summary" :label="t('common.summary')" min-width="220" resizable show-overflow-tooltip />
      <el-table-column :label="t('common.enabled')" width="80" min-width="70" resizable>
        <template #default="{ row }">
          <el-tag :type="row.enabled ? 'success' : 'info'" size="small">{{ row.enabled ? t('common.enabled') : t('common.disabled') }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column prop="updatedAt" :label="t('common.updatedAt')" width="170" min-width="140" resizable>
        <template #default="{ row }">{{ new Date(row.updatedAt).toLocaleString(undefined) }}</template>
      </el-table-column>
      <el-table-column :label="t('common.actions')" min-width="140" resizable>
        <template #default="{ row }">
          <el-button size="small" text :disabled="ro" @click="openEdit(row)">{{ t('common.edit') }}</el-button>
          <el-button size="small" text type="danger" :disabled="ro" @click="remove(row)">{{ t('common.delete') }}</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="dialogOpen" :title="editingId ? t('admin.prompts.editTitle') : t('admin.prompts.addTitle')" width="680px">
      <el-form label-width="90px" label-position="left">
        <el-form-item :label="t('common.name')" required><el-input v-model="form.name" /></el-form-item>
        <el-form-item :label="t('common.description')"><el-input v-model="form.description" /></el-form-item>
        <el-form-item :label="t('common.summary')"><el-input v-model="form.summary" :placeholder="t('admin.prompts.summaryPlaceholder')" /></el-form-item>
        <el-form-item :label="t('admin.prompts.contentLabel')" required>
          <el-input v-model="form.content" type="textarea" :rows="10" class="nc-mono" :placeholder="t('admin.prompts.contentPlaceholder', { syntax: syntaxFull })" />
        </el-form-item>
        <el-form-item :label="t('admin.prompts.tags')"><el-input v-model="form.tags" :placeholder="t('admin.prompts.tagsPlaceholder')" /></el-form-item>
        <el-form-item :label="t('common.enabled')"><el-switch v-model="form.enabled" /></el-form-item>
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
