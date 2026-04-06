# NimBus WebApp ClientApp

React + TypeScript + Vite frontend for `NimBus.WebApp`.

## Requirements

- Node.js 20+ recommended
- npm

## Install

```powershell
npm install
```

## Run in development

```powershell
npm run start
```

Starts Vite on `https://localhost:3001`.

The dev server proxies:

- `/api`
- `/hubs`
- `/login`
- `/logout`

to the backend URL defined in `vite.config.ts` (Aspire-injected URL when available, otherwise `https://localhost:28375`).

## Build

```powershell
npm run build
```

Outputs static assets to `build/public`.

## Test

```powershell
npm test
```

CI test run:

```powershell
npm run test:ci
```

## Lint and format

```powershell
npm run lint
npm run lint:fix
npm run fmt
```

## Certificates for HTTPS dev server

If `devcert.crt` and `devcert.key` exist in this folder, Vite uses them.
If they do not exist, Vite still starts with its default local behavior.

## Key files

- `vite.config.ts` — dev server proxy, HTTPS cert loading, build output
- `src/main.tsx` — client entry point
- `src/app.tsx` — app shell and routes
- `src/api-client/` — generated API client from `../api-spec.yaml`
