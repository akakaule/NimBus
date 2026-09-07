import { appendFileSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import { dirname, resolve } from 'node:path';
import { pathToFileURL } from 'node:url';

function scenarios(report) {
  const rows = [];
  function visit(suite, parents = []) {
    const context = [...parents, suite.title].filter(Boolean);
    for (const spec of suite.specs ?? []) {
      for (const test of spec.tests ?? []) {
        rows.push({ key: `${spec.id}:${test.projectName}`, title: spec.title,
          file: spec.file, line: spec.line, context: context.join(' / '), test });
      }
    }
    for (const child of suite.suites ?? []) visit(child, context);
  }
  for (const suite of report?.suites ?? []) visit(suite);
  return rows;
}

export function summarize(catalog, actual) {
  const expected = scenarios(catalog);
  const observed = scenarios(actual);
  const byKey = new Map(observed.map(row => [row.key, row]));
  const errors = [...(catalog?.errors ?? []), ...(actual?.errors ?? [])]
    .map(error => error.message ?? 'Playwright runner error');
  if (!actual) errors.push('No Playwright results were produced. Check setup and application startup logs.');
  if (!expected.length) errors.push('No scenarios were discovered in the test catalog.');
  const keys = new Set(expected.map(row => row.key));
  if (observed.some(row => !keys.has(row.key)) || byKey.size !== observed.length || keys.size !== expected.length)
    errors.push('Results do not match the discovered catalog, or contain duplicate scenario identities.');
  const counts = { passed: 0, failed: 0, flaky: 0, skipped: 0, unexecuted: 0 };
  const rows = expected.map(({ test: unused, ...row }) => {
    const test = byKey.get(row.key)?.test;
    const attempts = test?.results ?? [];
    const last = attempts.at(-1);
    let status = 'unexecuted';
    if (last) {
      if (last.status === 'skipped') status = 'skipped';
      else if (test.status === 'flaky') status = 'flaky';
      else if (test.status === 'expected' && last.status === 'passed') status = 'passed';
      else status = 'failed';
    }
    counts[status]++;
    return { ...row, status, attempts: attempts.length,
      duration: attempts.reduce((sum, attempt) => sum + (attempt.duration ?? 0), 0) };
  });
  return { rows, counts, errors, clean: rows.length > 0 && counts.passed === rows.length && !errors.length,
    startTime: actual?.stats?.startTime, duration: actual?.stats?.duration };
}

const escape = value => String(value ?? '').replace(/[&<>"']/g,
  char => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&#39;' })[char]);

export function renderHtml(result, provenance = {}) {
  const cards = Object.entries(result.counts).map(([status, count]) =>
    `<div class="stat ${status}"><strong>${count}</strong><span>${status}</span></div>`).join('');
  const rows = result.rows.map(row => `<tr data-status="${row.status}">
    <td><span class="badge ${row.status}">${row.status}</span></td>
    <td><strong>${escape(row.title)}</strong><small>${escape(row.context)} · ${escape(row.file)}:${escape(row.line)}</small></td>
    <td>${row.attempts}</td><td>${(row.duration / 1000).toFixed(1)}s</td></tr>`).join('');
  const errors = result.errors.map(error => `<li>${escape(error)}</li>`).join('');
  return `<!doctype html>
<html lang="en"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width,initial-scale=1">
<meta http-equiv="Content-Security-Policy" content="default-src 'none'; style-src 'unsafe-inline'; script-src 'unsafe-inline'; base-uri 'none'; form-action 'none'">
<title>CRM / ERP · E2E run results</title><style>
:root{font:16px/1.5 system-ui,sans-serif;color:#18352f;background:#f5f4ee}*{box-sizing:border-box}
body{margin:0}main{max-width:1200px;margin:auto;padding:48px 24px}h1{font-size:clamp(2rem,5vw,3.4rem);line-height:1.1;margin:12px 0}
.eyebrow{letter-spacing:.16em;text-transform:uppercase;font-size:.75rem}p{max-width:85ch}li{overflow-wrap:anywhere}.meta,small{color:#52645e;overflow-wrap:anywhere}
.stats{display:grid;grid-template-columns:repeat(5,1fr);gap:12px;margin:28px 0}.stat{background:white;border:1px solid #d4dcd4;border-top:4px solid;padding:18px;border-radius:10px}
.stat strong{display:block;font-size:2rem}.stat span{text-transform:capitalize}.passed{color:#176344}.failed{color:#a02f25}.flaky{color:#875000}.skipped,.unexecuted{color:#586166}
.filters{display:flex;gap:16px;flex-wrap:wrap;margin:24px 0}label{display:grid;gap:6px}input,select{font:inherit;padding:10px;border:1px solid #879a8f;border-radius:6px;background:white}
.table-wrap{overflow-x:auto;border:1px solid #d4dcd4;border-radius:10px;background:white}table{width:100%;border-collapse:collapse;text-align:left}th,td{padding:16px;border-bottom:1px solid #e4e8e2}th{background:#eaf0e9;font-size:.8rem;text-transform:uppercase}small{display:block;margin-top:4px;font-size:.75rem}.badge{font-size:.8rem;font-weight:700;text-transform:capitalize}td:nth-child(3),td:nth-child(4){white-space:nowrap}ul{padding:20px 40px;background:#fce7da;border-radius:10px}footer{margin-top:24px;color:#52645e;font-size:.85rem}
@media(max-width:600px){main{padding:24px 14px}.stats{grid-template-columns:repeat(2,1fr)}th,td{padding:10px}.filters label,input{width:100%}}
</style></head><body><main>
<div class="eyebrow">NimBus · Integration verification</div><h1>CRM / ERP test results</h1>
<p><strong>${result.clean ? 'Complete pass' : 'Run needs attention'}</strong> · ${result.rows.length} catalog scenarios.
Each scenario is counted once; retry attempts remain visible. Flaky, skipped and unexecuted scenarios do not qualify as a clean run.</p>
<p class="meta">Started: ${escape(result.startTime ?? 'not started')} · Duration: ${((result.duration ?? 0) / 60000).toFixed(1)} min<br>
Commit: ${escape(provenance.commit ?? 'local')}<br>Run: ${escape(provenance.run ?? 'local')}</p>
<section class="stats" aria-label="Outcome counts">${cards}</section>
${errors ? `<ul aria-label="Runner errors">${errors}</ul>` : ''}
<div class="filters"><label>Search scenarios<input id="search" type="search" placeholder="Handoff, CRM, outbox…"></label>
<label>Outcome<select id="status"><option value="">All outcomes</option>${Object.keys(result.counts).map(status => `<option>${status}</option>`).join('')}</select></label></div>
<p id="visible" role="status" aria-live="polite">${result.rows.length} scenarios shown</p>
<div class="table-wrap"><table><thead><tr><th>Outcome</th><th>Scenario</th><th>Attempts</th><th>Duration</th></tr></thead><tbody>${rows}</tbody></table></div>
<footer>Generated from this run's Playwright catalog and results. Open the accompanying Playwright report for failure details, screenshots and videos. Local emulator coverage does not establish Azure broker restart durability.</footer>
</main><script>
const search=document.querySelector('#search'),status=document.querySelector('#status'),rows=[...document.querySelectorAll('tbody tr')];
function filter(){let count=0;for(const row of rows){row.hidden=!!((status.value&&row.dataset.status!==status.value)||!row.textContent.toLowerCase().includes(search.value.toLowerCase()));if(!row.hidden)count++;}document.querySelector('#visible').textContent=count+' scenarios shown';}
search.addEventListener('input',filter);status.addEventListener('change',filter);
</script></body></html>`;
}

function readReport(path, errors) {
  try { return JSON.parse(readFileSync(path, 'utf8').replace(/^\uFEFF/, '')); }
  catch (error) { errors.push(`Cannot read ${path}: ${error.message}`); return undefined; }
}

if (process.argv[1] && import.meta.url === pathToFileURL(resolve(process.argv[1])).href) {
  const [catalogPath = 'artifacts/catalog.json', resultsPath = 'artifacts/results.json',
    outputPath = 'artifacts/test-coverage.html'] = process.argv.slice(2);
  const errors = [];
  const catalog = readReport(catalogPath, errors);
  const actual = readReport(resultsPath, errors);
  const result = summarize(catalog, actual);
  result.errors.push(...errors);
  result.clean &&= !errors.length;
  const run = process.env.GITHUB_RUN_ID ? `${process.env.GITHUB_SERVER_URL}/${process.env.GITHUB_REPOSITORY}/actions/runs/${process.env.GITHUB_RUN_ID}` : 'local';
  mkdirSync(dirname(outputPath), { recursive: true });
  writeFileSync(outputPath, renderHtml(result, { commit: process.env.GITHUB_SHA, run }));
  const summary = `CRM/ERP E2E: ${result.clean ? 'complete pass' : 'needs attention'}\n\n` +
    Object.entries(result.counts).map(([status, count]) => `- ${status}: ${count}`).join('\n') +
    `\n\nRunner/report errors: ${result.errors.length}. Download the crm-erp-e2e artifact and open test-coverage.html.\n`;
  console.log(summary);
  if (process.env.GITHUB_STEP_SUMMARY) appendFileSync(process.env.GITHUB_STEP_SUMMARY, summary);
  process.exitCode = result.clean ? 0 : 1;
}
