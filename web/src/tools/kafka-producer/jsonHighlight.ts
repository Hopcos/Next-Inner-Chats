/**
 * JSON 语法高亮（轻量 tokenizer，零依赖，与 json-formatter 同配色规范）：
 * 输出为带 span 的 HTML，交给 <pre v-html> 渲染。用于 Kafka Explorer 的
 * key/value 详情弹窗；JSON 解析失败时调用方以纯文本原样展示。
 */
export function highlightJson(text: string): string {
  const esc = (s: string) => s.replace(/&/g, '&amp;').replace(/</g, '&lt;').replace(/>/g, '&gt;')
  let out = ''
  let i = 0
  while (i < text.length) {
    const ch = text[i]
    if (ch === '"') {
      // 读取字符串（含转义）
      let j = i + 1
      let raw = '"'
      while (j < text.length) {
        const c = text[j]
        if (c === '\\' && j + 1 < text.length) {
          raw += text.slice(j, j + 2)
          j += 2
          continue
        }
        raw += c
        if (c === '"') {
          j++
          break
        }
        j++
      }
      // 字符串后（跳过空白）紧跟冒号 → 键
      let k = j
      while (k < text.length && /\s/.test(text[k])) k++
      const cls = text[k] === ':' ? 'jq-key' : 'jq-str'
      out += `<span class="${cls}">${esc(raw)}</span>`
      i = j
    } else if (/[0-9-]/.test(ch)) {
      const m = /^-?\d+(\.\d+)?([eE][+-]?\d+)?/.exec(text.slice(i))
      if (m) {
        out += `<span class="jq-num">${m[0]}</span>`
        i += m[0].length
        continue
      }
      out += esc(ch)
      i++
    } else if (/^(true|false|null)/.test(text.slice(i))) {
      const word = /^(true|false|null)/.exec(text.slice(i))![0]
      out += `<span class="jq-bool">${word}</span>`
      i += word.length
    } else if (/\s/.test(ch)) {
      out += ch
      i++
    } else {
      out += `<span class="jq-punc">${esc(ch)}</span>`
      i++
    }
  }
  return out
}
