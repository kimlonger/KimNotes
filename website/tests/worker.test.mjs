import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const source = await readFile(new URL('../worker.js',import.meta.url),'utf8');
const {default:worker} = await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const asset = {ASSETS:{fetch:async () => new Response('asset')}};
test('canonical URLs preserve paths and queries',async () => {
 const res = await worker.fetch(new Request('https://kimlulu.com/login/?mode=register'),asset);
 assert.equal(res.status,301); assert.equal(res.headers.get('location'),'https://note.kimlulu.com/login/?mode=register');
});
test('account pages are never cached; mutable JS has short caching',async () => {
 for (const path of ['/login/','/notes/']) {
  const res=await worker.fetch(new Request('https://note.kimlulu.com'+path),asset);
  assert.equal(res.headers.get('cache-control'),'private, no-store'); assert.equal(res.headers.get('x-robots-tag'),'noindex, nofollow');
 }
 for (const path of ['site-v2.js','site-v2.css','session-client.js','notes-time.js','notes-time.css','refined.css']) {
  const response=await worker.fetch(new Request('https://note.kimlulu.com/assets/'+path),asset);
  assert.equal(response.headers.get('cache-control'),'public, max-age=300, must-revalidate',path);
 }
});
test('unconfigured API fails explicitly',async () => {
 const res=await worker.fetch(new Request('https://note.kimlulu.com/api/v1/apps/kimnotes/auth/me'),asset);
 assert.equal(res.status,503); assert.equal((await res.json()).code,'backend_unavailable');
});
test('gateway replaces untrusted forwarding headers and preserves private session',async () => {
 const original=globalThis.fetch; let call;
 globalThis.fetch=async (url,options) => {call={url,options};return new Response('{}',{headers:{'content-type':'application/json','set-cookie':'__Host-kim_session_kimnotes=token; Path=/; HttpOnly; Secure; SameSite=Lax','cache-control':'public'}});};
 try {
  const res=await worker.fetch(new Request('https://note.kimlulu.com/api/v1/apps/kimnotes/auth/password',{method:'POST',headers:{'Origin':'https://note.kimlulu.com','CF-Connecting-IP':'192.0.2.1','X-Kim-Gateway':'attacker','X-Kim-Client-IP':'forged','X-Kim-Auth-Version':'2','X-Kim-Inline-Images':'1','Cookie':'old=token','Content-Type':'application/json'},body:'{}'}),{...asset,BACKEND_ORIGIN:'https://backend.example.com',BACKEND_GATEWAY_SECRET:'test-key'});
  assert.equal(call.url.href,'https://backend.example.com/api/v1/apps/kimnotes/auth/password');
  assert.equal(call.options.headers.get('x-kim-gateway'),'test-key');assert.equal(call.options.headers.get('x-kim-client-ip'),'192.0.2.1');
  assert.equal(call.options.headers.get('x-kim-auth-version'),'2');
  assert.equal(call.options.headers.get('x-kim-inline-images'),'1');
  assert.equal(call.options.headers.get('origin'),'https://note.kimlulu.com');assert.equal(call.options.redirect,'manual');
  assert.equal(res.headers.get('cache-control'),'private, no-store');assert.match(res.headers.get('set-cookie'),/HttpOnly/);
 } finally {globalThis.fetch=original;}
});
test('local preview has separate cookies and normalizes Wrangler Origin',async () => {
 const original=globalThis.fetch;let headers;
 globalThis.fetch=async (_,options) => {headers=options.headers;return new Response('{}',{headers:{'set-cookie':'__Host-kim_session_kimnotes=abc; Path=/; Secure; HttpOnly'}});};
 try {
  const res=await worker.fetch(new Request('http://note.kimlulu.com/api/v1/apps/kimnotes/auth/logout',{method:'POST',headers:{Origin:'http://note.kimlulu.com',Cookie:'kim_preview_session_kimnotes=old'},body:'{}'}),{...asset,LOCAL_PREVIEW:'true',BACKEND_ORIGIN:'https://backend.example.com',BACKEND_GATEWAY_SECRET:'test-key'});
  assert.equal(headers.get('origin'),'https://note.kimlulu.com');assert.equal(headers.get('cookie'),'__Host-kim_session_kimnotes=old');
  assert.match(res.headers.get('set-cookie'),/^kim_preview_session_kimnotes=abc;/);assert.doesNotMatch(res.headers.get('set-cookie'),/; Secure/);
 } finally {globalThis.fetch=original;}
});
test('upstream redirects are not passed through',async () => {
 const original=globalThis.fetch;globalThis.fetch=async () => Response.redirect('https://unexpected.example.com');
 try {const res=await worker.fetch(new Request('https://note.kimlulu.com/api/v1/apps/kimnotes/config'),{...asset,BACKEND_ORIGIN:'https://backend.example.com',BACKEND_GATEWAY_SECRET:'test-key'});assert.equal(res.status,503);assert.equal(res.headers.get('location'),null);}finally{globalThis.fetch=original;}
});
