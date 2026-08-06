/**
 * QuickMail bug-report relay.
 *
 * QuickMail used to POST bug reports straight to api.github.com with a personal access
 * token compiled into the shipped executable. That token was extractable by anyone who
 * downloaded the app, and every user-filed issue was authored by the maintainer.
 *
 * This Worker holds a GitHub App private key instead. The app posts a report here with a
 * low-value relay key; the Worker mints a one-hour installation token and creates the
 * issue under the App's bot identity. The relay key is still extractable from the binary,
 * but all it can do is file an issue on one repo — and it can be rotated without touching
 * any GitHub account.
 *
 * See issue #501.
 */

const REPO_OWNER = 'kellylford';
const REPO_NAME = 'QuickMail';
const USER_AGENT = 'QuickMail-BugReport-Relay';
const ISSUE_LABELS = ['bug', 'user-reported'];

// The app's own timeout is 15s (BugReportService), so anything slower than this is already
// a failure the user has seen. Bail out rather than hold the request open.
const GITHUB_TIMEOUT_MS = 10_000;

const MAX_BODY_BYTES = 64 * 1024;
const MAX_TITLE_CHARS = 200;
const MAX_FIELD_CHARS = 8_000;
const MAX_CONTACT_CHARS = 200;

export default {
  async fetch(request, env, ctx) {
    if (request.method !== 'POST') return text(405, 'Method not allowed.');

    const url = new URL(request.url);
    if (url.pathname !== '/report') return text(404, 'Not found.');

    if (!timingSafeEqual(request.headers.get('X-QuickMail-Key') || '', env.RELAY_KEY || '')) {
      return text(401, 'Unauthorized.');
    }

    // Fail open on a rate-limiter problem. A misconfigured binding blocking every bug report
    // is worse than the junk issues it would have stopped — junk is deletable, a report the
    // user could not file is gone.
    try {
      const ip = request.headers.get('CF-Connecting-IP') || 'unknown';
      const { success } = await env.RATE_LIMITER.limit({ key: ip });
      if (!success) return text(429, 'Too many reports from this address. Try again shortly.');
    } catch (err) {
      console.error('rate limiter unavailable, allowing request:', err);
    }

    const raw = await request.text();
    if (byteLength(raw) > MAX_BODY_BYTES) return text(413, 'Report too large.');

    let report;
    try {
      report = JSON.parse(raw);
    } catch {
      return text(400, 'Malformed JSON.');
    }

    const title = clip(report?.title, MAX_TITLE_CHARS);
    const body = clip(report?.body, MAX_FIELD_CHARS);
    if (!title || !body) return text(400, 'Both title and body are required.');

    const contact = clip(report?.contact, MAX_CONTACT_CHARS);
    const issueBody = contact ? `${body}\n\n### Contact\n${contact}\n` : body;

    try {
      const token = await getInstallationToken(env, ctx);
      const issue = await githubFetch(
        `https://api.github.com/repos/${REPO_OWNER}/${REPO_NAME}/issues`,
        token,
        { title, body: issueBody, labels: ISSUE_LABELS },
      );
      return Response.json({ issueUrl: issue.html_url, number: issue.number });
    } catch (err) {
      // The message may carry GitHub's response text; log it, but tell the caller nothing
      // it could use to probe the App's permissions.
      console.error('issue creation failed:', err);
      return text(502, 'Could not create the issue.');
    }
  },
};

// ---------------------------------------------------------------- GitHub auth

// Installation tokens last an hour. Caching per isolate saves two round-trips on the (rare)
// second report handled by the same instance; a cold isolate just mints a fresh one.
let cachedToken = null; // { token, expiresAt }

async function getInstallationToken(env, ctx) {
  const now = Date.now();
  if (cachedToken && cachedToken.expiresAt > now + 60_000) return cachedToken.token;

  const jwt = await createAppJwt(env.GITHUB_APP_ID, env.GITHUB_PRIVATE_KEY);
  const result = await githubFetch(
    `https://api.github.com/app/installations/${env.GITHUB_INSTALLATION_ID}/access_tokens`,
    jwt,
    undefined,
  );

  cachedToken = { token: result.token, expiresAt: Date.parse(result.expires_at) };
  return result.token;
}

async function createAppJwt(appId, privateKeyPem) {
  const key = await importRsaKey(privateKeyPem);
  const now = Math.floor(Date.now() / 1000);

  // iat is backdated 60s because GitHub rejects a JWT whose iat is in its future, and the
  // Worker's clock and GitHub's can differ by a few seconds.
  const header = b64url(JSON.stringify({ alg: 'RS256', typ: 'JWT' }));
  const payload = b64url(JSON.stringify({ iat: now - 60, exp: now + 540, iss: appId }));
  const signingInput = `${header}.${payload}`;

  const signature = await crypto.subtle.sign(
    'RSASSA-PKCS1-v1_5',
    key,
    new TextEncoder().encode(signingInput),
  );

  return `${signingInput}.${b64urlBytes(new Uint8Array(signature))}`;
}

/**
 * GitHub hands out App private keys in PKCS#1 ("BEGIN RSA PRIVATE KEY"), which WebCrypto
 * cannot import — it only takes PKCS#8. Rather than make key setup depend on remembering an
 * `openssl pkcs8 -topk8` incantation, accept either and wrap PKCS#1 here. A key pasted in
 * whichever form GitHub happened to give you should just work.
 */
async function importRsaKey(pem) {
  const isPkcs1 = /BEGIN RSA PRIVATE KEY/.test(pem);
  const der = pemBody(pem);
  const pkcs8 = isPkcs1 ? wrapPkcs1AsPkcs8(der) : der;

  return crypto.subtle.importKey(
    'pkcs8',
    pkcs8,
    { name: 'RSASSA-PKCS1-v1_5', hash: 'SHA-256' },
    false,
    ['sign'],
  );
}

function pemBody(pem) {
  const base64 = pem
    .replace(/-----BEGIN [^-]+-----/, '')
    .replace(/-----END [^-]+-----/, '')
    .replace(/\s+/g, '');
  const binary = atob(base64);
  const bytes = new Uint8Array(binary.length);
  for (let i = 0; i < binary.length; i++) bytes[i] = binary.charCodeAt(i);
  return bytes;
}

// PrivateKeyInfo ::= SEQUENCE { version INTEGER, algorithm AlgorithmIdentifier, key OCTET STRING }
function wrapPkcs1AsPkcs8(pkcs1) {
  const version = [0x02, 0x01, 0x00];
  // AlgorithmIdentifier for rsaEncryption (1.2.840.113549.1.1.1) with NULL parameters.
  const algorithm = [
    0x30, 0x0d, 0x06, 0x09, 0x2a, 0x86, 0x48, 0x86,
    0xf7, 0x0d, 0x01, 0x01, 0x01, 0x05, 0x00,
  ];
  const keyOctetString = [0x04, ...derLength(pkcs1.length), ...pkcs1];
  const contents = [...version, ...algorithm, ...keyOctetString];
  return new Uint8Array([0x30, ...derLength(contents.length), ...contents]);
}

function derLength(n) {
  if (n < 0x80) return [n];
  const bytes = [];
  for (let v = n; v > 0; v >>>= 8) bytes.unshift(v & 0xff);
  return [0x80 | bytes.length, ...bytes];
}

// ---------------------------------------------------------------- helpers

async function githubFetch(url, token, jsonBody) {
  const response = await fetch(url, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${token}`,
      Accept: 'application/vnd.github+json',
      'X-GitHub-Api-Version': '2022-11-28',
      'User-Agent': USER_AGENT,
      ...(jsonBody ? { 'Content-Type': 'application/json' } : {}),
    },
    body: jsonBody ? JSON.stringify(jsonBody) : undefined,
    signal: AbortSignal.timeout(GITHUB_TIMEOUT_MS),
  });

  if (!response.ok) {
    throw new Error(`GitHub ${response.status} for ${url}: ${await response.text()}`);
  }
  return response.json();
}

function clip(value, max) {
  if (typeof value !== 'string') return '';
  const trimmed = value.trim();
  return trimmed.length <= max ? trimmed : `${trimmed.slice(0, max)}\n\n…(truncated)`;
}

function byteLength(str) {
  return new TextEncoder().encode(str).length;
}

function timingSafeEqual(a, b) {
  if (a.length !== b.length || a.length === 0) return false;
  let diff = 0;
  for (let i = 0; i < a.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}

function b64url(str) {
  return b64urlBytes(new TextEncoder().encode(str));
}

function b64urlBytes(bytes) {
  let binary = '';
  for (const byte of bytes) binary += String.fromCharCode(byte);
  return btoa(binary).replace(/\+/g, '-').replace(/\//g, '_').replace(/=+$/, '');
}

function text(status, message) {
  return new Response(message, { status, headers: { 'Content-Type': 'text/plain' } });
}
