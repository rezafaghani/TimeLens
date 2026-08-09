import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

export default defineConfig({
  plugins: [react()],
  server: {
    proxy: {
      '/api': {
        target: 'http://localhost:5100',
        changeOrigin: true
      },
      '/hubs': {
        target: 'http://localhost:5100',
        changeOrigin: true,
        ws: true
      }
    }
  },
  test: {
    include: ['src/**/*.test.ts', 'src/**/*.test.tsx']
  }
});
