// 临时：验证主-从委派（delegate_task + 并行子 Agent）—— 用完删除
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

// 开启主-从委派（用户设置）
const set = await fetch(`${APP}api/me/settings`, { method: 'PUT', headers: H, body: JSON.stringify({ 'agent.delegationEnabled': 'true' }) })
console.log('PUT delegationEnabled:', set.status)

const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title: 'delegate验证' }) })
const sid = (await r.json()).id

const msg = '请使用 delegate_task 工具，把我下面的两个任务各自发给独立的子代理并行完成，然后把两个子代理的结论汇总成最终回答。' +
  '任务一：用一句话分别介绍 TCP 和 UDP。任务二：用一句话介绍 HTTP 和 HTTPS 的区别。'
const res = await fetch(`${APP}api/chat/stream`, {
  method: 'POST', headers: H,
  body: JSON.stringify({ sessionId: sid, message: msg, clientMessageId: randomUUID(), thinkingEnabled: false }),
})
const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = ''
const tools = []
let delegateStart = null, delegateResult = null, done = null, finalText = ''
while (true) { const x = await reader.read(); if (x.done) break; buf += dec.decode(x.value, { stream: true }); let i
  while ((i = buf.indexOf('\n\n')) >= 0) { const raw = buf.slice(0, i); buf = buf.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; const ev = JSON.parse(d)
    if (ev.kind === 'tool_start') { tools.push(`${ev.serverName}.${ev.toolName}`); if (ev.toolName === 'delegate_task') delegateStart = ev }
    if (ev.kind === 'tool_result' && ev.toolName === 'delegate_task') delegateResult = ev
    if (ev.kind === 'text_delta') finalText += ev.text || ''
    if (ev.kind === 'done') done = ev
  } } }
console.log('tools used:', tools.join(', ') || '(none)')
console.log('delegate_task called:', !!delegateStart)
console.log('delegate result preview:', delegateResult ? (delegateResult.resultPreview || '').slice(0, 260) : '(none)')
console.log('final text chars:', finalText.length)
console.log('done model:', done?.model, 'rounds:', done?.rounds, 'toolCalls:', done?.toolCalls)
const ok = !!delegateStart && !!delegateResult && finalText.length > 50
console.log(ok ? 'DELEGATION OK (sub-agent executed & summarized)' : 'CHECK FAILED')

// ---- 开关关闭回归：delegate_task 工具不应可用（模型无法调用） ----
const setOff = await fetch(`${APP}api/me/settings`, { method: 'PUT', headers: H, body: JSON.stringify({ 'agent.delegationEnabled': 'false' }) })
console.log('PUT off:', setOff.status)
const r2 = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title: 'delegate-off验证' }) })
const sid2 = (await r2.json()).id
const sw = Date.now()
const res2 = await fetch(`${APP}api/chat/stream`, {
  method: 'POST', headers: H,
  body: JSON.stringify({ sessionId: sid2, message: '请使用 delegate_task 工具做两个任务，然后汇总。', clientMessageId: randomUUID(), thinkingEnabled: false }),
})
const reader2 = res2.body.getReader(); const dec2 = new TextDecoder(); let buf2 = ''
const tools2 = []
while (true) { const x = await reader2.read(); if (x.done) break; buf2 += dec2.decode(x.value, { stream: true }); let i
  while ((i = buf2.indexOf('\n\n')) >= 0) { const raw = buf2.slice(0, i); buf2 = buf2.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; const ev = JSON.parse(d); if (ev.kind === 'tool_start') tools2.push(ev.toolName) } } }
console.log('off-mode tools used:', tools2.join(', ') || '(none)')
console.log(tools2.includes('delegate_task') ? 'OFF CHECK FAILED (delegate still callable!)' : 'OFF CHECK OK (delegate unavailable)')
console.log('total wall time:', ((Date.now() - sw) / 1000).toFixed(1) + 's')
process.exit(0)
