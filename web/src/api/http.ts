import { getLang, i18n } from '@/i18n'

export interface ApiError {
  code: string
  message: string
}

const TOKEN_KEY = 'nextchats.token'
const REFRESH_KEY = 'nextchats.refresh'

export const tokenStore = {
  get: () => localStorage.getItem(TOKEN_KEY),
  set: (token: string) => localStorage.setItem(TOKEN_KEY, token),
  clear: () => localStorage.removeItem(TOKEN_KEY),
  getRefresh: () => localStorage.getItem(REFRESH_KEY),
  setRefresh: (token: string) => localStorage.setItem(REFRESH_KEY, token),
  /** 登出 / 凭证失效：access 与 refresh 一并清除 */
  clearAll: () => {
    localStorage.removeItem(TOKEN_KEY)
    localStorage.removeItem(REFRESH_KEY)
  },
}

/** 登录态彻底失效（刷新失败）：清空凭证并通知全局跳转登录页 */
function expireSession(): void {
  tokenStore.clearAll()
  window.dispatchEvent(new CustomEvent('nextchats:unauthorized'))
}

/**
 * 错误码 → 前端 i18n 文案（默认英文/中文随语言切换）。
 * 已知错误码用译文；未知码回退到服务端 message；再兜底通用文案。
 */
export function translateError(code: string | undefined, fallback: string): string {
  if (code) {
    const key = `err.${code}`
    if (i18n.global.te(key)) return i18n.global.t(key)
  }
  return fallback || i18n.global.t('common.requestFailed')
}

/**
 * 用 refresh token 静默续期（方案 B）。并发去重：同一时刻只发一个刷新请求，
 * 多个 401 请求共享同一次结果；成功则更新本地 access + refresh（服务端已轮换）。
 */
let refreshInFlight: Promise<boolean> | null = null

async function refreshTokens(): Promise<boolean> {
  const refreshToken = tokenStore.getRefresh()
  if (!refreshToken) return false
  if (!refreshInFlight) {
    refreshInFlight = (async () => {
      try {
        const res = await fetch('/api/auth/refresh', {
          method: 'POST',
          headers: { 'Content-Type': 'application/json', 'X-Lang': getLang() },
          body: JSON.stringify({ refreshToken }),
        })
        if (!res.ok) {
          expireSession()
          return false
        }
        const body = (await res.json()) as { token: string; refreshToken: string; expiresIn: number }
        tokenStore.set(body.token)
        tokenStore.setRefresh(body.refreshToken)
        return true
      } catch {
        expireSession()
        return false
      } finally {
        refreshInFlight = null
      }
    })()
  }
  return refreshInFlight
}

/**
 * 轻量 HTTP 客户端：统一携带 JWT、401 自动刷新并重放一次、统一错误解析（友好文案 + 错误码）。
 */
export async function request<T = unknown>(path: string, options: RequestInit = {}): Promise<T> {
  const buildHeaders = (): Record<string, string> => ({
    'Content-Type': 'application/json',
    'X-Lang': getLang(),
    ...(options.headers as Record<string, string> | undefined),
    ...(tokenStore.get() ? { Authorization: `Bearer ${tokenStore.get()}` } : {}),
  })

  let res = await fetch(path, { ...options, headers: buildHeaders() })

  // access 过期：先尝试静默续期，成功后用新 token 重放原请求一次
  if (res.status === 401 && isAuthPath(path) && (await refreshTokens())) {
    res = await fetch(path, { ...options, headers: buildHeaders() })
  }

  if (res.status === 401 && isAuthPath(path) && !path.startsWith('/api/auth/login')) {
    throw { code: 'AUTH_EXPIRED', message: translateError('AUTH_EXPIRED', '登录已过期，请重新登录') } as ApiError
  }

  if (res.status === 204) return undefined as T

  const text = await res.text()
  let body: unknown = null
  try {
    body = text ? JSON.parse(text) : null
  } catch {
    body = text
  }

  if (!res.ok) {
    const err = (body ?? {}) as Partial<ApiError>
    throw { code: err.code ?? 'HTTP_ERROR', message: err.message ? translateError(err.code, err.message) : i18n.global.t('err.HTTP_ERROR') } as ApiError
  }
  return body as T
}

/**
 * 以 Blob 形式下载（带 JWT；401 自动刷新后重放一次）。用于二进制 / 附件响应，不能走 request()（它按 JSON/文本解析）。
 * 文件名取自 Content-Disposition（filename* 优先），取不到由调用方回退。
 */
export async function downloadBlob(path: string): Promise<{ blob: Blob; filename: string | null }> {
  const build = (): Record<string, string> => ({
    'X-Lang': getLang(),
    ...(tokenStore.get() ? { Authorization: `Bearer ${tokenStore.get()}` } : {}),
  })
  let res = await fetch(path, { method: 'GET', headers: build() })
  if (res.status === 401 && (await refreshTokens())) {
    res = await fetch(path, { method: 'GET', headers: build() })
  }
  if (res.status === 401) {
    throw { code: 'AUTH_EXPIRED', message: translateError('AUTH_EXPIRED', '登录已过期，请重新登录') } as ApiError
  }
  if (!res.ok) {
    let err: Partial<ApiError> = {}
    try {
      err = (await res.json()) as Partial<ApiError>
    } catch {
      /* 非 JSON 错误体 */
    }
    throw { code: err.code ?? 'HTTP_ERROR', message: err.message ? translateError(err.code, err.message) : i18n.global.t('err.HTTP_ERROR') } as ApiError
  }
  const cd = res.headers.get('content-disposition') ?? ''
  const star = /filename\*\s*=\s*(?:UTF-8'')?([^;]+)/i.exec(cd)
  const plain = /filename\s*=\s*"?([^";]+)"?/i.exec(cd)
  let filename: string | null = null
  try {
    filename = star ? decodeURIComponent(star[1].trim()) : plain ? plain[1].trim() : null
  } catch {
    filename = plain ? plain[1].trim() : null
  }
  return { blob: await res.blob(), filename }
}

/** 需要走「401 → 刷新重放」的路径（登录、刷新本身除外） */
function isAuthPath(path: string): boolean {
  return !path.startsWith('/api/auth/login') && !path.startsWith('/api/auth/refresh')
}

export const http = {
  get: <T = unknown>(path: string) => request<T>(path, { method: 'GET' }),
  post: <T = unknown>(path: string, body?: unknown) =>
    request<T>(path, { method: 'POST', body: body === undefined ? undefined : JSON.stringify(body) }),
  put: <T = unknown>(path: string, body?: unknown) =>
    request<T>(path, { method: 'PUT', body: body === undefined ? undefined : JSON.stringify(body) }),
  delete: <T = unknown>(path: string) => request<T>(path, { method: 'DELETE' }),
}

/** 流式 POST（SSE）：读取 data: 行并回调解析后的 JSON 对象；401 自动刷新后重放一次 */
export async function streamPost(
  path: string,
  body: unknown,
  onEvent: (data: Record<string, unknown>) => void,
  signal?: AbortSignal,
): Promise<void> {
  const attempt = async (): Promise<Response> => {
    const token = tokenStore.get()
    return fetch(path, {
      method: 'POST',
      headers: {
        'Content-Type': 'application/json',
        'X-Lang': getLang(),
        ...(token ? { Authorization: `Bearer ${token}` } : {}),
      },
      body: JSON.stringify(body),
      signal,
    })
  }

  let res = await attempt()
  if (res.status === 401 && (await refreshTokens())) {
    res = await attempt()
  }

  if (!res.ok || !res.body) {
    let code = 'STREAM_ERROR'
    let message = translateError(code, i18n.global.t('err.STREAM_ERROR'))
    try {
      const err = (await res.json()) as Partial<ApiError>
      if (err.code || err.message) {
        code = err.code ?? code
        message = err.message ? translateError(err.code, err.message) : message
      }
    } catch {
      /* 忽略 */
    }
    if (res.status === 401) expireSession()
    throw { code, message } as ApiError
  }

  const reader = res.body.getReader()
  const decoder = new TextDecoder()
  let buffer = ''

  while (true) {
    const { done, value } = await reader.read()
    if (done) break
    buffer += decoder.decode(value, { stream: true })

    let sepIndex = buffer.indexOf('\n\n')
    while (sepIndex >= 0) {
      const chunk = buffer.slice(0, sepIndex)
      buffer = buffer.slice(sepIndex + 2)
      for (const line of chunk.split('\n')) {
        if (line.startsWith('data:')) {
          const payload = line.slice(5).trim()
          if (!payload) continue
          try {
            onEvent(JSON.parse(payload) as Record<string, unknown>)
          } catch {
            /* 忽略非 JSON 行 */
          }
        }
      }
      sepIndex = buffer.indexOf('\n\n')
    }
  }
}
