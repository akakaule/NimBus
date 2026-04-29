import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Aspire injects service discovery URLs via process.env.
// Vite's loadEnv() only reads .env files, NOT process.env — always use process.env here.
// The key name depends on the Aspire version's dash-normalization of the resource name.
const env = process.env;
const apiTarget =
  env['services__erp_api__https__0'] ||
  env['services__erp_api__http__0'] ||
  env['services__erp-api__https__0'] ||
  env['services__erp-api__http__0'] ||
  'http://localhost:5090';

// eslint-disable-next-line no-console
console.log('[erp-web] proxy target:', apiTarget);
const discoveryKeys = Object.keys(env).filter((k) => k.startsWith('services__'));
// eslint-disable-next-line no-console
console.log('[erp-web] discovered services__ keys:', discoveryKeys);

export default defineConfig({
  plugins: [react()],
  server: {
    port: Number(env.PORT) || 5174,
    proxy: {
      '/api': { target: apiTarget, changeOrigin: true, secure: false },
    },
  },
});
