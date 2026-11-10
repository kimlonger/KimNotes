import test from 'node:test';
import assert from 'node:assert/strict';
import http from 'node:http';
import { gzipSync } from 'node:zlib';
import { mkdtemp, mkdir, writeFile, rm } from 'node:fs/promises';
import os from 'node:os';
import path from 'node:path';
import { createNoteServer } from '../src/server.mjs';

const token='e'.repeat(64);
const env={NODE_ENV:'production',PUBLIC_ORIGIN:'https://note-test.example.com',EDGE_ORIGIN_SECRET:token,BACKEND_ORIGIN:'https://backend.invalid',BACKEND_GATEWAY_SECRET:'g'.repeat(64),TURNSTILE_SITE_KEY:'0xreal-public-test-fixture',TURNSTILE_SECRET_KEY:'non-dummy-fixture-secret'};
let dir,server,port;
const originalFetch=globalThis.fetch;
let calls=[];
let siteverify={success:true,hostname:'note-test.example.com',action:'login'};
let upstreamStatus=200;
let upstreamBody=JSON.stringify({emailVerification:false});
let upstreamStream=false;

function request(method,url,headers={},body) {
  return new Promise((resolve,reject)=>{
    const req=http.request({host:'127.0.0.1',port,path:url,method,headers},res=>{
      const chunks=[]; res.on('data',x=>chunks.push(x)); res.on('end',()=>resolve({status:res.statusCode,headers:res.headers,body:Buffer.concat(chunks)}));
    }); req.on('error',reject); req.end(body);
  });
}
const trusted={'x-kim-edge-origin':token,'eo-connecting-ip':'198.51.100.10'};

test.before(async()=>{
  dir=await mkdtemp(path.join(os.tmpdir(),'note-gateway-test-'));
  await mkdir(path.join(dir,'login')); await mkdir(path.join(dir,'notes')); await mkdir(path.join(dir,'assets')); await mkdir(path.join(dir,'assets','v1.4.11')); await mkdir(path.join(dir,'downloads'));
  await writeFile(path.join(dir,'index.html'),'home'); await writeFile(path.join(dir,'login','index.html'),'login');
  await writeFile(path.join(dir,'robots.txt'),'User-agent: *\nDisallow: /login/\nSitemap: https://note-test.example.com/sitemap.xml\n');
  await writeFile(path.join(dir,'edge-robots-snapshot.txt'),'User-agent: *\nDisallow: /login/\nSitemap: https://note-test.example.com/sitemap.xml\n');
  await writeFile(path.join(dir,'notes','index.html'),'notes'); await writeFile(path.join(dir,'assets','site-v2.js'),'script'); await writeFile(path.join(dir,'404.html'),'missing');
  await writeFile(path.join(dir,'downloads','KimNotes-v1.4.18.zip'),'0123456789');
  await writeFile(path.join(dir,'downloads','KimNotes-v1.4.18.sha256'),'sha'); await writeFile(path.join(dir,'assets','v1.4.11','x.png'),'png');
  await writeFile(path.join(dir,'assets','photo.png'),'png'); await writeFile(path.join(dir,'assets','bundle-a1b2c3d4.js'),'js');
  globalThis.fetch=async(url,init={})=>{
    const value=String(url); calls.push({url:value,init});
    if (value.startsWith('https://challenges.cloudflare.com/')) return Response.json(siteverify);
    if (upstreamStream) {
      const stream=new ReadableStream({start(controller){controller.enqueue(new Uint8Array([1,2])); controller.enqueue(new Uint8Array([3,4,5])); controller.close();}});
      return new Response(stream,{status:200,headers:{'Content-Type':'image/png','Content-Length':'5','Content-Disposition':'inline; filename="fixture.png"','ETag':'"fixture"'}});
    }
    const headers=new Headers({'Content-Type':'application/json','Set-Cookie':'__Host-kim_session_kimnotes=one; Path=/; Secure; HttpOnly'});
    headers.append('Set-Cookie','other=two; Path=/; Secure; HttpOnly');
    return new Response(upstreamBody,{status:upstreamStatus,headers});
  };
  server=createNoteServer({root:dir,env}); await new Promise(resolve=>server.listen(0,'127.0.0.1',resolve)); port=server.address().port;
});
test.after(async()=>{globalThis.fetch=originalFetch; await new Promise(resolve=>server.close(resolve)); await rm(dir,{recursive:true,force:true});});
test.beforeEach(()=>{calls=[]; siteverify={success:true,hostname:'note-test.example.com',action:'login'}; upstreamStatus=200; upstreamBody=JSON.stringify({emailVerification:false}); upstreamStream=false;});

test('production requires edge secret and rejects dummy verification credentials',()=>{
  assert.throws(()=>createNoteServer({env:{NODE_ENV:'production'}}),/configuration/);
  assert.throws(()=>createNoteServer({env:{...env,LOCAL_PREVIEW:'true'}}),/forbidden/);
  assert.throws(()=>createNoteServer({env:{...env,TURNSTILE_SITE_KEY:'1x00000000000000000000AA'}}),/Test Turnstile/);
});
test('edge authentication and IP headers must both be valid',async()=>{
  assert.equal((await request('GET','/healthz')).status,200);
  assert.equal((await request('GET','/')).status,403);
  assert.equal((await request('GET','/',{'x-kim-edge-origin':'wrong','eo-connecting-ip':'198.51.100.1'})).status,403);
  assert.equal((await request('GET','/',{'x-kim-edge-origin':token,'eo-connecting-ip':'spoofed'})).status,403);
  assert.equal((await request('GET','/',trusted)).body.toString(),'home');
});
test('only exact public robots GET and HEAD bypass origin authentication',async()=>{
  let r=await request('GET','/robots.txt'); assert.equal(r.status,200); assert.match(r.body.toString(),/Sitemap:/);
  r=await request('GET','/robots.txt?probe=1'); assert.equal(r.status,200); assert.match(r.body.toString(),/Disallow: \/login\//);
  r=await request('HEAD','/robots.txt'); assert.equal(r.status,200); assert.equal(r.body.length,0);
  for (const [method,url] of [['POST','/robots.txt'],['GET','/robots.txt/'],['GET','/robots%2etxt'],['GET','/login/'],['GET','/api/v1/apps/kimnotes/config']]) {
    assert.equal((await request(method,url)).status,403,`${method} ${url}`);
  }
  assert.equal(calls.length,0);
});
test('robots snapshot alias requires the existing edge authentication',async()=>{
  let r=await request('GET','/edge-robots-snapshot.txt'); assert.equal(r.status,403);
  r=await request('GET','/edge-robots-snapshot.txt',{'x-kim-edge-origin':'wrong','eo-connecting-ip':'198.51.100.10'}); assert.equal(r.status,403);
  r=await request('GET','/edge-robots-snapshot.txt',trusted); assert.equal(r.status,200); assert.match(r.body.toString(),/Sitemap:/);
  assert.equal(r.headers['content-type'],'text/plain');
  assert.equal((await request('POST','/edge-robots-snapshot.txt',trusted)).status,405);
  assert.equal(calls.length,0);
});
test('static pages preserve redirects, safety and cache controls',async()=>{
  let r=await request('GET','/login',trusted); assert.equal(r.status,307); assert.equal(r.headers.location,'https://note-test.example.com/login/'); assert.match(r.headers['cache-control'],/no-store/);
  r=await request('GET','/login/',trusted); assert.equal(r.status,200); assert.match(r.headers['cache-control'],/no-store/); assert.equal(r.headers['x-robots-tag'],'noindex, nofollow');
  r=await request('GET','/assets/site-v2.js?v=1',trusted); assert.match(r.headers['cache-control'],/max-age=3600/);
  r=await request('GET','/assets/site-v2.js',trusted); assert.match(r.headers['cache-control'],/max-age=300/);
  r=await request('GET','/assets/photo.png',trusted); assert.match(r.headers['cache-control'],/max-age=300/);
  r=await request('GET','/assets/v1.4.11/x.png',trusted); assert.match(r.headers['cache-control'],/max-age=31536000/);
  r=await request('GET','/assets/bundle-a1b2c3d4.js',trusted); assert.match(r.headers['cache-control'],/max-age=31536000/);
  r=await request('GET','/.env',trusted); assert.equal(r.status,404);
  r=await request('GET','/404',trusted); assert.equal(r.status,200); assert.equal(r.body.toString(),'missing');
});
test('downloads preserve content type, attachment and single byte ranges',async()=>{
  let r=await request('GET','/downloads/KimNotes-v1.4.18.zip',{...trusted,'range':'bytes=2-5'});
  assert.equal(r.status,206); assert.equal(r.body.toString(),'2345'); assert.equal(r.headers['content-range'],'bytes 2-5/10'); assert.equal(r.headers['content-type'],'application/zip'); assert.match(r.headers['content-disposition'],/attachment/);
  r=await request('GET','/downloads/KimNotes-v1.4.18.zip',{...trusted,'range':'bytes=99-'}); assert.equal(r.status,416);
  r=await request('HEAD','/downloads/KimNotes-v1.4.18.zip',{...trusted,'range':'bytes=2-5'}); assert.equal(r.status,200); assert.equal(r.headers['content-length'],'10'); assert.equal(r.headers['content-range'],undefined);
  r=await request('GET','/downloads/KimNotes-v1.4.18.zip',{...trusted,'range':'bytes=2-5','if-range':'"outdated"'}); assert.equal(r.status,200); assert.equal(r.body.toString(),'0123456789');
  r=await request('GET','/downloads/KimNotes-v1.4.18.sha256',trusted); assert.equal(r.headers['content-type'],undefined);
});
test('config injects verification and API forwards precise client context',async()=>{
  const r=await request('GET','/api/v1/apps/kimnotes/config',{...trusted,'cf-connecting-ip':'8.8.8.8','authorization':'Bearer fixture','x-kim-expected-user':'expected'});
  assert.equal(r.status,200); const v=JSON.parse(r.body); assert.equal(v.humanVerification.provider,'turnstile'); assert.equal(v.webSessionIdleSeconds,1800);
  assert.match(r.headers['cache-control'],/no-store/); assert.equal(r.headers['set-cookie'].length,2);
  const h=calls[0].init.headers; assert.equal(h.get('X-Kim-Client-IP'),'198.51.100.10'); assert.equal(h.get('X-Kim-Expected-User'),'expected'); assert.equal(h.get('Authorization'),'Bearer fixture');
});
test('web login requires verified hostname, action and token',async()=>{
  let r=await request('POST','/api/v1/apps/kimnotes/auth/login',{...trusted,'content-type':'application/json'},'{}'); assert.equal(r.status,403); assert.equal(calls.length,0);
  siteverify={success:true,hostname:'evil.example',action:'login'};
  r=await request('POST','/api/v1/apps/kimnotes/auth/login',{...trusted,'x-kim-turnstile':'fixture','content-type':'application/json'},'{}'); assert.equal(r.status,403); assert.equal(calls.length,1);
  siteverify={success:true,hostname:'note-test.example.com',action:'register'}; calls=[];
  r=await request('POST','/api/v1/apps/kimnotes/auth/login',{...trusted,'x-kim-turnstile':'fixture','content-type':'application/json'},'{}'); assert.equal(r.status,403); assert.equal(calls.length,1);
  siteverify={success:true,hostname:'note-test.example.com',action:'login'}; calls=[];
  r=await request('POST','/api/v1/apps/kimnotes/auth/login',{...trusted,'x-kim-turnstile':'fixture','content-type':'application/json'},'{}'); assert.equal(r.status,200); assert.equal(calls.length,2);
  assert.equal(new URLSearchParams(calls[0].init.body).get('remoteip'),'198.51.100.10');
  assert.equal(calls[1].init.headers.get('X-Kim-Turnstile'),null);
});
test('desktop login bypasses website CAPTCHA and retains its protocol headers',async()=>{
  const r=await request('POST','/api/v1/apps/kimnotes/auth/desktop/login',{...trusted,'x-kim-client':'desktop','x-kim-auth-version':'2','content-type':'application/json'},'{}');
  assert.equal(r.status,200); assert.equal(calls.length,1); assert.equal(calls[0].init.headers.get('X-Kim-Client'),'desktop'); assert.equal(calls[0].init.headers.get('X-Kim-Auth-Version'),'2');
});
test('private attachment streams all chunks and preserves essential headers',async()=>{
  upstreamStream=true;
  const r=await request('GET','/api/v1/apps/kimnotes/attachments/fixture',{...trusted,'authorization':'Bearer fixture'});
  assert.equal(r.status,200); assert.deepEqual([...r.body],[1,2,3,4,5]); assert.equal(r.headers['content-type'],'image/png');
  assert.equal(r.headers['content-length'],'5'); assert.equal(r.headers.etag,'"fixture"'); assert.match(r.headers['cache-control'],/no-store/); assert.match(r.headers['content-disposition'],/inline/);
});
test('real gzip upstream is decoded without preserving its compressed length',async()=>{
  const payload=Buffer.from('中文附件内容-migration-fixture-'.repeat(500));
  const compressed=gzipSync(payload);
  assert.notEqual(payload.length,compressed.length);
  const upstream=http.createServer((req,res)=>{res.writeHead(200,{'content-type':'application/octet-stream','content-encoding':'gzip','content-length':String(compressed.length)}); res.end(compressed);});
  await new Promise(resolve=>upstream.listen(0,'127.0.0.1',resolve));
  const upstreamPort=upstream.address().port;
  const saved=globalThis.fetch;
  globalThis.fetch=async(url,init)=>originalFetch(`http://127.0.0.1:${upstreamPort}/`,init);
  try {
    const r=await request('GET','/api/v1/apps/kimnotes/attachments/fixture',trusted);
    assert.equal(r.status,200); assert.deepEqual(r.body,payload); assert.equal(r.headers['content-encoding'],undefined);
    assert.notEqual(r.headers['content-length'],String(compressed.length)); assert.match(r.headers['cache-control'],/no-store/);
  } finally {
    globalThis.fetch=saved; upstream.closeAllConnections(); await new Promise(resolve=>upstream.close(resolve));
  }
});
test('oversize requests are rejected and upstream redirects never leak credentials',async()=>{
  let r=await request('POST','/api/v1/apps/kimnotes/attachments',{...trusted,'content-type':'application/octet-stream'},Buffer.alloc(10*1024*1024+1));
  assert.equal(r.status,413); assert.equal(calls.length,0);
  upstreamStatus=302; upstreamBody=''; r=await request('GET','/api/v1/apps/kimnotes/notes',trusted); assert.equal(r.status,503);
});
