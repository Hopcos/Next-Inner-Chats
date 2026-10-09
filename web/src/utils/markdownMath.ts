import katex from 'katex'
import MarkdownIt from 'markdown-it'
import type { StateBlock as MdBlockState, StateInline as MdInlineState } from 'markdown-it'

/**
 * 给 markdown-it 挂载 KaTeX 数学渲染（与聊天/markdown 预览共用）：
 * - 块级公式：独占一行的 $$...$$（可跨多行），输出 <div class="math-block">
 * - 行内公式：$...$
 * 防御规则（避免误伤普通文本）：
 * - $$ 后无内容 / 首尾空白 / 含换行 → 不渲染，保持原文
 * - 普通美元符号（如 "$5"、价格区间）不受影响
 * - 转义 \$ 由 markdown-it 的 escape 规则先行消费，输出字面 $
 * - 渲染失败（throwOnError:false 已兜底）仍回退为代码块显示原文
 */
export function installMarkdownMath(md: InstanceType<typeof MarkdownIt>): void {
  // ---------------- 块级 $$...$$ ----------------
  md.block.ruler.before('fence', 'katex_block', (state: MdBlockState, startLine: number, endLine: number, silent: boolean) => {
    const startPos = state.bMarks[startLine] + state.tShift[startLine]
    const maxPos = state.eMarks[startLine]
    if (startPos + 2 > maxPos) return false
    if (state.src.charCodeAt(startPos) !== 0x24 /* $ */) return false
    if (state.src.charCodeAt(startPos + 1) !== 0x24 /* $ */) return false
    if (state.src.charCodeAt(startPos + 2) === 0x24 /* $ */) return false // $$$ 不按数学处理

    const contentStart = startPos + 2
    let content: string
    let endLineIdx: number

    // 同一行闭合：$$ ... $$（该行 $$ 之后不允许再有非空内容，避免误吞文字）
    const sameLineClose = state.src.indexOf('$$', contentStart)
    if (sameLineClose !== -1 && sameLineClose <= maxPos) {
      const inline = state.src.slice(contentStart, sameLineClose)
      const after = state.src.slice(sameLineClose + 2, maxPos).trim()
      if (!inline.trim() || after.length > 0) return false
      content = inline.trim()
      endLineIdx = startLine
    } else {
      // 多行：逐行累积，直到某行出现闭合 $$
      const buf: string[] = []
      let closeLine = -1
      for (let line = startLine + 1; line < endLine; line++) {
        const bpos = state.bMarks[line] + state.tShift[line]
        const epos = state.eMarks[line]
        const lineText = state.src.slice(bpos, epos)
        const lineClose = lineText.indexOf('$$')
        if (lineClose >= 0) {
          buf.push(lineText.slice(0, lineClose))
          closeLine = line
          break
        }
        buf.push(lineText)
      }
      if (closeLine === -1) return false
      content = buf.join('\n').trim()
      if (!content) return false
      endLineIdx = closeLine
    }

    if (silent) return true
    const token = state.push('katex_block', 'katex', 0)
    token.content = content
    token.map = [startLine, endLineIdx]
    state.line = endLineIdx + 1
    return true
  })

  // ---------------- 行内 $...$ ----------------
  md.inline.ruler.after('escape', 'katex_inline', (state: MdInlineState, silent: boolean) => {
    const start = state.pos
    if (state.src.charCodeAt(start) !== 0x24 /* $ */) return false
    if (start > 0 && state.src.charCodeAt(start - 1) === 0x24 /* $ */) return false // 紧邻的第二个 $：$$ 标记的一部分
    if (state.src.charCodeAt(start + 1) === 0x24 /* $ */) return false // 留给块级

    // 找未被奇数个反斜杠转义的闭合 $
    let close = -1
    for (let i = start + 1; i < state.src.length; i++) {
      if (state.src.charCodeAt(i) !== 0x24) continue
      let backslashes = 0
      for (let j = i - 1; j >= 0 && state.src.charCodeAt(j) === 0x5c /* \ */; j--) backslashes++
      if (backslashes % 2 === 0) {
        close = i
        break
      }
    }
    if (close === -1) return false

    const content = state.src.slice(start + 1, close)
    // 防御：空 / 首尾空白 / 含换行 / 含块级标记 → 不当作数学，保持原文
    if (!content || content.startsWith(' ') || content.endsWith(' ') || content.includes('\n') || content.includes('$$')) {
      return false
    }
    // 以反斜杠结尾（如 \ 与闭合并行）→ 多半是普通用法，跳过
    if (content.endsWith('\\')) return false

    // 关键：silent 模式（markdown-it 在链接标号扫描 parseLinkLabel → skipToken、
    // 以及嵌套前瞻时以 silent 调用行内规则）也必须推进 state.pos；
    // 否则命中 $...$ 却原地返回 true，markdown-it 会抛
    // “inline rule didn't increment state.pos”，导致消息渲染崩溃。
    if (!silent) {
      const token = state.push('katex_inline', 'katex', 0)
      token.content = content
      token.markup = '$'
    }
    state.pos = close + 1
    return true
  })

  // ---------------- Renderer ----------------
  md.renderer.rules.katex_block = (tokens, idx) => {
    const body = safeRender(tokens[idx].content, true, md)
    return `<div class="math-block">${body}</div>\n`
  }
  md.renderer.rules.katex_inline = (tokens, idx) => safeRender(tokens[idx].content, false, md)
}

function safeRender(code: string, displayMode: boolean, md: InstanceType<typeof MarkdownIt>): string {
  try {
    return katex.renderToString(code, { displayMode, throwOnError: false, strict: 'ignore' })
  } catch {
    // 极端情况兜底：回退为带边框的原文（保持可读、无 XSS：内容已转义）
    const raw = displayMode ? `$$\n${code}\n$$` : `$${code}$`
    return `<code class="math-fallback">${md.utils.escapeHtml(raw)}</code>`
  }
}
