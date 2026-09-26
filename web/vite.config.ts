/// <reference types="vitest/config" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// In development the API runs on http://localhost:5080; the proxy avoids CORS (VITE_API_BASE_URL overrides it).
export default defineConfig({
  plugins: [react()],
  server: {
    port: 5173,
    proxy: { '/api': { target: process.env.DOCHUB_API ?? 'http://localhost:5080', changeOrigin: true } },
  },
  test: {
    environment: 'jsdom',
    globals: true,
    setupFiles: ['./src/test/setup.ts'],
    css: false,
    env: { VITE_API_BASE_URL: 'http://api.test' },
    include: ['src/**/*.test.{ts,tsx}'],
  },
});
