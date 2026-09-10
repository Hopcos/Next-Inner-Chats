// 临时：探活 MCP 端点 + 拉 catalog 工具名 —— 用完删除
const APP = 'http://localhost:6510/'
const KEY = '9f8e7d6c5b4a39281706f5e4d3c2b1a0'
const uid = '9E1553D5-7319-44C6-9E11-AF958395AB4D'
const now = Math.floor(Date.now() / 1000)
const b64u = (o) => Buffer.from(JSON.stringify(o)).toString('base64url')
const h = b64u({ alg: 'HS256', typ: 'JWT' })
const p = b64u({ uid, sub: uid, unique_name: 'admin', name: 'A', role: ['admin'], nbf: now - 10, exp: now + 1800, iat: now, iss: 'next-chats', aud: 'next-chats-web' })
const { createHmac } = await import('node:crypto')
const s = createHmac('sha256', KEY).update(h + '.' + p).digest('base64url')
const tok = `${h}.${p}.${s}`
const H = { Authorization: 'Bearer ' + tok }

for (const [name, url] of [['JIRA', 'http://10.14.6.49:8003/mcp'], ['KIBANA', 'http://10.14.6.49:8008/mcp'], ['UCS', 'http://10.14.6.49:8004/mcp'], ['CODE', 'http://10.14.6.49:8011/codebase-memory-mcp/mcp'], ['REVIEW', 'http://10.14.6.49:6521/mcp']]) {
  try {
    const t0 = Date.now()
    const r = await fetch(url, { method: 'POST', headers: { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' }, body: JSON.stringify({ jsonrpc: '2.0', id: 1, method: 'initialize', params: { protocolVersion: '2025-03-26', capabilities: {}, clientInfo: { name: 'probe', version: '0.0.1' } } }), signal: AbortSignal.timeout(8000) })
    console.log(`${name}: ${r.status} in ${Date.now() - t0}ms ${(await r.text()).slice(0, 80)}`)
  } catch (e) { console.log(`${name}: FAIL ${String(e.cause?.code || e.message).slice(0, 50)}`) }
}

const cat = await fetch(`${APP}api/me/catalog`, { headers: H }).then((r) => r.json())
const tools = []
for (const srv of cat.mcpServers ?? []) {
  for (const t of srv.tools ?? []) tools.push(`${srv.name}: ${t.name}`)
}
console.log('MCP tools total:', tools.length)
console.log(tools.slice(0, 40).join('\n'))
process.exit(0)
