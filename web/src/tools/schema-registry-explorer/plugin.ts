import { Service } from 'cordis'
import type { Context } from 'cordis'
import { registerToolPlugin } from '../registry'

/**
 * Schema Registry Explorer —— 沉浸式工具栏的模式注册表浏览插件。
 * 对接 kafka-producer-mcp-server 的 REST 端点（GET /api/environments、/api/schemas、/api/schemas/{schemaId}）；
 * 端点地址与授权头由管理端「工具 → API Endpoint / 授权头」配置（AppTool.BaseUrl / HeadersJson），
 * 前端经同源代理 /api/ext/tool/schema-registry-explorer/** 访问。
 * 独立 Cordis 插件：构造即自注册，拔插 = tools/index.ts 数组增删一行，主应用零耦合。
 */
export class SchemaRegistryExplorerToolPlugin extends Service {
  constructor(ctx: Context) {
    super(ctx, 'tool.schema-registry-explorer')
    registerToolPlugin({
      key: 'schema-registry-explorer',
      defaultName: 'Schema Registry Explorer',
      defaultIcon: 'braces',
      nameKey: 'tools.schemaRegistry.name',
      descriptionKey: 'tools.schemaRegistry.desc',
      loader: () => import('./SchemaRegistryExplorerView.vue'),
    })
  }
}
