// Run only against a deliberately selected deployment. Creates synthetic test
// accounts (never real email addresses) and writes their IDs to a cleanup journal.
import assert from 'node:assert/strict';
import {randomUUID,randomBytes,createHash} from 'node:crypto';
import {readFile,writeFile} from 'node:fs/promises';
import {tmpdir} from 'node:os';
import {join} from 'node:path';
const origin=process.env.KIMNOTES_TEST_ORIGIN;
if (!origin || !/^https?:\/\//.test(origin)) throw new Error('Set KIMNOTES_TEST_ORIGIN explicitly.');
const base=`${origin}/api/v1/apps/kimnotes`, password=randomBytes(24).toString('base64url');
const journal={origin,createdAt:new Date().toISOString(),accounts:[],checks:[]};
const journalPath=join(tmpdir(),`kimnotes-api-e2e-${randomUUID()}.json`);
const save=() => writeFile(journalPath,JSON.stringify(journal,null,2));
await save();
function client(){let cookie='';return async function(path,method='GET',body,contentType='application/json'){
 const headers={Origin:origin};if(cookie)headers.Cookie=cookie;if(body!==undefined)headers['Content-Type']=contentType;
 const response=await fetch(base+path,{method,headers,body:body===undefined?undefined:Buffer.isBuffer(body)?body:JSON.stringify(body),signal:AbortSignal.timeout(60000),redirect:'manual'});
 const values=response.headers.getSetCookie();if(values.length)cookie=values.map(value=>value.split(';')[0]).join('; ');
 const raw=Buffer.from(await response.arrayBuffer());let data;try{data=JSON.parse(raw)}catch{data=null};return{response,data,raw};
};}
const one=client(),two=client(),anonymous=client();
const config=await anonymous('/config');assert.equal(config.response.status,200);if(config.data.humanVerification)throw new Error('Human verification enabled: use isolated backend tests; never bypass production CAPTCHA.');if(config.data.emailVerification)throw new Error('Email verification enabled: do not auto-send emails to synthetic addresses.');
for(const [index,api] of [one,two].entries()){
 const email=`kimnotes-e2e-${randomUUID()}@example.com`;
 const result=await api('/auth/register','POST',{email,password});assert.equal(result.response.status,200,JSON.stringify(result.data));
 const setCookie=result.response.headers.get('set-cookie');assert.match(setCookie,/HttpOnly/i);if(origin.startsWith('https:'))assert.match(setCookie,/Secure/i);
 assert.equal(result.data.token,undefined);journal.accounts.push({id:result.data.user.id,email});await save();
}
journal.checks.push('registration and HttpOnly session cookies');
assert.equal((await anonymous('/auth/me')).response.status,401);
const id=randomUUID(),op={operationId:randomUUID(),noteId:id,baseRevision:0,action:'upsert',document:{title:'<script>literal title</script>',text:'云端存储验证\n正文与主题色保留',themeId:7,attachments:[]}};
const push=await one('/sync/push','POST',{operations:[op]});assert.equal(push.response.status,200,JSON.stringify(push.data));assert.equal(push.data.results[0].note.revision,1);
const retry=await one('/sync/push','POST',{operations:[op]});assert.equal(retry.data.results[0].note.revision,1);
const stale=await one('/sync/push','POST',{operations:[{...op,operationId:randomUUID()}]});assert.equal(stale.data.results[0].status,'conflict_copy');assert.notEqual(stale.data.results[0].note.id,id);
const other=await two('/sync/pull?cursor=0');assert.equal(other.data.changes.length,0);
assert.equal((await two(`/notes/${id}/versions`)).response.status,404);
journal.checks.push('note write/read, theme 7, idempotency, conflict preservation, account isolation');
const image=await readFile(new URL('../public/assets/v1.4.11/note-mint-2x.png',import.meta.url));
const upload=await one('/attachments','POST',image,'image/png');assert.equal(upload.response.status,201,JSON.stringify(upload.data));
const duplicate=await one('/attachments','POST',image,'image/png');assert.equal(duplicate.data.id,upload.data.id);
const ownImage=await one(`/attachments/${upload.data.id}`);assert.equal(ownImage.response.status,200);assert.equal(createHash('sha256').update(ownImage.raw).digest('hex'),createHash('sha256').update(image).digest('hex'));
assert.equal((await two(`/attachments/${upload.data.id}`)).response.status,404);assert.equal((await anonymous(`/attachments/${upload.data.id}`)).response.status,401);
journal.checks.push('private image upload, deduplication, original byte hash, permission enforcement');
for(const [action,baseRevision] of [['delete',1],['restore',2]]){
 const value=await one('/sync/push','POST',{operations:[{operationId:randomUUID(),noteId:id,baseRevision,action}]});assert.equal(value.response.status,200);assert.equal(value.data.results[0].status,'applied');
 assert.equal(!!value.data.results[0].note.deletedAt,action==='delete');
}
assert.equal((await one(`/notes/${id}/versions`)).data.versions.length,3);
journal.checks.push('soft delete, restore, immutable version history');
const csrf=await fetch(base+'/auth/logout',{method:'POST',headers:{Origin:'https://attacker.example','Content-Type':'application/json'},body:'{}'});assert.equal(csrf.status,403);
await one('/auth/logout','POST',{});assert.equal((await one('/auth/me')).response.status,401);await two('/auth/logout','POST',{});
journal.checks.push('origin protection and session revocation');await save();
console.log(JSON.stringify({passed:true,origin,checks:journal.checks,cleanupJournal:journalPath},null,2));
