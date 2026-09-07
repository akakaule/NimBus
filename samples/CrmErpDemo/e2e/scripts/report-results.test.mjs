import assert from 'node:assert/strict';
import { test } from 'node:test';
import { spawnSync } from 'node:child_process';
import { mkdtempSync, writeFileSync, rmSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve } from 'node:path';
import { fileURLToPath } from 'node:url';
import { summarize, renderHtml } from './report-results.mjs';

const spec = (id, status = 'expected', results = [{ status: 'passed', duration: 12 }]) => ({
  id, title: id, file: 'example.spec.ts', line: 1,
  tests: [{ projectName: 'chromium', status, results }],
});
const report = (...specs) => ({ suites: [{ title: 'Examples', specs }], errors: [] });

test('counts retries once and keeps failures, flaky, skipped and unexecuted distinct', () => {
  const catalog = report(...['pass', 'fail', 'retry', 'skip', 'missing'].map(id => spec(id)));
  const actual = report(spec('pass'), spec('fail', 'unexpected', [{ status: 'timedOut' }]),
    spec('retry', 'flaky', [{ status: 'failed' }, { status: 'passed' }]),
    spec('skip', 'skipped', [{ status: 'skipped' }]));
  const result = summarize(catalog, actual);
  assert.deepEqual(result.counts, { passed: 1, failed: 1, flaky: 1, skipped: 1, unexecuted: 1 });
  assert.equal(result.clean, false);
  assert.equal(result.rows.find(row => row.title === 'retry').attempts, 2);
});

test('only a complete passing catalog is clean', () => {
  assert.equal(summarize(report(spec('one')), report(spec('one'))).clean, true);
  assert.equal(summarize(report(spec('one')), undefined).clean, false);
  assert.equal(summarize(report(), report()).clean, false);
  assert.equal(summarize(report(spec('one')), report(spec('one', 'skipped', []))).clean, false);
});

test('global errors and catalog mismatches cannot produce a green report', () => {
  const actual = report(spec('one'));
  actual.errors.push({ message: 'Worker teardown failed' });
  assert.equal(summarize(report(spec('one')), actual).clean, false);
  const mismatch = summarize(report(spec('one')), report(spec('one'), spec('extra')));
  assert.equal(mismatch.clean, false);
  assert.match(mismatch.errors.join(' '), /catalog/i);
});

test('expected failures are still not successful scenario executions', () => {
  assert.equal(summarize(report(spec('one')), report(spec('one', 'expected', [{ status: 'failed' }]))).clean, false);
});

test('HTML encodes report text and provenance, including script termination', () => {
  const title = '</script><img src=x onerror=alert(1)>';
  const result = summarize(report(spec(title)), report(spec(title)));
  const html = renderHtml(result, { commit: '<unsafe>', run: 'javascript:alert(1)' });
  assert.ok(html.includes('&lt;/script&gt;&lt;img'));
  assert.ok(!html.includes('<img src=x'));
  assert.ok(!html.includes('href="javascript:'));
  assert.ok(html.includes('&lt;unsafe&gt;'));
});

test('matches a real Playwright list with executed retry, failure and skip results', () => {
  const directory = mkdtempSync(join(tmpdir(), 'nimbus-report-test-'));
  const cli = fileURLToPath(new URL('../node_modules/@playwright/test/cli.js', import.meta.url));
  const playwright = new URL('../node_modules/@playwright/test/index.mjs', import.meta.url).href;
  const config = join(directory, 'playwright.config.mjs');
  writeFileSync(config, 'export default { workers: 1, retries: 1, reporter: "json", outputDir: "./results" };');
  writeFileSync(join(directory, 'fixture.spec.mjs'), `import { test } from ${JSON.stringify(playwright)};
    test('passed', () => {});
    test('failed', () => { throw new Error('intentional report fixture failure'); });
    test('flaky', ({}, info) => { if (!info.retry) throw new Error('intentional retry'); });
    test.skip('skipped', () => {});`);
  try {
    const run = args => spawnSync(process.execPath, [cli, 'test', '--config', config, ...args],
      { cwd: directory, encoding: 'utf8', timeout: 30_000, windowsHide: true });
    const listing = run(['--list']);
    assert.equal(listing.status, 0, listing.stderr);
    const execution = run([]);
    assert.equal(execution.status, 1, execution.stderr);
    const result = summarize(JSON.parse(listing.stdout), JSON.parse(execution.stdout));
    assert.deepEqual(result.counts, { passed: 1, failed: 1, flaky: 1, skipped: 1, unexecuted: 0 });
    assert.equal(result.errors.length, 0);
  } finally {
    assert.ok(resolve(directory).startsWith(join(resolve(tmpdir()), 'nimbus-report-test-')));
    rmSync(directory, { recursive: true, force: true });
  }
});
