// src/api/files.ts
// 文件接口。路径一律是用户目录内的相对路径（'/' 分隔，空串=根目录）。
// 上传用 XMLHttpRequest 以获得真实上传进度（fetch 目前拿不到上行进度）。

import { ApiError, requestJson, tokenStore, tryRefresh } from './http'
import type {
  ErrorResponse,
  FileListResponse,
  MessageResponse,
  UploadResponse,
  UploadZipResponse,
} from './types'

export async function listDir(path: string, signal?: AbortSignal): Promise<FileListResponse> {
  const res = await requestJson<FileListResponse>(
    `/api/files/list?path=${encodeURIComponent(path)}`,
    { signal },
  )
  // 形状校验：若后端版本不匹配（如旧版 PascalCase 响应），给出可诊断的错误而不是静默空目录。
  if (!Array.isArray(res.entries)) {
    throw new ApiError(0, '后端响应格式不符：请将 NasServer 更新到最新版本（响应字段已统一为 camelCase）')
  }
  return res
}

export function mkdir(path: string): Promise<MessageResponse> {
  return requestJson<MessageResponse>('/api/files/mkdir', { method: 'POST', json: { path } })
}

export function deleteEntry(path: string, recursive: boolean): Promise<MessageResponse> {
  const qs = `path=${encodeURIComponent(path)}&recursive=${recursive ? 'true' : 'false'}`
  return requestJson<MessageResponse>(`/api/files/delete?${qs}`, { method: 'DELETE' })
}

/** 在指定相对路径新建一个空文件（父目录自动创建）。 */
export function newFile(path: string): Promise<MessageResponse> {
  return requestJson<MessageResponse>('/api/files/new-file', { method: 'POST', json: { path } })
}

/** 移动 / 重命名：dest 为完整目标相对路径。overwrite=true 允许覆盖已存在目标文件。 */
export function moveEntry(source: string, dest: string, overwrite: boolean): Promise<MessageResponse> {
  return requestJson<MessageResponse>('/api/files/move', { method: 'POST', json: { source, dest, overwrite } })
}

/** 复制：dest 为完整目标相对路径（文件或目录，目录递归）。overwrite=true 允许覆盖已存在目标文件。 */
export function copyEntry(source: string, dest: string, overwrite: boolean): Promise<MessageResponse> {
  return requestJson<MessageResponse>('/api/files/copy', { method: 'POST', json: { source, dest, overwrite } })
}

// ---------------------------------------------------------------- 上传
export interface UploadHandle {
  promise: Promise<UploadResponse | UploadZipResponse>
  abort: () => void
}

interface XhrUploadOptions {
  url: string
  method: 'PUT' | 'POST'
  body: Blob
  onProgress: (sent: number, total: number) => void
}

function parseXhrError(xhr: XMLHttpRequest): ApiError {
  let message = `请求失败（HTTP ${xhr.status}）`
  try {
    const body = JSON.parse(xhr.responseText) as Partial<ErrorResponse>
    if (typeof body.message === 'string' && body.message.length > 0) message = body.message
  } catch {
    /* 非 JSON 响应体 */
  }
  return new ApiError(xhr.status, message)
}

function xhrOnce<T>(opts: XhrUploadOptions): { promise: Promise<T>; xhr: XMLHttpRequest } {
  const xhr = new XMLHttpRequest()
  const promise = new Promise<T>((resolve, reject) => {
    xhr.open(opts.method, opts.url)
    const token = tokenStore.access
    if (token) xhr.setRequestHeader('Authorization', `Bearer ${token}`)
    xhr.setRequestHeader('Content-Type', 'application/octet-stream')
    xhr.responseType = 'text'
    xhr.upload.onprogress = (e) => {
      if (e.lengthComputable) opts.onProgress(e.loaded, e.total)
    }
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) {
        try {
          resolve(JSON.parse(xhr.responseText) as T)
        } catch {
          reject(new ApiError(xhr.status, '响应解析失败'))
        }
      } else {
        reject(parseXhrError(xhr))
      }
    }
    xhr.onerror = () => reject(new ApiError(0, '网络错误，上传中断'))
    xhr.onabort = () => reject(new ApiError(0, '已取消'))
    xhr.send(opts.body)
  })
  return { promise, xhr }
}

/** 带 401 自动刷新重试一次的 XHR 上传。 */
function xhrUpload<T>(opts: XhrUploadOptions): UploadHandle {
  let current: XMLHttpRequest | null = null
  let aborted = false

  const run = async (): Promise<T> => {
    const first = xhrOnce<T>(opts)
    current = first.xhr
    try {
      return await first.promise
    } catch (err) {
      if (!aborted && err instanceof ApiError && err.status === 401 && (await tryRefresh())) {
        const second = xhrOnce<T>(opts)
        current = second.xhr
        return await second.promise
      }
      throw err
    }
  }

  return {
    promise: run() as Promise<UploadResponse | UploadZipResponse>,
    abort: () => {
      aborted = true
      current?.abort()
    },
  }
}

/** 上传单个文件（请求体即文件内容）。 */
export function uploadFile(
  path: string,
  file: Blob,
  overwrite: boolean,
  onProgress: (sent: number, total: number) => void,
): UploadHandle {
  const qs = `path=${encodeURIComponent(path)}&overwrite=${overwrite ? 'true' : 'false'}`
  return xhrUpload<UploadResponse>({
    url: `/api/files/upload?${qs}`,
    method: 'PUT',
    body: file,
    onProgress,
  })
}

/** 上传 zip 并解压到 path 目录（目录上传）。 */
export function uploadZip(
  path: string,
  zip: Blob,
  overwrite: boolean,
  onProgress: (sent: number, total: number) => void,
): UploadHandle {
  const qs = `path=${encodeURIComponent(path)}&overwrite=${overwrite ? 'true' : 'false'}`
  return xhrUpload<UploadZipResponse>({
    url: `/api/files/upload-zip?${qs}`,
    method: 'POST',
    body: zip,
    onProgress,
  })
}

// ---------------------------------------------------------------- 下载
/**
 * 下载文件或目录（目录由后端流式打包为 zip）。
 * 采用「一次性票据 + 浏览器原生下载」：先 POST 换一个短时效票据，再用普通链接触发下载，
 * 由浏览器接管——大文件有进度条、不受内存限制、目录 zip 流式下载也更稳定。
 */
export async function download(path: string): Promise<void> {
  const { ticket } = await requestJson<{ ticket: string }>(
    `/api/files/download-ticket?path=${encodeURIComponent(path)}`,
    { method: 'POST' },
  )
  // 直链交给浏览器。文件名由后端的 Content-Disposition 决定（含目录 zip 的 .zip 名）。
  const a = document.createElement('a')
  a.href = `/api/files/download-by-ticket?ticket=${encodeURIComponent(ticket)}`
  // 同源导航下载；不设 target 以免被拦截弹窗。
  document.body.appendChild(a)
  a.click()
  a.remove()
}

// ---------------------------------------------------------------- 在线预览
/**
 * 换取一个「内联预览」直链：后端以正确 MIME + Content-Disposition: inline 返回，
 * 并支持 HTTP Range（音视频可拖动进度）。返回的 URL 可直接喂给 <img>/<video>/<audio>。
 */
export async function getPreviewUrl(path: string): Promise<string> {
  const { ticket } = await requestJson<{ ticket: string }>(
    `/api/files/download-ticket?path=${encodeURIComponent(path)}&inline=true`,
    { method: 'POST' },
  )
  return `/api/files/download-by-ticket?ticket=${encodeURIComponent(ticket)}`
}

// ---------------------------------------------------------------- 文本在线阅读 / 修改
export interface TextContent {
  path: string
  content: string
  size: number
}

/** 读取文本文件内容（受后端大小上限限制；非 UTF-8 会被拒）。 */
export function readText(path: string, signal?: AbortSignal): Promise<TextContent> {
  return requestJson<TextContent>(`/api/files/text?path=${encodeURIComponent(path)}`, { signal })
}

/** 覆盖保存文本文件内容（不保留历史版本）。 */
export function saveText(path: string, content: string): Promise<UploadResponse> {
  return requestJson<UploadResponse>('/api/files/text', {
    method: 'POST',
    json: { path, content },
  })
}
