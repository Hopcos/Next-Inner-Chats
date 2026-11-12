/**
 * 智能体专用图标库：预留不同类型的几十个精致图标供选择。
 * 统一风格：viewBox(0 0 24 24)、线性圆角端点、currentColor。
 */
export interface AgentIconItem {
  key: string
  name: string
  category: 'role' | 'tech' | 'creativity' | 'efficiency' | 'tools'
  paths: string[]
}

export const AGENT_ICONS: AgentIconItem[] = [
  // 角色 / 身份
  { key: 'bot', name: '机器人', category: 'role', paths: ['M12 2v2', 'M4 11a8 8 0 0 1 16 0v7a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2v-7Z', 'M9 13v.01', 'M15 13v.01', 'M2 14h2', 'M20 14h2'] },
  { key: 'brain', name: '智慧大脑', category: 'role', paths: ['M9.5 2A4.5 4.5 0 0 0 5 6.5C5 7.4 5.3 8.3 5.8 9 4.1 9.8 3 11.5 3 13.5c0 2.2 1.4 4 3.4 4.7.1.6.3 1.2.6 1.8A4 4 0 0 0 10.5 22h1', 'M14.5 2A4.5 4.5 0 0 1 19 6.5c0 .9-.3 1.8-.8 2.5 1.7.8 2.8 2.5 2.8 4.5 0 2.2-1.4 4-3.4 4.7-.1.6-.3 1.2-.6 1.8A4 4 0 0 1 13.5 22h-1', 'M12 2v20'] },
  { key: 'wizard', name: '魔法导师', category: 'role', paths: ['m19 11-8-8-8.5 8.5a2.1 2.1 0 0 0 0 3l5 5a2.1 2.1 0 0 0 3 0L19 11Z', 'm5 2 5 5', 'M2 5l5 5', 'M18 13l4 4', 'M14 17l4 4'] },
  { key: 'user-check', name: '专家顾问', category: 'role', paths: ['M16 21v-2a4 4 0 0 0-4-4H6a4 4 0 0 0-4 4v2', 'M9 11a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z', 'm16 11 2 2 4-4'] },
  { key: 'detective', name: '调查侦探', category: 'role', paths: ['M3 11h18', 'M5 11V8a7 7 0 0 1 14 0v3', 'M10 15a2 2 0 1 0 4 0 2 2 0 0 0-4 0Z', 'M12 17v4'] },
  { key: 'crown', name: '决策领袖', category: 'role', paths: ['m2 4 3 12h14l3-12-6 7-4-7-4 7-6-7Z', 'M3 20h18'] },
  { key: 'smile', name: '亲和助手', category: 'role', paths: ['M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2Z', 'M8 13a4 4 0 0 0 8 0', 'M9 9h.01', 'M15 9h.01'] },
  { key: 'graduation-cap', name: '学术导师', category: 'role', paths: ['M22 10 12 5 2 10l10 5 10-5Z', 'M6 12.5V17c0 2 2.7 3.5 6 3.5s6-1.5 6-3.5v-4.5', 'M22 10v6'] },

  // 技术 / 编程
  { key: 'code', name: '代码助手', category: 'tech', paths: ['M16 18l6-6-6-6', 'M8 6l-6 6 6 6'] },
  { key: 'terminal', name: '命令行', category: 'tech', paths: ['M4 17l6-6-6-6', 'M12 19h8'] },
  { key: 'bug', name: 'Debug 排错', category: 'tech', paths: ['M8 2h8', 'M9 2v4', 'M15 2v4', 'M4 13h16', 'M5 8h14', 'M6 18h12', 'M12 6a6 6 0 0 1 6 6v4a6 6 0 0 1-12 0v-4a6 6 0 0 1 6-6Z'] },
  { key: 'database', name: '数据库 SQL', category: 'tech', paths: ['M21 5c0 1.7-4 3-9 3S3 6.7 3 5s4-3 9-3 9 1.3 9 3Z', 'M21 12c0 1.7-4 3-9 3s-9-1.3-9-3', 'M3 5v14c0 1.7 4 3 9 3s9-1.3 9-3V5'] },
  { key: 'cpu', name: '架构专家', category: 'tech', paths: ['M4 4h16v16H4V4Z', 'M9 9h6v6H9V9Z', 'M9 1v3', 'M15 1v3', 'M9 20v3', 'M15 20v3', 'M1 9h3', 'M1 15h3', 'M20 9h3', 'M20 15h3'] },
  { key: 'git-branch', name: '版本流专家', category: 'tech', paths: ['M6 3a3 3 0 1 0 0 6 3 3 0 0 0 0-6Z', 'M18 15a3 3 0 1 0 0 6 3 3 0 0 0 0-6Z', 'M6 9a9 9 0 0 0 9 9', 'M6 9v12'] },
  { key: 'server', name: '运维专家', category: 'tech', paths: ['M2 4h20v6H2V4Z', 'M2 14h20v6H2V14Z', 'M6 7h.01', 'M6 17h.01'] },
  { key: 'shield', name: '安全合规', category: 'tech', paths: ['M12 22s8-4 8-10V5l-8-3-8 3v7c0 6 8 10 8 10Z'] },

  // 创意 / 文创
  { key: 'feather', name: '创意文案', category: 'creativity', paths: ['M20.2 3.8a4.5 4.5 0 0 0-6.4 0l-10 10a4.5 4.5 0 0 0-.8 1.4L2 22l6.8-1a4.5 4.5 0 0 0 1.4-.8l10-10a4.5 4.5 0 0 0 0-6.4Z', 'M14 6l4 4'] },
  { key: 'sparkles', name: '灵感火花', category: 'creativity', paths: ['M11 4.5l1.4 4.1 4.1 1.4-4.1 1.4L11 15.5l-1.4-4.1L5.5 10l4.1-1.4L11 4.5Z', 'M18.5 14.5l.9 2.6 2.6.9-2.6.9-.9 2.6-.9-2.6-2.6-.9 2.6-.9.9-2.6Z'] },
  { key: 'palette', name: '美术设计', category: 'creativity', paths: ['M12 2a10 10 0 1 0 10 10c0-1.5-1.2-2.7-2.7-2.7h-1.6a2.7 2.7 0 0 1-2.7-2.7V6A4 4 0 0 0 12 2Z', 'M7.5 10.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3Z', 'M11.5 7.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3Z', 'M16.5 10.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3Z', 'M8.5 14.5a1.5 1.5 0 1 0 0-3 1.5 1.5 0 0 0 0 3Z'] },
  { key: 'music', name: '音乐乐理', category: 'creativity', paths: ['M9 18V5l12-2v13', 'M6 21a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z', 'M18 19a3 3 0 1 0 0-6 3 3 0 0 0 0 6Z'] },
  { key: 'camera', name: '影视摄影', category: 'creativity', paths: ['M4 7h4l2-3h4l2 3h4a2 2 0 0 1 2 2v10a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V9a2 2 0 0 1 2-2Z', 'M12 17a4 4 0 1 0 0-8 4 4 0 0 0 0 8Z'] },
  { key: 'megaphone', name: '营销推广', category: 'creativity', paths: ['M3 11v2a2 2 0 0 0 2 2h2l5 4V5L7 9H5a2 2 0 0 0-2 2Z', 'M17 9a4 4 0 0 1 0 6', 'M19 6a8 8 0 0 1 0 12'] },

  // 效率 / 商务
  { key: 'briefcase', name: '商业策划', category: 'efficiency', paths: ['M16 4h2a2 2 0 0 1 2 2v13a2 2 0 0 1-2 2H6a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2h2', 'M8 4a2 2 0 0 1 2-2h4a2 2 0 0 1 2 2v2H8V4Z', 'M2 13h20'] },
  { key: 'chart', name: '数据分析', category: 'efficiency', paths: ['M4 20V10', 'M10 20V4', 'M16 20v-8', 'M21 20H3'] },
  { key: 'file-text', name: '公文报告', category: 'efficiency', paths: ['M14 2H6a2 2 0 0 0-2 2v16a2 2 0 0 0 2 2h12a2 2 0 0 0 2-2V8l-6-6Z', 'M14 2v6h6', 'M8 13h8', 'M8 17h6'] },
  { key: 'check-circle', name: '任务清单', category: 'efficiency', paths: ['M22 11.08V12a10 10 0 1 1-5.93-9.14', 'M22 4 12 14.01l-3-3'] },
  { key: 'search', name: '文献检索', category: 'efficiency', paths: ['M11 2a9 9 0 1 0 0 18 9 9 0 0 0 0-18Z', 'm21 21-4.35-4.35'] },
  { key: 'mail', name: '邮件助理', category: 'efficiency', paths: ['M4 4h16a2 2 0 0 1 2 2v12a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V6a2 2 0 0 1 2-2Z', 'm22 6-10 7L2 6'] },
  { key: 'calendar', name: '日程安排', category: 'efficiency', paths: ['M19 4H5a2 2 0 0 0-2 2v14a2 2 0 0 0 2 2h14a2 2 0 0 0 2-2V6a2 2 0 0 0-2-2Z', 'M16 2v4', 'M8 2v4', 'M3 10h18'] },
  { key: 'zap', name: '急速响应', category: 'efficiency', paths: ['M13 2 3 14h8l-2 8 10-12h-8l2-8Z'] },

  // 实用工具 / 通用
  { key: 'calculator', name: '数学计算', category: 'tools', paths: ['M4 2h16a2 2 0 0 1 2 2v16a2 2 0 0 1-2 2H4a2 2 0 0 1-2-2V4a2 2 0 0 1 2-2Z', 'M8 6h8', 'M8 11h2', 'M14 11h2', 'M8 16h2', 'M14 16h2'] },
  { key: 'globe', name: '多语全球化', category: 'tools', paths: ['M12 3a9 9 0 1 0 0 18 9 9 0 0 0 0-18Z', 'M3.5 9h17', 'M3.5 15h17', 'M12 3c-2.6 2.4-4 5.6-4 9s1.4 6.6 4 9c2.6-2.4 4-5.6 4-9s-1.4-6.6-4-9Z'] },
  { key: 'translate', name: '翻译润色', category: 'tools', paths: ['M4 6h9', 'M8.5 4.2V6', 'M11 6C10.3 9.6 7.8 12.6 4.2 14.2', 'M6.8 9.4c1.1 2.1 2.9 3.8 5.2 4.8', 'M13.2 20l4.4-10.4L22 20', 'M14.7 16.6h5.6'] },
  { key: 'book-open', name: '百科知识', category: 'tools', paths: ['M2 4.5A2.5 2.5 0 0 1 4.5 2H10a2 2 0 0 1 2 2v16a2 2 0 0 0-2-2H4.5A2.5 2.5 0 0 0 2 20.5v-16Z', 'M22 4.5A2.5 2.5 0 0 0 19.5 2H14a2 2 0 0 0-2 2v16a2 2 0 0 1 2-2h5.5A2.5 2.5 0 0 1 22 20.5v-16Z'] },
  { key: 'compass', name: '人生向导', category: 'tools', paths: ['M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2Z', 'm16.2 7.8-2.8 7.4-7.4 2.8 2.8-7.4 7.4-2.8Z'] },
  { key: 'heart', name: '情感疏导', category: 'tools', paths: ['M19 14c1.5-1.5 2.5-3.2 2.5-5.2 0-2.8-2.2-5-5-5-1.9 0-3.6 1.1-4.5 2.7C11.1 4.9 9.4 3.8 7.5 3.8 4.7 3.8 2.5 6 2.5 8.8c0 2 1 3.7 2.5 5.2L12 21.5l7-7.5Z'] },
  { key: 'coffee', name: '生活闲谈', category: 'tools', paths: ['M18 8h1a4 4 0 0 1 0 8h-1', 'M2 8h16v9a4 4 0 0 1-4 4H6a4 4 0 0 1-4-4V8Z', 'M6 1v3', 'M10 1v3', 'M14 1v3'] },
  { key: 'lightbulb', name: '头脑风暴', category: 'tools', paths: ['M9 18h6', 'M10 22h4', 'M12 2a7 7 0 0 0-7 7c0 2.4 1.2 4.5 3 5.7V17h8v-2.3c1.8-1.2 3-3.3 3-5.7a7 7 0 0 0-7-7Z'] },
  { key: 'rocket', name: '产品发版', category: 'tools', paths: ['M12 2.5c2.8 1.9 4.5 5.3 4.5 9.2l-1.7 3h-5.6l-1.7-3C7.5 7.8 9.2 4.4 12 2.5Z', 'M12 8.5v.1', 'M9.5 16.5 8 21l3-1.6L14 21l-1.5-4.5', 'M7.5 13 5 14.5 6 11.5', 'M16.5 13 19 14.5 18 11.5'] },
  { key: 'target', name: '目标达成', category: 'tools', paths: ['M12 2a10 10 0 1 0 10 10A10 10 0 0 0 12 2Z', 'M12 6a6 6 0 1 0 6 6 6 6 0 0 0-6-6Z', 'M12 10a2 2 0 1 0 2 2 2 2 0 0 0-2-2Z'] },
]

const iconMap = new Map(AGENT_ICONS.map((i) => [i.key, i]))

export function getAgentIcon(key: string): AgentIconItem | undefined {
  return iconMap.get(key)
}

export const DEFAULT_AGENT_ICON = 'bot'
