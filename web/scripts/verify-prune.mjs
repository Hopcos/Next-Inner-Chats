// 临时：验证工具白名单裁剪 —— 对比裁剪前后子代理输入 token —— 用完删除
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

// 纯文本复合任务（子代理无需 MCP 工具）：裁剪后子代理 tools 应为 [] → 无工具定义开销
const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title: 'prune验证' }) })
const sid = (await r.json()).id
const res = await fetch(`${APP}api/chat/stream`, {
  method: 'POST', headers: H,
  body: JSON.stringify({ sessionId: sid, message: '帮我总结 TCP 和 UDP 协议的区别，同时介绍一下 HTTP 与 HTTPS 的区别。', clientMessageId: randomUUID(), thinkingEnabled: false }),
})
const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = '', done = null
while (true) { const x = await reader.read(); if (x.done) break; buf += dec.decode(x.value, { stream: true }); let i
  while ((i = buf.indexOf('\n\n')) >= 0) { const raw = buf.slice(0, i); buf = buf.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; const ev = JSON.parse(d); if (ev.kind === 'done') done = ev } } }
console.log('sub-agent metrics:', JSON.stringify({ sub: done?.subAgentCount, in: done?.subAgentInputTokens, out: done?.subAgentOutputTokens }))
console.log('baseline before pruning (same request): sub=2, in=36438')
const saved = 36438 - (done?.subAgentInputTokens ?? 0)
console.log(`saved input tokens: ${saved} (${((saved / 36438) * 100).toFixed(0)}% less)`)
console.log(saved > 5000 ? 'PRUNING OK: sub-agent input dropped' : 'PRUNING CHECK (small or no diff)')
process.exit(0)
