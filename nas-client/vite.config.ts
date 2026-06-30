import { defineConfig } from 'vite'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// https://vite.dev/config/
export default defineConfig({
  plugins: [react(), tailwindcss()],
  server: {
    // 开发期把 /api 代理到本机 NasServer，避免跨域；生产建议同源部署（反向代理统一入口）。
    proxy: {
      '/api': {
        target: 'http://127.0.0.1:8172',
        changeOrigin: true,
      },
    },
  },
})
