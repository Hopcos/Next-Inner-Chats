// 临时：UI 验证历史消息 Token 浮窗全量字段（含模型/费用/耗时）—— 用完删除
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

const title = '指标落库验证'
for (let i = 0; i < 10; i++) {
  const ok = await evaljs(`(() => { const items = [...document.querySelectorAll('.sidebar .list .item')]; const it = items.find(x => (x.querySelector('.item-title')||{}).textContent === ${JSON.stringify(title)}); if (!it) return false; it.click(); return true })()`)
  if (ok) break
  await sleep(500)
}
await sleep(1500)

const btn = await evaljs(`(() => { const els = [...document.querySelectorAll('.actions .act')]; const b = [...els].reverse().find(x => (x.textContent||'').includes('Token')); if (!b) return null; const r = b.getBoundingClientRect(); return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) } })()`)
if (!btn) { console.log('NO Token button'); process.exit(2) }
await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: btn.x, y: btn.y })
await sleep(1200)
const rows = await evaljs(`JSON.stringify([...document.querySelectorAll('.token-pop .tp-row')].map(r => r.textContent.replace(/\\s+/g, ' ').trim()))`)
const title2 = await evaljs(`(() => { const t = document.querySelector('.token-pop .tp-title'); return t ? t.textContent : null })()`)
console.log('title:', title2)
console.log('rows:', rows)
const parsed = JSON.parse(rows)
const hasAll = parsed.length >= 6 && parsed.some((r) => r.includes('Model')) && parsed.some((r) => r.includes('Total time')) && parsed.some((r) => r.includes('TTFT'))
console.log(hasAll ? 'FULL POPOVER OK (history)' : 'POPOVER CHECK FAILED')
process.exit(0)
