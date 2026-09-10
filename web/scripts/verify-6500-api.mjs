// 临时：6500 真实账号全链路验证（API 部分）—— 用完删除
const APP = 'http://localhost:6500/'
const { randomUUID } = await import('node:crypto')
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const login = await fetch(`${APP}api/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username: 'admin', password: 'Sa!10' }) })
if (!login.ok) { console.log('LOGIN FAIL', login.status); process.exit(2) }
const tok = (await login.json()).token
const H = { Authorization: 'Bearer ' + tok, 'Content-Type': 'application/json' }

const settings = await fetch(`${APP}api/me/settings`, { headers: H }).then((x) => x.json())
console.log('current settings:', JSON.stringify({ delegationEnabled: settings['agent.delegationEnabled'], subAgentModelId: settings['chat.subAgentModelId'] }))
const origEnabled = settings['agent.delegationEnabled']

if (origEnabled !== 'true') {
  await fetch(`${APP}api/me/settings`, { method: 'PUT', headers: H, body: JSON.stringify({ 'agent.delegationEnabled': 'true' }) })
  console.log('switched delegation ON (will restore to original later)')
}

const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title: 'subagent-6500实测' }) })
const sid = (await r.json()).id
console.log('session:', sid)
const msg = '请使用 delegate_task 工具，把下面两个任务发给独立的子代理并行完成，然后汇总成最终回答。任务一：用一句话介绍 TCP 协议。任务二：用一句话介绍 UDP 协议。'
const res = await fetch(`${APP}api/chat/stream`, {
  method: 'POST', headers: H,
  body: JSON.stringify({ sessionId: sid, message: msg, clientMessageId: randomUUID(), thinkingEnabled: false }),
})
const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = ''
let done = null, toolNames = []
while (true) { const x = await reader.read(); if (x.done) break; buf += dec.decode(x.value, { stream: true }); let i
  while ((i = buf.indexOf('\n\n')) >= 0) { const raw = buf.slice(0, i); buf = buf.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; const ev = JSON.parse(d)
    if (ev.kind === 'tool_start') toolNames.push(ev.toolName || ev.name)
    if (ev.kind === 'done') done = ev
  } } }
console.log('tools:', toolNames.join(', ') || '(none)')
console.log('done:', JSON.stringify({ subAgentCount: done?.subAgentCount, subIn: done?.subAgentInputTokens, subOut: done?.subAgentOutputTokens, rounds: done?.rounds, toolCalls: done?.toolCalls, cost: done?.cost }))

const msgs = await fetch(`${APP}api/chat/sessions/${sid}/messages`, { headers: H }).then((x) => x.json())
const asst = msgs.find((m) => m.role === 'Assistant')
console.log('history DTO:', JSON.stringify({ subAgentCount: asst?.subAgentCount, subIn: asst?.subAgentInputTokens, subOut: asst?.subAgentOutputTokens, cost: asst?.cost }))

const ok = toolNames.includes('delegate_task') && (done?.subAgentCount ?? 0) >= 1 && (done?.subAgentInputTokens ?? 0) > 0
console.log(ok ? '6500 DELEGATION OK (real account)' : '6500 CHECK FAILED')
// 保存会话ID到文件供 UI 阶段使用
await import('node:fs').then((fs) => fs.writeFileSync('scripts/.last-sid', sid))
process.exit(0)
