import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Aspire injects service discovery URLs via process.env.
// Vite's loadEnv() only reads .env files, NOT process.env — always use process.env here.
// The key name depends on the Aspire version's dash-normalization of the resource name.
// (VITE_* variables such as VITE_BC_WEB_URL are different: Vite exposes those to the browser
// code from process.env by itself.)
const env = process.env;
const apiTarget =
  env['services__d365_api__https__0'] ||
  env['services__d365_api__http__0'] ||
  env['services__d365-api__https__0'] ||
  env['services__d365-api__http__0'] ||
  'http://localhost:5280';

// Log what we resolved so the Aspire console shows it.
// eslint-disable-next-line no-console
console.log('[d365-web] proxy target:', apiTarget);
const discoveryKeys = Object.keys(env).filter((k) => k.startsWith('services__'));
// eslint-disable-next-line no-console
console.log('[d365-web] discovered services__ keys:', discoveryKeys);

export default defineConfig({
  plugins: [react()],
  build: {
    // Fluent UI is most of the one bundle (about 240 kB gzipped); fine for a demo served locally.
    chunkSizeWarningLimit: 1024,
  },
  server: {
    port: Number(env.PORT) || 5283,
    // Aspire owns this port and proxies its front-door to it. Without strictPort,
    // Vite silently falls back to the next free port when the assigned one is briefly
    // held during a restart, leaving Aspire's proxy pointed at a dead port ("does not
    // load"). Fail fast instead so Aspire restarts us cleanly on the right port.
    strictPort: true,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true, secure: false },
    },
  },
});
