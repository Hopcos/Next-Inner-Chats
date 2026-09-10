// 临时：验证 Planner 消耗独立行 —— 用完删除
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

const msg = '帮我总结 TCP 和 UDP 协议的区别，同时介绍一下 HTTP 与 HTTPS 的区别。'
const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title: 'planner-tokens' }) })
const sid = (await r.json()).id
const res = await fetch(`${APP}api/chat/stream`, { method: 'POST', headers: H, body: JSON.stringify({ sessionId: sid, message: msg, clientMessageId: randomUUID(), thinkingEnabled: false }) })
const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = '', done = null
while (true) { const x = await reader.read(); if (x.done) break; buf += dec.decode(x.value, { stream: true }); let i
  while ((i = buf.indexOf('\n\n')) >= 0) { const raw = buf.slice(0, i); buf = buf.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; const ev = JSON.parse(d); if (ev.kind === 'done') done = ev } } }
console.log('done usage:', JSON.stringify({
  prompt: done?.promptTokens, completion: done?.completionTokens,
  sub: done?.subAgentCount, subIn: done?.subAgentInputTokens, subOut: done?.subAgentOutputTokens,
  plannerIn: done?.plannerInputTokens, plannerOut: done?.plannerOutputTokens,
  cost: done?.cost,
}))
// 从消息列表读持久化（planner 落库）
const msgs = await (await fetch(`${APP}api/chat/sessions/${sid}/messages`, { headers: H })).json()
const last = msgs[msgs.length - 1]
console.log('persisted:', JSON.stringify({ plannerIn: last?.plannerInputTokens, plannerOut: last?.plannerOutputTokens, prompt: last?.promptTokens }))
const okPersist = (done?.plannerInputTokens ?? 0) > 0 && (done?.plannerOutputTokens ?? 0) > 0
const okBucket = (done?.promptTokens ?? 0) < 15000 // 主输入不再含 planner（纯文本 2 子任务时主循环输入应远小于含 planner 的量）
console.log(okPersist && okBucket ? 'PLANNER TOKENS OK' : 'CHECK FAILED')
process.exit(0)
