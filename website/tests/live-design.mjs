import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
const origin=process.env.KIMNOTES_TEST_ORIGIN||'https://note.kimlulu.com';
const root=new URL('../public/',import.meta.url);
const digest=bytes=>createHash('sha256').update(bytes).digest('hex');
async function request(path){return fetch(origin+path,{redirect:'manual',headers:{'Cache-Control':'no-cache'},signal:AbortSignal.timeout(45000)});}
for(const path of ['/','/features/screenshot-pin/','/features/ocr/','/features/reminders/','/changelog/','/privacy/','/login/','/notes/','/sitemap.xml','/favicon.ico','/apple-touch-icon.png']){
 const response=await request(path);assert.equal(response.status,200,path);
 const actual=Buffer.from(await response.arrayBuffer());const filename=path==='/'?'index.html':path.endsWith('/')?path.slice(1)+'index.html':path.slice(1);
 assert.equal(digest(actual),digest(await readFile(new URL(filename,root))),path+' content');
 if(path==='/login/'||path==='/notes/')assert.match(response.headers.get('cache-control'),/no-store/);
 else if(path.endsWith('/')||path==='/sitemap.xml')assert.doesNotMatch(response.headers.get('x-robots-tag')||'',/noindex/i,path);
 console.log('PASS '+path);
}
const homepage=await (await request('/')).text();
const css=homepage.match(/href="(\/assets\/marketing-[a-f0-9]{12}\.css)"/)[1];
const image=homepage.match(/property="og:image" content="https:\/\/note\.kimlulu\.com(\/assets\/showcase\/share-[^"]+\.png)"/)[1];
for(const path of [css,image]){
 const response=await request(path);assert.equal(response.status,200,path);assert.match(response.headers.get('cache-control'),/max-age=31536000.*immutable/,path);
 assert.equal(digest(Buffer.from(await response.arrayBuffer())),digest(await readFile(new URL(path.slice(1),root))),path+' hash');console.log('PASS '+path+' immutable');
}
const robots=await (await request('/robots.txt')).text();assert.match(robots,/User-agent: Baiduspider\s+Allow: \/\s+Disallow: \/login\/\s+Disallow: \/notes\//);assert.match(robots,/https:\/\/note\.kimlulu\.com\/sitemap.xml/);
const config=await (await request('/api/v1/apps/kimnotes/config')).json();assert.equal(config.appId,'kimnotes');assert.equal(config.humanVerification,null);assert.equal(config.webSessionIdleSeconds,1800);assert.equal(config.webSessionAbsoluteSeconds,28800);
const auth=await request('/api/v1/apps/kimnotes/auth/me');assert.equal(auth.status,401);assert.match(auth.headers.get('cache-control'),/no-store/);
const update=await (await request('/update.json')).json();const manifest=await (await request('/downloads/release.json')).json();assert.equal(update.version,manifest.version);assert.deepEqual(manifest,JSON.parse(await readFile(new URL('downloads/release.json',root),'utf8')));
const zip=await request('/downloads/'+manifest.file);assert.equal(zip.status,200);const bytes=Buffer.from(await zip.arrayBuffer());assert.equal(bytes.length,manifest.bytes);assert.equal(digest(bytes),manifest.sha256);
console.log('PASS robots, auth/session, update manifest and unchanged download');
