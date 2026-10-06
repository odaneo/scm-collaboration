import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
export default defineConfig({
  plugins: [react()],
  server: { host: '127.0.0.1', port: 5173, strictPort: true, proxy: {
    '/api/production-tasks': process.env.PRODUCTION_API_URL ?? 'http://127.0.0.1:5181',
    '/api': process.env.PROCUREMENT_API_URL ?? 'http://127.0.0.1:5180',
  } },
});
