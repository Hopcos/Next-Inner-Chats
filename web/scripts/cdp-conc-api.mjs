// 临时：沙箱 6510 双会话并发 API 实验 —— 观察排队/超时/轮次/工具行为，用完删除
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

async function newSession(title) {
  const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title }) })
  return (await r.json()).id
}

async function streamOnce(sid, message, label, log, abortAtMs) {
  const started = Date.now()
  const controller = new AbortController()
  const timer = abortAtMs ? setTimeout(() => controller.abort(), abortAtMs) : null
  const stats = { text: 0, rounds: 0, tools: 0, toolErrors: 0, events: [], status: 'open', ttft: -1, firstEventAt: -1, lastEventAt: -1 }
  let firstEvent = null
  try {
    const res = await fetch(`${APP}api/chat/stream`, {
      method: 'POST', headers: H, signal: controller.signal,
      body: JSON.stringify({ sessionId: sid, message, clientMessageId: randomUUID(), thinkingEnabled: false }),
    })
    const reader = res.body.getReader()
    const decoder = new TextDecoder()
    let buf = ''
    while (true) {
      const { done, value } = await reader.read()
      if (done) break
      buf += decoder.decode(value, { stream: true })
      let idx
      while ((idx = buf.indexOf('\n\n')) >= 0) {
        const raw = buf.slice(0, idx); buf = buf.slice(idx + 2)
        for (const line of raw.split('\n')) {
          if (!line.startsWith('data:')) continue
          const data = line.slice(5).trim()
          if (!data) continue
          const ev = JSON.parse(data)
          const at = Date.now() - started
          if (firstEvent === null) { firstEvent = ev.kind; stats.firstEventAt = at }
          stats.lastEventAt = at
          if (ev.kind === 'text_delta') stats.text += (ev.text || '').length
          if (ev.kind === 'round_start') { stats.rounds++; stats.events.push(`r${ev.round}@${at}ms`) }
          if (ev.kind === 'tool_start') { stats.tools++; stats.events.push(`tool:${ev.toolName}@${at}ms`) }
          if (ev.kind === 'tool_error') stats.toolErrors++
          if (ev.kind === 'error') { stats.status = `error:${ev.code}`; stats.events.push(`ERR ${ev.code}@${at}ms`) }
          if (ev.kind === 'done') { stats.status = 'done'; stats.events.push(`done@${at}ms ttft=${ev.ttftMs} total=${ev.totalMs}ms tokens=${ev.totalTokens}`) }
        }
      }
    }
    stats.status = stats.status === 'open' ? 'stream-closed' : stats.status
  } catch (e) {
    stats.status = `exception:${e.name || 'err'}`
    if (e.name === 'AbortError') stats.status = `aborted@${Date.now() - started}ms`
  } finally {
    if (timer) clearTimeout(timer)
  }
  stats.text = stats.text
  log.push({ label, ...stats })
}

const log = []
const A = await newSession('并发A-实验')
const B = await newSession('并发B-实验')
console.log('sessions:', A.slice(0, 8), B.slice(0, 8))
const msgA = '请使用 http_fetch 工具抓取 https://example.com 页面，摘录页面中的标题与正文要点，并用中文分点总结。'
const msgB = '请使用 http_fetch 工具抓取 https://www.example.org 页面，说明页面主要内容，并用中文总结。'

const t0 = Date.now()
const pA = streamOnce(A, msgA, 'A', log, 150000)
const pB = streamOnce(B, msgB, 'B', log, 150000)

// 每 8s 采样一次进度（不切换任何窗口 = 纯后台）
for (let i = 0; i < 10; i++) {
  await new Promise((r) => setTimeout(r, 8000))
  await new Promise((r) => setImmediate(r))
  const elapsed = Math.round((Date.now() - t0) / 1000)
  const a = log.find((x) => x.label === 'A') ?? { text: '?', status: 'pending' }
  const b = log.find((x) => x.label === 'B') ?? { text: '?', status: 'pending' }
  console.log(`t=${elapsed}s  A: ${typeof a.text === 'number' ? a.text : '?'} chars, status=${a.status} | B: ${typeof b.text === 'number' ? b.text : '?'} chars, status=${b.status}`)
  if ((a.status && b.status && !['pending', 'open', 'stream-closed'].includes(String(a.status)) && !['pending', 'open', 'stream-closed'].includes(String(b.status)))) break
  if (typeof a.text === 'number' && typeof b.text === 'number') {
    await new Promise((r) => setTimeout(r, 0))
  }
}

await Promise.all([pA, pB])
console.log('\n==== 结果 ====')
for (const l of log) console.log(JSON.stringify(l, null, 1))
process.exit(0)
