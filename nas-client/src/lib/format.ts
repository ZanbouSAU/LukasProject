// src/lib/format.ts

/** 1234567 → "1.2 MB"（文件系统素材，配合等宽字体展示）。 */
export function formatSize(bytes: number): string {
  if (bytes < 1024) return `${bytes} B`
  const units = ['KB', 'MB', 'GB', 'TB'] as const
  let value = bytes
  let unit: string = 'B'
  for (const u of units) {
    value /= 1024
    unit = u
    if (value < 1024) break
  }
  return `${value >= 100 ? value.toFixed(0) : value.toFixed(1)} ${unit}`
}

/** ISO 时间 → 本地 "2026-06-11 09:30"。 */
export function formatTime(iso: string): string {
  const d = new Date(iso)
  if (Number.isNaN(d.getTime())) return '—'
  const pad = (n: number) => String(n).padStart(2, '0')
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())} ${pad(d.getHours())}:${pad(d.getMinutes())}`
}

/** 把路径拆成面包屑段：'docs/2026' → ['docs', '2026']。 */
export function splitPath(path: string): string[] {
  return path.split('/').filter((s) => s.length > 0)
}

/** 拼接相对路径，自动去除多余分隔符。 */
export function joinPath(...parts: string[]): string {
  return parts
    .flatMap((p) => p.split('/'))
    .filter((s) => s.length > 0)
    .join('/')
}

/** 客户端预检：目录/文件名里不允许的输入（与后端 SafeName 同向，最终以后端校验为准）。 */
export function validateNameSegment(name: string): string | null {
  const trimmed = name.trim()
  if (trimmed.length === 0) return '名称不能为空'
  if (trimmed === '.' || trimmed === '..') return '名称不能是 "." 或 ".."'
  if (/[\\/:*?"<>|]/.test(trimmed)) return '名称含有不允许的字符（\\ / : * ? " < > |）'
  for (const ch of trimmed) {
    const code = ch.codePointAt(0) ?? 0
    if (code < 0x20) return '名称含有控制字符'
  }
  if (trimmed.length > 255) return '名称过长（最多 255 字符）'
  return null
}

// ---------------------------------------------------------------- 文件类型识别
export type PreviewKind = 'image' | 'video' | 'audio' | 'text' | 'none'

const EXT_IMAGE = new Set(['jpg', 'jpeg', 'png', 'gif', 'webp', 'bmp', 'svg', 'ico', 'avif'])
const EXT_VIDEO = new Set(['mp4', 'webm', 'ogv', 'mov', 'mkv'])
const EXT_AUDIO = new Set(['mp3', 'wav', 'ogg', 'oga', 'flac', 'aac', 'm4a', 'opus'])
const EXT_TEXT = new Set([
  'txt', 'md', 'markdown', 'log', 'json', 'xml', 'csv', 'tsv', 'yaml', 'yml', 'ini', 'conf',
  'html', 'htm', 'css', 'js', 'jsx', 'ts', 'tsx', 'c', 'h', 'cpp', 'hpp', 'cs', 'java', 'py',
  'rb', 'go', 'rs', 'php', 'sh', 'bash', 'sql', 'toml', 'env', 'gitignore', 'dockerfile',
])

function extOf(name: string): string {
  const dot = name.lastIndexOf('.')
  return dot >= 0 ? name.slice(dot + 1).toLowerCase() : ''
}

/** 根据文件名判断可用的在线预览方式。 */
export function previewKindOf(name: string): PreviewKind {
  const ext = extOf(name)
  if (EXT_IMAGE.has(ext)) return 'image'
  if (EXT_VIDEO.has(ext)) return 'video'
  if (EXT_AUDIO.has(ext)) return 'audio'
  if (EXT_TEXT.has(ext)) return 'text'
  return 'none'
}
