import http from 'node:http';
import { readFile, stat } from 'node:fs/promises';
import path from 'node:path';
import { Readable } from 'node:stream';
import { timingSafeEqual } from 'node:crypto';
import { isIP } from 'node:net';
import { pathToFileURL } from 'node:url';
import worker from './note-worker.mjs';

const mime = {'.html':'text/html','.js':'text/javascript','.css':'text/css','.svg':'image/svg+xml','.png':'image/png','.jpg':'image/jpeg','.jpeg':'image/jpeg','.ico':'image/vnd.microsoft.icon','.txt':'text/plain','.json':'application/json','.xml':'application/xml','.zip':'application/zip'};
function equalSecret(a,b) {
  if (typeof a !== 'string' || typeof b !== 'string') return false;
  const left=Buffer.from(a), right=Buffer.from(b);
  return right.length >= 32 && right.length <= 256 && left.length===right.length && timingSafeEqual(left,right);
}
function checkOrigin(raw) {
  let url; try { url = new URL(raw); } catch { throw Error('PUBLIC_ORIGIN must be an HTTPS origin'); }
  if (url.protocol !== 'https:' || url.username || url.password || url.pathname !== '/' || url.search || url.hash) throw Error('PUBLIC_ORIGIN must be an HTTPS origin');
  return url.origin;
}
function error(code, message) {
  return Response.json({code:'invalid_request',message},{status:code,headers:{'Cache-Control':'private, no-store','X-Content-Type-Options':'nosniff','X-Robots-Tag':'noindex, nofollow'}});
}
async function staticResponse(request, root) {
  const url=new URL(request.url);
  let pathname; try { pathname=decodeURIComponent(url.pathname); } catch { return error(400,'请求地址无效。'); }
  if (pathname.includes('\\') || pathname.split('/').some(part => part==='.' || part==='..') || /^\/(?:\.env|\.local|__local|tests|_headers)(?:\/|$)/.test(pathname)) return error(404,'页面不存在。');
  if (!['GET','HEAD'].includes(request.method)) return error(405,'请求方式无效。');
  let file=path.resolve(root,'.'+pathname);
  if (file!==root && !file.startsWith(root+path.sep)) return error(404,'页面不存在。');
  try {
    let info;
    try { info=await stat(file); }
    catch (missing) {
      // Cloudflare Assets auto-trailing-slash serves extensionless HTML
      // names (for example /404 -> /404.html) when the file exists.
      if (path.extname(file)) throw missing;
      file += '.html'; info=await stat(file);
    }
    if (info.isDirectory()) {
      if (!url.pathname.endsWith('/')) { url.pathname += '/'; return Response.redirect(url.toString(),307); }
      file=path.join(file,'index.html'); info=await stat(file);
    }
    if (!info.isFile()) throw Error('not file');
  } catch {
    // Cloudflare Assets returns its 404 page with HTTP 404 for missing paths.
    let body='页面不存在。'; try { body=await readFile(path.join(root,'404.html')); } catch {}
    return new Response(request.method==='HEAD'?null:body,{status:404,headers:{'Content-Type':'text/html','Cache-Control':'public, max-age=300, must-revalidate','X-Content-Type-Options':'nosniff','Referrer-Policy':'strict-origin-when-cross-origin','X-Frame-Options':'SAMEORIGIN'}});
  }
  let bytes=await readFile(file);
  // Generic asset names can change across releases. Long immutable caching is
  // safe for the explicitly versioned asset directory, while the worker below
  // separately handles versioned shared CSS/JS query strings.
  const versionedAsset=/^\/assets\/v\d+\.\d+\.\d+\//.test(pathname) || /^\/assets\/.*[-.][a-f0-9]{8,}\.[a-z0-9]+$/i.test(pathname);
  const headers=new Headers({'Content-Length':String(bytes.length),'Cache-Control':versionedAsset?'public, max-age=31536000, immutable':'public, max-age=300, must-revalidate','X-Content-Type-Options':'nosniff','Referrer-Policy':'strict-origin-when-cross-origin','X-Frame-Options':'SAMEORIGIN'});
  const extension=path.extname(file).toLowerCase();
  // The existing checksum endpoints intentionally have no Content-Type.
  if (extension!=='.sha256') headers.set('Content-Type',mime[extension] || 'application/octet-stream');
  if (/^\/(?:login|notes)(?:\/|$)/.test(pathname)) { headers.set('Cache-Control','no-store'); headers.set('X-Robots-Tag','noindex, nofollow'); }
  if (/^\/downloads\/KimNotes-v1\.4\.(?:10|18)\.zip$/.test(pathname)) headers.set('Content-Disposition',`attachment; filename="${path.basename(file)}"`);
  headers.set('Accept-Ranges','bytes');
  const range=request.headers.get('Range'); let status=200;
  // HEAD ignores Range. We do not expose a strong static validator, so an
  // If-Range request must fall back to the full representation.
  const match=request.method==='GET' && !request.headers.has('If-Range') ? range?.match(/^bytes=(\d*)-(\d*)$/) : null;
  if (match && (match[1] || match[2])) {
    const total=bytes.length;
    let start=match[1]?Number(match[1]):Math.max(0,total-Number(match[2]));
    let end=match[1]?(match[2]?Number(match[2]):total-1):total-1;
    if (!Number.isSafeInteger(start) || !Number.isSafeInteger(end) || start<0 || start>=total || end<start) { headers.set('Content-Range',`bytes */${total}`); headers.set('Content-Length','0'); return new Response(null,{status:416,headers}); }
    end=Math.min(end,total-1); bytes=bytes.subarray(start,end+1); status=206;
    headers.set('Content-Range',`bytes ${start}-${end}/${total}`); headers.set('Content-Length',String(bytes.length));
  }
  return new Response(request.method==='HEAD'?null:bytes,{status,headers});
}
export function createNoteServer({ root=path.resolve('public'), env=process.env }={}) {
  root=path.resolve(root);
  const origin=checkOrigin(env.PUBLIC_ORIGIN || 'https://note.kimlulu.com');
  const production=env.NODE_ENV==='production';
  if (production && env.LOCAL_PREVIEW==='true') throw Error('LOCAL_PREVIEW forbidden in production');
  if (production && (!env.EDGE_ORIGIN_SECRET || Buffer.byteLength(env.EDGE_ORIGIN_SECRET)<32 || Buffer.byteLength(env.EDGE_ORIGIN_SECRET)>256 || !env.BACKEND_ORIGIN || !env.BACKEND_GATEWAY_SECRET || Buffer.byteLength(env.BACKEND_GATEWAY_SECRET)<32)) throw Error('Required production configuration is missing');
  if (production && checkOrigin(env.BACKEND_ORIGIN)===origin) throw Error('Backend cannot point to public origin');
  const workerEnv={...env,PUBLIC_ORIGIN:origin,ASSETS:{fetch:request=>staticResponse(request,root)}};
  return http.createServer(async(req,res)=>{
    try {
      if (req.url==='/healthz') { res.writeHead(200,{'Content-Type':'application/json','Cache-Control':'no-store'}); return res.end('{"status":"ok"}'); }
      // The CDN's managed robots probe does not preserve the origin auth headers.
      // This file is intentionally public; keep every other route behind the gate.
      if (['GET','HEAD'].includes(req.method) && req.url?.split('?',1)[0] === '/robots.txt') {
        const response=await staticResponse(new Request(origin+req.url,{method:req.method}),root);
        res.writeHead(response.status,Object.fromEntries(response.headers));
        if (!response.body || req.method==='HEAD') return res.end();
        return Readable.fromWeb(response.body).on('error',()=>res.destroy()).pipe(res);
      }
      if (production || env.EDGE_ORIGIN_SECRET !== undefined) {
        if (!equalSecret(req.headers['x-kim-edge-origin'],env.EDGE_ORIGIN_SECRET) || typeof req.headers['eo-connecting-ip']!=='string' || !isIP(req.headers['eo-connecting-ip'])) {
          const response=error(403,'请求来源无效。'); res.writeHead(response.status,Object.fromEntries(response.headers)); return res.end(await response.text());
        }
      }
      if (!req.url?.startsWith('/') || req.url.startsWith('//')) { const response=error(400,'请求地址无效。'); res.writeHead(response.status,Object.fromEntries(response.headers)); return res.end(await response.text()); }
      const headers=new Headers();
      for (const [key,value] of Object.entries(req.headers)) if (value!==undefined && !['host','connection','transfer-encoding','cf-connecting-ip','x-kim-edge-origin','eo-connecting-ip'].includes(key)) headers.set(key,Array.isArray(value)?value.join(', '):value);
      // The origin secret gate authenticates the proxy which must overwrite EO-Connecting-IP.
      const client = env.EDGE_ORIGIN_SECRET ? req.headers['eo-connecting-ip'] : req.socket.remoteAddress;
      if (typeof client==='string' && isIP(client)) headers.set('CF-Connecting-IP',client);
      const init={method:req.method,headers};
      if (!['GET','HEAD'].includes(req.method)) { init.body=Readable.toWeb(req); init.duplex='half'; }
      const request=new Request(origin+req.url,init);
      const response=await worker.fetch(request,workerEnv);
      const out={}; for (const [key,value] of response.headers) if (key!=='set-cookie') out[key]=value;
      const cookies=response.headers.getSetCookie(); if (cookies.length) out['set-cookie']=cookies;
      res.writeHead(response.status,out);
      if (!response.body || req.method==='HEAD') return res.end();
      Readable.fromWeb(response.body).on('error',()=>res.destroy()).pipe(res);
    } catch(error) { if (!res.headersSent) { const response=errorResponse(); res.writeHead(response.status,Object.fromEntries(response.headers)); res.end(await response.text()); } else res.destroy(); }
  });
}
function errorResponse(){return error(502,'服务暂时不可用，请稍后重试。');}
if (process.argv[1] && import.meta.url===pathToFileURL(path.resolve(process.argv[1])).href) {
  const server=createNoteServer(); server.listen(Number(process.env.PORT || 4173),'0.0.0.0');
}
