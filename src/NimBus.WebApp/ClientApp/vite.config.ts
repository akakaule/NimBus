/// <reference types="vitest" />
import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';
import path from 'path';
import fs from 'fs';

// Check if HTTPS certificates exist
const certPath = './devcert.crt';
const keyPath = './devcert.key';
const httpsConfig =
  fs.existsSync(certPath) && fs.existsSync(keyPath)
    ? { cert: fs.readFileSync(certPath), key: fs.readFileSync(keyPath) }
    : undefined;

// When running under Aspire, use the injected service URL; otherwise fall back to the default
const apiTarget =
  process.env.services__webapp__https__0 ||
  process.env.services__webapp__http__0 ||
  'https://localhost:28375';

export default defineConfig({
  plugins: [react()],
  resolve: {
    alias: {
      'api-client': path.resolve(__dirname, './src/api-client'),
      components: path.resolve(__dirname, './src/components'),
      pages: path.resolve(__dirname, './src/pages'),
      hooks: path.resolve(__dirname, './src/hooks'),
      functions: path.resolve(__dirname, './src/functions'),
      lib: path.resolve(__dirname, './src/lib'),
      models: path.resolve(__dirname, './src/models'),
      'shared-styles': path.resolve(__dirname, './src/shared-styles'),
    },
  },
  server: {
    port: 3001,
    https: httpsConfig,
    proxy: {
      '/api': {
        target: apiTarget,
        secure: false,
        changeOrigin: true,
      },
      '/hubs': {
        target: apiTarget,
        secure: false,
        changeOrigin: true,
        ws: true,
      },
      '/login': {
        target: apiTarget,
        secure: false,
        changeOrigin: true,
      },
      '/logout': {
        target: apiTarget,
        secure: false,
        changeOrigin: true,
      },
    },
  },
  build: {
    outDir: 'build/public',
    sourcemap: true,
    rollupOptions: {
      output: {
        manualChunks(id) {
          if (/node_modules[\\/](react|react-dom|react-router-dom)[\\/]/.test(id)) {
            return 'vendor';
          }
        },
      },
    },
  },
  test: {
    environment: 'jsdom',
  },
});
