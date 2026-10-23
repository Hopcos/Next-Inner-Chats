import { Service } from 'cordis'
import type { Context } from 'cordis'
import { registerToolPlugin } from '../registry'

/**
 * Kafka Producer —— 沉浸式工具栏的 Kafka 消息发送插件（与 Kafka Explorer 同构）。
 * 对接 kafka-producer-mcp-server 的 REST 端点（GET /api/environments、/api/topics、/api/topics/{topic}/partitions、
 * /api/schemas、/api/schemas/{id}；POST /api/topics/{topic}/messages）；
 * 端点地址与授权头由管理端「工具 → API Endpoint / 授权头」配置（AppTool.BaseUrl / HeadersJson），
 * 前端经同源代理 /api/ext/tool/kafka-producer/** 访问（POST 仅该路径在后端白名单内放行）。
 * 独立 Cordis 插件：构造即自注册，拔插 = tools/index.ts 数组增删一行，主应用零耦合。
 */
export class KafkaProducerToolPlugin extends Service {
  constructor(ctx: Context) {
    super(ctx, 'tool.kafka-producer')
    registerToolPlugin({
      key: 'kafka-producer',
      defaultName: 'Kafka Producer',
      defaultIcon: 'kafka-send',
      nameKey: 'tools.kafkaProducer.name',
      descriptionKey: 'tools.kafkaProducer.desc',
      loader: () => import('./KafkaProducerView.vue'),
    })
  }
}
