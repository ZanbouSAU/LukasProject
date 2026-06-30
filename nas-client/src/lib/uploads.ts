// src/lib/uploads.ts
// 上传队列：
//  - 文件夹上传 = 逐文件 PUT（保留 webkitRelativePath 的目录结构），流式、低内存、逐文件进度；
//  - Zip 上传 = POST /upload-zip，由服务端解压；
//  - 并发上限 3，支持取消、失败后「覆盖重传」（应对 409 同名冲突）。

import { useEffect, useRef, useState } from 'react'
import { uploadFile, uploadZip } from '../api/files'
import type { UploadHandle } from '../api/files'
import { ApiError } from '../api/http'
import { joinPath } from './format'

export type UploadStatus = 'queued' | 'running' | 'done' | 'error' | 'cancelled'

export interface UploadTask {
  id: number
  /** 展示给用户的目标相对路径 */
  label: string
  kind: 'file' | 'zip'
  status: UploadStatus
  sent: number
  total: number
  /** 失败原因（status === 'error' 时） */
  error?: string
  /** 是否是同名冲突（可一键覆盖重传） */
  conflict?: boolean
}

interface InternalTask extends UploadTask {
  blob: Blob
  destPath: string
  overwrite: boolean
  handle?: UploadHandle
}

const CONCURRENCY = 3

export function useUploads(onUploaded: () => void) {
  const [tasks, setTasks] = useState<UploadTask[]>([])
  const internals = useRef(new Map<number, InternalTask>())
  const nextId = useRef(1)
  const runningCount = useRef(0)
  const onUploadedRef = useRef<() => void>(() => {})

  useEffect(() => {
    onUploadedRef.current = onUploaded
  }, [onUploaded])

  function sync(): void {
    setTasks(
      [...internals.current.values()].map(
        ({ id, label, kind, status, sent, total, error, conflict }) => ({
          id, label, kind, status, sent, total, error, conflict,
        }),
      ),
    )
  }

  function pump(): void {
    while (runningCount.current < CONCURRENCY) {
      const next = [...internals.current.values()].find((t) => t.status === 'queued')
      if (!next) return

      runningCount.current++
      next.status = 'running'
      const start = next.kind === 'zip' ? uploadZip : uploadFile
      next.handle = start(next.destPath, next.blob, next.overwrite, (sent, total) => {
        next.sent = sent
        next.total = total
        sync()
      })

      void next.handle.promise
        .then(() => {
          next.status = 'done'
          next.sent = next.total
          onUploadedRef.current()
        })
        .catch((err: unknown) => {
          if (next.status === 'cancelled') return
          next.status = 'error'
          if (err instanceof ApiError) {
            next.error = err.message
            next.conflict = err.status === 409
          } else {
            next.error = '上传失败'
          }
        })
        .finally(() => {
          runningCount.current--
          sync()
          pump()
        })
    }
    sync()
  }

  function enqueue(
    items: Array<{ blob: Blob; destPath: string; kind: 'file' | 'zip' }>,
    overwrite: boolean,
  ): void {
    for (const item of items) {
      const id = nextId.current++
      internals.current.set(id, {
        id,
        label: item.destPath,
        kind: item.kind,
        status: 'queued',
        sent: 0,
        total: item.blob.size,
        blob: item.blob,
        destPath: item.destPath,
        overwrite,
      })
    }
    pump()
  }

  /** 上传若干文件到 cwd；来自文件夹选择器的文件会保留其相对目录结构。 */
  function addFiles(cwd: string, files: FileList | File[]): void {
    const items = [...files].map((f) => {
      const rel = (f as File & { webkitRelativePath?: string }).webkitRelativePath
      return {
        blob: f as Blob,
        destPath: joinPath(cwd, rel && rel.length > 0 ? rel : f.name),
        kind: 'file' as const,
      }
    })
    enqueue(items, false)
  }

  /** 上传 zip 并由服务端解压到 cwd。 */
  function addZip(cwd: string, zip: File): void {
    enqueue([{ blob: zip, destPath: cwd, kind: 'zip' }], false)
  }

  function cancel(id: number): void {
    const t = internals.current.get(id)
    if (!t) return
    if (t.status === 'running') {
      t.status = 'cancelled'
      t.handle?.abort()
    } else if (t.status === 'queued') {
      t.status = 'cancelled'
    }
    sync()
  }

  /** 同名冲突后，以 overwrite=true 重新入队。 */
  function retryOverwrite(id: number): void {
    const t = internals.current.get(id)
    if (!t || t.status !== 'error') return
    t.status = 'queued'
    t.sent = 0
    t.error = undefined
    t.conflict = false
    t.overwrite = true
    pump()
  }

  function clearFinished(): void {
    for (const [id, t] of internals.current) {
      if (t.status === 'done' || t.status === 'cancelled') internals.current.delete(id)
    }
    sync()
  }

  return { tasks, addFiles, addZip, cancel, retryOverwrite, clearFinished }
}
