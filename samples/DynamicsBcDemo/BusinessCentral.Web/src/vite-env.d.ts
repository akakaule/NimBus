/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** The Sales Hub look-alike (d365-web); the AppHost injects it. */
  readonly VITE_D365_WEB_URL?: string;
  /** nimbus-ops, the NimBus operator WebApp; the AppHost injects it. */
  readonly VITE_NIMBUS_OPS_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
