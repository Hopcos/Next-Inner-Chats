// 临时：6500 UI 实测 —— 真实登录 token → 打开新会话 → hover Token 读浮窗行 —— 用完删除
const APP = 'http://localhost:6500/'
const { readFileSync } = await import('node:fs')
const sid = readFileSync('scripts/.last-sid', 'utf8').trim()
const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
const login = await fetch(`${APP}api/auth/login`, { method: 'POST', headers: { 'Content-Type': 'application/json' }, body: JSON.stringify({ username: 'admin', password: 'Sa!10' }) })
const tok = (await login.json()).token

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

// 侧栏点击"subagent-6500实测"会话
let clicked = false
for (let i = 0; i < 8; i++) {
  clicked = await evaljs(`(() => { const items = [...document.querySelectorAll('.sidebar .item, .sidebar li, .session-item')]; const it = items.find(x => (x.textContent || '').includes('subagent-6500实测')); if (!it) return false; it.click(); return true })()`)
  if (clicked) break
  await sleep(600)
}
console.log('clicked session:', clicked)
await sleep(1800)

// 找最后一条 assistant 气泡的 Token 按钮并 hover
await sleep(2500)
const diag = await evaljs(`JSON.stringify({ bubbles: [...document.querySelectorAll('.bubble')].length, acts: [...document.querySelectorAll('.act')].map(x => x.textContent.trim()), hasPop: !!document.querySelector('.token-pop') })`)
console.log('diag:', diag)
const btn = await evaljs(`(() => { const all = [...document.querySelectorAll('.act')].filter(x => (x.textContent || '').includes('Token')); const b = all[all.length - 1]; if (!b) return null; const r = b.getBoundingClientRect(); return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) } })()`)
if (!btn) { console.log('NO Token button (old frontend cache?)'); process.exit(2) }
await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: btn.x, y: btn.y })
await sleep(1200)
const rows = await evaljs(`JSON.stringify([...document.querySelectorAll('.token-pop .tp-row')].map(r => r.textContent.replace(/\\s+/g, ' ').trim()))`)
console.log('popover rows:', rows)
const parsed = JSON.parse(rows)
const hasSub = parsed.some((r) => r.toLowerCase().includes('sub-agent'))
console.log(hasSub ? 'UI POPOVER HAS SUB-AGENT ROWS' : 'UI POPOVER MISSING SUB-AGENT ROWS')
await send('Page.captureScreenshot', { format: 'png' }).then(() => null)
const shot = await send('Page.captureScreenshot', { format: 'png' })
const { writeFileSync } = await import('node:fs')
writeFileSync('scripts/6500-popover.png', Buffer.from(shot.data, 'base64'))
console.log('screenshot saved: web/scripts/6500-popover.png')
process.exit(0)
