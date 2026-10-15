import { Service } from 'cordis'
import type { Context } from 'cordis'
import { registerToolPlugin } from '../registry'

/**
 * K8S Explorer —— 沉浸式工具栏的 Kubernetes Pod 浏览插件。
 * 对接 k8s-mcp-server 的 REST 网关（GET /api/environments、/api/environments/{env}/pods[/detailed]、
 * .../pods/{pod}/files[|/content]）；端点地址由管理端「工具 → API Endpoint」配置（AppTool.BaseUrl），
 * 外部 API 需要的授权头（如 {"authorization":"Bearer <token>"}）由管理端「工具 → 授权头」配置，
 * 后端代理转发时自动附加。独立 Cordis 插件：构造即自注册，拔插 = tools/index.ts 数组增删一行，主应用零耦合。
 */
export class K8sExplorerToolPlugin extends Service {
  constructor(ctx: Context) {
    super(ctx, 'tool.k8s-explorer')
    registerToolPlugin({
      key: 'k8s-explorer',
      defaultName: 'K8S Explorer',
      defaultIcon: 'kube',
      nameKey: 'tools.k8s.name',
      descriptionKey: 'tools.k8s.desc',
      loader: () => import('./K8sExplorerView.vue'),
    })
  }
}
