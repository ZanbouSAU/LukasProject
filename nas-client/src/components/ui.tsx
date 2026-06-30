// src/components/ui.tsx
// 少量克制的 UI 原语。视觉令牌见 src/index.css。

import { useEffect, useRef, useState } from 'react'
import type { ButtonHTMLAttributes, ReactNode } from 'react'
import * as React from "react";

type ButtonVariant = 'primary' | 'ghost' | 'danger'

const variantClass: Record<ButtonVariant, string> = {
  primary:
      'bg-copper text-bg font-medium hover:bg-copper-bright disabled:opacity-50 disabled:hover:bg-copper',
  ghost:
      'border border-line text-ink hover:border-copper hover:text-copper-bright disabled:opacity-50',
  danger:
      'border border-line text-danger hover:border-danger disabled:opacity-50',
}

export function Button({
                         variant = 'ghost',
                         className = '',
                         ...rest
                       }: ButtonHTMLAttributes<HTMLButtonElement> & { variant?: ButtonVariant }) {
  return (
      <button
          type="button"
          className={`rounded px-3 py-1.5 text-sm transition-colors cursor-pointer disabled:cursor-not-allowed ${variantClass[variant]} ${className}`}
          {...rest}
      />
  )
}

export function Spinner({ className = '' }: { className?: string }) {
  return (
      <span
          role="status"
          aria-label="加载中"
          className={`inline-block size-4 animate-spin rounded-full border-2 border-line border-t-copper ${className}`}
      />
  )
}

export interface MenuItem {
  label: string
  onSelect: () => void
  title?: string
}

/**
 * 轻量下拉菜单：一个触发按钮 + 一组操作项。点击外部或按 Esc 自动关闭。
 * 用于在窄屏把不常用的操作收纳起来，避免按钮行溢出。
 */
export function Menu({ label, items }: { label: ReactNode; items: MenuItem[] }) {
  const [open, setOpen] = useState(false)
  const rootRef = useRef<HTMLDivElement>(null)

  useEffect(() => {
    if (!open) return
    const onPointerDown = (e: PointerEvent) => {
      if (rootRef.current && !rootRef.current.contains(e.target as Node)) setOpen(false)
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setOpen(false)
    }
    document.addEventListener('pointerdown', onPointerDown)
    document.addEventListener('keydown', onKey)
    return () => {
      document.removeEventListener('pointerdown', onPointerDown)
      document.removeEventListener('keydown', onKey)
    }
  }, [open])

  return (
      <div ref={rootRef} className="relative">
        <button
            type="button"
            aria-haspopup="menu"
            aria-expanded={open}
            onClick={() => setOpen((v) => !v)}
            className="cursor-pointer rounded border border-line px-3 py-1.5 text-sm text-ink transition-colors hover:border-copper hover:text-copper-bright"
        >
          {label}
        </button>
        {open && (
            <div
                role="menu"
                className="absolute right-0 z-20 mt-1 min-w-44 overflow-hidden rounded-lg border border-line bg-panel shadow-lg"
            >
              {items.map((item) => (
                  <button
                      key={item.label}
                      type="button"
                      role="menuitem"
                      title={item.title}
                      onClick={() => {
                        setOpen(false)
                        item.onSelect()
                      }}
                      className="block w-full cursor-pointer px-4 py-2.5 text-left text-sm text-ink transition-colors hover:bg-panel-2 hover:text-copper-bright"
                  >
                    {item.label}
                  </button>
              ))}
            </div>
        )}
      </div>
  )
}

export function Dialog({
                         title,
                         open,
                         onClose,
                         children,
                         footer,
                       }: {
  title: string
  open: boolean
  onClose: () => void
  children: ReactNode
  footer?: ReactNode
}) {
  const ref = useRef<HTMLDialogElement>(null)

  useEffect(() => {
    const dialog = ref.current
    if (!dialog) return
    if (open && !dialog.open) dialog.showModal()
    if (!open && dialog.open) dialog.close()
  }, [open])

  return (
      <dialog
          ref={ref}
          onCancel={(e) => {
            e.preventDefault()
            onClose()
          }}
          onClick={(e) => {
            // 点击遮罩区域关闭
            if (e.target === ref.current) onClose()
          }}
          className="m-auto w-104 max-w-[calc(100vw-2rem)] rounded-lg border border-line bg-panel text-ink shadow-2xl backdrop:bg-black/60"
      >
        <div className="border-b border-line px-5 py-3 font-mono text-sm text-copper">{title}</div>
        <div className="px-5 py-4 text-sm">{children}</div>
        {footer && <div className="flex justify-end gap-2 border-t border-line px-5 py-3">{footer}</div>}
      </dialog>
  )
}

export function TextField({
                            label,
                            ...rest
                          }: React.InputHTMLAttributes<HTMLInputElement> & { label: string }) {
  return (
      <label className="block">
        <span className="mb-1 block text-xs text-ink-muted">{label}</span>
        <input
            className="w-full rounded border border-line bg-panel-2 px-3 py-2 text-sm text-ink placeholder:text-ink-faint focus:border-copper focus:outline-none"
            {...rest}
        />
      </label>
  )
}
