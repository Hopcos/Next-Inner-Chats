// 临时：验证工具箱→后台→返回工具箱 —— 用完删除
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
const path = async () => (await evaljs(`location.pathname`)) || ''

await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false })
await send('Page.navigate', { url: APP })
await sleep(6000)
await evaljs(`localStorage.setItem('nextchats.token', ${JSON.stringify(tok)}); 'ok'`)
await send('Page.reload', {})
await sleep(6000)
await send('Page.navigate', { url: APP + 'tools' })
await sleep(6000)

// 头像 → Admin
await evaljs(`(() => { const a = document.querySelector('.hub-avatar'); if (a) a.click(); return !!a })()`)
await sleep(1200)
await evaljs(`(() => { const its = [...document.querySelectorAll('.el-dropdown-menu__item')]; const it = its.find(x => /Admin/.test(x.textContent)); if (it) it.click(); return !!it })()`)
await sleep(2500)
console.log('after Admin click:', await path())
// 后台内导航（验证标记在内部导航后仍保留）
await evaljs(`(() => { const items = document.querySelectorAll('.menu-item'); const m = [...items].find(x => x.textContent.includes('工作台') || x.textContent.includes('Metrics') || x.textContent.includes('📊')); if (m) m.click(); return !!m })()`)
await sleep(1500)
console.log('after internal nav:', await path())
// 返回
await evaljs(`(() => { const b = [...document.querySelectorAll('.back button, .back .el-button')].pop(); if (b) b.click(); return !!b })()`)
await sleep(2500)
const finalPath = await path()
console.log('after back:', finalPath)
console.log(finalPath === '/tools' ? 'BACK-TO-TOOLBOX OK' : 'BACK TOOLBOX FAILED (path=' + finalPath + ')')

// 反向验证：直接从聊天页进 admin → 返回应回 '/'
await send('Page.navigate', { url: APP })
await sleep(5000)
await evaljs(`(() => { const a = document.querySelector('.avatar'); if (a) a.click(); return !!a })()`)
await sleep(1200)
await evaljs(`(() => { const its = [...document.querySelectorAll('.el-dropdown-menu__item')]; const it = its.find(x => /Admin/.test(x.textContent)); if (it) it.click(); return !!it })()`)
await sleep(2500)
console.log('from chat: entered', await path())
await evaljs(`(() => { const b = [...document.querySelectorAll('.back button, .back .el-button')].pop(); if (b) b.click(); return !!b })()`)
await sleep(2500)
const chatBack = await path()
console.log('from chat: back ->', chatBack)
console.log(chatBack === '/' ? 'CHAT-BACK-OK' : 'CHAT-BACK anomaly (' + chatBack + ')')
process.exit(0)
