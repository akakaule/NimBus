import { useEffect, useRef, useState } from "react";
import { Button } from "components/ui/button";
import { Input } from "components/ui/input";
import { Select } from "components/ui/select";
import { Toggle } from "components/ui/toggle";
import { Card, CardContent, CardHeader, CardTitle } from "components/ui/card";

type Settings = {
  enabled: boolean; model: string; includeEventPayload: boolean; includeRecentFailureHistory: boolean;
  maximumHistoryItems: number; additionalRedactedKeys: string[]; allowedEndpoints: string[];
  timeoutSeconds: number; minimumCategoryConfidence: number; retryLikely: number; changeRequired: number;
};
type SettingsState = {
  active: Settings; saved: Settings; revision: string; activeRevision: string; restartRequired: boolean;
  startupLoadFailed: boolean; credentialConfigured: boolean; csrfToken: string;
};
const url = "/api/admin/failure-intelligence";
const names = (value: string) => value.split(",").map(item => item.trim()).filter(Boolean);

export default function FailureIntelligenceSettings() {
  const [state, setState] = useState<SettingsState>();
  const [draft, setDraft] = useState<Settings>();
  const [redactedKeys, setRedactedKeys] = useState("");
  const [endpoints, setEndpoints] = useState("");
  const [selected, setSelected] = useState(false);
  const [error, setError] = useState<string>();
  const [message, setMessage] = useState<string>();
  const [saving, setSaving] = useState(false);
  const [loading, setLoading] = useState(true);
  const [review, setReview] = useState(false);
  const [consent, setConsent] = useState(false);
  const [reload, setReload] = useState(0);
  const alive = useRef(true);
  const abortSave = useRef<AbortController | undefined>(undefined);
  function accept(next: SettingsState) {
    setState(next); setDraft(next.saved); setRedactedKeys(next.saved.additionalRedactedKeys.join(", "));
    setEndpoints(next.saved.allowedEndpoints.join(", ")); setSelected(next.saved.allowedEndpoints.length > 0);
    setReview(false); setConsent(false);
  }
  useEffect(() => {
    alive.current = true;
    setLoading(true);
    const controller = new AbortController();
    void (async () => {
      try {
        const response = await fetch(url, { credentials: "same-origin", signal: controller.signal });
        if (!response.ok) throw new Error(response.status === 403 || response.status === 401
          ? "Only site Owners can configure failure intelligence." : "Settings are unavailable. Check shared storage and try again.");
        const next = await response.json() as SettingsState;
        if (!controller.signal.aborted) { accept(next); setError(undefined); }
      } catch (cause) {
        if (!controller.signal.aborted) setError(cause instanceof Error ? cause.message : "Settings are unavailable.");
      } finally {
        if (!controller.signal.aborted) setLoading(false);
      }
    })();
    return () => { alive.current = false; controller.abort(); abortSave.current?.abort(); };
  }, [reload]);
  const edit = <K extends keyof Settings>(key: K, value: Settings[K]) => {
    setDraft(current => current ? { ...current, [key]: value } : current);
    setReview(false); setConsent(false); setMessage(undefined);
  };
  async function save() {
    if (!draft || !state || saving || loading) return;
    setSaving(true); setError(undefined);
    const controller = new AbortController(); abortSave.current = controller;
    try {
      const response = await fetch(url, {
        method: "PUT", credentials: "same-origin", signal: controller.signal,
        headers: { "Content-Type": "application/json", "X-NimBus-CSRF": state.csrfToken },
        body: JSON.stringify({ revision: state.revision, payloadSharingAcknowledged: consent,
          settings: { ...draft, additionalRedactedKeys: names(redactedKeys), allowedEndpoints: selected ? names(endpoints) : [] } }),
      });
      if (!response.ok) throw new Error(response.status === 409
        ? "Another administrator changed these settings. Reload saved settings before reviewing your edits again."
        : response.status === 400 ? "Settings were rejected. Check values and reload if your security token has expired."
          : response.status === 401 || response.status === 403 ? "Only site Owners can save settings."
            : "Save could not be confirmed. Reload saved settings before retrying.");
      const next = await response.json() as SettingsState;
      if (alive.current) { accept(next); setMessage("Settings saved. Restart all WebApp instances to apply them. No analysis was requested."); }
    } catch (cause) {
      if (alive.current) setError(cause instanceof Error ? cause.message : "Save could not be confirmed. Reload before retrying.");
    } finally { if (alive.current) setSaving(false); }
  }
  if (!draft || !state) return <div className="space-y-3"><p role={error ? "alert" : "status"}>{error ?? "Loading failure intelligence settings…"}</p>
    {error && <Button onClick={() => setReload(value => value + 1)}>Try again</Button>}</div>;

  const preview = {
    failure: { eventType: "CrmAccountCreated", endpoint: "ErpEndpoint", status: "Failed", retryCount: 1, retryLimit: 3,
      exception: { type: "InvalidOperationException", message: "ERP is in error mode.", source: "Erp.Adapter" }, deadLetterReason: null },
    recentHistory: draft.includeRecentFailureHistory && draft.maximumHistoryItems > 0
      ? [{ attempt: 1, outcome: "ErrorResponse", errorType: "InvalidOperationException", errorMessage: "ERP is in error mode." }] : [],
    eventPayload: draft.includeEventPayload ? Object.fromEntries(Object.entries({ AccountId: "[redacted]", LegalName: "Example Company", TaxId: "[redacted]", CountryCode: "ES" })
      .map(([key, value]) => [key, names(redactedKeys).some(name => name.toLowerCase() === key.toLowerCase()) ? "[redacted]" : value])) : null,
  };
  return <form className="w-full min-w-0 space-y-6 max-sm:fixed max-sm:inset-0 max-sm:z-40 max-sm:overflow-y-auto max-sm:bg-background max-sm:p-4" onSubmit={event => {
    event.preventDefault();
    if (selected && names(endpoints).length === 0) { setError("Enter at least one endpoint ID, or select all authorized endpoints."); return; }
    setReview(true); setError(undefined);
  }}>
    <header><a href="/Admin" className="mb-4 inline-block text-sm text-primary sm:hidden">← Back to Admin</a><h2 className="text-xl font-semibold">Failure intelligence</h2><p className="text-muted-foreground">On-demand advisory analysis. You control the provider, evidence and endpoint scope.</p>
      <p className="mt-2 text-sm">Active on this instance: {state.active.enabled ? "enabled" : "disabled"} · payload {state.active.includeEventPayload ? "included" : "excluded"}</p>
      {state.restartRequired && <p role="status" className="mt-2 text-status-warning">Saved settings differ from this instance. Restart all WebApp instances to apply.</p>}
      {state.startupLoadFailed && <p role="alert">Startup settings could not be loaded. Classification is disabled until storage is restored and the WebApp restarts.</p>}
      {!state.credentialConfigured && <p role="status">Provider key is missing. Configure it through deployment before enabling analysis.</p>}
    </header>
    {error && <p role="alert" className="text-status-danger">{error}</p>}
    {message && <p role="status" className="text-status-success">{message}</p>}
    <div className="grid gap-6 xl:grid-cols-[minmax(0,1.5fr)_minmax(0,1fr)]">
      <fieldset disabled={saving || loading} className="min-w-0 space-y-5">
        <Card><CardHeader><CardTitle>01 · Activation &amp; provider</CardTitle></CardHeader><CardContent className="space-y-4">
          <SettingToggle label="Enable failure intelligence" description="Contributors can request analysis. Nothing runs automatically." checked={draft.enabled} onChange={value => edit("enabled", value)} />
          <div className="grid gap-4 sm:grid-cols-2"><label className="text-sm">Provider<Input value="TypeSafe" readOnly /></label>
            <label className="text-sm">Model<Input value={draft.model} required maxLength={100} pattern="[a-zA-Z0-9_.\-]+" onChange={event => edit("model", event.target.value)} /></label></div>
          <p className="text-sm text-muted-foreground">API key: {state.credentialConfigured ? "configured" : "not configured"}. Credentials and the provider URL are managed through deployment and never displayed here.</p>
        </CardContent></Card>
        <Card><CardHeader><CardTitle>02 · Evidence sent to Jev</CardTitle></CardHeader><CardContent className="space-y-4">
          <SettingToggle label="Include redacted event payload" description="Data.IncludeEventPayload — business context from this failure occurrence." checked={draft.includeEventPayload} onChange={value => edit("includeEventPayload", value)} />
          {draft.includeEventPayload && <p className="rounded-md border border-status-warning/40 bg-status-warning/10 p-3 text-sm">Event data will leave NimBus. PII annotations and secret-key rules are applied first, but unmarked business data may remain. Confirm your organization’s data-sharing policy.</p>}
          <SettingToggle label="Include recent failure history" description="Earlier failures for the same event, endpoint and session." checked={draft.includeRecentFailureHistory} onChange={value => edit("includeRecentFailureHistory", value)} />
          <label className="block text-sm">Maximum history items<Input type="number" min={0} max={5} required disabled={!draft.includeRecentFailureHistory} value={draft.maximumHistoryItems} onChange={event => edit("maximumHistoryItems", Number(event.target.value))} /></label>
          <label className="block text-sm">Additional redacted keys<Input value={redactedKeys} maxLength={20100} placeholder="TaxId, AccountId" onChange={event => { setRedactedKeys(event.target.value); setReview(false); }} /><span className="text-xs text-muted-foreground">Comma-separated field names. Unverifiable payloads are omitted.</span></label>
        </CardContent></Card>
        <Card><CardHeader><CardTitle>03 · Scope &amp; guardrails</CardTitle></CardHeader><CardContent className="space-y-4">
          <label className="block text-sm">Allow new analysis on<Select value={selected ? "selected" : "all"} onChange={event => { setSelected(event.target.value === "selected"); setReview(false); }}><option value="all">All authorized endpoints</option><option value="selected">Selected endpoints</option></Select></label>
          {selected && <label className="block text-sm">Endpoint IDs<Input value={endpoints} required maxLength={20100} onChange={event => { setEndpoints(event.target.value); setReview(false); }} placeholder="ErpEndpoint, CrmEndpoint" /></label>}
          <p className="text-xs text-muted-foreground">Existing permissions still apply. Contributors analyze; Readers view saved results. Restricting analysis does not remove history access.</p>
          <details><summary className="cursor-pointer text-sm font-semibold">Advanced limits &amp; guidance thresholds</summary><div className="mt-4 grid gap-4 sm:grid-cols-2">
            {([ ["timeoutSeconds", "Provider timeout (seconds)", 1, 20, 1], ["minimumCategoryConfidence", "Minimum category confidence", 0, 1, 0.01], ["retryLikely", "Retry-likely threshold", 0, 1, 0.01], ["changeRequired", "Change-required threshold", 0, 1, 0.01] ] as const).map(([key, label, min, max, step]) =>
              <label key={key} className="text-sm">{label}<Input type="number" required min={min} max={max} step={step} value={draft[key]} onChange={event => edit(key, Number(event.target.value))} /></label>)}
          </div></details>
        </CardContent></Card>
      </fieldset>
      <aside className="min-w-0 space-y-5"><Card><CardHeader><CardTitle>What leaves NimBus</CardTitle><p className="text-xs text-muted-foreground">Illustrative ERP failure — not live data or a production redaction check.</p></CardHeader><CardContent>
        <pre className="max-h-[540px] overflow-auto whitespace-pre-wrap break-all rounded-md bg-zinc-950 p-4 text-xs leading-relaxed text-amber-100">{draft.enabled ? JSON.stringify(preview, null, 2) : "No classification request is sent while disabled."}</pre>
        <p className="mt-3 text-xs text-muted-foreground">No operational IDs, timestamps, stack traces or credentials. The request also includes the configured model and four fixed questions. Evidence is size-limited before sending.</p>
      </CardContent></Card><Card><CardContent className="pt-5 text-sm text-muted-foreground">Saving creates a shared configuration revision. Active requests and this instance’s settings stay unchanged until restart. Restart every instance; this page cannot verify other instances. Disabling does not delete saved classifications.</CardContent></Card></aside>
    </div>
    {review && <section aria-label="Review settings" className="space-y-4 rounded-md border border-primary/40 p-5">
      <h3 className="font-semibold">Review changes</h3><p className="text-sm">{draft.enabled ? "Enable" : "Disable"} analysis · {draft.model} · payload {draft.includeEventPayload ? "included after redaction" : "excluded"} · {selected ? endpoints : "all authorized endpoints"}.</p>
      {draft.includeEventPayload && <label className="flex gap-2 text-sm"><input type="checkbox" checked={consent} onChange={event => setConsent(event.target.checked)} />I authorize sending redacted event payloads to TypeSafe; unmarked business data may remain.</label>}
      <div className="flex gap-2"><Button type="button" variant="outline" disabled={saving || loading} onClick={() => setReview(false)}>Back</Button><Button type="button" disabled={saving || loading || (draft.includeEventPayload && !consent)} onClick={() => void save()}>{saving ? "Saving…" : "Save settings"}</Button></div>
    </section>}
    <footer className="flex flex-wrap justify-between gap-3 border-t pt-4"><p className="text-sm text-muted-foreground">Site Owner only · restart required · no provider calls from this page</p><div className="flex gap-2"><Button type="button" variant="outline" disabled={saving || loading} onClick={() => { setLoading(true); setReview(false); setMessage(undefined); setReload(value => value + 1); }}>{loading ? "Loading…" : "Reload saved settings"}</Button><Button type="submit" disabled={saving || loading}>Review changes</Button></div></footer>
  </form>;
}

function SettingToggle({ label, description, checked, onChange }: { label: string; description: string; checked: boolean; onChange: (value: boolean) => void }) {
  return <div className="flex items-center justify-between gap-4"><div><p className="text-sm font-semibold">{label}</p><p className="text-xs text-muted-foreground">{description}</p></div><Toggle aria-label={label} checked={checked} onChange={onChange} /></div>;
}
