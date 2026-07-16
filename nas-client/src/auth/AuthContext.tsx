// src/auth/AuthContext.tsx
// 会话状态：
//  - 应用启动时若 localStorage 里有刷新令牌，先静默 refresh 一次恢复会话；
//  - access token 失效由 http 层自动续期；刷新令牌也失效时回到登录页。

/* eslint-disable react-refresh/only-export-components */
import { createContext, useCallback, useContext, useEffect, useMemo, useState } from 'react'
import type { ReactNode } from 'react'
import * as authApi from '../api/auth'
import { setSessionExpiredHandler, tokenStore, tryRefresh } from '../api/http'

interface AuthState {
  /** 启动时的会话恢复是否完成 */
  ready: boolean
  /** 已登录用户邮箱；null 表示未登录 */
  email: string | null
  login: (email: string, password: string) => Promise<void>
  register: (email: string, password: string, confirmPassword: string, fullName?: string) => Promise<void>
  logout: () => Promise<void>
  logoutAll: () => Promise<void>
}

const AuthContext = createContext<AuthState | null>(null)

export function useAuth(): AuthState {
  const ctx = useContext(AuthContext)
  if (!ctx) throw new Error('useAuth 必须在 <AuthProvider> 内使用')
  return ctx
}

export function AuthProvider({ children }: { children: ReactNode }) {
  const [ready, setReady] = useState(false)
  const [email, setEmail] = useState<string | null>(null)

  // 启动恢复会话 + 注册「会话彻底失效」回调
  useEffect(() => {
    setSessionExpiredHandler(() => setEmail(null))
    let cancelled = false
    void (async () => {
      if (tokenStore.refresh) {
        const ok = await tryRefresh()
        if (!cancelled && ok) setEmail(tokenStore.email)
      }
      if (!cancelled) setReady(true)
    })()
    return () => {
      cancelled = true
      setSessionExpiredHandler(null)
    }
  }, [])

  const login = useCallback(async (loginEmail: string, password: string) => {
    const auth = await authApi.login({ email: loginEmail, password })
    setEmail(auth.email)
  }, [])

  const register = useCallback(
    async (regEmail: string, password: string, confirmPassword: string, fullName?: string) => {
      await authApi.register({
        email: regEmail,
        password,
        confirmPassword,
        fullName: fullName || null,
      })
    },
    [],
  )

  const logout = useCallback(async () => {
    await authApi.logout()
    setEmail(null)
  }, [])

  const logoutAll = useCallback(async () => {
    await authApi.logoutAll()
    setEmail(null)
  }, [])

  const value = useMemo<AuthState>(
    () => ({ ready, email, login, register, logout, logoutAll }),
    [ready, email, login, register, logout, logoutAll],
  )

  return <AuthContext.Provider value={value}>{children}</AuthContext.Provider>
}
