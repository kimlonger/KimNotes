import {readFile,writeFile,copyFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import path from 'node:path';
import {fileURLToPath} from 'node:url';
import {spawnSync} from 'node:child_process';
const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'..');
const pub=path.join(root,'public');
const html=['index.html','login/index.html','notes/index.html','features/screenshot-pin/index.html','features/ocr/index.html','features/reminders/index.html','changelog/index.html','privacy/index.html'];
const files=['site-v2.css','refined.css','marketing.css','site-v2.js','session-client.js','notes-time.css','notes-time.js','showcase/note-1420.png','showcase/capture-1420.png','showcase/pinned-1420.png','showcase/todo-1420.png','showcase/todo-new-1420.png','showcase/share-1200x630-v1420.png'];
const mappings=[];
for(const name of files){
 const original=path.join(pub,'assets',name);const bytes=await readFile(original);
 const hash=createHash('sha256').update(bytes).digest('hex').slice(0,12);
 const extension=path.extname(name),stem=name.slice(0,-extension.length);
 const hashed=`${stem}-${hash}${extension}`;
 await copyFile(original,path.join(pub,'assets',hashed));
 const escaped=stem.replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
 mappings.push([new RegExp(`/assets/${escaped}(?:-[a-f0-9]{12})?\\${extension}(?:\\?v=[^"\\s]*)?`,'g'),`/assets/${hashed}`]);
}
const release=JSON.parse(await readFile(path.join(pub,'downloads/release.json'),'utf8'));
for(const name of html){
 const file=path.join(pub,name);let value=(await readFile(file,'utf8')).replace(/\r\n/g,'\n');
 for(const [pattern,replacement] of mappings)value=value.replace(pattern,replacement);
 if(name==='index.html'){
  value=value.replace(/(<p class="download-meta" data-release-info>)[^<]*(<\/p>)/,`$1v${release.version} · 约 ${Math.round(release.bytes/1024)} KB · ${release.published} 更新$2`);
  value=value.replace(/(<code data-release-sha>)[^<]*(<\/code>)/,`$1${release.sha256}$2`);
  value=value.replace(/KimNotes-v\d+\.\d+\.\d+\.zip/g,release.file).replace(/("softwareVersion":")\d+\.\d+\.\d+("\s*)/,`$1${release.version}$2`);
 }
 await writeFile(file,value);
}
const result=spawnSync(process.execPath,[path.join(root,'edge-gateway/generate-sitemap.mjs')],{stdio:'inherit'});
if(result.status!==0)process.exit(result.status||1);
console.log(`Prepared ${files.length} content-addressed assets and ${html.length} HTML pages.`);
