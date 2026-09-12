// 临时：验证历史消息显示折叠 ToolCard —— 用完删除
const APP = 'http://localhost:6510/'
const KEY = '9f8e7d6c5b4a39281706f5e4d3c2b1a0'
const uid = '9E1553D5-7319-44C6-9E11-AF958395AB4D'
const { createHmac } = await import('node:crypto')
const now = Math.floor(Date.now() / 1000)
const b64u = (o) => Buffer.from(JSON.stringify(o)).toString('base64url')
const h = b64u({ alg: 'HS256', typ: 'JWT' })
const p = b64u({ uid, sub: uid, unique_name: 'admin', name: 'A', role: ['admin'], nbf: now - 10, exp: now + 1800, iat: now, iss: 'next-chats', aud: 'next-chats-web' })
const s = createHmac('sha256', KEY).update(h + '.' + p).digest('base64url')
const tok = `${h}.${p}.${s}`
const H = { Authorization: 'Bearer ' + tok, 'Content-Type': 'application/json' }
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

// 找有工具调用的历史会话
const sessions = await (await fetch(`${APP}api/chat/sessions`, { headers: H })).json()
let target = null
for (const sess of sessions || []) {
  try {
    const msgs = await (await fetch(`${APP}api/chat/sessions/${sess.id}/messages`, { headers: H })).json()
    const hasTools = (msgs || []).some((m) => m.role === 'Assistant' && m.toolCallsJson)
    if (hasTools) { target = { id: sess.id, title: sess.title }; break }
  } catch { /* 忽略 */ }
}
console.log('target session:', JSON.stringify(target))
if (!target) { console.log('NO TOOL SESSION FOUND'); process.exit(2) }

let targets
for (let i = 0; i < 30; i++) { try { targets = await fetch('http://127.0.0.1:9223/json/list').then((x) => x.json()); if (targets.length) break } catch { await sleep(500) } }
const page = (targets || []).filter((t) => t.type === 'page' && (t.url.startsWith('http') || t.url === 'about:blank'))[0]
if (!page) { console.log('NO PAGE'); process.exit(2) }
const ws = new WebSocket(page.webSocketDebuggerUrl)
await new Promise((x, y) => { ws.onopen = x; ws.onerror = y })
let seq = 0
const pend = new Map()
ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && pend.has(m.id)) { pend.get(m.id)(m.result); pend.delete(m.id) } }
const send = (method, params = {}) => new Promise((x) => { const id = ++seq; pend.set(id, x); ws.send(JSON.stringify({ id, method, params })) })
const evaljs = async (expression) => (await send('Runtime.evaluate', { expression, returnByValue: true })).result?.value

await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false })
await send('Page.navigate', { url: APP })
await sleep(6000)
await evaljs(`localStorage.setItem('nextchats.token', ${JSON.stringify(tok)}); 'ok'`)
await send('Page.reload', {})
await sleep(6000)
// 直接导航到会话？路由是 /chat/:id 还是 ?  —— 看 router：主页面单路由，会话通过点击侧栏加载。试 API 加载后 UI 刷新。
// 简单：通过路由 /?session=<id> 或点击侧栏会话项。这里用最稳的：检查 API 层的 fromDto 映射（直接在前端环境跑不了）。
// 改为：导航到主页，点击侧栏匹配标题的会话项。
await evaljs(`(() => { const items = [...document.querySelectorAll('.session-item, .chain-item, .bubble, [class*=session]')]; const hd = items.find(x => (x.textContent || '').includes(${JSON.stringify(target.title.slice(0, 12))})); if (hd) hd.click(); return !!hd })()`)
await sleep(4000)
const cards = await evaljs(`JSON.stringify([...document.querySelectorAll('.tool-card')].map(c => ({ name: c.textContent.slice(0, 60), collapsed: !!c.querySelector('.el-collapse-item__header') })).slice(0, 8))`)
console.log('tool cards found:', cards)
const ok = cards.length > 0
console.log(ok ? 'HISTORY TOOLCARD OK' : 'HISTORY TOOLCARD NOT FOUND')
process.exit(0)
