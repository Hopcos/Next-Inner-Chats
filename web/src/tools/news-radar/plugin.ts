import { Service } from 'cordis'
import type { Context } from 'cordis'
import { registerToolPlugin } from '../registry'

/**
 * News Radar —— 沉浸式工具箱的新闻雷达插件（与 Kafka Producer 同构）。
 * 对接 news-radar 服务（GET /api/v1/sources、/api/v1/news、/api/v1/news/search）；
 * 端点地址与授权头由管理端「工具 → API Endpoint / 授权头」配置（AppTool.BaseUrl / HeadersJson），
 * 前端经同源代理 /api/ext/tool/news-radar/** 访问。
 * 独立 Cordis 插件：构造即自注册，拔插 = tools/index.ts 数组增删一行，主应用零耦合。
 */
export class NewsRadarToolPlugin extends Service {
  constructor(ctx: Context) {
    super(ctx, 'tool.news-radar')
    registerToolPlugin({
      key: 'news-radar',
      defaultName: 'News Radar',
      defaultIcon: 'radar',
      nameKey: 'tools.newsRadar.name',
      descriptionKey: 'tools.newsRadar.desc',
      loader: () => import('./NewsRadarView.vue'),
    })
  }
}
