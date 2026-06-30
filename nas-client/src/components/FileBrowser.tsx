// src/components/FileBrowser.tsx
// 主界面。签名元素是 shell 提示符样式的路径栏（user@nas:~/path $），同时承担面包屑导航。

import { useCallback, useEffect, useRef, useState } from 'react'
import { copyEntry, deleteEntry, download, listDir, mkdir, moveEntry, newFile } from '../api/files'
import { ApiError } from '../api/http'
import type { FileEntry } from '../api/types'
import { useAuth } from '../auth/AuthContext'
import { formatSize, formatTime, joinPath, previewKindOf, splitPath, validateNameSegment } from '../lib/format'
import { useUploads } from '../lib/uploads'
import type { UploadTask } from '../lib/uploads'
import { Preview } from './Preview'
import { DirectoryPicker } from './DirectoryPicker'
import { Button, Dialog, Menu, Spinner, TextField } from './ui'
import * as React from "react";

// 覆盖确认弹窗点「取消」时抛出的哨兵，调用方据此静默中止。
const CANCELLED = Symbol('cancelled')

// ---------------------------------------------------------------- 顶栏
function TopBar() {
    const { email, logout, logoutAll } = useAuth()
    const [menuOpen, setMenuOpen] = useState(false)
    const menuRef = useRef<HTMLDivElement>(null)

    useEffect(() => {
        if (!menuOpen) return
        const onDown = (e: MouseEvent) => {
            if (!menuRef.current?.contains(e.target as Node)) setMenuOpen(false)
        }
        document.addEventListener('mousedown', onDown)
        return () => document.removeEventListener('mousedown', onDown)
    }, [menuOpen])

    return (
        <header className="flex items-center justify-between border-b border-line bg-panel px-5 py-3">
      <span className="font-mono text-lg">
        <span className="text-copper">nas</span>
        <span className="text-ink-faint">://</span>
      </span>
            <div className="relative" ref={menuRef}>
                <button
                    type="button"
                    onClick={() => setMenuOpen((v) => !v)}
                    aria-haspopup="menu"
                    aria-expanded={menuOpen}
                    className="cursor-pointer font-mono text-sm text-ink-muted transition-colors hover:text-ink"
                >
                    {email} ▾
                </button>
                {menuOpen && (
                    <div
                        role="menu"
                        className="absolute right-0 z-20 mt-2 w-44 overflow-hidden rounded border border-line bg-panel-2 text-sm shadow-xl"
                    >
                        <button
                            type="button"
                            role="menuitem"
                            onClick={() => void logout()}
                            className="block w-full cursor-pointer px-4 py-2 text-left hover:bg-line/40"
                        >
                            登出本设备
                        </button>
                        <button
                            type="button"
                            role="menuitem"
                            onClick={() => {
                                logoutAll().catch(() => window.alert('吊销失败，请检查网络后重试'))
                            }}
                            className="block w-full cursor-pointer px-4 py-2 text-left text-danger hover:bg-line/40"
                        >
                            登出所有设备
                        </button>
                    </div>
                )}
            </div>
        </header>
    )
}

// ---------------------------------------------------------------- 路径提示符
function PathPrompt({ path, onNavigate }: { path: string; onNavigate: (p: string) => void }) {
    const { email } = useAuth()
    const user = email?.split('@')[0] ?? 'guest'
    const segments = splitPath(path)
    return (
        <nav aria-label="当前路径" className="flex min-w-0 items-center gap-0 overflow-x-auto font-mono text-sm">
            <span className="shrink-0 text-ink-faint">{user}@nas:</span>
            <button
                type="button"
                onClick={() => onNavigate('')}
                className="shrink-0 cursor-pointer text-copper hover:text-copper-bright"
                aria-label="回到根目录"
            >
                ~
            </button>
            {segments.map((seg, i) => {
                const target = segments.slice(0, i + 1).join('/')
                return (
                    <span key={target} className="flex shrink-0 items-center">
            <span className="text-ink-faint">/</span>
            <button
                type="button"
                onClick={() => onNavigate(target)}
                className="cursor-pointer text-copper hover:text-copper-bright"
            >
              {seg}
            </button>
          </span>
                )
            })}
            <span className="ml-2 shrink-0 text-ink-faint">$</span>
            <span className="prompt-cursor ml-1.5 shrink-0" aria-hidden="true" />
        </nav>
    )
}

// ---------------------------------------------------------------- 上传托盘
function UploadTray({
                        tasks,
                        onCancel,
                        onRetryOverwrite,
                        onClear,
                    }: {
    tasks: UploadTask[]
    onCancel: (id: number) => void
    onRetryOverwrite: (id: number) => void
    onClear: () => void
}) {
    if (tasks.length === 0) return null
    const active = tasks.filter((t) => t.status === 'running' || t.status === 'queued').length
    return (
        <aside
            aria-label="上传任务"
            className="fixed right-4 bottom-4 z-30 w-96 max-w-[calc(100vw-2rem)] overflow-hidden rounded-lg border border-line bg-panel shadow-2xl"
        >
            <div className="flex items-center justify-between border-b border-line px-4 py-2 font-mono text-xs text-ink-muted">
                <span>上传 {active > 0 ? `· ${active} 进行中` : '· 已完成'}</span>
                <button type="button" onClick={onClear} className="cursor-pointer hover:text-ink">
                    清除已完成
                </button>
            </div>
            <ul className="max-h-64 overflow-y-auto">
                {tasks.map((t) => {
                    const pct = t.total > 0 ? Math.floor((t.sent / t.total) * 100) : 0
                    return (
                        <li key={t.id} className="border-b border-line/50 px-4 py-2 last:border-b-0">
                            <div className="flex items-center justify-between gap-2 text-xs">
                <span className="truncate font-mono" title={t.label}>
                  {t.kind === 'zip' && <span className="text-copper">[zip] </span>}
                    {t.label || '~'}
                </span>
                                <span className="shrink-0 font-mono text-ink-muted">
                  {t.status === 'running' && `${pct}%`}
                                    {t.status === 'queued' && '排队中'}
                                    {t.status === 'done' && <span className="text-ok">完成</span>}
                                    {t.status === 'cancelled' && '已取消'}
                                    {t.status === 'error' && <span className="text-danger">失败</span>}
                </span>
                            </div>
                            {(t.status === 'running' || t.status === 'queued') && (
                                <div className="mt-1.5 flex items-center gap-2">
                                    <div className="h-1 flex-1 overflow-hidden rounded bg-panel-2">
                                        <div className="h-full bg-copper transition-[width]" style={{ width: `${pct}%` }} />
                                    </div>
                                    <button
                                        type="button"
                                        onClick={() => onCancel(t.id)}
                                        className="cursor-pointer font-mono text-xs text-ink-muted hover:text-danger"
                                    >
                                        取消
                                    </button>
                                </div>
                            )}
                            {t.status === 'error' && (
                                <div className="mt-1 flex items-center justify-between gap-2 text-xs text-danger">
                                    <span className="truncate" title={t.error}>{t.error}</span>
                                    {t.conflict && (
                                        <button
                                            type="button"
                                            onClick={() => onRetryOverwrite(t.id)}
                                            className="shrink-0 cursor-pointer text-copper hover:text-copper-bright"
                                        >
                                            覆盖重传
                                        </button>
                                    )}
                                </div>
                            )}
                        </li>
                    )
                })}
            </ul>
        </aside>
    )
}

// ---------------------------------------------------------------- 主组件
export function FileBrowser() {
    const [cwd, setCwd] = useState('')
    // 结果按「它属于哪个路径」键控；loading 由 result.path !== cwd 派生，避免在 effect 里同步 setState。
    const [result, setResult] = useState<
        | { path: string; entries: FileEntry[]; error: null }
        | { path: string; entries: null; error: string }
        | null
    >(null)
    const loading = result === null || result.path !== cwd
    const entries = !loading && result.entries ? result.entries : []
    const loadError = !loading ? result.error : null
    const [toast, setToast] = useState<string | null>(null)
    const toastTimer = useRef<ReturnType<typeof setTimeout> | null>(null)

    const [mkdirOpen, setMkdirOpen] = useState(false)
    const [mkdirName, setMkdirName] = useState('')
    const [mkdirError, setMkdirError] = useState<string | null>(null)
    const [mkdirBusy, setMkdirBusy] = useState(false)

    const [newFileOpen, setNewFileOpen] = useState(false)
    const [newFileName, setNewFileName] = useState('')
    const [newFileError, setNewFileError] = useState<string | null>(null)
    const [newFileBusy, setNewFileBusy] = useState(false)

    const [renameTarget, setRenameTarget] = useState<FileEntry | null>(null)
    const [renameName, setRenameName] = useState('')
    const [renameError, setRenameError] = useState<string | null>(null)
    const [renameBusy, setRenameBusy] = useState(false)

    const [transfer, setTransfer] = useState<{ entry: FileEntry; mode: 'move' | 'copy' } | null>(null)

    const [deleteTarget, setDeleteTarget] = useState<FileEntry | null>(null)
    const [deleteRecursive, setDeleteRecursive] = useState(false)
    const [deleteError, setDeleteError] = useState<string | null>(null)
    const [deleteBusy, setDeleteBusy] = useState(false)

    const [downloading, setDownloading] = useState<string | null>(null)
    const [dragOver, setDragOver] = useState(false)
    const [preview, setPreview] = useState<{ entry: FileEntry; kind: 'image' | 'video' | 'audio' | 'text' } | null>(null)

    const fileInput = useRef<HTMLInputElement>(null)
    const folderInput = useRef<HTMLInputElement>(null)
    const zipInput = useRef<HTMLInputElement>(null)

    const showToast = useCallback((message: string) => {
        setToast(message)
        if (toastTimer.current) clearTimeout(toastTimer.current)
        toastTimer.current = setTimeout(() => setToast(null), 3500)
    }, [])

    const refresh = useCallback((path: string, signal?: AbortSignal): void => {
        listDir(path, signal)
            .then((res) => setResult({ path, entries: res.entries, error: null }))
            .catch((err: unknown) => {
                if (err instanceof DOMException && err.name === 'AbortError') return
                setResult({
                    path,
                    entries: null,
                    error: err instanceof ApiError ? err.message : '加载失败，请检查网络',
                })
            })
    }, [])

    useEffect(() => {
        const ctrl = new AbortController()
        refresh(cwd, ctrl.signal)
        return () => ctrl.abort()
    }, [cwd, refresh])

    const refreshCwd = useCallback(() => refresh(cwd), [cwd, refresh])

    const uploads = useUploads(refreshCwd)

    // ---------------- 操作 ----------------
    const submitMkdir = async () => {
        const invalid = validateNameSegment(mkdirName)
        if (invalid) {
            setMkdirError(invalid)
            return
        }
        setMkdirBusy(true)
        setMkdirError(null)
        try {
            await mkdir(joinPath(cwd, mkdirName.trim()))
            setMkdirOpen(false)
            setMkdirName('')
            showToast('目录已创建')
            refreshCwd()
        } catch (err) {
            setMkdirError(err instanceof ApiError ? err.message : '创建失败')
        } finally {
            setMkdirBusy(false)
        }
    }

    const submitDelete = async () => {
        if (!deleteTarget) return
        setDeleteBusy(true)
        setDeleteError(null)
        try {
            const res = await deleteEntry(deleteTarget.path, deleteTarget.isDirectory && deleteRecursive)
            setDeleteTarget(null)
            showToast(res.message)
            refreshCwd()
        } catch (err) {
            setDeleteError(err instanceof ApiError ? err.message : '删除失败')
        } finally {
            setDeleteBusy(false)
        }
    }

    const submitNewFile = async () => {
        const invalid = validateNameSegment(newFileName)
        if (invalid) { setNewFileError(invalid); return }
        setNewFileBusy(true); setNewFileError(null)
        try {
            await newFile(joinPath(cwd, newFileName.trim()))
            setNewFileOpen(false); setNewFileName(''); showToast('文件已创建'); refreshCwd()
        } catch (err) {
            setNewFileError(err instanceof ApiError ? err.message : '创建失败')
        } finally { setNewFileBusy(false) }
    }

    const submitRename = async () => {
        if (!renameTarget) return
        const invalid = validateNameSegment(renameName)
        if (invalid) { setRenameError(invalid); return }
        const parent = splitPath(renameTarget.path).slice(0, -1).join('/')
        const dest = joinPath(parent, renameName.trim())
        if (dest === renameTarget.path) { setRenameTarget(null); return }
        setRenameBusy(true); setRenameError(null)
        try {
            await moveWithOverwritePrompt(renameTarget.path, dest)
            setRenameTarget(null); setRenameName(''); showToast('已重命名'); refreshCwd()
        } catch (err) {
            if (err !== CANCELLED) setRenameError(err instanceof ApiError ? err.message : '重命名失败')
        } finally { setRenameBusy(false) }
    }

    const submitTransfer = async (destDir: string) => {
        if (!transfer) return
        const { entry, mode } = transfer
        const dest = joinPath(destDir, entry.name)
        setTransfer(null)
        if (dest === entry.path) { showToast('源与目标目录相同'); return }
        try {
            if (mode === 'move') { await moveWithOverwritePrompt(entry.path, dest); showToast('已移动') }
            else { await copyWithOverwritePrompt(entry.path, dest, entry.isDirectory); showToast('已复制') }
            refreshCwd()
        } catch (err) {
            if (err !== CANCELLED) showToast(err instanceof ApiError ? err.message : (mode === 'move' ? '移动失败' : '复制失败'))
        }
    }

    const moveWithOverwritePrompt = async (source: string, dest: string) => {
        try { await moveEntry(source, dest, false) }
        catch (err) {
            if (err instanceof ApiError && err.status === 409) {
                if (!window.confirm(`目标已存在：~/${dest}\n是否覆盖？`)) throw CANCELLED
                await moveEntry(source, dest, true)
            } else { throw err }
        }
    }

    const copyWithOverwritePrompt = async (source: string, dest: string, isDir: boolean) => {
        try { await copyEntry(source, dest, false) }
        catch (err) {
            if (err instanceof ApiError && err.status === 409) {
                const msg = isDir
                    ? `目标目录下存在同名文件：~/${dest}\n是否覆盖已存在的文件？`
                    : `目标已存在：~/${dest}\n是否覆盖？`
                if (!window.confirm(msg)) throw CANCELLED
                await copyEntry(source, dest, true)
            } else { throw err }
        }
    }

    const openPreview = (entry: FileEntry) => {
        const kind = previewKindOf(entry.name)
        if (kind !== 'none') setPreview({ entry, kind })
    }

    const startDownload = async (entry: FileEntry) => {
        setDownloading(entry.path)
        try {
            await download(entry.path)
        } catch (err) {
            showToast(err instanceof ApiError ? err.message : '下载失败')
        } finally {
            setDownloading(null)
        }
    }

    const onDrop = (e: React.DragEvent) => {
        e.preventDefault()
        setDragOver(false)
        if (e.dataTransfer.files.length > 0) uploads.addFiles(cwd, e.dataTransfer.files)
    }

    // ---------------- 渲染 ----------------
    return (
        <div className="flex min-h-full flex-col">
            <TopBar />

            {/* 路径栏 + 工具按钮 */}
            <div className="flex flex-wrap items-center gap-3 border-b border-line bg-panel/60 px-5 py-3">
                <div className="min-w-0 flex-1">
                    <PathPrompt path={cwd} onNavigate={setCwd} />
                </div>
                <div className="flex shrink-0 gap-2">
                    <Button onClick={() => fileInput.current?.click()}>上传文件</Button>
                    <Menu
                        label="更多"
                        items={[
                            {
                                label: '新建文件',
                                onSelect: () => { setNewFileName(''); setNewFileError(null); setNewFileOpen(true) },
                            },
                            {
                                label: '新建目录',
                                onSelect: () => { setMkdirName(''); setMkdirError(null); setMkdirOpen(true) },
                            },
                            {
                                label: '上传文件夹',
                                onSelect: () => folderInput.current?.click(),
                            },
                            {
                                label: '上传 Zip 解压',
                                title: '上传 zip 包并在服务端解压到当前目录',
                                onSelect: () => zipInput.current?.click(),
                            },
                        ]}
                    />
                </div>
            </div>

            {/* 隐藏的文件选择器 */}
            <input
                ref={fileInput}
                type="file"
                multiple
                hidden
                onChange={(e) => {
                    if (e.target.files?.length) uploads.addFiles(cwd, e.target.files)
                    e.target.value = ''
                }}
            />
            <input
                ref={folderInput}
                type="file"
                hidden
                // @ts-expect-error 非标准但被主流浏览器支持的目录选择属性
                webkitdirectory=""
                onChange={(e) => {
                    if (e.target.files?.length) uploads.addFiles(cwd, e.target.files)
                    e.target.value = ''
                }}
            />
            <input
                ref={zipInput}
                type="file"
                accept=".zip,application/zip"
                hidden
                onChange={(e) => {
                    const f = e.target.files?.[0]
                    if (f) uploads.addZip(cwd, f)
                    e.target.value = ''
                }}
            />

            {/* 文件表 */}
            <main
                className={`relative flex-1 overflow-auto px-5 py-4 ${dragOver ? 'outline-2 outline-dashed outline-copper -outline-offset-8' : ''}`}
                onDragOver={(e) => { e.preventDefault(); setDragOver(true) }}
                onDragLeave={() => setDragOver(false)}
                onDrop={onDrop}
            >
                {loading && (
                    <div className="flex items-center gap-2 py-10 text-sm text-ink-muted">
                        <Spinner /> 正在读取目录…
                    </div>
                )}

                {!loading && loadError && (
                    <div className="py-10 text-sm">
                        <p className="text-danger">{loadError}</p>
                        <Button className="mt-3" onClick={refreshCwd}>重试</Button>
                    </div>
                )}

                {!loading && !loadError && entries.length === 0 && (
                    <div className="py-16 text-center text-sm text-ink-muted">
                        <p className="font-mono text-ink-faint">（空目录）</p>
                        <p className="mt-2">把文件拖进来，或使用上方「上传文件」开始</p>
                    </div>
                )}

                {!loading && !loadError && entries.length > 0 && (
                    <table className="w-full border-collapse text-sm">
                        <thead>
                        <tr className="border-b border-line text-left font-mono text-xs text-ink-faint">
                            <th className="py-2 pr-4 font-normal">名称</th>
                            <th className="w-28 py-2 pr-4 text-right font-normal">大小</th>
                            <th className="w-40 py-2 pr-4 font-normal max-sm:hidden">修改时间</th>
                            <th className="w-20 py-2 font-normal" aria-label="操作"></th>
                        </tr>
                        </thead>
                        <tbody>
                        {entries.map((entry) => (
                            <tr key={entry.path} className="group border-b border-line/40 hover:bg-panel">
                                <td className="py-2 pr-4">
                                    {entry.isDirectory ? (
                                        <button
                                            type="button"
                                            onClick={() => setCwd(entry.path)}
                                            className="cursor-pointer font-mono text-copper hover:text-copper-bright"
                                        >
                                            {entry.name}/
                                        </button>
                                    ) : previewKindOf(entry.name) !== 'none' ? (
                                        <button
                                            type="button"
                                            onClick={() => openPreview(entry)}
                                            className="cursor-pointer font-mono text-ink hover:text-copper-bright"
                                            title="在线预览"
                                        >
                                            {entry.name}
                                        </button>
                                    ) : (
                                        <span className="font-mono">{entry.name}</span>
                                    )}
                                </td>
                                <td className="py-2 pr-4 text-right font-mono text-ink-muted">
                                    {entry.isDirectory ? '—' : formatSize(entry.size)}
                                </td>
                                <td className="py-2 pr-4 font-mono text-ink-muted max-sm:hidden">
                                    {formatTime(entry.modifiedAtUtc)}
                                </td>
                                <td className="py-2 text-right">
                                    <div className="flex justify-end">
                                        <Menu
                                            label="操作"
                                            items={[
                                                ...(!entry.isDirectory && previewKindOf(entry.name) !== 'none'
                                                    ? [{
                                                        label: previewKindOf(entry.name) === 'text' ? '查看/编辑' : '预览',
                                                        onSelect: () => openPreview(entry),
                                                    }]
                                                    : []),
                                                {
                                                    label: downloading === entry.path ? '下载中…' : '下载',
                                                    onSelect: () => { if (downloading !== entry.path) void startDownload(entry) },
                                                },
                                                {
                                                    label: '重命名',
                                                    onSelect: () => {
                                                        setRenameTarget(entry)
                                                        setRenameName(entry.name)
                                                        setRenameError(null)
                                                    },
                                                },
                                                {
                                                    label: '移动到…',
                                                    onSelect: () => setTransfer({ entry, mode: 'move' }),
                                                },
                                                {
                                                    label: '复制到…',
                                                    onSelect: () => setTransfer({ entry, mode: 'copy' }),
                                                },
                                                {
                                                    label: '删除',
                                                    onSelect: () => {
                                                        setDeleteTarget(entry)
                                                        setDeleteRecursive(false)
                                                        setDeleteError(null)
                                                    },
                                                },
                                            ]}
                                        />
                                    </div>
                                </td>
                            </tr>
                        ))}
                        </tbody>
                    </table>
                )}
            </main>

            {/* 新建目录 */}
            <Dialog
                title="mkdir"
                open={mkdirOpen}
                onClose={() => setMkdirOpen(false)}
                footer={
                    <>
                        <Button onClick={() => setMkdirOpen(false)}>取消</Button>
                        <Button variant="primary" disabled={mkdirBusy} onClick={() => void submitMkdir()}>
                            {mkdirBusy ? <Spinner /> : '创建'}
                        </Button>
                    </>
                }
            >
                <TextField
                    label={`在 ~/${cwd} 下新建目录`}
                    autoFocus
                    value={mkdirName}
                    onChange={(e) => setMkdirName(e.target.value)}
                    onKeyDown={(e) => { if (e.key === 'Enter') void submitMkdir() }}
                    placeholder="目录名"
                />
                {mkdirError && <p className="mt-2 text-xs text-danger" role="alert">{mkdirError}</p>}
            </Dialog>

            {/* 新建文件 */}
            <Dialog
                title="new file"
                open={newFileOpen}
                onClose={() => setNewFileOpen(false)}
                footer={
                    <>
                        <Button onClick={() => setNewFileOpen(false)}>取消</Button>
                        <Button variant="primary" disabled={newFileBusy} onClick={() => void submitNewFile()}>
                            {newFileBusy ? <Spinner /> : '创建'}
                        </Button>
                    </>
                }
            >
                <TextField
                    label={`在 ~/${cwd} 下新建文件`}
                    autoFocus
                    value={newFileName}
                    onChange={(e) => setNewFileName(e.target.value)}
                    onKeyDown={(e) => { if (e.key === 'Enter') void submitNewFile() }}
                    placeholder="文件名（含扩展名，如 notes.txt）"
                />
                {newFileError && <p className="mt-2 text-xs text-danger" role="alert">{newFileError}</p>}
            </Dialog>

            {/* 重命名 */}
            <Dialog
                title="rename"
                open={renameTarget !== null}
                onClose={() => setRenameTarget(null)}
                footer={
                    <>
                        <Button onClick={() => setRenameTarget(null)}>取消</Button>
                        <Button variant="primary" disabled={renameBusy} onClick={() => void submitRename()}>
                            {renameBusy ? <Spinner /> : '确定'}
                        </Button>
                    </>
                }
            >
                <TextField
                    label={`重命名 ${renameTarget?.name ?? ''}`}
                    autoFocus
                    value={renameName}
                    onChange={(e) => setRenameName(e.target.value)}
                    onKeyDown={(e) => { if (e.key === 'Enter') void submitRename() }}
                    placeholder="新名称"
                />
                {renameError && <p className="mt-2 text-xs text-danger" role="alert">{renameError}</p>}
            </Dialog>

            {/* 移动 / 复制：目录选择器 */}
            <DirectoryPicker
                open={transfer !== null}
                title={transfer?.mode === 'copy' ? `复制「${transfer?.entry.name}」到…` : `移动「${transfer?.entry.name}」到…`}
                initialPath={cwd}
                confirmLabel={transfer?.mode === 'copy' ? '复制到此目录' : '移动到此目录'}
                onConfirm={(dir) => void submitTransfer(dir)}
                onClose={() => setTransfer(null)}
            />

            {/* 删除确认 */}
            <Dialog
                title="rm"
                open={deleteTarget !== null}
                onClose={() => setDeleteTarget(null)}
                footer={
                    <>
                        <Button onClick={() => setDeleteTarget(null)}>取消</Button>
                        <Button variant="danger" disabled={deleteBusy} onClick={() => void submitDelete()}>
                            {deleteBusy ? <Spinner /> : '删除'}
                        </Button>
                    </>
                }
            >
                <p>
                    确认删除{deleteTarget?.isDirectory ? '目录' : '文件'}{' '}
                    <span className="font-mono text-copper">{deleteTarget?.name}</span>
                    {deleteTarget?.isDirectory ? '/' : ''} ？此操作不可恢复。
                </p>
                {deleteTarget?.isDirectory && (
                    <label className="mt-3 flex cursor-pointer items-center gap-2 text-xs text-ink-muted">
                        <input
                            type="checkbox"
                            checked={deleteRecursive}
                            onChange={(e) => setDeleteRecursive(e.target.checked)}
                            className="accent-danger"
                        />
                        递归删除目录及其全部内容
                    </label>
                )}
                {deleteError && <p className="mt-2 text-xs text-danger" role="alert">{deleteError}</p>}
            </Dialog>

            {/* 上传托盘与消息 */}
            {preview && (
                <Preview
                    key={preview.entry.path}
                    entry={preview.entry}
                    kind={preview.kind}
                    onClose={() => setPreview(null)}
                    onSaved={refreshCwd}
                />
            )}

            <UploadTray
                tasks={uploads.tasks}
                onCancel={uploads.cancel}
                onRetryOverwrite={uploads.retryOverwrite}
                onClear={uploads.clearFinished}
            />
            {toast && (
                <div
                    role="status"
                    className="fixed top-4 left-1/2 z-40 -translate-x-1/2 rounded border border-line bg-panel-2 px-4 py-2 text-sm shadow-xl"
                >
                    {toast}
                </div>
            )}
        </div>
    )
}
