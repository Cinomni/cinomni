import { fileURLToPath, URL } from 'node:url'
// `vitest/config` re-exports Vite's own defineConfig with the `test` block typed, so the dev server
// and the test runner stay described by one file and cannot drift on aliases or plugins.
import { defineConfig } from 'vitest/config'
import react from '@vitejs/plugin-react'
import tailwindcss from '@tailwindcss/vite'

// The API host in development. Vite proxies /api and /health to it so the SPA can call the backend
// same-origin (no CORS) and prod can serve the built bundle from the Host itself.
const API_TARGET = process.env.CINOMNI_API ?? 'http://localhost:5268'

export default defineConfig({
  plugins: [react(), tailwindcss()],
  resolve: {
    alias: { '@': fileURLToPath(new URL('./src', import.meta.url)) },
  },
  server: {
    port: 5173,
    proxy: {
      '/api': { target: API_TARGET, changeOrigin: true },
      '/health': { target: API_TARGET, changeOrigin: true },
    },
  },
  test: {
    environment: 'jsdom',
    setupFiles: ['./src/test/setup.ts'],
    include: ['src/**/*.test.{ts,tsx}'],
    // The suite mocks the API module and never touches the network, so it stays fast and
    // deterministic: a test that hangs is a test that forgot to mock something.
    testTimeout: 5_000,
    restoreMocks: true,
    css: false,
  },
})
