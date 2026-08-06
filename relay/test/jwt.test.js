/**
 * Verifies the two pieces of the Worker that fail silently and expensively if wrong: the
 * PKCS#1 -> PKCS#8 conversion (GitHub hands out App keys in PKCS#1, WebCrypto only imports
 * PKCS#8), and RS256 JWT signing.
 *
 * Plain node, no test framework:  node relay/test/jwt.test.js
 */

import { generateKeyPairSync, createVerify } from 'node:crypto';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fileURLToPath } from 'node:url';
import { dirname, join } from 'node:path';

// The Worker is a default-export module, so pull the internals out by evaluating the source
// with the pieces under test re-exported. Keeping them unexported in the Worker itself avoids
// widening its public surface just for tests.
const workerSource = readFileSync(
  join(dirname(fileURLToPath(import.meta.url)), '..', 'src', 'index.js'),
  'utf8',
);
const testable = `${workerSource}\nexport { importRsaKey, createAppJwt, wrapPkcs1AsPkcs8, timingSafeEqual, clip };`;
const module = await import(
  `data:text/javascript;base64,${Buffer.from(testable).toString('base64')}`
);

const { publicKey, privateKey } = generateKeyPairSync('rsa', { modulusLength: 2048 });
const pkcs1Pem = privateKey.export({ type: 'pkcs1', format: 'pem' });
const pkcs8Pem = privateKey.export({ type: 'pkcs8', format: 'pem' });

let failures = 0;
async function check(name, fn) {
  try {
    await fn();
    console.log(`  ok   ${name}`);
  } catch (err) {
    failures++;
    console.error(`  FAIL ${name}\n       ${err.message}`);
  }
}

console.log('relay JWT tests');

await check('imports a PKCS#8 key as GitHub-compatible signing material', async () => {
  const key = await module.importRsaKey(pkcs8Pem);
  assert.equal(key.algorithm.name, 'RSASSA-PKCS1-v1_5');
});

await check('imports a PKCS#1 key — the format GitHub actually downloads', async () => {
  assert.match(pkcs1Pem, /BEGIN RSA PRIVATE KEY/);
  const key = await module.importRsaKey(pkcs1Pem);
  assert.equal(key.algorithm.name, 'RSASSA-PKCS1-v1_5');
});

await check('PKCS#1 conversion yields byte-identical DER to a real PKCS#8 export', async () => {
  const pkcs1Der = privateKey.export({ type: 'pkcs1', format: 'der' });
  const expected = privateKey.export({ type: 'pkcs8', format: 'der' });
  const actual = module.wrapPkcs1AsPkcs8(new Uint8Array(pkcs1Der));
  assert.deepEqual(Buffer.from(actual), Buffer.from(expected));
});

await check('signs a JWT that verifies against the public key', async () => {
  const jwt = await module.createAppJwt('123456', pkcs1Pem);
  const [header, payload, signature] = jwt.split('.');
  assert.ok(header && payload && signature);

  const verifier = createVerify('RSA-SHA256');
  verifier.update(`${header}.${payload}`);
  const ok = verifier.verify(
    publicKey,
    Buffer.from(signature.replace(/-/g, '+').replace(/_/g, '/'), 'base64'),
  );
  assert.ok(ok, 'signature did not verify');
});

await check('JWT claims match what GitHub requires', async () => {
  const jwt = await module.createAppJwt('123456', pkcs8Pem);
  const [headerB64, payloadB64] = jwt.split('.');
  const header = JSON.parse(Buffer.from(headerB64, 'base64url'));
  const claims = JSON.parse(Buffer.from(payloadB64, 'base64url'));

  assert.equal(header.alg, 'RS256');
  assert.equal(claims.iss, '123456');
  // GitHub rejects a JWT whose iat is in its future and caps lifetime at 10 minutes.
  const now = Math.floor(Date.now() / 1000);
  assert.ok(claims.iat < now, 'iat must be backdated for clock skew');
  assert.ok(claims.exp - claims.iat <= 600, 'lifetime must stay within 10 minutes');
});

await check('relay key comparison rejects mismatches and empty keys', () => {
  assert.equal(module.timingSafeEqual('secret', 'secret'), true);
  assert.equal(module.timingSafeEqual('secret', 'secrat'), false);
  assert.equal(module.timingSafeEqual('secret', 'secretx'), false);
  // An unconfigured RELAY_KEY must fail closed, not admit an empty header.
  assert.equal(module.timingSafeEqual('', ''), false);
});

await check('oversized fields are truncated rather than dropped', () => {
  assert.equal(module.clip('  hello  ', 100), 'hello');
  assert.equal(module.clip(undefined, 100), '');
  assert.match(module.clip('x'.repeat(50), 10), /^x{10}\n\n…\(truncated\)$/);
});

console.log(failures ? `\n${failures} failure(s)` : '\nall passed');
process.exit(failures ? 1 : 0);
