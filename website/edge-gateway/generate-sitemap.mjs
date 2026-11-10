import {readFile,writeFile} from 'node:fs/promises';
import {createHash} from 'node:crypto';
import path from 'node:path';
import {fileURLToPath} from 'node:url';

const site=path.resolve(process.argv[2] || path.join(path.dirname(fileURLToPath(import.meta.url)),'../public'));
const stateFile=path.resolve(process.argv[3] || path.join(path.dirname(fileURLToPath(import.meta.url)),'sitemap-state.json'));
const check=process.argv.includes('--check');
const today=new Intl.DateTimeFormat('en-CA',{timeZone:'Asia/Shanghai',year:'numeric',month:'2-digit',day:'2-digit'}).format(new Date());
const routes=['/','/features/screenshot-pin/','/features/ocr/','/features/reminders/','/changelog/','/privacy/'];
let state={};try {state=JSON.parse(await readFile(stateFile,'utf8'));} catch(error) {if(error.code!=='ENOENT')throw error;}
const next={};
for(const route of routes) {
 const filename=path.join(site,route==='/'?'index.html':route.slice(1)+'index.html');
 const hash=createHash('sha256').update(await readFile(filename)).digest('hex');
 const prior=state[route];next[route]={sha256:hash,lastmod:prior?.sha256===hash ? prior.lastmod : today};
}
const xml='<?xml version="1.0" encoding="UTF-8"?>\n<urlset xmlns="http://www.sitemaps.org/schemas/sitemap/0.9">\n'+routes.map(route=>`  <url><loc>https://note.kimlulu.com${route}</loc><lastmod>${next[route].lastmod}</lastmod></url>\n`).join('')+'</urlset>\n';
const json=JSON.stringify(next,null,2)+'\n';
if(check){
 if(JSON.stringify(state)!==JSON.stringify(next) || await readFile(path.join(site,'sitemap.xml'),'utf8')!==xml)throw Error('Sitemap is stale. Run npm run prepare:site before publishing.');
 console.log('Sitemap matches current public pages.');
} else {
 await writeFile(path.join(site,'sitemap.xml'),xml);await writeFile(stateFile,json);console.log('Updated sitemap for '+routes.length+' public pages.');
}
