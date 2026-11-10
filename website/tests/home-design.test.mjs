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
test('homepage presents real capabilities, download details and visible setup',()=>{
 assert.equal(document.querySelector('#hero-title').textContent,'把想法，留在眼前。');
 assert.equal(document.querySelectorAll('.intro-actions a[download]').length,1);
 assert.equal(document.querySelector('.intro-actions .web-link').getAttribute('href'),'/notes/');
 assert.match(document.querySelector('.web-explainer').textContent,/同步.*便签/);
 assert.match(document.querySelector('[data-release-info]').textContent,new RegExp(`v${release.version}.*${Math.round(release.bytes/1024)} KB.*${release.published}`));
 assert.equal(document.querySelector('[data-release-sha]').textContent,release.sha256);
 assert.equal(document.querySelector('#guide').open,true);
 assert.equal(document.querySelectorAll('.feature-card').length,5);
 assert.equal(document.querySelectorAll('.feature-card [data-lightbox]').length,5,'all five product visuals can be enlarged');
 for(const path of ['/features/screenshot-pin/','/features/ocr/','/features/reminders/','/privacy/','/changelog/'])assert.ok(document.querySelector(`a[href="${path}"]`),path);
 assert.ok(document.querySelector('a[href="https://github.com/kimlonger/KimNotes/issues"]'));
 assert.match(document.querySelector('.intro-eyebrow').textContent,/Apache-2.0/);
 assert.doesNotMatch(html,/完整安装包/);
});
test('homepage uses sharp current product visuals and a horizontal social image',async()=>{
 const imgs=[...document.querySelectorAll('.intro-visual img,.feature-media img')];
 assert.ok(imgs.length>=8);assert.ok(document.querySelector('.demo-capture'));assert.ok(document.querySelector('.demo-pin'));
 for(const image of imgs)assert.ok((await stat(new URL(image.getAttribute('src').slice(1),root))).size>300);
 const image=new URL(document.querySelector('meta[property="og:image"]').content);
 assert.equal(document.querySelector('meta[property="og:image:width"]').content,'1200');assert.equal(document.querySelector('meta[property="og:image:height"]').content,'630');
 assert.equal(document.querySelector('meta[name="twitter:card"]').content,'summary_large_image');
 const file=await readFile(new URL(image.pathname.slice(1),root));assert.equal(file.readUInt32BE(16),1200);assert.equal(file.readUInt32BE(20),630);
 assert.ok((await stat(new URL('favicon.ico',root))).size>1000);const touch=await readFile(new URL('apple-touch-icon.png',root));assert.equal(touch.readUInt32BE(16),180);assert.equal(touch.readUInt32BE(20),180);
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
