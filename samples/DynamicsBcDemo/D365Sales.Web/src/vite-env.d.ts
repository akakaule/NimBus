/// <reference types="vite/client" />

interface ImportMetaEnv {
  /** Base URL of the Business Central look-alike, for quote deep links. Injected by the AppHost. */
  readonly VITE_BC_WEB_URL?: string;
  /** Base URL of nimbus-ops (the NimBus operator WebApp), for integration-trail links. Injected by the AppHost. */
  readonly VITE_NIMBUS_OPS_URL?: string;
}

interface ImportMeta {
  readonly env: ImportMetaEnv;
}
