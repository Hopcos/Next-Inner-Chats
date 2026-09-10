// 临时：验证 Token 浮窗「Task 拆解 消耗」行 —— 用完删除
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
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))

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
await evaljs(`(() => { const items = [...document.querySelectorAll('.session-item, .chain-item, .bubble')]; const hd = items.find(x => x.textContent.includes('planner-tokens')); if (hd) hd.click(); return !!hd })()`)
await sleep(4000)
// 找 ⚡ 按钮并 hover（el-tooltip 150ms 后显示）
const info = await evaljs(`(() => { const acts = [...document.querySelectorAll('.act')].filter(x => x.textContent.includes('⚡')); const b = acts[acts.length - 1]; if (!b) return null; const r = b.getBoundingClientRect(); return { x: r.x + r.width / 2, y: r.y + r.height / 2 } })()`)
console.log('token btn at:', JSON.stringify(info))
if (!info) { console.log('NO TOKEN BTN'); process.exit(2) }
await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: info.x, y: info.y })
await sleep(900)
const rows = await evaljs(`JSON.stringify([...document.querySelectorAll('.token-pop .tp-row')].map(x => x.textContent.trim().replace(/\\s+/g, ' ')))`)
console.log('token-pop rows:', rows)
const ok = /Task 拆解 消耗/.test(rows) && /↑1477 ↓374/.test(rows)
console.log(ok ? 'PLANNER ROW OK' : 'PLANNER ROW NOT FOUND')
process.exit(0)
