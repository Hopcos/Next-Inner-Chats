// 临时：验证 done 事件的 usage 字段（reasoningTokens/rounds/toolCalls 透传）—— 用完删除
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
for (let i = 0; i < 20; i++) { try { const r = await fetch(`${APP}api/auth/providers`); if (r.ok) { up = true; break } } catch { } await sleep(1000) }
if (!up) { console.log('sandbox down'); process.exit(2) }

const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title: 'token验证' }) })
const sid = (await r.json()).id
console.log('session:', sid.slice(0, 8))

const res = await fetch(`${APP}api/chat/stream`, {
  method: 'POST', headers: H,
  body: JSON.stringify({ sessionId: sid, message: '请用一句话介绍你自己。', clientMessageId: randomUUID() }),
})
const reader = res.body.getReader()
const dec = new TextDecoder()
let buf = ''
let doneEvent = null
let rounds = 0
const texts = []
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
      if (ev.kind === 'round_start') rounds++
      if (ev.kind === 'text_delta') texts.push(ev.text)
      if (ev.kind === 'done') doneEvent = ev
    }
  }
}
console.log('rounds:', rounds)
console.log('raw done:', JSON.stringify(doneEvent))
console.log('text chars:', texts.join('').length)
process.exit(0)
