// 临时：验证 Planner 自动拆解（自然请求不依赖提示词）—— 用完删除
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
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

let up = false
for (let i = 0; i < 25; i++) { try { const r = await fetch(`${APP}api/auth/providers`); if (r.ok) { up = true; break } } catch { } await sleep(1000) }
if (!up) { console.log('sandbox down'); process.exit(2) }

await fetch(`${APP}api/me/settings`, { method: 'PUT', headers: H, body: JSON.stringify({ 'agent.delegationEnabled': 'true' }) })

async function run(sid, message, label) {
  const res = await fetch(`${APP}api/chat/stream`, {
    method: 'POST', headers: H,
    body: JSON.stringify({ sessionId: sid, message, clientMessageId: randomUUID(), thinkingEnabled: false }),
  })
  const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = ''
  const events = []
  while (true) { const x = await reader.read(); if (x.done) break; buf += dec.decode(x.value, { stream: true }); let i
    while ((i = buf.indexOf('\n\n')) >= 0) { const raw = buf.slice(0, i); buf = buf.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; events.push(JSON.parse(d)) } } }
  // 压缩序列：round/tool/text
  const seq = []
  let round = 0
  for (const e of events) {
    if (e.kind === 'round_start') { round = e.round; seq.push(`R${round}`) }
    else if (e.kind === 'tool_start') seq.push(`${e.toolName || e.name}`)
    else if (e.kind === 'thinking_delta') seq.push('THINK')
    else if (e.kind === 'text_delta') seq.push('TEXT')
    else if (e.kind === 'done') { seq.push(`done(sub=${e.subAgentCount},in=${e.subAgentInputTokens})`) }
  }
  const done = events.find((e) => e.kind === 'done')
  console.log(`[${label}] seq: ${seq.join(' ')}`)
  console.log(`[${label}] done: sub=${done?.subAgentCount} in=${done?.subAgentInputTokens} out=${done?.subAgentOutputTokens} rounds=${done?.rounds} toolCalls=${done?.toolCalls}`)
  return { seq, done }
}

const r1 = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title: 'planner自然请求' }) })
const sid1 = (await r1.json()).id
// 自然复合请求（不提示用 delegate_task）
const a = await run(sid1, '帮我总结一下 TCP 和 UDP 协议的区别，同时介绍一下 HTTP 与 HTTPS 的区别。', 'compound')

const r2 = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title: 'planner简单请求' }) })
const sid2 = (await r2.json()).id
const b = await run(sid2, '你好，用一句话打个招呼。', 'simple')

const decomposed = a.done?.subAgentCount >= 2 && a.seq.includes('R1') && a.seq.indexOf('delegate_task') >= 0 && a.seq.indexOf('delegate_task') < a.seq.indexOf('R2')
const notOverSplit = !b.seq.includes('delegate_task')
console.log('compound auto-decomposed:', decomposed)
console.log('simple not split:', notOverSplit)
console.log(decomposed && notOverSplit ? 'PLANNER OK' : 'PLANNER CHECK FAILED')
process.exit(0)
