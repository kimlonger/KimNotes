import { test } from 'node:test';
import assert from 'node:assert/strict';
import { createRequire } from 'node:module';
import { fileURLToPath } from 'node:url';
const require = createRequire(new URL('../website/package.json', import.meta.url));
const { Miniflare, convertV4MiniflareOptions } = require('miniflare');
const secret = 'isolated-test-only-secret-1234567890';

test('Cloudflare local runtime dispatches requests to the intended application binding', async () => {
 for (const [site, origin, publicHost] of [['notes','origin-note.kimlulu.com','note.kimlulu.com'], ['guitar','origin-guitar.kimlulu.com','guitar.kimlulu.com']]) {
  const mf = new Miniflare(convertV4MiniflareOptions({ workers: [{ name:'gateway', modules: true, scriptPath: fileURLToPath(new URL('./origin-gateway.mjs',import.meta.url)), compatibilityDate:'2026-10-01', bindings:{ SITE:site, ORIGIN_SECRET:secret }, serviceBindings:{ APP: async request => Response.json({ url:request.url, origin:request.headers.get('origin'), auth:request.headers.get('x-kim-origin-auth'), text:await request.text() },{headers:{'Cache-Control':'private, no-store'}}) } }] }));
  try {
   const denied=await mf.dispatchFetch(`https://${origin}/`); assert.equal(denied.status,403);
   const r=await mf.dispatchFetch(`https://${origin}/api/check`, { method:'POST', headers:{'x-kim-origin-auth':secret,Origin:`https://${publicHost}`},body:'sample' });
   assert.equal(r.status,200);const data=await r.json();assert.equal(data.url,`https://${publicHost}/api/check`);assert.equal(data.origin,`https://${publicHost}`);assert.equal(data.auth,null);assert.equal(data.text,'sample');assert.equal(r.headers.get('cache-control'),'private, no-store');
  } finally { await mf.dispose(); }
 }
});
