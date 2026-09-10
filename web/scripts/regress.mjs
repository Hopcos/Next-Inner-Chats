// 临时：新 DLL 回归（单会话 + 双并发）—— 用完删除
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

async function newSession(title) {
  const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title }) })
  return (await r.json()).id
}

async function run(sid, msg, label) {
  const t0 = Date.now()
  let text = 0, status = 'open', notes = [], tools = 0
  const res = await fetch(`${APP}api/chat/stream`, {
    method: 'POST', headers: H,
    body: JSON.stringify({ sessionId: sid, message: msg, clientMessageId: randomUUID(), thinkingEnabled: false }),
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
        const d = line.slice(5).trim(); if (!d) continue
        const ev = JSON.parse(d)
        if (ev.kind === 'text_delta') text += (ev.text || '').length
        if (ev.kind === 'tool_start') tools++
        if (ev.kind === 'context') notes.push(`${ev.reason}:${(ev.text || '').slice(0, 40)}`)
        if (ev.kind === 'error') status = `error:${ev.code}`
        if (ev.kind === 'done') status = 'done'
      }
    }
  }
  console.log(`[${label}] ${status} text=${text} tools=${tools} notes=${notes.join('|') || '(none)'} dur=${Math.round((Date.now() - t0) / 1000)}s`)
}

// 探活等待
let up = false
for (let i = 0; i < 20; i++) { try { const r = await fetch(`${APP}api/auth/providers`); if (r.ok) { up = true; break } } catch { } await sleep(1000) }
if (!up) { console.log('sandbox not up'); process.exit(2) }
console.log('sandbox up')

const s1 = await newSession('回归1')
await run(s1, '请简要介绍湖州，100字左右。', 'solo')

const cA = await newSession('回归并发A')
const cB = await newSession('回归并发B')
await Promise.all([
  run(cA, '请简要介绍杭州，80字左右。', 'concA'),
  run(cB, '请简要介绍苏州，80字左右。', 'concB'),
])
console.log('回归完成')
process.exit(0)
