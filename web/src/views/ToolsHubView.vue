<script setup lang="ts">
/**
 * 沉浸式工具栏 · 主页面：以"资源管理器"式卡片陈列当前用户有权使用的工具。
 * 数据来源：/api/me/tools（服务端按启用状态 + 角色绑定过滤）；
 * 呈现组件来自前端 Cordis 插件注册中心——未注册 key 直接跳过（前后端以 ToolKey 对接）。
 */
import { computed, onMounted, ref } from 'vue'
import { useI18n } from 'vue-i18n'
import { useRouter } from 'vue-router'
import { http } from '@/api/http'
import { kernel } from '@/kernel'
import { getToolDefinition } from '@/tools/registry'
import ToolIcon from '@/tools/ToolIcon.vue'

interface UserToolDto {
  id: string
  key: string
  name: string
  icon: string
  description?: string | null
}

const { t } = useI18n()
const router = useRouter()

const loading = ref(true)
const tools = ref<UserToolDto[]>([])

const user = computed(() => kernel.auth.state.user)

function logout() {
  void kernel.auth.logout()
}

/** 进入后台管理：记录来源，后台的"返回"据此回到工具箱页面 */
function goAdmin() {
  localStorage.setItem('nextchats.admin.from', 'tools')
  router.push('/admin')
}

/** 只渲染本端已注册（可打开）的工具，未识别 key 静默过滤 */
const visibleTools = computed(() => tools.value.filter((x) => !!getToolDefinition(x.key)))

function displayName(tool: UserToolDto): string {
  if (tool.name) return tool.name
  const def = getToolDefinition(tool.key)
  return def ? (def.nameKey ? t(def.nameKey) : def.defaultName) : tool.key
}

function displayDesc(tool: UserToolDto): string {
  if (tool.description) return tool.description
  const def = getToolDefinition(tool.key)
  return def?.descriptionKey ? t(def.descriptionKey) : ''
}

onMounted(async () => {
  try {
    tools.value = await http.get<UserToolDto[]>('/api/me/tools')
  } catch (e) {
    kernel.notify.error((e as { message?: string }).message ?? t('tools.loadFailed'), (e as { code?: string }).code)
  } finally {
    loading.value = false
  }
})
</script>

<template>
  <div class="hub">
    <header class="hub-top">
      <button class="hub-back" @click="router.push('/')">
        <svg viewBox="0 0 24 24" width="15" height="15" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
          <path d="M15 5l-7 7 7 7" />
        </svg>
        {{ t('tools.backToChat') }}
      </button>
      <div class="hub-brand">
        <span class="hub-logo">
          <ToolIcon icon="toolbox" :size="20" />
        </span>
        <div>
          <h1 class="hub-title">{{ t('tools.hubTitle') }}</h1>
          <p class="hub-sub">{{ t('tools.hubSubtitle') }}</p>
        </div>
      </div>
      <div class="hub-user">
        <el-dropdown trigger="click">
          <el-avatar :size="28" class="hub-avatar">{{ (user?.displayName ?? user?.username ?? '?').slice(0, 1) }}</el-avatar>
          <template #dropdown>
            <el-dropdown-menu>
              <el-dropdown-item @click="router.push('/settings')">{{ t('chat.personalCatalog') }}</el-dropdown-item>
              <el-dropdown-item v-if="user?.isAdmin" @click="goAdmin">{{ t('common.admin') }}</el-dropdown-item>
              <el-dropdown-item divided @click="logout">{{ t('common.logout') }}</el-dropdown-item>
            </el-dropdown-menu>
          </template>
        </el-dropdown>
      </div>
    </header>

    <main class="hub-body" v-loading="loading">
      <div v-if="!loading && visibleTools.length === 0" class="hub-empty">
        <ToolIcon icon="toolbox" :size="44" />
        <p>{{ t('tools.empty') }}</p>
      </div>

      <div v-else class="hub-grid">
        <button v-for="tool in visibleTools" :key="tool.id" class="hub-card" @click="router.push(`/tools/${tool.key}`)">
          <span class="hub-card-ico">
            <ToolIcon :icon="tool.icon" :size="26" />
          </span>
          <span class="hub-card-main">
            <span class="hub-card-name">{{ displayName(tool) }}</span>
            <span class="hub-card-desc">{{ displayDesc(tool) }}</span>
          </span>
          <svg class="hub-card-go" viewBox="0 0 24 24" width="16" height="16" fill="none" stroke="currentColor" stroke-width="1.9" stroke-linecap="round" stroke-linejoin="round" aria-hidden="true">
            <path d="M9 5l7 7-7 7" />
          </svg>
        </button>
      </div>
    </main>
  </div>
</template>

<style scoped>
.hub {
  min-height: 100vh;
  display: flex;
  flex-direction: column;
  background:
    radial-gradient(1100px 500px at 85% -10%, color-mix(in srgb, var(--nc-primary) 16%, transparent), transparent 60%),
    radial-gradient(900px 480px at -10% 110%, color-mix(in srgb, var(--nc-primary) 10%, transparent), transparent 55%),
    var(--nc-bg);
  color: var(--nc-text);
}

.hub-top {
  height: 64px;
  display: flex;
  align-items: center;
  gap: 18px;
  padding: 0 22px;
  border-bottom: 1px solid var(--nc-border);
  backdrop-filter: blur(10px);
}

.hub-back {
  display: inline-flex;
  align-items: center;
  gap: 5px;
  border: 1px solid var(--nc-border);
  background: var(--nc-surface);
  color: var(--nc-text-dim, #8a94a6);
  border-radius: 8px;
  padding: 6px 12px;
  font-size: 12.5px;
  cursor: pointer;
  transition: all 0.15s;
}

.hub-back:hover {
  color: var(--nc-primary);
  border-color: var(--nc-primary);
}

.hub-brand {
  display: flex;
  align-items: center;
  gap: 12px;
}

.hub-logo {
  width: 38px;
  height: 38px;
  border-radius: 11px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  color: #fff;
  background: linear-gradient(135deg, var(--nc-primary), color-mix(in srgb, var(--nc-primary) 55%, #8b5cf6));
  box-shadow: 0 6px 18px color-mix(in srgb, var(--nc-primary) 35%, transparent);
}

.hub-title {
  margin: 0;
  font-size: 16px;
  font-weight: 700;
}

.hub-sub {
  margin: 0;
  font-size: 12px;
  color: var(--nc-text-dim, #8a94a6);
}

.hub-user {
  margin-left: auto;
}

.hub-avatar {
  cursor: pointer;
  background: var(--nc-primary);
  color: #04121f;
  font-weight: 700;
  transition: box-shadow 0.15s;
}

.hub-avatar:hover {
  box-shadow: 0 0 0 2.5px color-mix(in srgb, var(--nc-primary) 35%, transparent);
}

.hub-body {
  flex: 1;
  padding: 30px 34px 50px;
  overflow: auto;
}

.hub-grid {
  display: grid;
  grid-template-columns: repeat(auto-fill, minmax(280px, 1fr));
  gap: 18px;
  max-width: 1280px;
  margin: 0 auto;
}

.hub-card {
  display: flex;
  align-items: center;
  gap: 14px;
  text-align: left;
  border: 1px solid var(--nc-border);
  border-radius: 14px;
  background: var(--nc-surface);
  padding: 18px;
  cursor: pointer;
  color: var(--nc-text);
  transition: transform 0.16s ease, box-shadow 0.16s ease, border-color 0.16s ease;
  min-width: 0;
}

.hub-card:hover {
  transform: translateY(-3px);
  border-color: color-mix(in srgb, var(--nc-primary) 55%, var(--nc-border));
  box-shadow: 0 14px 30px rgba(2, 12, 27, 0.22);
}

.hub-card-ico {
  width: 52px;
  height: 52px;
  flex-shrink: 0;
  border-radius: 14px;
  display: inline-flex;
  align-items: center;
  justify-content: center;
  color: var(--nc-primary);
  background: color-mix(in srgb, var(--nc-primary) 12%, transparent);
  border: 1px solid color-mix(in srgb, var(--nc-primary) 25%, transparent);
}

.hub-card-main {
  display: flex;
  flex-direction: column;
  gap: 4px;
  min-width: 0;
}

.hub-card-name {
  font-size: 15px;
  font-weight: 700;
}

.hub-card-desc {
  font-size: 12.5px;
  color: var(--nc-text-dim, #8a94a6);
  display: -webkit-box;
  -webkit-line-clamp: 2;
  -webkit-box-orient: vertical;
  overflow: hidden;
}

.hub-card-go {
  margin-left: auto;
  flex-shrink: 0;
  color: var(--nc-text-dim, #8a94a6);
  transition: all 0.16s;
}

.hub-card:hover .hub-card-go {
  color: var(--nc-primary);
  transform: translateX(3px);
}

.hub-empty {
  height: 40vh;
  display: flex;
  flex-direction: column;
  align-items: center;
  justify-content: center;
  gap: 12px;
  color: var(--nc-text-dim, #8a94a6);
}

.hub-empty p {
  margin: 0;
  font-size: 13.5px;
}
</style>
