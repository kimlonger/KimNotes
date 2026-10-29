// Read-only checks for the published website. No login or account writes.
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
const origin='https://note.kimlulu.com';
const root=new URL('../public/',import.meta.url);
const digest=bytes=>createHash('sha256').update(bytes).digest('hex');
async function request(path) {
  const response=await fetch(origin+path,{redirect:'manual',headers:{'Cache-Control':'no-cache'},signal:AbortSignal.timeout(30000)});
  return response;
}
const paths=['/','/notes/','/login/','/assets/site-v2.js','/assets/notes-time.js','/assets/notes-time.css','/assets/refined.css','/assets/showcase/hero-mint.png','/assets/showcase/hero-blue.png','/assets/showcase/hero-lilac.png'];
for(const path of paths) {
  const response=await request(path+(path.startsWith('/assets/')?'?v=20261029-white1':''));
  assert.equal(response.status,200,path);
  const data=new Uint8Array(await response.arrayBuffer());
  const local=await readFile(new URL(path==='/'?'index.html':path.endsWith('/')?path.slice(1)+'index.html':path.slice(1),root));
  assert.equal(digest(data),digest(local),`${path} must match the released local content`);
  if(['/notes/','/login/'].includes(path))assert.match(response.headers.get('cache-control'),/no-store/,path);
  if(/^\/assets\/(?:site-v2\.js|notes-time\.(?:css|js)|refined\.css)$/.test(path))assert.equal(response.headers.get('cache-control'),'public, max-age=300, must-revalidate',path);
  console.log(`PASS ${path}: published content and cache policy`);
}
const home=await (await request('/')).text();
assert.ok(home.includes('href="https://linux.do"'));
assert.ok(home.includes('20261029-white1'));
const config=await (await request('/api/v1/apps/kimnotes/config')).json();
assert.equal(config.appId,'kimnotes');assert.ok(config.humanVerification.siteKey);
assert.equal(config.webSessionIdleSeconds,1800);assert.equal(config.webSessionAbsoluteSeconds,28800);
assert.equal((await request('/api/v1/apps/kimnotes/auth/me')).status,401);
console.log('PASS app gateway, public verification config and anonymous session isolation');
const manifestResponse=await request('/downloads/release.json');assert.equal(manifestResponse.status,200);
const manifest=await manifestResponse.json();
assert.deepEqual(manifest,JSON.parse(await readFile(new URL('downloads/release.json',root),'utf8')));
assert.ok(home.includes(`/downloads/${manifest.file}`));
const zip=await request('/downloads/'+manifest.file);assert.equal(zip.status,200);
assert.match(zip.headers.get('content-disposition'),/attachment/);
const zipBytes=new Uint8Array(await zip.arrayBuffer());
assert.equal(zipBytes.length,manifest.bytes);assert.equal(digest(zipBytes),manifest.sha256);
assert.equal(digest(zipBytes),digest(await readFile(new URL('downloads/'+manifest.file,root))));
const checksumName=manifest.file.replace(/\.zip$/,'.sha256');
const checksum=await request('/downloads/'+checksumName);assert.equal(checksum.status,200);
assert.equal((await checksum.text()).trim(),`${manifest.sha256}  ${manifest.file}`);
assert.equal((await request('/downloads/KimNotes-v1.4.10.zip')).status,200);
console.log(`PASS Windows v${manifest.version} download bytes, manifest, checksum and attachment headers; previous download remains available`);
for(const domain of ['https://kimlulu.com','https://www.kimlulu.com']) {
  const response=await fetch(domain+'/notes/?demo=1',{redirect:'manual',signal:AbortSignal.timeout(30000)});
  assert.equal(response.status,301);assert.equal(response.headers.get('location'),origin+'/notes/?demo=1');
}
console.log('PASS canonical-domain redirects; preview mode is not enabled in production');
