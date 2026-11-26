import {readFile,writeFile,copyFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const pub=path.join(root,'public');
const homeOnly=process.argv.includes('--home-only');
const html=homeOnly ? ['index.html'] : ['index.html','login/index.html','notes/index.html','features/screenshot-pin/index.html','features/ocr/index.html','features/reminders/index.html','changelog/index.html','privacy/index.html'];
const homeAssets=['product-home.css','product-home.js','product-nav.css'];
const files=homeOnly ? homeAssets : [...homeAssets,'auth-register.css','site-v2.css','refined.css','marketing.css','site-v2.js','session-client.js','notes-time.css','notes-time.js','showcase/note-1420.png','showcase/capture-1420.png','showcase/pinned-1420.png','showcase/todo-1420.png','showcase/todo-new-1420.png','showcase/share-1200x630-v1420.png','community/qq-group-1135382915.jpg'];
const mappings=[];
// A download-only refresh must retain the asset versions chosen for each page.
for(const name of process.argv.includes('--release-only') ? [] : files){
 const original=path.join(pub,'assets',name);const bytes=await readFile(original);
 const hash=createHash('sha256').update(bytes).digest('hex').slice(0,12);
 const extension=path.extname(name),stem=name.slice(0,-extension.length);
 const hashed=`${stem}-${hash}${extension}`;
 await copyFile(original,path.join(pub,'assets',hashed));
 const escaped=stem.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
 mappings.push([new RegExp(`/assets/${escaped}(?:-[a-f0-9]{12})?\\${extension}(?:\\?v=[^"\\s]*)?`,'g'),`/assets/${hashed}`]);
}
const release=JSON.parse(await readFile(path.join(pub,'downloads/release.json'),'utf8'));
const headerTemplate = (await readFile(path.join(root,'tools/product-header.html'),'utf8')).replace(/\r\n/g,'\n').trim();
const productHeader = /<header class="product-header">[\s\S]*?<\/header>/;
for(const name of html){
 const file=path.join(pub,name);let value=(await readFile(file,'utf8')).replace(/\r\n/g,'\n');
 if (name === 'index.html' || name === 'changelog/index.html') {
  if (!productHeader.test(value)) throw new Error(`Missing product header in ${name}`);
  value = value.replace(productHeader, headerTemplate
   .replace('{{changelogCurrent}}', name === 'changelog/index.html' ? ' aria-current="page"' : '')
   .replace('{{download}}', release.file));
 }
 for(const [pattern,replacement] of mappings)value=value.replace(pattern,replacement);
 value=value.replace(/KimNotes-v\d+\.\d+\.\d+\.zip/g,release.file)
  .replace(/(https:\/\/github\.com\/kimlonger\/KimNotes\/releases\/download\/)v\d+\.\d+\.\d+\//g,`$1v${release.version}/`);
 if(name==='index.html'){
  const size=`${Math.round(release.bytes/1024)} KB`;
  value=value.replace(/(<b data-release-info>)[^<]*(<\/b>)/, `$1v${release.version} · 约 ${size}$2`)
   .replace(/(<b data-release-size>)[^<]*(<\/b>)/, `$1${size}$2`)
   .replace(/(<span data-release-date>)[^<]*(<\/span>)/, `$1${release.published} 发布$2`);
  value=value.replace(/("softwareVersion":")\d+\.\d+\.\d+("\s*)/,`$1${release.version}$2`)
   .replace(/("dateModified":")[^"]+("\s*)/,`$1${release.published}$2`);
 }
 await writeFile(file,value);
}
const result=spawnSync(process.execPath,[path.join(root,'edge-gateway/generate-sitemap.mjs')],{stdio:'inherit'});
if(result.status!==0)process.exit(result.status||1);
console.log(`Prepared ${mappings.length} content-addressed assets and ${html.length} HTML pages.`);
