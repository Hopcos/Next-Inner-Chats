<script setup lang="ts">
import { computed, reactive, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { kernel } from '@/kernel'
import type { TeamEngineerDto } from '@/kernel/plugins'
import { translateError } from '@/api/http'

const props = defineProps<{ modelValue: boolean; sessionId: string | null }>()
const emit = defineEmits<{ 'update:modelValue': [boolean] }>()
const { t } = useI18n()

const loading = ref(false)
const saving = ref(false)
const errorText = ref('')

const enabled = ref(false)
const maxRounds = ref(2)
const parallel = ref(true)
const stopOnConsensus = ref(true)
const maxParallel = ref(4)

interface Row extends TeamEngineerDto {}

const engineers = ref<Row[]>([])

const catalog = computed(() => kernel.catalog.state)

watch(
  () => props.modelValue,
  async (open) => {
    if (!open || !props.sessionId) return
    errorText.value = ''
    loading.value = true
    if (!catalog.value.loaded) {
      await kernel.catalog.load().catch(() => undefined)
    }
    const cfg = await kernel.chat.fetchTeamConfig(props.sessionId)
    if (cfg) {
      enabled.value = cfg.enabled
      maxRounds.value = cfg.maxRounds
      parallel.value = cfg.parallel
      stopOnConsensus.value = cfg.stopOnConsensus
      maxParallel.value = cfg.maxParallel
      const rows = (cfg.engineers ?? []).map((e) => ({ ...e }))
      if (rows.length === 0) rows.push(newResponsible())
      engineers.value = rows
    } else {
      enabled.value = false
      maxRounds.value = 2
      parallel.value = true
      stopOnConsensus.value = true
      maxParallel.value = 4
      engineers.value = [newResponsible()]
    }
    loading.value = false
  },
)

function newResponsible(): Row {
  return {
    id: null,
    name: '',
    role: 1,
    providerId: '',
    modelId: '',
    displayOrder: 0,
    enabled: true,
  }
}

function newAssistant(order: number): Row {
  return {
    id: null,
    name: '',
    role: 2,
    providerId: '',
    modelId: '',
    displayOrder: order,
    enabled: true,
  }
}

function addAssistant() {
  const order = engineers.value.length
  engineers.value.push(newAssistant(order))
}

function removeAssistant(idx: number) {
  engineers.value.splice(idx, 1)
}

function onProviderChange(row: Row, providerId: string) {
  row.providerId = providerId
  row.modelId = ''
}

function modelLabel(m: { name: string; contextWindow: number | null; priceInPer1K: number; priceOutPer1K: number }): string {
  const ctx = m.contextWindow ? ` · ${m.contextWindow}k ctx` : ''
  const price = m.priceInPer1K > 0 || m.priceOutPer1K > 0 ? ` · ¥${m.priceInPer1K}/${m.priceOutPer1K}/1k` : ''
  return m.name + ctx + price
}

const responsible = computed(() => engineers.value.filter((e) => e.role === 1))
const assistants = computed(() => engineers.value.filter((e) => e.role === 2))

async function save() {
  const sid = props.sessionId
  if (!sid) return
  errorText.value = ''
  // 仅“启用团队模式”需要工程师完整性校验；关闭团队模式允许清空/暂缺工程师（服务端一致放宽）
  if (enabled.value) {
    // 本地预检（与服务端一致；具体的错误码给用户友好提示）
    const resp = engineers.value.find((e) => e.role === 1 && e.enabled)
    const assts = engineers.value.filter((e) => e.role === 2 && e.enabled)
    if (!resp || !resp.name.trim()) return (errorText.value = t('chat.teamNameRequired'))
    if (assts.length === 0) return (errorText.value = t('chat.teamNeedAssistant'))
    const names = engineers.value
      .filter((e) => e.enabled)
      .map((e) => e.name.trim())
      .filter(Boolean)
    if (new Set(names.map((n) => n.toLowerCase())).size !== names.length) return (errorText.value = t('chat.teamNameDuplicate'))
    for (const e of engineers.value) {
      if (!e.providerId || !e.modelId) return (errorText.value = t('chat.teamModelInvalid'))
    }
  }
  saving.value = true
  try {
    const dto = await kernel.chat.saveTeamConfig(sid, {
      enabled: enabled.value,
      maxRounds: Math.max(1, Math.min(10, Math.round(maxRounds.value || 2))),
      parallel: parallel.value,
      stopOnConsensus: stopOnConsensus.value,
      maxParallel: Math.max(1, Math.min(8, Math.round(maxParallel.value || 4))),
      engineers: engineers.value.map((e, i) => ({
        id: e.id ?? null,
        name: e.name.trim(),
        role: e.role,
        providerId: e.providerId,
        modelId: e.modelId,
        displayOrder: e.displayOrder || i,
        enabled: e.enabled,
      })),
    })
    kernel.notify.success(t('chat.teamSaved'))
    // 同步会话列表中的 teamMode（已由 saveTeamConfig 完成），保持抽屉与最新返回一致
    enabled.value = dto.enabled
    maxRounds.value = dto.maxRounds
    parallel.value = dto.parallel
    stopOnConsensus.value = dto.stopOnConsensus
    maxParallel.value = dto.maxParallel
    engineers.value = dto.engineers.map((e) => ({ ...e }))
    emit('update:modelValue', false)
  } catch (e) {
    const err = e as { code?: string; message?: string }
    const key = `chat.${(err.code ?? '').toLowerCase()}`
    errorText.value = translateError(err.code, err.message ?? '')
    kernel.notify.error(errorText.value, err.code)
  } finally {
    saving.value = false
  }
}
</script>

<template>
  <ElDrawer
    :model-value="modelValue"
    :title="t('chat.teamConfig')"
    size="440px"
    @update:model-value="emit('update:modelValue', $event)"
  >
    <div v-if="loading" class="nc-dim">{{ t('common.loading') }}</div>
    <template v-else>
      <p class="nc-dim hint">{{ t('chat.teamConfigHint') }}</p>

      <div class="sec">
        <el-switch v-model="enabled" />
        <span class="sec-title">{{ t('chat.teamEnable') }}</span>
        <div class="nc-dim sub">{{ t('chat.teamEnableHint') }}</div>
      </div>

      <div class="sec">
        <div class="sec-title">{{ t('chat.teamResponsible') }}</div>
        <div v-for="(row, i) in responsible" :key="'r' + i" class="eng-row">
          <el-input v-model="row.name" :placeholder="t('chat.teamEngineerName')" size="small" />
          <el-select v-model="row.providerId" :placeholder="t('chat.teamProvider')" size="small" @change="(v: string) => onProviderChange(row, v)">
            <el-option v-for="p in catalog.providers" :key="p.id" :label="p.isHealthy === false ? p.name + ' ⛔' : p.name" :value="p.id" />
          </el-select>
          <el-select v-model="row.modelId" :placeholder="t('chat.teamModel')" size="small" :disabled="!row.providerId">
            <el-option v-for="m in (catalog.providers.find((p) => p.id === row.providerId)?.models ?? [])" :key="m.id" :label="modelLabel(m)" :value="m.id" />
          </el-select>
        </div>
      </div>

      <div class="sec">
        <div class="sec-title">{{ t('chat.teamAssistantTitle') }}</div>
        <div v-for="(row, i) in assistants" :key="'a' + i" class="eng-row">
          <el-input v-model="row.name" :placeholder="t('chat.teamEngineerName')" size="small" />
          <el-select v-model="row.providerId" :placeholder="t('chat.teamProvider')" size="small" @change="(v: string) => onProviderChange(row, v)">
            <el-option v-for="p in catalog.providers" :key="p.id" :label="p.isHealthy === false ? p.name + ' ⛔' : p.name" :value="p.id" />
          </el-select>
          <el-select v-model="row.modelId" :placeholder="t('chat.teamModel')" size="small" :disabled="!row.providerId">
            <el-option v-for="m in (catalog.providers.find((p) => p.id === row.providerId)?.models ?? [])" :key="m.id" :label="modelLabel(m)" :value="m.id" />
          </el-select>
          <el-tooltip :content="t('common.delete')" placement="top">
            <el-button size="small" text type="danger" :aria-label="t('common.delete')" @click="removeAssistant(i)">🗑</el-button>
          </el-tooltip>
        </div>
        <el-button size="small" text type="primary" @click="addAssistant">{{ t('chat.teamAddAssistant') }}</el-button>
      </div>

      <div class="sec param-grid">
        <div class="param-item">
          <div class="nc-dim label">{{ t('chat.teamMaxRounds') }}</div>
          <el-input-number v-model="maxRounds" :min="1" :max="10" size="small" />
          <div class="nc-dim sub">{{ t('chat.teamMaxRoundsHint') }}</div>
        </div>
        <div class="param-item">
          <div class="nc-dim label">{{ t('chat.teamMaxParallel') }}</div>
          <el-input-number v-model="maxParallel" :min="1" :max="8" size="small" />
        </div>
      </div>

      <div class="sec">
        <el-switch v-model="parallel" />
        <span class="sec-title">{{ t('chat.teamParallel') }}</span>
        <div class="nc-dim sub">{{ t('chat.teamParallelHint') }}</div>
      </div>

      <div class="sec">
        <el-switch v-model="stopOnConsensus" />
        <span class="sec-title">{{ t('chat.teamStopOnConsensus') }}</span>
        <div class="nc-dim sub">{{ t('chat.teamStopOnConsensusHint') }}</div>
      </div>

      <div v-if="errorText" class="err nc-dim">{{ errorText }}</div>

      <div class="drawer-footer">
        <el-button @click="emit('update:modelValue', false)">{{ t('common.cancel') }}</el-button>
        <el-button type="primary" :loading="saving" @click="save">{{ t('chat.teamSave') }}</el-button>
      </div>
    </template>
  </ElDrawer>
</template>

<style scoped>
.hint {
  font-size: 12px;
  line-height: 1.6;
  margin: 0 0 12px;
}

.sec {
  margin-bottom: 14px;
  padding-bottom: 10px;
  border-bottom: 1px dashed var(--nc-border);
}

.sec:last-of-type {
  border-bottom: none;
}

.sec-title {
  font-size: 13px;
  font-weight: 600;
  margin-left: 8px;
  vertical-align: middle;
}

.sub {
  font-size: 11.5px;
  line-height: 1.5;
  margin-top: 4px;
}

.eng-row {
  display: grid;
  grid-template-columns: 1fr;
  gap: 6px;
  margin-top: 8px;
  padding: 8px;
  border: 1px solid var(--nc-border);
  border-radius: 8px;
}

.param-grid {
  display: grid;
  grid-template-columns: 1fr 1fr;
  gap: 10px;
}

.param-item .label {
  font-size: 11.5px;
  margin-bottom: 4px;
}

.err {
  color: var(--el-color-danger);
  font-size: 12px;
  margin: 8px 0;
}

.drawer-footer {
  display: flex;
  justify-content: flex-end;
  gap: 8px;
  margin-top: 16px;
}
</style>
