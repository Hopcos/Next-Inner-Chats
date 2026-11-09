/** Text Compare 插件配置 */
export const LS = {
  left: 'nc.tool.text-compare.left',
  right: 'nc.tool.text-compare.right',
  keysOnly: 'nc.tool.text-compare.keysOnly',
} as const

export function loadSide(key: string): string {
  try {
    return localStorage.getItem(key) ?? ''
  } catch {
    return ''
  }
}

export function saveSide(key: string, value: string): void {
  try {
    localStorage.setItem(key, value)
  } catch {
    /* ignore */
  }
}
