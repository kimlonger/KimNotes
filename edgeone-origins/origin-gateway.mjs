// Dedicated EdgeOne origin adapter. Public hosts and service bindings are
// selected by deployment config, never by a request-supplied header.
const sites = Object.freeze({
  guitar: { origin: 'origin-guitar.kimlulu.com', public: 'guitar.kimlulu.com' },
  notes: { origin: 'origin-note.kimlulu.com', public: 'note.kimlulu.com' },
});
const secretHeader = 'x-kim-origin-auth';

function sameSecret(a, b) {
  if (typeof a !== 'string' || typeof b !== 'string' || b.length < 32 || a.length !== b.length || a.length > 256) return false;
  let diff = 0;
  for (let i = 0; i < b.length; i++) diff |= a.charCodeAt(i) ^ b.charCodeAt(i);
  return diff === 0;
}
function deny(status) {
  return new Response('服务暂时不可用。', { status, headers: { 'Cache-Control': 'private, no-store', 'X-Content-Type-Options': 'nosniff' } });
}

export default {
  async fetch(request, env) {
    const site = sites[env.SITE];
    if (!site || !env.APP || typeof env.APP.fetch !== 'function') return deny(503);
    const url = new URL(request.url);
    if (url.protocol !== 'https:' || url.hostname !== site.origin || url.port || !sameSecret(request.headers.get(secretHeader), env.ORIGIN_SECRET)) return deny(403);

    // Existing Workers must still see the real public origin for their strict
    // canonical URL, Cookie and CSRF rules. Do not accept that origin from a
    // client-controlled header. Strip infrastructure-authentication material.
    url.hostname = site.public;
    const forwarded = new Request(url, request);
    for (const name of [secretHeader, 'host', 'x-kim-gateway', 'x-kim-client-ip', 'x-forwarded-host', 'x-original-host']) forwarded.headers.delete(name);
    try { return await env.APP.fetch(forwarded); }
    catch { return deny(502); }
  },
};
