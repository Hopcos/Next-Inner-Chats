<script setup lang="ts">
import { computed, onErrorCaptured, ref, watch } from 'vue'
import { useI18n } from 'vue-i18n'
import { ElDrawer } from 'element-plus'
import { kernel } from '@/kernel'
import type { ChatSettings } from '@/api/types'

const { t } = useI18n()

defineProps<{ modelValue: boolean }>()
const emit = defineEmits<{ 'update:modelValue': [boolean] }>()

const chat = computed(() => kernel.settings.state.chat)
const catalog = computed(() => kernel.catalog.state)

// 渲染错误兜底：不出现“一片空白”，给出可恢复的错误卡片
const renderError = ref<string | null>(null)
onErrorCaptured((err) => {
  renderError.value = err instanceof Error ? err.message : String(err)
  console.error('[ChatSettingsDrawer] render error:', err)
  return false
})

function patch(p: Partial<typeof chat.value>) {
  kernel.settings.updateChat(p)
}

const selectedProvider = computed(() => catalog.value.providers.find((p) => p.id === chat.value.providerId))

const unhealthySelected = computed(
  () => !!chat.value.providerId && catalog.value.providers.some((p) => p.id === chat.value.providerId && p.isHealthy === false),
)

// 设置与 catalog 校准：已删除/禁用的供应商与模型自动清空（避免静默降级困惑）。
// 门控：catalog 已加载 且 服务端记忆已拉到，避免在 pull 完成前用过期本地值误清选择。
watch(
  () => [catalog.value.loaded, kernel.settings.state.loaded] as const,
  ([loaded, settingsLoaded]) => {
    if (!loaded || !settingsLoaded) return
    const patchData: Partial<ChatSettings> = {}
    if (chat.value.providerId && !catalog.value.providers.some((p) => p.id === chat.value.providerId)) {
      patchData.providerId = null
      patchData.modelId = null
    } else if (chat.value.modelId) {
      const sel = catalog.value.providers.find((p) => p.id === chat.value.providerId)
      if (!sel?.models.some((m) => m.id === chat.value.modelId)) patchData.modelId = null
    }
    if (patchData.providerId !== undefined || patchData.modelId !== undefined) kernel.settings.updateChat(patchData)
  },
)

function onProviderChange(v: string) {
  const next = catalog.value.providers.find((p) => p.id === v)
  const keep = next?.models.some((m) => m.id === chat.value.modelId) ? chat.value.modelId : null
  patch({ providerId: v, modelId: keep })
}

function modelLabel(m: { name: string; isVision: boolean; contextWindow: number; priceInPer1K: number; priceOutPer1K: number }): string {
  const vision = m.isVision ? ' 🖼' : ''
  return `${m.name}${vision} (${m.contextWindow})`
}

function toolCountLabel(m: { endpoint?: string | null; items: { kind: string }[] }): string {
  const n = m.items.filter((i) => i.kind === 'Tool').length
  return t('chat.toolCount', { n, endpoint: m.endpoint ?? t('common.placeholderDash') })
}
</script>

<template>
  <ElDrawer :model-value="modelValue" :title="t('chat.drawerTitle')" size="380px" @update:model-value="emit('update:modelValue', $event)">
    <div class="drawer-body">
      <div v-if="renderError" class="render-error">
        <p class="render-error-title">⚠️ {{ t('chat.settingsRenderError') }}</p>
        <p class="nc-dim render-error-detail">{{ renderError }}</p>
        <el-button size="small" @click="renderError = null; void kernel.catalog.load().catch(() => {})">{{ t('common.refresh') }}</el-button>
      </div>
      <template v-else-if="!catalog.loaded">
        <el-skeleton :rows="6" animated />
      </template>
      <template v-else>
        <h4 class="sec">{{ t('chat.secPrompt') }}</h4>
        <el-radio-group :model-value="chat.promptId ?? ''" class="vert" @change="(v: string | number | boolean) => patch({ promptId: v === '' ? null : (v as string) })">
          <el-radio v-for="p in catalog.prompts" :key="p.id" :value="p.id" class="radio-card">
            <div class="radio-title">{{ p.name }}</div>
            <div class="radio-desc nc-dim">{{ p.summary || p.description }}</div>
          </el-radio>
          <el-radio value="" class="radio-card">
            <div class="radio-title">{{ t('chat.notSpecified') }}</div>
            <div class="radio-desc nc-dim">{{ t('chat.defaultPrompt') }}</div>
          </el-radio>
        </el-radio-group>

        <h4 class="sec">{{ t('chat.secProvider') }}</h4>
        <el-select :model-value="chat.providerId ?? undefined" :placeholder="t('chat.autoRoute')" @change="(v: string) => onProviderChange(v)">
          <el-option v-for="p in catalog.providers" :key="p.id" :label="p.isHealthy === false ? p.name + ' ⛔' : p.name" :value="p.id" />
        </el-select>
        <div v-if="catalog.providers.length === 0" class="nc-dim empty-note">{{ t('chat.noProvider') }}</div>
        <div v-if="unhealthySelected" class="nc-dim empty-note warn-note">⛔ {{ t('chat.providerUnhealthy') }}</div>

        <h4 class="sec">{{ t('chat.secModel') }}</h4>
        <el-select :model-value="chat.modelId ?? undefined" :placeholder="t('chat.modelAuto')" :disabled="!selectedProvider" @change="(v: string) => patch({ modelId: v })">
          <el-option v-for="m in selectedProvider?.models ?? []" :key="m.id" :label="modelLabel(m)" :value="m.id" />
        </el-select>
        <div v-if="selectedProvider && (selectedProvider.models ?? []).length === 0" class="nc-dim empty-note">{{ t('chat.noModel') }}</div>

        <h4 class="sec">{{ t('chat.secDelegation') }}</h4>
        <div class="deleg-row">
          <span class="deleg-label">{{ t('chat.delegationToggle') }}</span>
          <el-switch :model-value="!!chat.delegationEnabled" @change="(v: string | number | boolean) => patch({ delegationEnabled: v === true })" />
        </div>
        <p class="nc-dim empty-note">{{ t('chat.delegationHint') }}</p>
        <el-select
          :model-value="chat.subAgentModelId ?? ''"
          :placeholder="t('chat.subAgentFollowMain')"
          :disabled="!chat.delegationEnabled || !selectedProvider"
          @change="(v: string) => patch({ subAgentModelId: v === '' ? null : v })"
        >
          <el-option :label="t('chat.subAgentFollowMain')" value="" />
          <el-option v-for="m in selectedProvider?.models ?? []" :key="m.id" :label="modelLabel(m)" :value="m.id" />
        </el-select>
        <p class="nc-dim empty-note">{{ t('chat.subAgentModelHint') }}</p>

        <h4 class="sec">{{ t('chat.secMcp') }}</h4>
        <el-checkbox-group :model-value="chat.mcpServerIds" class="vert" @change="(v: string[]) => patch({ mcpServerIds: v })">
          <el-checkbox v-for="m in catalog.mcps" :key="m.id" :value="m.id" class="radio-card">
            <div class="radio-title">{{ m.name }}</div>
            <div class="radio-desc nc-dim">{{ toolCountLabel(m) }}</div>
          </el-checkbox>
          <div v-if="catalog.mcps.length === 0" class="nc-dim empty-note">{{ t('chat.noMcpBound') }}</div>
        </el-checkbox-group>

        <h4 class="sec">{{ t('chat.secSkill') }}</h4>
        <el-checkbox-group :model-value="chat.skillIds" class="vert" @change="(v: string[]) => patch({ skillIds: v })">
          <el-checkbox v-for="s in catalog.skills" :key="s.id" :value="s.id" class="radio-card">
            <div class="radio-title">{{ s.name }}</div>
            <div class="radio-desc nc-dim">{{ s.summary || s.description }}</div>
          </el-checkbox>
          <div v-if="catalog.skills.length === 0" class="nc-dim empty-note">{{ t('chat.noSkillBound') }}</div>
        </el-checkbox-group>

        <p class="nc-dim auto-note">{{ t('chat.autoNote') }}</p>
      </template>
    </div>
  </ElDrawer>
</template>

<style scoped>
.drawer-body {
  padding: 0 4px;
}

.sec {
  margin: 18px 0 10px;
  font-size: 13.5px;
}

.vert {
  display: flex;
  flex-direction: column;
  align-items: stretch;
  gap: 4px;
}

.radio-card,
:deep(.el-checkbox) {
  margin-right: 0;
  height: auto;
  padding: 8px 10px;
  border: 1px solid var(--nc-border);
  border-radius: 10px;
  white-space: normal;
  align-items: flex-start;
}

.radio-title {
  font-size: 13px;
  font-weight: 600;
  line-height: 1.4;
}

.radio-desc {
  font-size: 12px;
  line-height: 1.5;
  margin-top: 2px;
}

.empty-note {
  font-size: 12px;
  padding: 6px 2px;
}

.deleg-row {
  display: flex;
  align-items: center;
  justify-content: space-between;
  gap: 10px;
  padding: 2px 0;
}

.deleg-label {
  font-size: 13px;
  line-height: 1.4;
}

.render-error {
  padding: 18px 6px;
  text-align: center;
}

.render-error-title {
  font-size: 13.5px;
  font-weight: 600;
  margin: 0 0 8px;
}

.render-error-detail {
  font-size: 11.5px;
  word-break: break-all;
  margin: 0 0 14px;
}

.auto-note {
  margin-top: 22px;
  font-size: 12px;
  border-top: 1px dashed var(--nc-border);
  padding-top: 14px;
}
</style>
