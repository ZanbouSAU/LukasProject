// src/api/http.ts
// HTTP 基础层：
//  - access token 只放内存（降低 XSS 窃取面）；refresh token 放 localStorage 以便刷新页面后续期。
//  - 401 时做「单飞」刷新：并发请求共享同一次 refresh，刷新成功后各自重试一次。
//  - 后端错误统一为 ApiError（带状态码与后端 message）。

import type { AuthResponse, ErrorResponse } from './types'

const REFRESH_KEY = 'nas.refreshToken'
const EMAIL_KEY = 'nas.email'

export class ApiError extends Error {
  readonly status: number
  constructor(status: number, message: string) {
    super(message)
    this.name = 'ApiError'
    this.status = status
  }
}

// ---------------------------------------------------------------- 令牌保管
let accessToken: string | null = null

export const tokenStore = {
  get access(): string | null {
    return accessToken
  },
  get refresh(): string | null {
    return localStorage.getItem(REFRESH_KEY)
  },
  get email(): string | null {
    return localStorage.getItem(EMAIL_KEY)
  },
  save(auth: AuthResponse): void {
    accessToken = auth.accessToken
    localStorage.setItem(REFRESH_KEY, auth.refreshToken)
    localStorage.setItem(EMAIL_KEY, auth.email)
  },
  clear(): void {
    accessToken = null
    localStorage.removeItem(REFRESH_KEY)
    localStorage.removeItem(EMAIL_KEY)
  },
}

/** 会话失效（刷新令牌也救不回来）时由 AuthProvider 注册的回调：跳回登录页。 */
let onSessionExpired: (() => void) | null = null
export function setSessionExpiredHandler(handler: (() => void) | null): void {
  onSessionExpired = handler
}

// ---------------------------------------------------------------- 刷新（单飞）
let refreshInFlight: Promise<boolean> | null = null

/** 用 refresh token 换新令牌对。成功返回 true；失败清空会话并返回 false。 */
export function tryRefresh(): Promise<boolean> {
  refreshInFlight ??= (async () => {
    const refreshToken = tokenStore.refresh
    if (!refreshToken) return false
    try {
      const res = await fetch('/api/auth/refresh', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ refreshToken }),
      })
      if (!res.ok) return false
      const auth = (await res.json()) as AuthResponse
      tokenStore.save(auth)
      return true
    } catch {
      return false
    }
  })().then((ok) => {
    refreshInFlight = null
    if (!ok) {
      tokenStore.clear()
      onSessionExpired?.()
    }
    return ok
  })
  return refreshInFlight
}

// ---------------------------------------------------------------- 请求封装
async function parseError(res: Response): Promise<ApiError> {
  let message = `请求失败（HTTP ${res.status}）`
  try {
    const body = (await res.json()) as Partial<ErrorResponse>
    if (typeof body.message === 'string' && body.message.length > 0) message = body.message
  } catch {
    /* 非 JSON 响应体，沿用默认消息 */
  }
  return new ApiError(res.status, message)
}

export interface RequestOptions {
  method?: string
  /** 自动 JSON 序列化的请求体 */
  json?: unknown
  /** 需要携带 Bearer 令牌（默认 true；登录/注册等公开接口传 false） */
  auth?: boolean
  signal?: AbortSignal
}

/**
 * 发起请求并返回原始 Response（已确保 res.ok）。
 * auth=true 时 401 会先尝试刷新令牌并重试一次。
 */
export async function request(path: string, options: RequestOptions = {}): Promise<Response> {
  const { method = 'GET', json, auth = true, signal } = options

  const doFetch = (): Promise<Response> => {
    const headers: Record<string, string> = {}
    if (json !== undefined) headers['Content-Type'] = 'application/json'
    if (auth && accessToken) headers['Authorization'] = `Bearer ${accessToken}`
    return fetch(path, {
      method,
      headers,
      body: json !== undefined ? JSON.stringify(json) : undefined,
      signal,
    })
  }

  let res = await doFetch()
  if (res.status === 401 && auth) {
    if (await tryRefresh()) res = await doFetch()
  }
  if (!res.ok) throw await parseError(res)
  return res
}

/** 发起请求并把响应体解析为 JSON。 */
export async function requestJson<T>(path: string, options: RequestOptions = {}): Promise<T> {
  const res = await request(path, options)
  return (await res.json()) as T
}
