// 临时：验证并行同名工具 callId 精确配对 —— 用完删除
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

const files = ['torvalds/linux/master/README','python/cpython/main/README.md','nodejs/node/main/README.md','microsoft/TypeScript/main/README.md']
const msg = '请使用 http_fetch 同时（同一轮）抓取以下 4 个文件并逐一总结（不要分多轮，一次性调用多个 http_fetch）：\n' + files.map((f, i) => `${i + 1}) https://raw.githubusercontent.com/${f}`).join('\n')
const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title: 'callid-para' }) })
const sid = (await r.json()).id
const res = await fetch(`${APP}api/chat/stream`, { method: 'POST', headers: H, body: JSON.stringify({ sessionId: sid, message: msg, clientMessageId: randomUUID(), thinkingEnabled: false }) })
const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = '', done = null, starts = [], results = []
while (true) { const x = await reader.read(); if (x.done) break; buf += dec.decode(x.value, { stream: true }); let i
  while ((i = buf.indexOf('\n\n')) >= 0) { const raw = buf.slice(0, i); buf = buf.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; const ev = JSON.parse(d); if (ev.kind === 'tool_start') starts.push({ n: ev.toolName, id: ev.toolCallId }); if (ev.kind === 'tool_result' || ev.kind === 'tool_error') results.push({ n: ev.toolName, id: ev.toolCallId, ok: ev.success }); if (ev.kind === 'done') done = ev } } }

// 检查事件级配对：每个 start 的 callId 都有对应 result 且配对数量一致
const startIds = starts.filter((x) => x.id != null)
const resultIds = results.filter((x) => x.id != null)
console.log('starts (with callId):', JSON.stringify(startIds))
console.log('results (with callId):', JSON.stringify(resultIds.map((x) => `${x.id}:${x.ok}`)))
const sSet = new Set(startIds.map((x) => x.id))
const rSet = new Set(resultIds.map((x) => x.id))
const allPaired = startIds.length === resultIds.length && [...sSet].every((id) => rSet.has(id))

// 检查持久化 toolTrace：每条 entry 都有 success（无 ⏳）
const msgs = await (await fetch(`${APP}api/chat/sessions/${sid}/messages`, { headers: H })).json()
const last = msgs[msgs.length - 1]
const trace = JSON.parse(last.toolCallsJson || '[]')
const missingSuccess = trace.filter((t) => !('success' in t))
console.log('persisted trace entries:', trace.length, 'missing success:', missingSuccess.length)
console.log('trace sample:', JSON.stringify(trace.map((t) => ({ tool: t.tool, ok: t.success, ms: t.durationMs }))))

console.log(allPaired && missingSuccess.length === 0 ? 'CALLID PAIRING OK' : 'PAIRING CHECK FAILED')
process.exit(0)
