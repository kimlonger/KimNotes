import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const source=await readFile(new URL('../worker.js',import.meta.url),'utf8');
const {default:worker}=await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const env={BACKEND_ORIGIN:'https://backend.example.com',BACKEND_GATEWAY_SECRET:'gateway-test'};
function request(path='login',extra={}) {return new Request(`https://note.kimlulu.com/api/v1/apps/kimnotes/auth/${path}`,{method:'POST',headers:{Origin:'https://note.kimlulu.com','Content-Type':'application/json',...extra},body:JSON.stringify({email:'fixture@example.com',password:'fixture-password'})});}
async function mocked(handler,run) {const original=globalThis.fetch;globalThis.fetch=handler;try{await run();}finally{globalThis.fetch=original;}}
test('account actions go directly to the backend without any CAPTCHA configuration or provider calls',async()=>{
 for(const action of ['login','register','reset','reset/complete','%6cogin']) {
  let calls=0;
  await mocked(async(url,options)=>{
   calls++; assert.equal(new URL(url).host,'backend.example.com');
   assert.equal(options.headers.get('X-Kim-Gateway'),'gateway-test');
   assert.equal(options.headers.get('X-Kim-Turnstile'),null);
   const body=typeof options.body==='string'?options.body:new TextDecoder().decode(options.body);
   assert.equal(JSON.parse(body).password,'fixture-password');
   return Response.json({user:{id:'fixture'}},{headers:{'Set-Cookie':'__Host-kim_session_kimnotes=session; Path=/; Max-Age=1800; HttpOnly; Secure; SameSite=Lax'}});
  },async()=>{
   const response=await worker.fetch(request(action,{'X-Kim-Turnstile':'obsolete'}),env);
   assert.equal(response.status,200);assert.equal(calls,1);assert.match(response.headers.get('Set-Cookie'),/Max-Age=1800/);assert.equal(response.headers.get('Cache-Control'),'private, no-store');
  });
 }
});
test('incorrect passwords and backend rate limits remain errors',async()=>{
 for(const status of [401,403,409,429]) {
  await mocked(async()=>Response.json({code:status===429?'rate_limited':'invalid_credentials',message:'backend error'},{status,headers:{'Retry-After':'60'}}),async()=>{
   const response=await worker.fetch(request(),env);assert.equal(response.status,status);assert.equal(response.headers.get('Retry-After'),'60');assert.equal((await response.json()).message,'backend error');
  });
 }
});
test('config disables the obsolete challenge while retaining all session policies',async()=>{
 await mocked(async()=>Response.json({humanVerification:{provider:'old'},emailVerification:false,webSessionIdleSeconds:1800,webSessionAbsoluteSeconds:28800,desktopAccessSeconds:900,desktopRefreshIdleSeconds:15552000}),async()=>{
  const response=await worker.fetch(new Request('https://note.kimlulu.com/api/v1/apps/kimnotes/config'),env),data=await response.json();
  assert.equal(data.humanVerification,null);assert.equal(data.webSessionIdleSeconds,1800);assert.equal(data.webSessionAbsoluteSeconds,28800);assert.equal(data.desktopAccessSeconds,900);assert.equal(data.desktopRefreshIdleSeconds,15552000);assert.equal(data.sessionLifetimeDays,undefined);assert.equal(response.headers.get('Cache-Control'),'private, no-store');
 });
});
test('local preview forwards credentials once without requiring a verification token',async()=>{
 let calls=0;
 await mocked(async(url,options)=>{calls++;assert.equal(String(url),'https://note.kimlulu.com/api/v1/apps/kimnotes/auth/login');assert.equal(options.headers.get('X-Kim-Turnstile'),null);return Response.json({ok:true});},async()=>{
  const response=await worker.fetch(request(),{...env,LOCAL_PREVIEW:'true',BACKEND_ORIGIN:'https://note.kimlulu.com'});assert.equal(response.status,200);assert.equal(calls,1);
 });
});
test('backend unavailability still fails closed',async()=>{
 await mocked(async()=>{throw new Error('offline');},async()=>{
  const response=await worker.fetch(request(),env);assert.equal(response.status,503);assert.equal((await response.json()).code,'backend_unavailable');
 });
});
