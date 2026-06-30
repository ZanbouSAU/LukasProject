// src/api/types.ts
// 与 NasServer 的 DTO 一一对应（JSON 为 camelCase）。

export interface RegisterRequest {
  email: string
  password: string
  confirmPassword: string
  fullName?: string | null
}

export interface LoginRequest {
  email: string
  password: string
}

export interface AuthResponse {
  accessToken: string
  refreshToken: string
  /** ISO 8601，access token 过期时间 */
  expiresAt: string
  email: string
}

export interface MessageResponse {
  message: string
}

export interface ErrorResponse {
  message: string
}

export interface FileEntry {
  name: string
  /** 用户目录内的规范相对路径（'/' 分隔） */
  path: string
  isDirectory: boolean
  size: number
  modifiedAtUtc: string
}

export interface FileListResponse {
  path: string
  entries: FileEntry[]
}

export interface UploadResponse {
  path: string
  size: number
}

export interface UploadZipResponse {
  path: string
  files: number
  directories: number
  totalBytes: number
}
