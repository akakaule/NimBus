import assert from 'node:assert/strict';
import { test } from 'node:test';
import { redact } from './collect-diagnostics.mjs';

test('diagnostics redact the E2E key and connection string credentials', () => {
  const source = 'X-NimBus-E2E-Key: ephemeral-key\nServer=sql;Password=sql-secret;User Id=sa;\n' +
    'Endpoint=sb://localhost;SharedAccessKey=broker-secret;SharedAccessKeyName=test\n"password": "json-secret"';
  const safe = redact(source, 'ephemeral-key');
  for (const secret of ['ephemeral-key', 'sql-secret', 'broker-secret', 'json-secret'])
    assert.ok(!safe.includes(secret));
  assert.ok(safe.includes('Server=sql'));
});
