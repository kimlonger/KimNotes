import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import worker from './origin-gateway.mjs';

const secret = 'this-is-a-fake-test-secret-1234567890';
function env(site, calls) { return { SITE: site, ORIGIN_SECRET: secret, APP: { fetch: async r => { calls.push(r); return new Response('ok', {headers: {'set-cookie':'__Host-example=abc; Path=/; Secure; HttpOnly','cache-control':'private, no-store'}}); } } }; }
function request(host, options = {}) { return new Request(`https://${host}/api/v1/apps/kimnotes/sync/push?x=1`, options); }
test('each origin dispatches only to its configured public application', async () => {
 for (const [site, host, target] of [['notes','origin-note.kimlulu.com','note.kimlulu.com'],['guitar','origin-guitar.kimlulu.com','guitar.kimlulu.com']]) {
  const calls = []; const response = await worker.fetch(request(host, { method:'POST', headers: { 'x-kim-origin-auth':secret, Origin:`https://${target}`, Cookie:'session=test', 'Content-Type':'application/json', 'x-kim-gateway':'forged' }, body:'{"test":1}' }), env(site,calls));
  assert.equal(response.status,200); assert.equal(response.headers.get('cache-control'),'private, no-store'); assert.match(response.headers.get('set-cookie'),/__Host-example=/);
  assert.equal(calls.length,1); assert.equal(calls[0].url,`https://${target}/api/v1/apps/kimnotes/sync/push?x=1`); assert.equal(calls[0].headers.get('origin'),`https://${target}`); assert.equal(calls[0].headers.get('cookie'),'session=test'); assert.equal(calls[0].headers.get('x-kim-origin-auth'),null); assert.equal(calls[0].headers.get('x-kim-gateway'),null); assert.equal(await calls[0].text(),'{"test":1}');
 }
});
test('rejects untrusted requests and cross-site origin hosts before dispatch', async () => {
 const calls=[]; const e=env('notes',calls);
 for (const r of [request('origin-note.kimlulu.com'),request('origin-note.kimlulu.com',{headers:{'x-kim-origin-auth':'bad'}}),request('origin-guitar.kimlulu.com',{headers:{'x-kim-origin-auth':secret}}),new Request('http://origin-note.kimlulu.com/',{headers:{'x-kim-origin-auth':secret}})]) assert.equal((await worker.fetch(r,e)).status,403);
 assert.equal(calls.length,0);
});
test('missing configuration and upstream failure do not leak secrets', async () => {
 assert.equal((await worker.fetch(request('origin-note.kimlulu.com'),{})).status,503);
 const e=env('notes',[]);e.APP.fetch=async()=>{throw new Error(secret)};
 const response=await worker.fetch(request('origin-note.kimlulu.com',{headers:{'x-kim-origin-auth':secret}}),e);
 assert.equal(response.status,502);assert.equal(response.headers.get('cache-control'),'private, no-store');assert.doesNotMatch(await response.text(),/fake-test-secret/);
});
test('existing note worker still serves canonical assets and rejects unconfigured API', async () => {
 const source=await readFile(new URL('../website/worker.js',import.meta.url),'utf8');
 const original=(await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`)).default;
 const e=env('notes',[]);e.APP={fetch:r=>original.fetch(r,{ASSETS:{fetch:async r=>new Response(new URL(r.url).hostname)}})};
 const headers={'x-kim-origin-auth':secret};
 const home=await worker.fetch(new Request('https://origin-note.kimlulu.com/',{headers}),e);
 assert.equal(home.status,200);assert.equal(await home.text(),'note.kimlulu.com');
 const api=await worker.fetch(new Request('https://origin-note.kimlulu.com/api/v1/apps/kimnotes/auth/me',{headers}),e);
 assert.equal(api.status,503);assert.equal(api.headers.get('cache-control'),'private, no-store');
});
test('existing guitar worker preserves CSRF origin checks through the gateway', async () => {
 const {createWorker}=await import('../../guitar-learning/cloudflare/worker.js');
 const app=createWorker(async()=>Response.json({ok:true}));
 const appEnv={API_ORIGIN:'https://backend.example.com',API_PUBLIC_ORIGIN:'https://note.kimlulu.com',GATEWAY_SECRET:'fake-test-only',ASSETS:{fetch:async()=>new Response('guitar')}};
 const e=env('guitar',[]);e.APP={fetch:r=>app.fetch(r,appEnv)};
 const call=origin=>worker.fetch(new Request('https://origin-guitar.kimlulu.com/api/v1/apps/guitar/test',{method:'POST',headers:{'x-kim-origin-auth':secret,Origin:origin},body:'{}'}),e);
 assert.equal((await call('https://attacker.example')).status,403);
 const accepted=await call('https://guitar.kimlulu.com');assert.equal(accepted.status,200);assert.equal(accepted.headers.get('cache-control'),'private, no-store');
});
