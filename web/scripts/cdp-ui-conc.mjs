// 临时：UI 双会话实验 —— 观察切走期间后台推理是否继续 + 中断按钮状态，用完删除
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

async function main() {
  let targets
  for (let i = 0; i < 20; i++) {
    try { targets = await fetch('http://127.0.0.1:9223/json/list').then((r) => r.json()); break } catch { await new Promise((r) => setTimeout(r, 500)) }
  }
  const pages = (targets || []).filter((t) => t.type === 'page' && t.url.startsWith('http'))
  const page = pages[0] || (targets || []).find((t) => t.type === 'page')
  const ws = new WebSocket(page.webSocketDebuggerUrl)
  await new Promise((res, rej) => { ws.onopen = res; ws.onerror = rej })
  let seq = 0
  const pending = new Map()
  ws.onmessage = (e) => { const m = JSON.parse(e.data); if (m.id && pending.has(m.id)) { pending.get(m.id)(m.result); pending.delete(m.id) } }
  const send = (method, params = {}) => new Promise((res) => { const id = ++seq; pending.set(id, res); ws.send(JSON.stringify({ id, method, params })) })
  const sleep = (ms) => new Promise((r) => setTimeout(r, ms))
  const evaljs = async (expression, awaitPromise = false) => (await send('Runtime.evaluate', { expression, returnByValue: true, awaitPromise })).result?.value

  const sidA = await newSession('并发A-UI')
  const sidB = await newSession('并发B-UI')

  await send('Emulation.setDeviceMetricsOverride', { width: 1600, height: 1000, deviceScaleFactor: 1, mobile: false })
  await send('Page.navigate', { url: APP })
  await sleep(6000)
  await evaljs(`localStorage.setItem('nextchats.token', ${JSON.stringify(tok)}); 'ok'`)
  await send('Page.reload', {})
  await sleep(6000)

  const clickItem = async (title) => {
    for (let i = 0; i < 10; i++) {
      const ok = await evaljs(`(() => { const items = [...document.querySelectorAll('.sidebar .list .item')]; const it = items.find(x => (x.querySelector('.item-title')||{}).textContent === ${JSON.stringify(title)}); if (!it) return false; if (it.classList.contains('active')) return true; it.click(); return true })()`)
      if (ok) { await sleep(600); return true }
      await sleep(500)
    }
    return false
  }
  const clickSend = async () => {
    const c = await evaljs(`(() => { const el = [...document.querySelectorAll('.bar-actions .el-button')].find(b => (b.textContent || '').includes('Send') || (b.textContent || '').includes('发送')); if (!el) return null; const r = el.getBoundingClientRect(); return { x: Math.round(r.left + r.width / 2), y: Math.round(r.top + r.height / 2), txt: el.textContent.trim() } })()`)
    if (!c) return false
    await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: c.x, y: c.y, button: 'left', clickCount: 1 })
    await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: c.x, y: c.y, button: 'left', clickCount: 1 })
    return c.txt
  }
  const typeInput = async (text) => {
    const r = await evaljs(`(() => { const ta = document.querySelector('.chat-input textarea, .chat-inputbar textarea, textarea'); if (!ta) return null; const r = ta.getBoundingClientRect(); return { x: Math.round(r.left + 20), y: Math.round(r.top + 20) } })()`)
    if (!r) { console.log('NO TEXTAREA'); return false }
    await send('Input.dispatchMouseEvent', { type: 'mousePressed', x: r.x, y: r.y, button: 'left', clickCount: 1 })
    await send('Input.dispatchMouseEvent', { type: 'mouseReleased', x: r.x, y: r.y, button: 'left', clickCount: 1 })
    await evaljs(`(() => { const ta = document.querySelector('.chat-input textarea, .chat-inputbar textarea, textarea'); if (!ta) return false; const setter = Object.getOwnPropertyDescriptor(window.HTMLTextAreaElement.prototype, 'value').set; setter.call(ta, ${JSON.stringify(text)}); ta.dispatchEvent(new Event('input', { bubbles: true })); return true })()`)
    await sleep(200)
    return true
  }
  const sample = async () => {
    const j = await evaljs(`(() => {
      const bubbles = [...document.querySelectorAll('.bubble')]
      const last = bubbles[bubbles.length - 1]
      const hasStop = [...document.querySelectorAll('.bar-actions .el-button')].some(b => (b.textContent || '').includes('Stop') || (b.textContent || '').includes('中断'))
      const sendBtn = [...document.querySelectorAll('.bar-actions .el-button')].find(b => (b.textContent || '').includes('Send') || (b.textContent || '').includes('发送'))
      const sendDisabled = sendBtn ? sendBtn.disabled || sendBtn.classList.contains('is-disabled') : null
      const title = (document.querySelector('.sidebar .list .item.active .item-title') || {}).textContent
      return JSON.stringify({
        title,
        bubbleCount: bubbles.length,
        lastLen: last ? (last.textContent || '').length : 0,
        lastText: last ? (last.textContent || '').slice(0, 60) : '',
        hasStop,
        sendDisabled,
      })
    })()`)
    return JSON.parse(j)
  }

  // 1. A 发送长任务
  await clickItem('并发A-UI')
  await typeInput('请写一篇关于人工智能发展历史的详细中文文章，至少 3000 字，分章节描述。')
  await clickSend()
  await sleep(2500)

  // 2. 切到 B 并发送（A 后台流式中）
  await clickItem('并发B-UI')
  await typeInput('请写一篇关于中国传统文化的中文长文，至少 3000 字，分章节描述，包含儒家道家佛家。')
  await clickSend()
  await sleep(2500)

  // 3. 切回 A：立即采样（A1 = 切走期间后台累计）
  await clickItem('并发A-UI')
  const A1 = await sample()
  console.log('A1(切回瞬间):', JSON.stringify(A1))

  // 4. A 激活 4s 后采样（A2）
  await sleep(4000)
  const A2 = await sample()
  console.log('A2(激活4s后):', JSON.stringify(A2))

  // 5. 切到 B：立即采样（B1 = 后台累计）
  await clickItem('并发B-UI')
  const B1 = await sample()
  console.log('B1(切回瞬间):', JSON.stringify(B1))

  // 6. B 激活 4s 后采样（B2）
  await sleep(4000)
  const B2 = await sample()
  console.log('B2(激活4s后):', JSON.stringify(B2))

  console.log('\n== 结论 ==')
  console.log('A 后台增长(应≈A1-A基线):', A1.lastLen, '→ A 激活增长:', A2.lastLen - A1.lastLen)
  console.log('B 后台增长:', B1.lastLen, '→ B 激活增长:', B2.lastLen - B1.lastLen)
  console.log('A2 hasStop:', A2.hasStop, 'sendDisabled:', A2.sendDisabled)
  process.exit(0)
}
main().catch((e) => { console.error(e); process.exit(1) })
