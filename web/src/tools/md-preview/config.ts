import MarkdownIt from 'markdown-it'
import { installMarkdownMath } from '@/utils/markdownMath'
import 'katex/dist/katex.min.css'

/** Markdown Preview 插件配置：渲染器 + 默认示例 + 本地状态键 */
export const DEFAULT_CONTENT = `# Hello Markdown

实时预览：**粗体**、~~删除线~~、\`行内代码\`、[链接](https://example.com)

## 列表

- 无序列表项
- [ ] 任务待办
- [x] 已完成任务

## 表格

| 功能 | 状态 |
| ---- | ---- |
| 表格 | ✅   |
| 代码块 | ✅ |

## 代码块

\`\`\`js
console.log('Hello Next Chats')
\`\`\`

## Mermaid 图

\`\`\`mermaid
flowchart LR
  A[输入 Markdown] --> B{包含 Mermaid?}
  B -- 是 --> C[渲染为流程图]
  B -- 否 --> D[普通 Markdown]
\`\`\`

> 引用：编辑左侧内容即可实时看到效果。
`

export const LS_KEY = 'nc.tool.md-preview.content'

/** 与聊天渲染一致的安全配置：不渲染原始 HTML（防 XSS）；数学公式 KaTeX（与聊天一致） */
export const md = new MarkdownIt({ html: false, linkify: true, breaks: true })
installMarkdownMath(md)

/** ```mermaid 代码块输出为普通 <pre>（由 MarkdownPreviewView 调用 mermaid.run 转成 SVG），
 *  其余 fence 继续走 markdown-it 默认渲染。 */
const defaultFence = md.renderer.rules.fence
md.renderer.rules.fence = (tokens, idx, options, env, self) => {
  const tok = tokens[idx]
  const info = (tok.info ?? '').trim().toLowerCase()
  if (info === 'mermaid' || info.startsWith('mermaid ')) {
    return `<pre class="mermaid">${md.utils.escapeHtml(tok.content)}</pre>\n`
  }
  return defaultFence ? defaultFence(tokens, idx, options, env, self) : self.renderToken(tokens, idx, options)
}

export function loadContent(): string {
  try {
    return localStorage.getItem(LS_KEY) || DEFAULT_CONTENT
  } catch {
    return DEFAULT_CONTENT
  }
}

export function saveContent(v: string): void {
  try {
    localStorage.setItem(LS_KEY, v)
  } catch {
    /* ignore */
  }
}
