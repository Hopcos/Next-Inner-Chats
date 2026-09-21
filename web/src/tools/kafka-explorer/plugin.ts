import { Service } from 'cordis'
import type { Context } from 'cordis'
import { registerToolPlugin } from '../registry'

/**
 * Kafka Explorer —— 沉浸式工具栏的 Kafka 消息浏览插件。
 * 对接 kafka-mcp-server 的 REST 端点（GET /api/environments、/api/topics、/api/topics/{topic}/partitions、
 * consume_topic / consume_partition）；端点地址由管理端「工具 → API Endpoint」配置（AppTool.BaseUrl）。
 * 独立 Cordis 插件：构造即自注册，拔插 = tools/index.ts 数组增删一行，主应用零耦合。
 */
export class KafkaExplorerToolPlugin extends Service {
  constructor(ctx: Context) {
    super(ctx, 'tool.kafka-explorer')
    registerToolPlugin({
      key: 'kafka-explorer',
      defaultName: 'Kafka Explorer',
      defaultIcon: 'kafka',
      nameKey: 'tools.kafka.name',
      descriptionKey: 'tools.kafka.desc',
      loader: () => import('./KafkaExplorerView.vue'),
    })
  }
}
