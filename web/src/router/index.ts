import { createRouter, createWebHistory } from 'vue-router'
import { kernel } from '@/kernel'
import { i18n } from '@/i18n'

export const router = createRouter({
  history: createWebHistory(),
  routes: [
    { path: '/login', name: 'login', component: () => import('@/views/LoginView.vue'), meta: { public: true } },
    { path: '/', name: 'chat', component: () => import('@/views/ChatView.vue') },
    { path: '/favorites', name: 'favorites', component: () => import('@/views/FavoritesView.vue') },
    { path: '/settings', name: 'settings', component: () => import('@/views/PersonalSettingsView.vue') },
    { path: '/tools', name: 'tools-hub', component: () => import('@/views/ToolsHubView.vue') },
    { path: '/tools/:key', name: 'tool-page', component: () => import('@/views/ToolPageView.vue') },
    {
      path: '/admin',
      name: 'admin',
      component: () => import('@/views/admin/AdminView.vue'),
      meta: { admin: true },
      children: [
        { path: '', redirect: '/admin/llm' },
        { path: 'llm', component: () => import('@/views/admin/AdminLlmView.vue') },
        { path: 'mcp', component: () => import('@/views/admin/AdminMcpView.vue') },
        { path: 'prompts', component: () => import('@/views/admin/AdminPromptsView.vue') },
        { path: 'skills', component: () => import('@/views/admin/AdminSkillsView.vue') },
        { path: 'users', component: () => import('@/views/admin/AdminUsersView.vue') },
        { path: 'roles', component: () => import('@/views/admin/AdminRolesView.vue') },
        { path: 'workspaces', component: () => import('@/views/admin/AdminWorkspacesView.vue') },
        { path: 'internal-auth', component: () => import('@/views/admin/AdminInternalAuthView.vue') },
        { path: 'tools', component: () => import('@/views/admin/AdminToolsView.vue') },
        { path: 'approvals', component: () => import('@/views/admin/AdminApprovalsView.vue') },
        { path: 'audit', component: () => import('@/views/admin/AdminAuditView.vue') },
        { path: 'metrics', component: () => import('@/views/admin/AdminMetricsView.vue') },
      ],
    },
    { path: '/:pathMatch(.*)*', redirect: '/' },
  ],
})

router.beforeEach(async (to) => {
  // 恢复登录态之后再做守卫判断
  if (!kernel.auth.state.ready) {
    await kernel.auth.restore()
  }
  if (to.meta.public) {
    if (kernel.auth.state.user) return { name: 'chat' }
    return true
  }
  if (!kernel.auth.state.user) {
    return { name: 'login', query: { redirect: to.fullPath } }
  }
  if (to.meta.admin && !kernel.auth.state.user.isAdmin) {
    return { name: 'chat' }
  }
  return true
})

// 登录过期（401）全局跳转：先清会话状态，守卫才会真正放行到登录页（避免被弹回）；带 redirect 方便登录后返回原页
window.addEventListener('nextchats:unauthorized', () => {
  kernel.auth.invalidate()
  kernel.notify.error(i18n.global.t('err.AUTH_EXPIRED'), 'AUTH_EXPIRED')
  void router.push({ name: 'login', query: { redirect: router.currentRoute.value.fullPath } })
})
