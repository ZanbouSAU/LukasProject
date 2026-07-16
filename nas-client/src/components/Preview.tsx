// src/components/Preview.tsx
// 在线预览 / 文本编辑的全屏模态。
//  - image/video/audio：用票据换内联直链，喂给原生标签（视频音频支持拖动，因后端支持 Range）。
//  - text：读文本内容到 <textarea>，可编辑后覆盖保存。

import { useCallback, useEffect, useRef, useState } from 'react'
import { getPreviewUrl, readText, saveText } from '../api/files'
import { ApiError } from '../api/http'
import type { FileEntry } from '../api/types'
import type { PreviewKind } from '../lib/format'
import { formatSize } from '../lib/format'
import { Button, Spinner } from './ui'
import * as React from "react";

interface PreviewProps {
  entry: FileEntry
  kind: Exclude<PreviewKind, 'none'>
  onClose: () => void
  /** 文本保存成功后通知外部刷新列表（大小/时间会变） */
  onSaved: () => void
}

export function Preview({ entry, kind, onClose, onSaved }: PreviewProps) {
  // Esc 关闭
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') onClose()
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [onClose])

  return (
    <div
      className="fixed inset-0 z-50 flex flex-col bg-black/80 backdrop-blur-sm"
      role="dialog"
      aria-modal="true"
      aria-label={`预览 ${entry.name}`}
    >
      <header className="flex items-center justify-between border-b border-line bg-panel px-5 py-3">
        <span className="truncate font-mono text-sm text-copper" title={entry.path}>
          {entry.name}
        </span>
        <button
          type="button"
          onClick={onClose}
          aria-label="关闭"
          className="ml-4 shrink-0 cursor-pointer rounded px-2 py-1 font-mono text-ink-muted hover:text-ink"
        >
          ✕ 关闭
        </button>
      </header>

      <div className="flex flex-1 items-stretch justify-center overflow-auto p-4">
        {kind === 'image' && <ImageView entry={entry} />}
        {kind === 'video' && <VideoView entry={entry} />}
        {kind === 'audio' && <AudioView entry={entry} />}
        {kind === 'text' && <TextEditor entry={entry} onSaved={onSaved} />}
      </div>
    </div>
  )
}

// ---------------------------------------------------------------- 媒体直链 Hook
function useMediaUrl(path: string) {
  const [url, setUrl] = useState<string | null>(null)
  const [error, setError] = useState<string | null>(null)

  useEffect(() => {
    let cancelled = false
    getPreviewUrl(path)
      .then((u) => {
        if (!cancelled) setUrl(u)
      })
      .catch((err: unknown) => {
        if (!cancelled) setError(err instanceof ApiError ? err.message : '无法加载预览')
      })
    return () => {
      cancelled = true
    }
  }, [path])

  return { url, error }
}

function CenterMessage({ children }: { children: React.ReactNode }) {
  return <div className="flex items-center gap-2 self-center text-sm text-ink-muted">{children}</div>
}

function ImageView({ entry }: { entry: FileEntry }) {
  const { url, error } = useMediaUrl(entry.path)
  if (error) return <CenterMessage>{error}</CenterMessage>
  if (!url) return <CenterMessage><Spinner /> 加载中…</CenterMessage>
  return (
    <img
      src={url}
      alt={entry.name}
      className="m-auto max-h-full max-w-full object-contain"
    />
  )
}

function VideoView({ entry }: { entry: FileEntry }) {
  const { url, error } = useMediaUrl(entry.path)
  if (error) return <CenterMessage>{error}</CenterMessage>
  if (!url) return <CenterMessage><Spinner /> 加载中…</CenterMessage>
  return (
    <video src={url} controls autoPlay className="m-auto max-h-full max-w-full" />
  )
}

function AudioView({ entry }: { entry: FileEntry }) {
  const { url, error } = useMediaUrl(entry.path)
  if (error) return <CenterMessage>{error}</CenterMessage>
  if (!url) return <CenterMessage><Spinner /> 加载中…</CenterMessage>
  return (
    <div className="m-auto w-full max-w-lg rounded-lg border border-line bg-panel p-6">
      <p className="mb-4 truncate text-center font-mono text-sm text-ink-muted">{entry.name}</p>
      <audio src={url} controls autoPlay className="w-full" />
    </div>
  )
}

// ---------------------------------------------------------------- 文本编辑器
function TextEditor({ entry, onSaved }: { entry: FileEntry; onSaved: () => void }) {
  const [original, setOriginal] = useState<string | null>(null)
  const [value, setValue] = useState('')
  const [loadError, setLoadError] = useState<string | null>(null)
  const [saving, setSaving] = useState(false)
  const [saveError, setSaveError] = useState<string | null>(null)
  const [savedAt, setSavedAt] = useState<number | null>(null)
  const taRef = useRef<HTMLTextAreaElement>(null)

  useEffect(() => {
    let cancelled = false
    readText(entry.path)
      .then((res) => {
        if (cancelled) return
        setOriginal(res.content)
        setValue(res.content)
      })
      .catch((err: unknown) => {
        if (!cancelled) setLoadError(err instanceof ApiError ? err.message : '无法读取文件')
      })
    return () => {
      cancelled = true
    }
  }, [entry.path])

  const dirty = original !== null && value !== original

  const save = useCallback(async () => {
    setSaving(true)
    setSaveError(null)
    try {
      await saveText(entry.path, value)
      setOriginal(value)
      setSavedAt(Date.now())
      onSaved()
    } catch (err) {
      setSaveError(err instanceof ApiError ? err.message : '保存失败')
    } finally {
      setSaving(false)
    }
  }, [entry.path, value, onSaved])

  // Ctrl/Cmd+S 保存
  useEffect(() => {
    const onKey = (e: KeyboardEvent) => {
      if ((e.ctrlKey || e.metaKey) && e.key.toLowerCase() === 's') {
        e.preventDefault()
        if (dirty && !saving) void save()
      }
    }
    document.addEventListener('keydown', onKey)
    return () => document.removeEventListener('keydown', onKey)
  }, [dirty, saving, save])

  if (loadError) return <CenterMessage>{loadError}</CenterMessage>
  if (original === null) return <CenterMessage><Spinner /> 读取中…</CenterMessage>

  return (
    <div className="flex h-full w-full max-w-5xl flex-col gap-3 self-stretch">
      <textarea
        ref={taRef}
        value={value}
        onChange={(e) => {
          setValue(e.target.value)
          if (savedAt !== null) setSavedAt(null)
        }}
        spellCheck={false}
        className="min-h-0 flex-1 resize-none rounded-lg border border-line bg-panel-2 p-4 font-mono text-sm text-ink focus:border-copper focus:outline-none"
        // 等宽字体下用 Tab 缩进
        onKeyDown={(e) => {
          if (e.key === 'Tab') {
            e.preventDefault()
            const ta = e.currentTarget
            const start = ta.selectionStart
            const end = ta.selectionEnd
            const next = value.slice(0, start) + '  ' + value.slice(end)
            setValue(next)
            requestAnimationFrame(() => {
              ta.selectionStart = ta.selectionEnd = start + 2
            })
          }
        }}
      />
      <div className="flex items-center justify-between gap-3 text-xs">
        <span className="font-mono text-ink-muted">
          {formatSize(new Blob([value]).size)}
          {dirty && <span className="ml-2 text-copper">● 未保存</span>}
          {!dirty && savedAt !== null && <span className="ml-2 text-ok">已保存</span>}
        </span>
        <div className="flex items-center gap-3">
          {saveError && <span className="text-danger">{saveError}</span>}
          <Button
            variant="primary"
            disabled={!dirty || saving}
            onClick={() => void save()}
          >
            {saving ? <Spinner /> : '保存 (Ctrl+S)'}
          </Button>
        </div>
      </div>
    </div>
  )
}
