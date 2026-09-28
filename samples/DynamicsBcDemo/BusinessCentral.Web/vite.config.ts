import { defineConfig } from 'vite';
import react from '@vitejs/plugin-react';

// Aspire injects service discovery URLs via process.env.
// Vite's loadEnv() only reads .env files, NOT process.env — always use process.env here.
// The key name depends on the Aspire version's dash-normalization of the resource name.
const env = process.env;

// The Business Central simulator: every /api call of the BC screens and the hidden /demo pages.
const bcApiTarget =
  env['services__bc_api__https__0'] ||
  env['services__bc_api__http__0'] ||
  env['services__bc-api__https__0'] ||
  env['services__bc-api__http__0'] ||
  'http://localhost:5290';

// The Dynamics 365 Sales simulator: only the presenter's /demo cockpit uses it (pilot-office burst).
const d365ApiTarget =
  env['services__d365_api__https__0'] ||
  env['services__d365_api__http__0'] ||
  env['services__d365-api__https__0'] ||
  env['services__d365-api__http__0'] ||
  'http://localhost:5280';

// Log what we resolved so the Aspire console shows it.
// eslint-disable-next-line no-console
console.log('[bc-web] proxy targets:', { '/api': bcApiTarget, '/d365-api': d365ApiTarget });
const discoveryKeys = Object.keys(env).filter((k) => k.startsWith('services__'));
// eslint-disable-next-line no-console
console.log('[bc-web] discovered services__ keys:', discoveryKeys);

export default defineConfig({
  plugins: [react()],
  build: {
    // Fluent UI is most of the one bundle (about 240 kB gzipped); fine for a demo served locally.
    chunkSizeWarningLimit: 1024,
  },
  server: {
    port: Number(env.PORT) || 5293,
    // Aspire owns this port and proxies its front-door to it. Without strictPort,
    // Vite silently falls back to the next free port when the assigned one is briefly
    // held during a restart, leaving Aspire's proxy pointed at a dead port ("does not
    // load"). Fail fast instead so Aspire restarts us cleanly on the right port.
    strictPort: true,
    proxy: {
      '/api': { target: bcApiTarget, changeOrigin: true, secure: false },
      // /d365-api/api/demo/burst → <d365-api>/api/demo/burst: the prefix only picks the target.
      '/d365-api': {
        target: d365ApiTarget,
        changeOrigin: true,
        secure: false,
        rewrite: (p) => p.replace(/^\/d365-api/, ''),
      },
    },
  },
});
