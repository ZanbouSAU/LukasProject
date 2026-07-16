// src/components/LoginPage.tsx

import { useState } from 'react'
import { ApiError } from '../api/http'
import { useAuth } from '../auth/AuthContext'
import { Button, Spinner, TextField } from './ui'
import * as React from "react";

type Mode = 'login' | 'register'

export function LoginPage() {
  const { login, register } = useAuth()
  const [mode, setMode] = useState<Mode>('login')
  const [email, setEmail] = useState('')
  const [password, setPassword] = useState('')
  const [confirm, setConfirm] = useState('')
  const [fullName, setFullName] = useState('')
  const [busy, setBusy] = useState(false)
  const [error, setError] = useState<string | null>(null)
  const [notice, setNotice] = useState<string | null>(null)

  const switchMode = (m: Mode) => {
    setMode(m)
    setError(null)
    setNotice(null)
  }

  const submit =
      async (e: Parameters<NonNullable<React.ComponentProps<'form'>['onSubmit']>>[0]) => {
    e.preventDefault()
    setError(null)
    setNotice(null)
    if (mode === 'register' && password !== confirm) {
      setError('两次输入的密码不一致')
      return
    }
    setBusy(true)
    try {
      if (mode === 'login') {
        await login(email, password)
      } else {
        await register(email, password, confirm, fullName.trim() || undefined)
        setNotice('注册成功，请登录')
        setMode('login')
        setPassword('')
        setConfirm('')
      }
    } catch (err) {
      setError(err instanceof ApiError ? err.message : '网络错误，请稍后重试')
    } finally {
      setBusy(false)
    }
  }

  return (
    <main className="flex min-h-full items-center justify-center p-6">
      <div className="w-full max-w-sm">
        {/* 品牌字：终端协议风格的 wordmark */}
        <h1 className="mb-1 text-center font-mono text-4xl tracking-tight">
          <span className="text-copper">nas</span>
          <span className="text-ink-faint">://</span>
          <span className="prompt-cursor ml-1" aria-hidden="true" />
        </h1>
        <p className="mb-8 text-center text-sm text-ink-muted">你自己的那块盘</p>

        <div className="rounded-lg border border-line bg-panel p-6">
          <div className="mb-5 grid grid-cols-2 gap-1 rounded bg-panel-2 p-1 font-mono text-sm" role="tablist">
            {(['login', 'register'] as const).map((m) => (
              <button
                key={m}
                type="button"
                role="tab"
                aria-selected={mode === m}
                onClick={() => switchMode(m)}
                className={`rounded py-1.5 transition-colors cursor-pointer ${
                  mode === m ? 'bg-copper text-bg' : 'text-ink-muted hover:text-ink'
                }`}
              >
                {m === 'login' ? '登录' : '注册'}
              </button>
            ))}
          </div>

          <form onSubmit={(e) => void submit(e)} className="space-y-4">
            <TextField
              label="邮箱"
              type="email"
              autoComplete="email"
              required
              value={email}
              onChange={(e) => setEmail(e.target.value)}
            />
            {mode === 'register' && (
              <TextField
                label="昵称（可选）"
                type="text"
                autoComplete="nickname"
                maxLength={50}
                value={fullName}
                onChange={(e) => setFullName(e.target.value)}
              />
            )}
            <TextField
              label="密码"
              type="password"
              autoComplete={mode === 'login' ? 'current-password' : 'new-password'}
              required
              minLength={8}
              maxLength={128}
              value={password}
              onChange={(e) => setPassword(e.target.value)}
            />
            {mode === 'register' && (
              <TextField
                label="确认密码"
                type="password"
                autoComplete="new-password"
                required
                minLength={8}
                maxLength={128}
                value={confirm}
                onChange={(e) => setConfirm(e.target.value)}
              />
            )}

            {error && <p className="text-sm text-danger" role="alert">{error}</p>}
            {notice && <p className="text-sm text-ok" role="status">{notice}</p>}

            <Button type="submit" variant="primary" className="w-full py-2" disabled={busy}>
              {busy ? <Spinner /> : mode === 'login' ? '登录' : '创建账号'}
            </Button>
          </form>
        </div>

        <p className="mt-4 text-center text-xs text-ink-faint">
          密码至少 8 位！
        </p>
      </div>
    </main>
  )
}
