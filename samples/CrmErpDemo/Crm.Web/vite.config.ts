import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Aspire injects service discovery URLs via process.env.
// Vite's loadEnv() only reads .env files, NOT process.env — always use process.env here.
// The key name depends on the Aspire version's dash-normalization of the resource name.
const env = process.env;
const apiTarget =
  env['services__crm_api__https__0'] ||
  env['services__crm_api__http__0'] ||
  env['services__crm-api__https__0'] ||
  env['services__crm-api__http__0'] ||
  'http://localhost:5080';

// Log what we resolved so the Aspire console shows it.
// eslint-disable-next-line no-console
console.log('[crm-web] proxy target:', apiTarget);
const discoveryKeys = Object.keys(env).filter((k) => k.startsWith('services__'));
// eslint-disable-next-line no-console
console.log('[crm-web] discovered services__ keys:', discoveryKeys);

export default defineConfig({
  plugins: [react()],
  server: {
    port: Number(env.PORT) || 5173,
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
