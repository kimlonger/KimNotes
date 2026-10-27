// Requires an isolated branch URL and its own injected gateway key.
import assert from 'node:assert/strict';
import {randomUUID,randomBytes} from 'node:crypto';
import {gzipSync} from 'node:zlib';
const origin=process.env.KIMNOTES_BRANCH_URL;
if(!origin?.includes('prod-desktop-sync-') || !process.env.GATEWAY_SECRET) throw new Error('Select the isolated desktop-sync branch explicitly.');
const base=origin+'/api/v1/apps/kimnotes',email='desktop-test-'+randomUUID()+'@example.invalid',password=randomBytes(24).toString('base64url');
async function api(path,body,token,raw=false) {
 const headers={'X-Kim-Gateway':process.env.GATEWAY_SECRET,'X-Kim-Client':'desktop','X-Kim-Client-IP':'192.0.2.41',Origin:'https://note.kimlulu.com'};
 if(token)headers.Authorization='Bearer '+token;
 if(body!==undefined)headers['Content-Type']=raw?'image/png':'application/json';
 const response=await fetch(base+path,{method:body===undefined?'GET':'POST',headers,body:body===undefined?undefined:raw?body:JSON.stringify(body),signal:AbortSignal.timeout(60000)});
 const value=await response.json();return {status:response.status,value};
}
const register=await api('/auth/register',{email,password,deviceName:'isolated-native-A'});assert.equal(register.status,200);assert.equal(register.value.token.length,43);const token=register.value.token;
const login=await api('/auth/desktop/login',{email,password,deviceName:'isolated-native-B'});assert.equal(login.status,200);const second=login.value.token;
const invalidLogin=await api('/auth/desktop/login',{email,password:'incorrect-password'});assert.equal(invalidLogin.status,401);
assert.equal((await api('/auth/me',undefined,second)).value.user.id,register.value.user.id);
const id=randomUUID(),rtf='{\\rtf1\\ansi '+ 'abc '.repeat(600000)+'}';
const document={title:'Native compressed note',text:'正文',rtfGzip:gzipSync(rtf).toString('base64'),themeId:6,attachments:[]};
const op={operationId:randomUUID(),noteId:id,baseRevision:0,action:'upsert',document};
const push=await api('/sync/push',{operations:[op]},token);assert.equal(push.status,200);assert.equal(push.value.results[0].note.revision,1);
const retry=await api('/sync/push',{operations:[op]},token);assert.equal(retry.value.results[0].note.revision,1);
const pull=await api('/sync/pull?cursor=0',undefined,second);assert.equal(pull.status,200);assert.equal(pull.value.changes[0].document.rtfGzip,document.rtfGzip);assert.equal(pull.value.changes[0].document.themeId,6);
const conflict=await api('/sync/push',{operations:[{...op,operationId:randomUUID()}]},second);assert.equal(conflict.value.results[0].status,'conflict_copy');
const unsafe=await api('/sync/push',{operations:[{...op,operationId:randomUUID(),document:{...document,rtfGzip:gzipSync('{\\rtf1{\\object bad}}').toString('base64')}}]},token);assert.equal(unsafe.status,400);
await api('/auth/logout',{},token);assert.equal((await api('/auth/me',undefined,token)).status,401);assert.equal((await api('/auth/me',undefined,second)).status,200);
let limited;for(let i=0;i<11;i++)limited=await api('/auth/desktop/login',{email,password:'incorrect-password'});
assert.equal(limited.status,429);
console.log(JSON.stringify({passed:true,checks:['desktop email/password session without CAPTCHA','incorrect password rejected','second device login','compressed RTF over original 2 MB limit','theme preservation','idempotent retry','pull on second device','conflict copy','unsafe object rejection','per-device logout','password login rate limiting'],fixture:'synthetic accounts only in disposable branch'}));
