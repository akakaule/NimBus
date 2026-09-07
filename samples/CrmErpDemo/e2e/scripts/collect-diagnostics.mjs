import { execFileSync } from 'node:child_process';
import { mkdirSync, writeFileSync } from 'node:fs';
import { resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

export function redact(text, key) {
  if (key) text = text.replaceAll(key, '[REDACTED]');
  return text
    .replace(/((?:Password|Pwd|SharedAccessKey)\s*=\s*)(?:"[^"]*"|'[^']*'|[^;\s"']+)/gi, '$1[REDACTED]')
    .replace(/("(?:password|E2E__Key|SharedAccessKey)"\s*:\s*")[^"]*/gi, '$1[REDACTED]');
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const appHost = process.env.E2E_APPHOST;
  if (!appHost) throw new Error('E2E_APPHOST must identify the demo AppHost.');
  mkdirSync('artifacts/logs', { recursive: true });
  for (const resource of ['servicebus', 'provisioner', 'crm-api', 'erp-api', 'crm-adapter',
    'erp-adapter', 'resolver', 'nimbus-ops', 'dataplatform-adapter', 'agent-zone', 'enrichment-agent']) {
    let output;
    try {
      output = execFileSync(process.platform === 'win32' ? 'aspire.exe' : 'aspire',
        ['logs', resource, '--apphost', appHost, '--tail', '300', '--non-interactive'],
        { encoding: 'utf8', timeout: 10_000, maxBuffer: 4 * 1024 * 1024, windowsHide: true, stdio: 'pipe' });
    } catch (error) {
      output = `Log collection failed for ${resource}.\n${error.stdout ?? ''}\n${error.stderr ?? ''}`;
    }
    writeFileSync(`artifacts/logs/${resource}.log`, redact(output, process.env.E2E__Key));
  }
}
