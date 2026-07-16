// src/components/DirectoryPicker.tsx
// 目录选择对话框：浏览用户目录树，选定一个目标目录。用于「移动到」「复制到」。
// 仅展示目录（文件不可选）；可逐级进入子目录，也可回到上级；确认即返回所选目录的相对路径。

import { useCallback, useEffect, useState } from 'react'
import { listDir } from '../api/files'
import { ApiError } from '../api/http'
import { splitPath } from '../lib/format'
import { Button, Dialog, Spinner } from './ui'

export function DirectoryPicker({
  open,
  title,
  initialPath,
  confirmLabel = '选择此目录',
  onConfirm,
  onClose,
}: {
  open: boolean
  title: string
  initialPath: string
  confirmLabel?: string
  onConfirm: (path: string) => void
  onClose: () => void
}) {
  const [cwd, setCwd] = useState(initialPath)
  const [dirs, setDirs] = useState<{ name: string; path: string }[]>([])
  const [loading, setLoading] = useState(false)
  const [error, setError] = useState<string | null>(null)

  const load = useCallback((path: string) => {
    setLoading(true)
    setError(null)
    listDir(path)
      .then((res) => {
        setDirs(res.entries.filter((e) => e.isDirectory).map((e) => ({ name: e.name, path: e.path })))
        setCwd(res.path)
      })
      .catch((err) => setError(err instanceof ApiError ? err.message : '加载目录失败'))
      .finally(() => setLoading(false))
  }, [])

  useEffect(() => {
    if (open) {
      setCwd(initialPath)
      load(initialPath)
    }
  }, [open, initialPath, load])

  const goUp = () => {
    const parts = splitPath(cwd)
    parts.pop()
    load(parts.join('/'))
  }

  const display = cwd.length === 0 ? '~' : `~/${cwd}`

  return (
    <Dialog
      title={title}
      open={open}
      onClose={onClose}
      footer={
        <>
          <Button onClick={onClose}>取消</Button>
          <Button variant="primary" onClick={() => onConfirm(cwd)}>
            {confirmLabel}
          </Button>
        </>
      }
    >
      <div className="mb-2 font-mono text-sm text-copper-bright">{display}</div>

      <div className="max-h-72 overflow-auto rounded border border-line">
        <button
          type="button"
          disabled={cwd.length === 0}
          onClick={goUp}
          className="block w-full cursor-pointer px-3 py-2 text-left font-mono text-sm text-ink-muted hover:bg-panel-2 hover:text-copper-bright disabled:cursor-not-allowed disabled:opacity-40"
        >
          ../（上级目录）
        </button>

        {loading && (
          <div className="flex justify-center py-6">
            <Spinner />
          </div>
        )}

        {!loading && !error && dirs.length === 0 && (
          <div className="px-3 py-4 text-center text-xs text-ink-faint">（此目录下没有子目录）</div>
        )}

        {!loading &&
          dirs.map((d) => (
            <button
              key={d.path}
              type="button"
              onClick={() => load(d.path)}
              className="block w-full cursor-pointer px-3 py-2 text-left font-mono text-sm text-copper hover:bg-panel-2 hover:text-copper-bright"
            >
              {d.name}/
            </button>
          ))}
      </div>

      {error && (
        <p className="mt-2 text-xs text-danger" role="alert">
          {error}
        </p>
      )}
    </Dialog>
  )
}
