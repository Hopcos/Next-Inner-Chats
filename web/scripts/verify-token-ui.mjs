// 临时：UI 验证 Token 按钮 + hover 浮窗 —— 用完删除
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

// 新建会话并让模型回复，保证有 usage
const r = await fetch(`${APP}api/chat/sessions`, { method: 'POST', headers: H, body: JSON.stringify({ title: 'token ui 验证' }) })
const sid = (await r.json()).id
const res = await fetch(`${APP}api/chat/stream`, {
  method: 'POST', headers: H,
  body: JSON.stringify({ sessionId: sid, message: '请用大约 50 字介绍钱塘江。', clientMessageId: `ui-${Date.now()}`, thinkingEnabled: false }),
})
const reader = res.body.getReader(); const dec = new TextDecoder(); let buf = ''
while (true) { const { done, value } = await reader.read(); if (done) break; buf += dec.decode(value, { stream: true }) }

let targets
for (let i = 0; i < 30; i++) { try { targets = await fetch('http://127.0.0.1:9223/json/list').then((x) => x.json()); if (targets.length) break } catch { await sleep(500) } }
console.log('targets:', JSON.stringify((targets || []).map((t) => t.url)))
const page = (targets || []).filter((t) => t.type === 'page' && (t.url.startsWith('http') || t.url === 'about:blank'))[0] || (targets || []).find((t) => t.type === 'page')
if (!page) { console.log('NO PAGE TARGET'); process.exit(2) }
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

// 切到目标会话
const title = 'token ui 验证'
for (let i = 0; i < 10; i++) {
  const ok = await evaljs(`(() => { const items = [...document.querySelectorAll('.sidebar .list .item')]; const it = items.find(x => (x.querySelector('.item-title')||{}).textContent === ${JSON.stringify(title)}); if (!it) return false; it.click(); return true })()`)
  if (ok) break
  await sleep(500)
}
await sleep(1500)

// 找 Token 按钮
const btn = await evaljs(`(() => { const els = [...document.querySelectorAll('.actions .act')]; const b = els.find(x => (x.textContent||'').includes('Token') || (x.title||'').includes('Token')); if (!b) return null; const r = b.getBoundingClientRect(); return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2), txt: b.textContent.trim() } })()`)
if (!btn) { console.log('NO Token button'); process.exit(2) }
console.log('Token button:', JSON.stringify(btn))

// hover
await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: btn.x, y: btn.y })
await sleep(1200)
const pop = await evaljs(`JSON.stringify([...document.querySelectorAll('.token-pop .tp-row')].map(r => r.textContent.replace(/\\s+/g, ' ').trim()))`)
const popTitle = await evaljs(`(() => { const t = document.querySelector('.token-pop .tp-title'); return t ? t.textContent : null })()`)
console.log('pop title(刷新场景):', popTitle)
console.log('pop rows(刷新场景):', pop)
console.log(popTitle && pop && pop.length >= 3 ? 'TOKEN POPOVER OK (refresh)' : 'POPOVER CHECK FAILED')

// ---- 实时场景：发一条新消息，完成后读取浮窗（应含全部明细行） ----
const typeInput = async (text) => {
  const c = await evaljs(`(() => { const els = [...document.querySelectorAll('.chat-input textarea, textarea')]; const ta = els[els.length - 1]; if (!ta) return null; const r = ta.getBoundingClientRect(); return { x: Math.round(r.left + 20), y: Math.round(r.top + 20) } })()`)
  if (!c) return false
  await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: c.x, y: c.y, button: 'left', clickCount: 1 })
  await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: c.x, y: c.y, button: 'left', clickCount: 1 })
  await evaljs(`(() => { const els = [...document.querySelectorAll('.chat-input textarea, textarea')]; const ta = els[els.length - 1]; if (!ta) return false; const setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set; setter.call(ta, ${JSON.stringify(text)}); ta.dispatchEvent(new Event('input', { bubbles: true })); return true })()`)
  await sleep(200)
  return true
}
const clickSendBtn = async () => {
  const c = await evaljs(`(() => { const el = [...document.querySelectorAll('.bar-actions .el-button')].find(b => (b.textContent || '').includes('Send') || (b.textContent || '').includes('发送')); if (!el) return null; const r = el.getBoundingClientRect(); return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2) } })()`)
  if (!c) return false
  await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: c.x, y: c.y, button: 'left', clickCount: 1 })
  await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: c.x, y: c.y, button: 'left', clickCount: 1 })
  return true
}
await typeInput('请用一句话介绍千岛湖。')
await clickSendBtn()
// 等流式完成（最后一个 bubble 内出现 Token 按钮）
let c2 = null
for (let i = 0; i < 40; i++) {
  await sleep(2000)
  const r2 = await evaljs(`(() => {
    const bubbles = [...document.querySelectorAll('.bubble')]
    const lastB = bubbles[bubbles.length - 1]
    if (!lastB) return null
    const b = [...lastB.querySelectorAll('.actions .act')].find(x => (x.textContent || '').includes('Token'))
    if (!b) return null
    const rect = b.getBoundingClientRect()
    return { x: Math.round(rect.left + rect.width / 2), y: Math.round(rect.top + rect.height / 2), bubbleText: lastB.textContent.length }
  })()`)
  if (r2) { c2 = r2; break }
}
if (!c2) { console.log('LIVE: no token btn in last bubble'); process.exit(2) }
console.log('live last-bubble chars:', c2.bubbleText)
await send('Input.dispatchMouseEvent', { type: 'mouseMoved', x: c2.x, y: c2.y })
await sleep(1200)
const pop2 = await evaljs(`JSON.stringify([...document.querySelectorAll('.token-pop .tp-row')].map(r => r.textContent.replace(/\\s+/g, ' ').trim()))`)
console.log('pop rows(实时):', pop2)
const rows2 = JSON.parse(pop2)
console.log(rows2.length >= 6 ? 'TOKEN POPOVER OK (live, full fields)' : 'LIVE FIELDS INCOMPLETE: ' + pop2)
process.exit(0)
