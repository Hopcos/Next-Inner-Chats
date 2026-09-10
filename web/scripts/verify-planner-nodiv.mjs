// 临时：验证 Planner 不拆时也计入 Agent委派消耗 —— 用完删除
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

async function run(message, title) {
  const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title }) })
  const sid = (await r.json()).id
  const res = await fetch(`${APP}api/chat/stream`, { method: 'POST', headers: H, body: JSON.stringify({ sessionId: sid, message, clientMessageId: randomUUID(), thinkingEnabled: false }) })
  const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = '', done = null
  while (true) { const x = await reader.read(); if (x.done) break; buf += dec.decode(x.value, { stream: true }); let i
    while ((i = buf.indexOf('\n\n')) >= 0) { const raw = buf.slice(0, i); buf = buf.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; const ev = JSON.parse(d); if (ev.kind === 'done') done = ev } } }
  const msgs = await (await fetch(`${APP}api/chat/sessions/${sid}/messages`, { headers: H })).json()
  const last = msgs[msgs.length - 1]
  return { done, last }
}

// ① 简单请求 → planner 判不拆 → 消耗仍应计入
const a = await run('你好', 'no-decompose')
console.log('[no-decompose]', JSON.stringify({ plannerIn: a.done?.plannerInputTokens, plannerOut: a.done?.plannerOutputTokens, sub: a.done?.subAgentCount, persist: a.last?.plannerInputTokens }))

// ② 复合请求 → planner 拆 → 消耗计入（回归）
const b = await run('帮我总结 TCP 和 UDP 协议的区别，同时介绍一下 HTTP 与 HTTPS 的区别。', 'decompose')
console.log('[decompose]   ', JSON.stringify({ plannerIn: b.done?.plannerInputTokens, plannerOut: b.done?.plannerOutputTokens, sub: b.done?.subAgentCount, persist: b.last?.plannerInputTokens }))

const okA = (a.done?.plannerInputTokens ?? 0) > 0 && (a.last?.plannerInputTokens ?? 0) > 0
const okB = (b.done?.plannerInputTokens ?? 0) > 0 && (b.last?.plannerInputTokens ?? 0) > 0
console.log(okA ? 'NO-DECOMPOSE PLANNER TOKENS OK' : 'NO-DECOMPOSE STILL MISSING')
console.log(okB ? 'DECOMPOSE PLANNER TOKENS OK' : 'DECOMPOSE REGRESSED')
process.exit(0)
