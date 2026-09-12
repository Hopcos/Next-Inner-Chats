// 临时：实测 sub-agent 内工具并发 vs 串行 —— 用完删除
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

async function timed(message, title) {
  const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title }) })
  const sid = (await r.json()).id
  const t0 = Date.now()
  const res = await fetch(`${APP}api/chat/stream`, { method: 'POST', headers: H, body: JSON.stringify({ sessionId: sid, message, clientMessageId: randomUUID(), thinkingEnabled: false }) })
  const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = '', done = null, tools = []
  while (true) { const x = await reader.read(); if (x.done) break; buf += dec.decode(x.value, { stream: true }); let i
    while ((i = buf.indexOf('\n\n')) >= 0) { const raw = buf.slice(0, i); buf = buf.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; const ev = JSON.parse(d); if (ev.kind === 'tool_start') tools.push(ev.toolName); if (ev.kind === 'done') done = ev } } }
  return { ms: Date.now() - t0, tools, sub: done?.subAgentCount }
}

const U1 = 'https://raw.githubusercontent.com/torvalds/linux/master/README'
const U2 = 'https://raw.githubusercontent.com/python/cpython/main/README.md'
// 单抓（基准）
const single = []
for (let k = 0; k < 2; k++) single.push(await timed(`请使用 delegate_task 工具，tools 参数传 ["http_fetch"]，子代理只做一件事：抓取并总结 ${U1}。`, `single-${k}`))
// 双抓（同轮两工具）
const dual = []
for (let k = 0; k < 2; k++) dual.push(await timed(`请使用 delegate_task 工具，tools 参数传 ["http_fetch"]，子代理在一个轮次里同时调用 http_fetch 两次，分别抓取并总结这两个文件：${U1} 和 ${U2}。`, `dual-${k}`))
console.log('single:', JSON.stringify(single.map((x) => x.ms)))
console.log('dual  :', JSON.stringify(dual.map((x) => x.ms)))
const sMed = single.map((x) => x.ms).sort((a, b) => a - b)[Math.floor(single.length / 2)]
const dMed = dual.map((x) => x.ms).sort((a, b) => a - b)[Math.floor(dual.length / 2)]
console.log(`median single=${sMed}ms dual=${dMed}ms ratio=${(dMed / sMed).toFixed(2)}x`)
console.log(dMed < sMed * 1.5 ? 'CONCURRENT EXECUTION CONFIRMED (dual ~ single)' : 'CHECK: dual looks serial (' + (dMed / sMed).toFixed(2) + 'x of single)')
process.exit(0)
