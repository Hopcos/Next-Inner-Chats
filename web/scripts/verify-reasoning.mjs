// 临时：验证思考过程 token 消耗（估算兜底）—— 用完删除
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

const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title: '推理token验证' }) })
const sid = (await r.json()).id

// 注意：thinkingEnabled 默认 true（有思考链）——用带思考的题验证估算
const res = await fetch(`${APP}api/chat/stream`, {
  method: 'POST', headers: H,
  body: JSON.stringify({ sessionId: sid, message: '请解释一下 TCP 三次握手的过程。', clientMessageId: randomUUID() }),
})
const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = ''
let done = null; let reasoningChars = 0
while (true) { const x = await reader.read(); if (x.done) break; buf += dec.decode(x.value, { stream: true }); let i
  while ((i = buf.indexOf('\n\n')) >= 0) { const raw = buf.slice(0, i); buf = buf.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; const ev = JSON.parse(d); if (ev.kind === 'thinking_delta') reasoningChars += (ev.text || '').length; if (ev.kind === 'done') done = ev } } }
console.log('thinking chars:', reasoningChars)
console.log('done event:', JSON.stringify({ model: done?.model, reasoningTokens: done?.reasoningTokens, completionTokens: done?.completionTokens, totalTokens: done?.totalTokens, cost: done?.cost }))
const msgs = await fetch(`${APP}api/chat/sessions/${sid}/messages`, { headers: H }).then((x) => x.json())
const asst = msgs.find((m) => m.role === 'Assistant')
console.log('history DTO reasoning shortcuts:', asst.reasoning ? `reasoning chars=${asst.reasoning.length}` : 'no reasoning', '| reasoningTokens=', asst.reasoningTokens)
const okDone = done?.reasoningTokens > 0
const okHist = asst.reasoningTokens > 0
console.log(okDone && okHist ? 'REASONING TOKENS OK (estimate visible)' : 'CHECK FAILED')
process.exit(0)
