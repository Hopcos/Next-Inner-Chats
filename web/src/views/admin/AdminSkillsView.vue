<script setup lang="ts">
import { computed, onMounted, reactive, ref } from 'vue'
import { ElMessageBox } from 'element-plus'
import { useI18n } from 'vue-i18n'
import { http } from '@/api/http'
import type { SkillDto } from '@/api/types'
import { kernel } from '@/kernel'

const { t } = useI18n()

/** 当前账号只读：仅可查看后台，写操作全部禁用 */
const ro = computed(() => kernel.auth.state.user?.isReadonly ?? false)

// 模板语法字面量（避开 Vue 模板编译器对 {{ }} 的插值解析）
const syntaxInput = '{{' + 'input' + '}}'

const list = ref<SkillDto[]>([])
const loading = ref(false)
const dialogOpen = ref(false)
const editingId = ref<string | null>(null)

const form = reactive({
  name: '', description: '', summary: '', metaToolName: '', instruction: '', enabled: true,
  exampleInput: '', exampleOutput: '', modelOverride: '', maxNestedSteps: 4,
})

async function load() {
  loading.value = true
  try {
    list.value = await http.get<SkillDto[]>('/api/admin/skills')
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.skills.loadFailed'), (e as { code?: string }).code)
  } finally {
    loading.value = false
  }
}

function openCreate() {
  editingId.value = null
  Object.assign(form, {
    name: '', description: '', summary: '', metaToolName: '', instruction: '', enabled: true,
    exampleInput: '', exampleOutput: '', modelOverride: '', maxNestedSteps: 4,
  })
  dialogOpen.value = true
}

function openEdit(row: SkillDto) {
  editingId.value = row.id
  Object.assign(form, {
    name: row.name, description: row.description ?? '', summary: row.summary ?? '',
    metaToolName: row.metaToolName, instruction: row.instruction, enabled: row.enabled,
    exampleInput: row.exampleInput ?? '', exampleOutput: row.exampleOutput ?? '',
    modelOverride: row.modelOverride ?? '', maxNestedSteps: row.maxNestedSteps,
  })
  dialogOpen.value = true
}

async function save() {
  try {
    const body = {
      name: form.name, description: form.description, summary: form.summary,
      metaToolName: form.metaToolName, instruction: form.instruction, enabled: form.enabled,
      exampleInput: form.exampleInput || undefined, exampleOutput: form.exampleOutput || undefined,
      modelOverride: form.modelOverride || undefined, maxNestedSteps: form.maxNestedSteps,
    }
    if (editingId.value) {
      await http.put(`/api/admin/skills/${editingId.value}`, body)
    } else {
      await http.post('/api/admin/skills', body)
    }
    dialogOpen.value = false
    kernel.notify.success(t('admin.skills.saved'))
    await load()
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('admin.skills.saveFailed'), (e as { code?: string }).code)
  }
}

async function remove(row: SkillDto) {
  try {
    await ElMessageBox.confirm(t('admin.skills.deleteConfirm', { name: row.name }), t('common.delete'), { type: 'warning' })
  } catch {
    return
  }
  await http.delete(`/api/admin/skills/${row.id}`)
  await load()
}

onMounted(load)
</script>

<template>
  <div>
    <div class="head">
      <h3>{{ t('admin.skills.title') }}</h3>
      <el-button type="primary" :disabled="ro" @click="openCreate">{{ t('admin.skills.create') }}</el-button>
    </div>
    <el-table :data="list" v-loading="loading" size="small" stripe border>
      <el-table-column prop="name" :label="t('common.name')" width="140" min-width="110" resizable show-overflow-tooltip />
      <el-table-column prop="metaToolName" :label="t('admin.skills.colMetaTool')" width="150" min-width="120" resizable class-name="nc-mono" show-overflow-tooltip />
      <el-table-column prop="summary" :label="t('common.summary')" min-width="200" resizable show-overflow-tooltip />
      <el-table-column :label="t('common.enabled')" width="80" min-width="70" resizable>
        <template #default="{ row }">
          <el-tag :type="row.enabled ? 'success' : 'info'" size="small">{{ row.enabled ? t('common.enabled') : t('common.disabled') }}</el-tag>
        </template>
      </el-table-column>
      <el-table-column :label="t('common.actions')" min-width="140" resizable>
        <template #default="{ row }">
          <el-button size="small" text :disabled="ro" @click="openEdit(row)">{{ t('common.edit') }}</el-button>
          <el-button size="small" text type="danger" :disabled="ro" @click="remove(row)">{{ t('common.delete') }}</el-button>
        </template>
      </el-table-column>
    </el-table>

    <el-dialog v-model="dialogOpen" :title="editingId ? t('admin.skills.editTitle') : t('admin.skills.addTitle')" width="700px">
      <el-form label-width="100px" label-position="left">
        <el-form-item :label="t('common.name')" required><el-input v-model="form.name" /></el-form-item>
        <el-form-item :label="t('common.description')"><el-input v-model="form.description" /></el-form-item>
        <el-form-item :label="t('common.summary')"><el-input v-model="form.summary" :placeholder="t('admin.skills.summaryPlaceholder')" /></el-form-item>
        <el-form-item :label="t('admin.skills.metaTool')" required>
          <el-input v-model="form.metaToolName" :placeholder="t('admin.skills.metaToolPlaceholder')" class="nc-mono" />
        </el-form-item>
        <el-form-item :label="t('admin.skills.instruction')" required>
          <el-input v-model="form.instruction" type="textarea" :rows="8" :placeholder="t('admin.skills.instructionPlaceholder', { syntax: syntaxInput })" />
        </el-form-item>
        <el-form-item :label="t('admin.skills.exampleInput')"><el-input v-model="form.exampleInput" /></el-form-item>
        <el-form-item :label="t('admin.skills.exampleOutput')"><el-input v-model="form.exampleOutput" /></el-form-item>
        <el-form-item :label="t('admin.skills.maxNestedSteps')"><el-input-number v-model="form.maxNestedSteps" :min="1" :max="20" /></el-form-item>
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
