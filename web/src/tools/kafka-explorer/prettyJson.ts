/**
 * 保持原文的 JSON 美化（lossless pretty-print，零依赖）。
 *
 * 背景：JSON.parse → Number → JSON.stringify 会按 double 精度归一化数字：
 *   0.000000000000000000 → 0、1e+400 → null、123456789012345678901234567890 → 123456789012345680000000000000000000
 * 详情弹窗需要按原文显示这些数字，因此本实现遍历原文：
 *   数字 / true / false / null 逐字保留（尾零、指数、超大整数不丢位）；
 *   字符串按 JSON 字符串语法解码后经 JSON.stringify 重新转义（与 JSON.parse + JSON.stringify 的既有行为一致）；
 *   容器结构与空白按 2 空格缩进重排，版式与 JSON.stringify(parsed, null, 2) 一致。
 * 假定输入合法 JSON：先用 JSON.parse 校验，非法时抛 SyntaxError，调用方按原样回退展示。
 * 本函数遇到意外字符时抛错，调用方同样按原样回退。
 */
export function prettyJson(text: string): string {
  JSON.parse(text) // 校验；非法时抛 SyntaxError，调用方按原样回退
  const out: string[] = []
  const len = text.length
  let i = 0

  const emit = (s: string): void => { out.push(s) }
  /** 在当前层级 d（0 = 根）换行缩进（与 JSON.stringify(…, null, 2) 的 2 空格一致） */
  const nl = (d: number): void => { out.push('\n' + '  '.repeat(d)) }

  /** 逐字跳过结构空白（不进入字面量内部） */
  function ws(): void {
    while (i < len && (text[i] === ' ' || text[i] === '\t' || text[i] === '\n' || text[i] === '\r')) i++
  }

  const isDigit = (c: string): boolean => c >= '0' && c <= '9'

  /** 字符串字面量：先扫到闭合引号确定边界，再按 JSON 字符串语法解码，经 JSON.stringify 重新转义（既有行为） */
  function stringLit(): void {
    const start = i
    i++ // 开引号
    while (i < len) {
      const c = text[i]
      if (c === '\\') { i += 2; continue }
      if (c === '"') { i++; break }
      i++
    }
    const raw = text.slice(start + 1, i - 1) // 引号内的原文（含转义序列）
    emit(JSON.stringify(decode(raw)))
  }

  const ESCAPES: Record<string, string> = { '"': '"', '\\': '\\', '/': '/', b: '\b', f: '\f', n: '\n', r: '\r', t: '\t' }

  /** 按 JSON 字符串语法解码引号内原文 → 与 JSON.parse 相同的字符串 */
  function decode(raw: string): string {
    let s = ''
    let j = 0
    while (j < raw.length) {
      const c = raw[j]
      if (c !== '\\') { s += c; j++; continue }
      const e = raw[j + 1]
      j += 2
      if (e === undefined) throw new Error('unexpected escape at end') // 孤立结尾反斜杠
      if (e === 'u') {
        const hex = raw.slice(j, j + 4)
        if (hex.length !== 4) throw new Error('invalid \\u escape')
        j += 4
        const cp = parseInt(hex, 16)
        if (!Number.isFinite(cp)) throw new Error('invalid \\u escape')
        s += String.fromCodePoint(cp) // 高 / 低代理位各自落位，代码单元序列与 JSON.parse 一致
      } else if (e === '"' || e === '\\' || e === '/' || e === 'b' || e === 'f' || e === 'n' || e === 'r' || e === 't') {
        s += ESCAPES[e]
      } else {
        throw new Error(`unexpected escape '\\${e}'`)
      }
    }
    return s
  }

  /** 数字字面量：-?\d+(\.\d+)?([eE][+-]?\d+)? 逐字保留（尾零 / 指数 / 超大整数不丢位） */
  function numberLit(): void {
    const start = i
    if (text[i] === '-') i++
    if (text[i] === '0') i++
    else while (i < len && isDigit(text[i])) i++
    if (text[i] === '.') {
      i++
      while (i < len && isDigit(text[i])) i++
    }
    if (text[i] === 'e' || text[i] === 'E') {
      i++
      if (text[i] === '+' || text[i] === '-') i++
      while (i < len && isDigit(text[i])) i++
    }
    emit(text.slice(start, i))
  }

  /** true / false / null 逐字保留 */
  function keywordLit(): void {
    if (text.startsWith('true', i)) { i += 4; emit('true') }
    else if (text.startsWith('false', i)) { i += 5; emit('false') }
    else if (text.startsWith('null', i)) { i += 4; emit('null') }
    else throw new Error(`unexpected keyword at ${i}`)
  }

  /** 一个 JSON 值：字面量逐字（字符串解码重转义）；容器空则行内 "{}"/"[]"，非空则逐行缩进 */
  function value(d: number): void {
    ws()
    if (i >= len) throw new Error('unexpected end')
    const c = text[i]
    if (c === '"') { stringLit(); return }
    if (c === '-' || isDigit(c)) { numberLit(); return }
    if (c === 't' || c === 'f' || c === 'n') { keywordLit(); return }
    if (c !== '{' && c !== '[') throw new Error(`unexpected '${c}' at ${i}`)
    i++ // 消费开括号
    emit(c)
    const closer = c === '{' ? '}' : ']'
    ws()
    if (i < len && text[i] === closer) {
      i++ // 空容器行内 "{}"/"[]"（与 JSON.stringify(…, null, 2) 一致）
      emit(closer)
      return
    }
    const cd = d + 1
    for (;;) {
      nl(cd)
      if (c === '{') pair(cd)
      else element(cd)
      ws()
      if (i >= len) throw new Error('unexpected end')
      const nc = text[i]
      if (nc === ',') { i++; emit(','); continue } // 逗号紧接上一值行内，逗号后换行
      break // 必为闭合括号
    }
    if (i < len && text[i] === closer) {
      i++
      nl(d) // 闭合括号回到本容器的缩进层级
      emit(closer)
    }
    else throw new Error(`unexpected '${text[i] ?? ''}' at ${i}`)
  }

  /** 对象键值对：nl 已由容器循环发出；键（字符串解码重转义）+ ': ' + 值行内 */
  function pair(d: number): void {
    ws()
    if (i >= len || text[i] !== '"') throw new Error(`unexpected '${text[i] ?? ''}' at ${i}`)
    stringLit() // 键
    ws()
    if (i >= len || text[i] !== ':') throw new Error(`unexpected '${text[i] ?? ''}' at ${i}`)
    i++
    emit(': ')
    value(d) // 值行内（空容器行内 / 字面量行内 / 非空容器逐行缩进）
  }

  /** 数组元素 */
  function element(d: number): void {
    value(d)
  }

  value(0)
  ws()
  if (i < len) throw new Error(`unexpected '${text[i]}' at ${i}`) // 根值后仅允许空白
  return out.join('')
}
