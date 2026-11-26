import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile,stat} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import {execFileSync} from 'node:child_process';
import {fileURLToPath} from 'node:url';
import {JSDOM} from 'jsdom';
const root=new URL('../public/',import.meta.url);
const html=await readFile(new URL('index.html',root),'utf8');
const document=new JSDOM(html).window.document;
const release=JSON.parse(await readFile(new URL('downloads/release.json',root),'utf8'));
test('V2 keeps the desktop download, signed-in entry, theme gallery and four feature walkthroughs',()=>{
 assert.equal(document.querySelector('#hero-title').textContent,'小羊便签');
 assert.equal(document.querySelector('.site-nav .login-link').getAttribute('href'),'/login/');
 assert.ok(document.querySelector('.hero a[download]'));
 assert.equal(document.querySelectorAll('#groups .sw').length,12);
 assert.deepEqual([...document.querySelectorAll('#groups > [role="group"]')].map(el=>el.getAttribute('aria-label')),['经典纯色','柔和渐变','风景背景']);
 const tabs=[...document.querySelectorAll('#tabs button')];
 assert.deepEqual(tabs.map(el=>el.textContent),['待办提醒','截图与贴图','文字识别','文件互传']);
 for(const tab of tabs) assert.ok(document.getElementById(tab.getAttribute('aria-controls')));
 assert.match(document.querySelector('[data-release-info]').textContent,new RegExp(release.version));
 assert.equal(document.querySelector('[data-release-size]').textContent,Math.round(release.bytes/1024)+' KB');
 assert.equal(document.querySelector('[data-release-date]').textContent,release.published+' 发布');
 assert.ok(document.querySelector('#note-trial textarea'));
 assert.ok(document.querySelector('#guide'));
 assert.equal(document.querySelector('[data-open-qq]').getAttribute('aria-controls'),'qq-group');
 assert.equal(document.querySelector('.community-body strong').textContent,'1135382915');
 for(const url of ['/features/reminders/','/features/screenshot-pin/','/features/ocr/','/privacy/','/changelog/']){
  assert.ok(document.querySelector('a[href="'+url+'"]'),url);
 }
 assert.doesNotMatch(document.body.textContent,/下载包校验|WPF 预览版/);
});
test('homepage has working product visuals, enlarged images and a horizontal social image',async()=>{
 const assets=[...document.querySelectorAll('img[src^="/assets/product/"],[data-lightbox]')];
 assert.ok(assets.length>25);
 for(const item of assets){
  const url=item.getAttribute('data-lightbox') || item.getAttribute('src');
  assert.ok((await stat(new URL(url.slice(1),root))).size>300,url);
 }
 assert.ok(document.querySelector('#image-preview [data-image-download]'));
 const image=new URL(document.querySelector('meta[property="og:image"]').content);
 assert.equal(document.querySelector('meta[property="og:image:width"]').content,'1200');assert.equal(document.querySelector('meta[property="og:image:height"]').content,'630');
 assert.equal(document.querySelector('meta[name="twitter:card"]').content,'summary_large_image');
 const file=await readFile(new URL(image.pathname.slice(1),root));assert.equal(file.readUInt32BE(16),1200);assert.equal(file.readUInt32BE(20),630);
 assert.ok((await stat(new URL('favicon.ico',root))).size>1000);const touch=await readFile(new URL('apple-touch-icon.png',root));assert.equal(touch.readUInt32BE(16),180);assert.equal(touch.readUInt32BE(20),180);
});
test('release links, checksum and desktop update manifest match the downloadable version',async()=>{
 const routes=['','features/screenshot-pin/','features/ocr/','features/reminders/','changelog/','privacy/'];
 for(const route of routes){
  const page=new JSDOM(await readFile(new URL(route+'index.html',root),'utf8')).window.document;
  const downloads=page.querySelectorAll('a[download][href^="/downloads/"]');
  assert.ok(downloads.length,route);
  for(const link of downloads)assert.equal(link.getAttribute('href'),`/downloads/${release.file}`,route);
 }
 const zip=await readFile(new URL(`downloads/${release.file}`,root));
 assert.equal(zip.length,release.bytes);assert.equal(createHash('sha256').update(zip).digest('hex'),release.sha256);
 const checksum=release.file.replace(/\.zip$/,'.sha256');
 assert.equal((await readFile(new URL('downloads/'+checksum,root),'utf8')).trim(),release.sha256+'  '+release.file);
 assert.equal(JSON.parse(await readFile(new URL('update.json',root),'utf8')).version,release.version);
 const application=JSON.parse(document.querySelector('script[type="application/ld+json"]').textContent);
 assert.equal(application.softwareVersion,release.version);assert.equal(application.dateModified,release.published);
});
test('indexable pages have working canonical links, content-addressed assets and an up-to-date sitemap',async()=>{
 const routes=['','features/screenshot-pin/','features/ocr/','features/reminders/','changelog/','privacy/'];
 for(const route of routes){
  const content=await readFile(new URL(route+'index.html',root),'utf8');const page=new JSDOM(content).window.document;
  assert.equal(page.querySelector('link[rel=canonical]').href,'https://note.kimlulu.com/'+route);
  assert.match(page.querySelector('meta[name=robots]').content,/index,follow/);assert.ok(page.querySelector('h1'));
  for(const link of page.querySelectorAll('link[href^="/assets/"],script[src^="/assets/"]')){
   const url=link.getAttribute('href')||link.getAttribute('src');const match=url.match(/-([a-f0-9]{12})\.(css|js)$/);assert.ok(match,url);
   const actual=createHash('sha256').update(await readFile(new URL(url.slice(1),root))).digest('hex');assert.ok(actual.startsWith(match[1]),url);
  }
 }
 execFileSync(process.execPath,[fileURLToPath(new URL('../edge-gateway/generate-sitemap.mjs',import.meta.url)),fileURLToPath(root),fileURLToPath(new URL('../edge-gateway/sitemap-state.json',import.meta.url)),'--check']);
 const robots=await readFile(new URL('edge-robots-snapshot.txt',root),'utf8');assert.match(robots,/User-agent: Baiduspider\s+Allow: \/\s+Disallow: \/login\/\s+Disallow: \/notes\//);
});
