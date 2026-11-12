import MarkdownIt from 'markdown-it'
import { installMarkdownMath } from '@/utils/markdownMath'
import 'katex/dist/katex.min.css'

/**
 * 智能体问答使用的 Markdown 渲染器：
 * - 遵循 GFM 规范（表格、删除线、换行、任务列表等）
 * - html: false 防范 XSS
 * - 集成 KaTeX 数学公式渲染
 */
export const md = new MarkdownIt({
  html: false,
  linkify: true,
  breaks: true,
})

installMarkdownMath(md)

/**
 * 针对问题内容或回答内容的语音朗读文本净化：
 * 1. 完整保留内容中的所有有效文字、代码内容（代码内容按普通文字朗读，不丢弃代码块内容）
 * 2. 移除纯装饰性的标记：```代码块包裹线、$$数学公式标记、Markdown链接URL、图片语法等
 * 3. 严格去除/过滤所有中英文标点符号与特殊符号，替换为自然呼吸间隔（空格），
 *    防止部分 TTS 引擎直接把标点符号的名字念出来（例如念出“句号”、“逗号”、“斜杠”、“下划线”等）
 * 4. 规整多余连续空白
 */
export function cleanTextForSpeech(raw: string): string {
  if (!raw) return ''

  let text = raw

  // 1. 去除代码块隔离符 ```lang / ```，但保留代码块内部的具体代码文本（让用户能听完整回答）
  text = text.replace(/```[^\n]*\n?/g, ' ')
  text = text.replace(/```/g, ' ')

  // 2. 去除数学公式包裹符 $$ 与 $
  text = text.replace(/\$\$/g, ' ')
  text = text.replace(/\$/g, ' ')

  // 3. 去除图片语法 ![alt](url) -> 仅保留 alt
  text = text.replace(/!\[([^\]]*)\]\([^)]*\)/g, '$1')

  // 4. 将链接 [text](url) -> 仅保留 text，去除 url
  text = text.replace(/\[([^\]]+)\]\([^)]*\)/g, '$1')

  // 5. 去除 HTML 标签
  text = text.replace(/<\/?[^>]+(>|$)/g, ' ')

  // 6. 去除标题前缀 #
  text = text.replace(/^#{1,6}\s+/gm, '')

  // 7. 去除引用前缀 >
  text = text.replace(/^>\s+/gm, '')

  // 8. 去除粗体/斜体/删除线标记符号
  text = text.replace(/(\*\*|__)(.*?)\1/g, '$2')
  text = text.replace(/(\*|_)(.*?)\1/g, '$2')
  text = text.replace(/~~(.*?)~~/g, '$1')
  text = text.replace(/`([^`]+)`/g, '$1')

  // 9. 去除列表前缀符号如 -、*、+ 以及 1.
  text = text.replace(/^\s*[-*+]\s+/gm, '')
  text = text.replace(/^\s*\d+\.\s+/gm, '')

  // 10. 表格分割线去除
  text = text.replace(/\|?\s*[-:]{2,}\s*\|?/g, ' ')
  text = text.replace(/\|/g, ' ')

  // 11. 【核心需求：忽略标点符号】
  // 去除所有常见中英文标点符号与控制符号，替换为普通空格（TTS 在遇到空格时会自然短暂停顿，而绝对不会念出标点名称）
  // 覆盖中文标点：，。？！；：“”‘’（）【】《》——……、·～「」『』
  // 覆盖英文及通用标点：, . ? ! ; : ' " ` ~ @ # % ^ & * ( ) _ + = - / \ { } [ ] < >
  text = text.replace(/[，。？！；：“”‘’（）【】《》——……、·～「」『』\n\r\t]/g, ' ')
  text = text.replace(/[,.?!;:;'"~@#%^&*()_+=\-\/\\{}\[\]<>]/g, ' ')

  // 12. 归一化连续空白字符
  text = text.replace(/\s+/g, ' ').trim()

  return text
}
