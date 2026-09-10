import { DatabaseSync } from 'node:sqlite'
const db = new DatabaseSync('E:/temp/verify/next-chats-test/data/nextchats.db')
const chat = db.prepare('PRAGMA table_info(ChatMessages)').all().map((c) => `${c.name}:${c.type}`).join(',')
console.log('ChatMessages cols:', chat)
console.log('has new cols:', ['ReasoningTokens', 'TtftMs', 'TotalMs', 'Rounds', 'ToolCalls', 'Cost'].every((c) => chat.includes(c)) ? 'YES - migration ok' : 'NO - missing columns!')
db.close()
