import { toBlob } from 'html-to-image'

/**
 * 把指定 DOM 元素渲染成 PNG Blob（用于“回答生成为图片下载”）。
 * - pixelRatio 2 保证清晰度
 * - 背景色取元素自身计算色，透明则退回主题表面色，保证三种主题下都可读
 * - skipFonts：不嵌入字体文件，规避跨域字体导致的 canvas 污染/失败
 * - options.width/height：显式输出尺寸。滚动容器（内容溢出）默认只渲染可视区域，
 *   需配合把容器临时展开（overflow visible + height=scrollHeight）并传全量尺寸，
 *   才能导出**完整内容**而非仅当前窗口。
 */
export async function captureElementToPng(el: HTMLElement, options: { width?: number; height?: number } = {}): Promise<Blob | null> {
  try {
    const style = getComputedStyle(el)
    let bg = style.backgroundColor || ''
    if (!bg || bg === 'transparent' || bg === 'rgba(0, 0, 0, 0)') {
      bg = getComputedStyle(document.documentElement).getPropertyValue('--nc-surface').trim() || '#ffffff'
    }
    return await toBlob(el, {
      pixelRatio: 2,
      backgroundColor: bg,
      cacheBust: true,
      skipFonts: true,
      width: options.width,
      height: options.height,
    })
  } catch (err) {
    console.warn('[capture] failed to render element to image', err)
    return null
  }
}

/** 下载 Blob 为本地文件 */
export function downloadBlob(blob: Blob, fileName: string): void {
  const url = URL.createObjectURL(blob)
  const a = document.createElement('a')
  a.href = url
  a.download = fileName
  document.body.appendChild(a)
  a.click()
  a.remove()
  setTimeout(() => URL.revokeObjectURL(url), 3000)
}

/** 生成形如 answer-20260831-093012.png 的文件名时间戳 */
export function stamp(): string {
  const d = new Date()
  const p = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}${p(d.getMonth() + 1)}${p(d.getDate())}-${p(d.getHours())}${p(d.getMinutes())}${p(d.getSeconds())}`
}
