// 临时：UI 冒烟 —— 聊天设置抽屉的主-从委派开关与 Sub-Agent 模型选择 —— 用完删除
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

// 打开设置抽屉：点击 aria-label=settings 的工具按钮
const diag = await evaljs(`JSON.stringify({ btn: !!document.querySelector('button[aria-label="settings"], button[aria-label="设置"]'), toolbar: !!document.querySelector('.toolbar-entry'), bodyLen: document.body.textContent.length })`)
console.log('diag:', diag)
let opened = false
for (let i = 0; i < 5; i++) {
  opened = await evaljs(`(() => { const b = document.querySelector('button[aria-label="settings"], button[aria-label="设置"]'); if (!b) return false; b.click(); return true })()`)
  const hasDrawer = await evaljs(`!!document.querySelector('.el-drawer')`)
  if (opened || hasDrawer) break
  await sleep(800)
}
await sleep(2500)
const drawerNow = await evaljs(`!!document.querySelector('.el-drawer')`)
console.log('drawer open now:', drawerNow)

// 等待抽屉内容渲染完成（catalog 异步加载，先显示 skeleton）
let contentReady = false
for (let i = 0; i < 20; i++) {
  const len = await evaljs(`(() => { const d = document.querySelector('.el-drawer'); return d ? d.textContent.length : 0 })()`)
  if (len > 400) { contentReady = true; break }
  await sleep(1000)
}
console.log('drawer content ready:', contentReady)
await sleep(1500)
const drawer = await evaljs(`(() => { const d = document.querySelector('.el-drawer'); if (!d) return null; const txt = d.textContent
  const hasToggle = txt.includes('Multi-Agent Delegation') || txt.includes('主-从委派')
  const hasSwitch = !!d.querySelector('.el-switch')
  const hasFollow = txt.includes('Follow main model') || txt.includes('跟随主模型')
  return { hasToggle, hasSwitch, hasFollow } })()`)
console.log('drawer:', JSON.stringify(drawer))
console.log(drawer && drawer.hasToggle && drawer.hasSwitch && drawer.hasFollow ? 'DRAWER UI OK' : 'DRAWER UI FAILED')
process.exit(0)
