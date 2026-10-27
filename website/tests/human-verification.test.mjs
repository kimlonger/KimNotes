import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
const source=await readFile(new URL('../worker.js',import.meta.url),'utf8');
const {default:worker}=await import(`data:text/javascript;base64,${Buffer.from(source).toString('base64')}`);
const env={BACKEND_ORIGIN:'https://backend.example.com',BACKEND_GATEWAY_SECRET:'gateway-test',TURNSTILE_SITE_KEY:'0x-public-test',TURNSTILE_SECRET_KEY:'private-test'};
function request(path='login',token,extra={}) {return new Request(`https://note.kimlulu.com/api/v1/apps/kimnotes/auth/${path}`,{method:'POST',headers:{Origin:'https://note.kimlulu.com','Content-Type':'application/json',...(token?{'X-Kim-Turnstile':token}:{}),...extra},body:'{}'});}
async function mocked(handler,run) {const original=globalThis.fetch;globalThis.fetch=handler;try{await run();}finally{globalThis.fetch=original;}}
test('login, registration, reset and encoded paths require human verification, even with desktop headers',async () => {
 await mocked(async()=>{throw new Error('must not forward');},async()=>{
  for(const path of ['login','register','reset','reset/complete','%6cogin']) {
   const result=await worker.fetch(request(path,undefined,{'X-Kim-Client':'desktop','X-Kim-Turnstile-Verified':'true'}),env);
   assert.equal(result.status,403);assert.equal((await result.json()).code,'verification_required');
  }
  assert.equal((await worker.fetch(request('login','a'.repeat(2049)),env)).status,403);
 });
});
test('successful server validation checks IP, action and hostname; secret/token never reach Go',async () => {
 for(const action of ['login','register','reset','reset/complete']) {
  const calls=[];
  await mocked(async(url,options)=>{
   calls.push({url:String(url),options});
   if(String(url).includes('/siteverify')) {const body=new URLSearchParams(options.body);assert.equal(body.get('secret'),'private-test');assert.equal(body.get('response'),'single-use-test');assert.equal(body.get('remoteip'),'192.0.2.1');assert.equal(options.redirect,'manual');return Response.json({success:true,hostname:'note.kimlulu.com',action:action.split('/')[0]});}
   assert.equal(options.headers.get('X-Kim-Turnstile'),null);assert.equal(options.headers.get('X-Kim-Gateway'),'gateway-test');
   return Response.json({ok:true},{headers:{'Set-Cookie':'__Host-kim_session_kimnotes=session; Path=/; Max-Age=2592000; HttpOnly; Secure; SameSite=Lax'}});
  },async()=>{
   const result=await worker.fetch(request(action,'single-use-test',{'CF-Connecting-IP':'192.0.2.1'}),env);
   assert.equal(result.status,200);assert.equal(calls.length,2);assert.match(result.headers.get('Set-Cookie'),/Max-Age=2592000/);
  });
 }
});
test('failed, expired, replayed and wrong-domain/action tokens cannot reach the backend',async () => {
 for(const validation of [{success:false,'error-codes':['timeout-or-duplicate']},{success:true,hostname:'attacker.example',action:'login'},{success:true,hostname:'note.kimlulu.com',action:'register'},{success:true,hostname:'note.kimlulu.com'},{success:'true',hostname:'note.kimlulu.com',action:'login'}]) {
  let calls=0;
  await mocked(async()=>{calls++;return Response.json(validation);},async()=>{
   const result=await worker.fetch(request('login','test'),env);assert.equal(result.status,403);assert.equal(calls,1);
  });
 }
});
test('missing config, test keys in production, provider errors and outages fail closed',async () => {
 for(const config of [{...env,TURNSTILE_SECRET_KEY:''},{...env,TURNSTILE_SITE_KEY:'1x00000000000000000000AA'},{...env,TURNSTILE_SECRET_KEY:'1x0000000000000000000000000000000AA'}]) {
  await mocked(async()=>{throw new Error('must not fetch');},async()=>assert.equal((await worker.fetch(request('login','test'),config)).status,503));
 }
 for(const response of [()=>{throw new Error('network');},()=>new Response('down',{status:503}),()=>new Response('invalid json')]) {
  await mocked(async()=>response(),async()=>assert.equal((await worker.fetch(request('register','test'),env)).status,503));
 }
});
test('public config preserves web and desktop session policy without restoring the old 30-day override',async () => {
 await mocked(async()=>Response.json({appId:'kimnotes',emailVerification:false,webSessionIdleSeconds:1800,webSessionAbsoluteSeconds:28800,desktopAccessSeconds:900,desktopRefreshIdleSeconds:15552000}),async()=>{
  const result=await worker.fetch(new Request('https://note.kimlulu.com/api/v1/apps/kimnotes/config'),env), data=await result.json();
  assert.equal(data.humanVerification.siteKey,env.TURNSTILE_SITE_KEY);assert.equal(data.sessionLifetimeDays,undefined);assert.equal(data.webSessionIdleSeconds,1800);assert.equal(data.webSessionAbsoluteSeconds,28800);assert.equal(data.desktopAccessSeconds,900);assert.equal(data.desktopRefreshIdleSeconds,15552000);assert.ok(!JSON.stringify(data).includes('private-test'));assert.equal(result.headers.get('Cache-Control'),'private, no-store');
 });
 await mocked(async(_,options)=>{assert.equal(options.headers.get('Cookie'),'__Host-kim_session_kimnotes=existing');return Response.json({user:{id:'synthetic'}});},async()=>{
  const result=await worker.fetch(new Request('https://note.kimlulu.com/api/v1/apps/kimnotes/auth/me',{headers:{Cookie:'__Host-kim_session_kimnotes=existing'}}),env);assert.equal(result.status,200);
 });
});
test('local proxy forwards a token to the deployed gateway without validating twice',async()=>{
 let calls=0;
 await mocked(async(url,options)=>{calls++;assert.equal(String(url),'https://note.kimlulu.com/api/v1/apps/kimnotes/auth/login');assert.equal(options.headers.get('X-Kim-Turnstile'),'test');return Response.json({ok:true});},async()=>{
  const result=await worker.fetch(request('login','test'),{...env,LOCAL_PREVIEW:'true',BACKEND_ORIGIN:'https://note.kimlulu.com'});assert.equal(result.status,200);assert.equal(calls,1);
 });
});
