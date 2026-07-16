// src/api/auth.ts

import { request, requestJson, tokenStore } from './http'
import type { AuthResponse, LoginRequest, RegisterRequest } from './types'

export async function register(req: RegisterRequest): Promise<void> {
  await request('/api/auth/register', { method: 'POST', json: req, auth: false })
}

export async function login(req: LoginRequest): Promise<AuthResponse> {
  const auth = await requestJson<AuthResponse>('/api/auth/login', {
    method: 'POST',
    json: req,
    auth: false,
  })
  tokenStore.save(auth)
  return auth
}

/** 登出当前设备：尽力吊销本地持有的刷新令牌；无论吊销是否成功，本地会话一律清空。 */
export async function logout(): Promise<void> {
  const refreshToken = tokenStore.refresh
  try {
    if (refreshToken) {
      await request('/api/auth/logout', { method: 'POST', json: { refreshToken } })
    }
  } catch {
    // 网络故障或令牌已失效都不应阻止本地登出；该令牌最长 7 天后自然过期。
  } finally {
    tokenStore.clear()
  }
}

/** 登出所有设备：吊销该账号全部刷新令牌（怀疑令牌泄露时使用）。失败时保留会话并抛出，由界面提示。 */
export async function logoutAll(): Promise<void> {
  await request('/api/auth/logout-all', { method: 'POST' })
  tokenStore.clear()
}
