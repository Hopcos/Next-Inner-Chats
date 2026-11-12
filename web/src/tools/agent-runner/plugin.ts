import { Service } from 'cordis'
import type { Context } from 'cordis'
import { registerToolPlugin } from '../registry'

/**
 * 智能体（agent-runner）—— 沉浸式工具箱 Cordis 工具插件：
 * - 上部：水平可拖动/滚动的智能体抽屉，展示每个智能体的 ICON 与名称，支持自定义、编辑与删除（最多 50 个）
 * - 持久化：智能体定义与登录用户绑定，服务端统一通过 /api/me/tool-settings/agent-runner 持久化
 * - 中间：模型选择、流式输出复选框、提示词展开预览
 * - 下部：问与答双栏（左问右答，中间解答操作），使用所选智能体的提示词作为 systemPrompt
 * - 记忆：localStorage 记住上次选中的智能体，以及每个智能体对应的上一次问与答
 */
export class AgentRunnerToolPlugin extends Service {
  constructor(ctx: Context) {
    super(ctx, 'tool.agent-runner')
    registerToolPlugin({
      key: 'agent-runner',
      defaultName: 'Agent Studio',
      defaultIcon: 'bot',
      nameKey: 'tools.agentRunner.name',
      descriptionKey: 'tools.agentRunner.desc',
      loader: () => import('./AgentRunnerView.vue'),
    })
  }
}
