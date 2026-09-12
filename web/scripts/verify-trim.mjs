// 临时：验证已用工具集按需裁剪触发 + token 收益 —— 用完删除
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

async function run(message, title) {
  const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title }) })
  const sid = (await r.json()).id
  const res = await fetch(`${APP}api/chat/stream`, { method: 'POST', headers: H, body: JSON.stringify({ sessionId: sid, message, clientMessageId: randomUUID(), thinkingEnabled: false }) })
  const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = '', done = null, ctx = [], tools = []
  while (true) { const x = await reader.read(); if (x.done) break; buf += dec.decode(x.value, { stream: true }); let i
    while ((i = buf.indexOf('\n\n')) >= 0) { const raw = buf.slice(0, i); buf = buf.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; const ev = JSON.parse(d); if (ev.kind === 'context') ctx.push(ev.reason + ': ' + (ev.text ?? '')); if (ev.kind === 'tool_start') tools.push(ev.toolName); if (ev.kind === 'done') done = ev } } }
  return { done, ctx, tools }
}

// 多轮工具任务：连续抓取多个文件（迫使多轮 http_fetch），观察 tool_trim 事件 + prompt 是否下降
const msg = '请用 http_fetch 依次抓取并总结这三个开源项目的 README：\n1) https://raw.githubusercontent.com/torvalds/linux/master/README\n2) https://raw.githubusercontent.com/python/cpython/main/README.md\n3) https://raw.githubusercontent.com/nodejs/node/main/README.md'
const a = await run(msg, 'trim-multi')
console.log('ctx events:', JSON.stringify(a.ctx.filter((x) => x.startsWith('tool_trim'))))
console.log('tools used:', [...new Set(a.tools)].join(', '))
console.log('done:', JSON.stringify({ prompt: a.done?.promptTokens, completion: a.done?.completionTokens, rounds: a.done?.rounds, toolCalls: a.done?.toolCalls }))
const trimmedEvt = a.ctx.some((x) => x.startsWith('tool_trim'))
console.log(trimmedEvt ? 'TOOL TRIM FIRED' : 'TOOL TRIM NOT FIRED (check criteria)')
process.exit(0)
