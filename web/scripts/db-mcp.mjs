// 临时：查沙箱 DB 的 MCP 服务器配置 —— 用完删除
import { DatabaseSync } from 'node:sqlite'
const db = new DatabaseSync('E:/temp/verify/next-chats-test/data/nextchats.db')
const tables = db.prepare("SELECT name FROM sqlite_master WHERE type='table'").all().map((r) => r.name)
console.log('mcp-ish tables:', JSON.stringify(tables.filter((t) => /mcp/i.test(t))))
for (const t of tables.filter((t) => /mcp/i.test(t))) {
  const cols = db.prepare(`PRAGMA table_info(${t})`).all().map((c) => c.name)
  console.log(t, 'cols:', cols.join(','))
  if (t.toLowerCase().includes('server')) {
    const rows = db.prepare(`SELECT * FROM ${t} LIMIT 20`).all()
    for (const r of rows) console.log(JSON.stringify(r).slice(0, 400))
  }
}
db.close()
