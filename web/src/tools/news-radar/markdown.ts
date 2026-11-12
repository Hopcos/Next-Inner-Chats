import MarkdownIt from 'markdown-it'
import { installMarkdownMath } from '@/utils/markdownMath'
import 'katex/dist/katex.min.css'

/**
 * News Radar 新闻正文专用的 GFM Markdown 渲染管线：
 * - 遵循 GFM 规范（表格、删除线、换行、链接、代码块、列表等）
 * - html: false 防范 XSS 注入风险
 * - 外链强制添加 target="_blank" 和 rel="noopener noreferrer"，避免当前页跳转
 * - 支持 KaTeX 数学公式渲染
 */
export const md = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
})

installMarkdownMath(md)

// 拦截所有 a 标签链接渲染，自动加上 target="_blank" 和 rel="noopener noreferrer"
const defaultRender =
  md.renderer.rules.link_open ||
  function (tokens, idx, options, _env, self) {
    return self.renderToken(tokens, idx, options)
  }

md.renderer.rules.link_open = function (tokens, idx, options, env, self) {
  const token = tokens[idx]
  const targetIndex = token.attrIndex('target')
  if (targetIndex < 0) {
    token.attrPush(['target', '_blank'])
  } else {
    token.attrs![targetIndex][1] = '_blank'
  }

  const relIndex = token.attrIndex('rel')
  if (relIndex < 0) {
    token.attrPush(['rel', 'noopener noreferrer'])
  } else {
    token.attrs![relIndex][1] = 'noopener noreferrer'
  }

  return defaultRender(tokens, idx, options, env, self)
}

/**
 * 将新闻正文内容清洗并渲染为 GFM Markdown 格式的 HTML
 */
export function renderNewsMarkdown(rawText: string | null | undefined): string {
  if (!rawText) return ''
  let cleaned = rawText.trim()
  // 清理末尾类似 "Link: https://..." 或换行后的冗余原始外链
  cleaned = cleaned.replace(/(?:\r?\n|\s)+(?:Link:\s*)?https?:\/\/[^\s]+$/i, '').trim()
  if (!cleaned) return ''
  return md.render(cleaned)
}
