// 临时：验证白名单裁剪（显式 tools 通道 + planner 自然拆解采样）—— 用完删除
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

async function run(message, title) {
  const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title }) })
  const sid = (await r.json()).id
  const res = await fetch(`${APP}api/chat/stream`, {
    method: 'POST', headers: H,
    body: JSON.stringify({ sessionId: sid, message, clientMessageId: randomUUID(), thinkingEnabled: false }),
  })
  const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = '', done = null
  while (true) { const x = await reader.read(); if (x.done) break; buf += dec.decode(x.value, { stream: true }); let i
    while ((i = buf.indexOf('\n\n')) >= 0) { const raw = buf.slice(0, i); buf = buf.slice(i + 2); for (const l of raw.split('\n')) { if (!l.startsWith('data:')) continue; const d = l.slice(5).trim(); if (!d) continue; const ev = JSON.parse(d); if (ev.kind === 'done') done = ev } } }
  return done
}

// ① Planner 自然拆解采样（增强 prompt 后）
for (let k = 0; k < 3; k++) {
  const done = await run('帮我总结 TCP 和 UDP 协议的区别，同时介绍一下 HTTP 与 HTTPS 的区别。', `nat-${k}`)
  console.log(`[natural-${k}] sub=${done?.subAgentCount} in=${done?.subAgentInputTokens} out=${done?.subAgentOutputTokens}`)
}

// ② 显式 tools 通道：让模型自主 delegate 且 tools 参数给 []（纯 LLM 子任务）→ 子代理工具应为空 → in 应显著小于全工具基线
const done2 = await run('请使用 delegate_task 工具完成下面两个任务，并且两个 delegate_task 的 tools 参数都传空数组 []（子代理不需要工具）：任务一：用一句话总结 TCP 协议；任务二：用一句话总结 UDP 协议。然后汇总。', 'explicit-tools')
console.log(`[explicit-tools] sub=${done2?.subAgentCount} in=${done2?.subAgentInputTokens} out=${done2?.subAgentOutputTokens}`)
const noToolsBaseline = 36438 // 未裁剪时纯文本 2 子任务基线
if ((done2?.subAgentCount ?? 0) >= 2) {
  const saved = noToolsBaseline - (done2?.subAgentInputTokens ?? 0)
  console.log(`  compared to no-trim baseline ${noToolsBaseline}: saved ${saved} (${((saved / noToolsBaseline) * 100).toFixed(0)}%)`)
  console.log(saved > 20000 ? 'TOOL-PRUNING CONFIRMED (sub-agent ran with zero tools)' : 'PRUNING CHECK')
}
process.exit(0)
