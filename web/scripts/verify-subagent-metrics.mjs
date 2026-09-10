// 临时：验证子 Agent 用量汇总（done 事件 + 历史 DTO + 浮窗数据）—— 用完删除
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

const set = await fetch(`${APP}api/me/settings`, { method: 'PUT', headers: H, body: JSON.stringify({ 'agent.delegationEnabled': 'true' }) })
console.log('PUT on:', set.status)

const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title: 'subagent用量验证' }) })
const sid = (await r.json()).id
const msg = '请使用 delegate_task 工具并发完成两个任务：任务一：用一句话总结 TCP 协议；任务二：用一句话总结 UDP 协议。然后汇总。'
const res = await fetch(`${APP}api/chat/stream`, {
  method: 'POST', headers: H,
  body: JSON.stringify({ sessionId: sid, message: msg, clientMessageId: randomUUID(), thinkingEnabled: false }),
})
const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = ''
let done = null
while (true) { const x = await reader.read(); if (x.done) break; buf += dec.decode(x.value, { stream: true }); let i
  while ((i = buf.indexOf('\n\n')) >= 0) { const raw = buf.slice(0, i); buf = buf.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; const ev = JSON.parse(d); if (ev.kind === 'done') done = ev } } }
console.log('done event sub-agent:', JSON.stringify({ subAgentCount: done?.subAgentCount, subAgentInputTokens: done?.subAgentInputTokens, subAgentOutputTokens: done?.subAgentOutputTokens, toolCalls: done?.toolCalls }))

const msgs = await fetch(`${APP}api/chat/sessions/${sid}/messages`, { headers: H }).then((x) => x.json())
const asst = msgs.find((m) => m.role === 'Assistant')
console.log('history DTO sub-agent:', JSON.stringify({ subAgentCount: asst.subAgentCount, subAgentInputTokens: asst.subAgentInputTokens, subAgentOutputTokens: asst.subAgentOutputTokens }))
const ok = done?.subAgentCount >= 1 && done?.subAgentInputTokens > 0 && asst.subAgentCount === done?.subAgentCount
console.log(ok ? 'SUB-AGENT METRICS OK' : 'CHECK FAILED: ' + JSON.stringify({ done: done?.subAgentCount, hist: asst.subAgentCount }))
process.exit(0)
