// 临时：真实 MCP 工具 单独 vs 并发 对照实验 —— 用完删除
const APP = 'http://localhost:6510/'
const KEY = '9f8e7d6c5b4a39281706f5e4d3c2b1a0'
const uid = '9E1553D5-7319-44C6-9E11-AF958395AB4D'
const { createHmac, randomUUID } = await import('node:crypto')
const now = Math.floor(Date.now() / 1000)
const b64u = (o) => Buffer.from(JSON.stringify(o)).toString('base64url')
const h = b64u({ alg: 'HS256', typ: 'JWT' })
const p = b64u({ uid, sub: uid, unique_name: 'admin', name: 'A', role: ['admin'], nbf: now - 10, exp: now + 1800, iat: now, iss: 'next-chats', aud: 'next-chats-web' })
const s = createHmac('sha256', KEY).update(h + '.' + p).digest('base64url')
const tok = `${h}.${p}.${s}`
const H = { Authorization: 'Bearer ' + tok, 'Content-Type': 'application/json' }
const JIRA = '3DB3B215-DE95-4935-80AE-BE2B4A2107E3'

async function newSession(title) {
  const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title }) })
  return (await r.json()).id
}

async function run(sid, message, label, label2) {
  const started = Date.now()
  const evs = []
  let text = 0
  let tools = []
  let status = 'open'
  try {
    const res = await fetch(`${APP}api/chat/stream`, {
      method: 'POST', headers: H,
      body: JSON.stringify({ sessionId: sid, message, clientMessageId: randomUUID(), thinkingEnabled: false, mcpServerIds: [JIRA] }),
    })
    const reader = res.body.getReader()
    const dec = new TextDecoder()
    let buf = ''
    while (true) {
      const { done, value } = await reader.read()
      if (done) break
      buf += dec.decode(value, { stream: true })
      let i
      while ((i = buf.indexOf('\n\n')) >= 0) {
        const raw = buf.slice(0, i); buf = buf.slice(i + 2)
        for (const line of raw.split('\n')) {
          if (!line.startsWith('data:')) continue
          const d = line.slice(5).trim()
          if (!d) continue
          const ev = JSON.parse(d)
          const at = Date.now() - started
          if (ev.kind === 'text_delta') text += (ev.text || '').length
          if (ev.kind === 'tool_start') { tools.push(`${ev.toolName}@${at}ms`); evs.push(`tool_start:${ev.toolName}@${at}ms`) }
          if (ev.kind === 'tool_result' || ev.kind === 'tool_error') { evs.push(`${ev.kind}@${at}ms`); if (ev.kind === 'tool_error') evs.push(`  err=${ev.errorCode} preview=${(ev.resultPreview || '').slice(0, 60)}`) }
          if (ev.kind === 'error') { status = `error:${ev.code}`; evs.push(`ERR ${ev.code}@${at}ms`) }
          if (ev.kind === 'done') { status = 'done'; evs.push(`done@${at}ms total=${ev.totalMs}ms tokens=${ev.totalTokens}`) }
        }
      }
    }
    if (status === 'open') status = 'stream-closed'
  } catch (e) {
    status = `exception:${e.name || 'err'} ${String(e.message).slice(0, 60)}`
  }
  console.log(`[${label}] status=${status} text=${text} tools=${tools.join(',')}`)
  console.log(`  events: ${evs.join(' | ')}`)
  return { label: label2 || label, status, text, tools, evs, dur: Date.now() - started }
}

const msgA = '请使用 JIRA 的搜索工具（jira_search_issues 或类似搜索能力）查询当前用户分配给我的 3 个未完成问题，逐条列出标题、状态和优先级。只查询一次，不要重复。'
const msgB = '请使用 JIRA 的搜索工具查询优先级为 High 的问题（最多取 5 条），逐条列出标题、类型和指派给谁。只查询一次，不要重复。'

// 1. A 单独
const sA = await newSession('MCP单独A')
console.log('== 实验1: A 单独 ==')
await run(sA, msgA, 'A-alone')

// 2. B 单独
const sB = await newSession('MCP单独B')
console.log('== 实验2: B 单独 ==')
await run(sB, msgB, 'B-alone')

// 3. A+B 并发
const cA = await newSession('MCP并发A')
const cB = await newSession('MCP并发B')
console.log('== 实验3: A+B 并发 ==')
const t0 = Date.now()
const [ra, rb] = await Promise.all([
  run(cA, msgA, 'A-conc', 'A'),
  run(cB, msgB, 'B-conc', 'B'),
])
console.log(`并发总耗时: ${Math.round((Date.now() - t0) / 1000)}s (单独基线: A=${Math.round(ra.dur / 1000)}s 需另测 B)`)
process.exit(0)
