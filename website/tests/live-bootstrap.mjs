// Deliberately limited to a disposable InstaCloud branch, never a live website.
// Run via `insta --agent run --branch <test-branch> --service compute/kimnotes-api`.
import assert from 'node:assert/strict';
import {randomUUID,randomBytes} from 'node:crypto';

const origin = process.env.KIMNOTES_TEST_ORIGIN;
const target = new URL(origin);
assert.ok(target.protocol === 'https:' && target.hostname.startsWith('prod-') && !target.hostname.startsWith('prod-main-') && target.hostname.endsWith('.compute.instacloud-edge.com'), 'Select a disposable branch URL explicitly.');
assert.ok(process.env.GATEWAY_SECRET, 'Run with branch-scoped service credentials.');
const base = `${origin}/api/v1/apps/kimnotes`;
let cookie = '';
async function request(path, method = 'GET', body) {
  const headers = {'X-Kim-Gateway':process.env.GATEWAY_SECRET, Origin:process.env.PUBLIC_ORIGIN, 'X-Kim-Auth-Version':'2', 'X-Kim-Inline-Images':'1'};
  if (cookie) headers.Cookie = cookie;
  if (body !== undefined) headers['Content-Type'] = 'application/json';
  const response = await fetch(base+path, {method,headers,body:body === undefined ? undefined : JSON.stringify(body),redirect:'manual',signal:AbortSignal.timeout(60000)});
  if (response.headers.getSetCookie().length) cookie = response.headers.getSetCookie().map(c=>c.split(';')[0]).join('; ');
  const value = await response.json();
  assert.equal(response.status,200, `${method} ${path}: ${response.status} ${value.code || ''}`);
  return value;
}
for (const path of ['/healthz','/readyz']) assert.equal((await fetch(origin+path,{signal:AbortSignal.timeout(60000)})).status,200);
const config = await request('/config');
assert.equal(config.syncBootstrapVersion,1); assert.equal(config.syncIntervalSeconds,60);
assert.equal(config.emailVerification,false,'Do not send registration messages during deployment checks.');
await request('/auth/register','POST',{email:`kimnotes-e2e-${randomUUID()}@example.com`,password:randomBytes(24).toString('base64url')});
const first = '10000000-0000-4000-8000-000000000001', second = '20000000-0000-4000-8000-000000000002';
async function push(id, revision, action='upsert') {
  const result = await request('/sync/push','POST',{operations:[{operationId:randomUUID(),noteId:id,baseRevision:revision,action,...(action === 'upsert' ? {document:{title:`latest-${revision+1}`,text:`content-${revision+1}`,themeId:3,attachments:[]}}:{})}]});
  assert.equal(result.results[0].status,'applied');
}
for(let n=0;n<5;n++) await push(first,n);
await push(second,0);
const page = await request('/sync/bootstrap?limit=1');
assert.equal(page.cursor,'6'); assert.equal(page.notes.length,1); assert.equal(page.notes[0].revision,5); assert.equal(page.notes[0].document.text,'content-5'); assert.equal(page.hasMore,true);
await push(first,5); await push(second,1,'delete');
const next = await request(`/sync/bootstrap?limit=1&cursor=${page.cursor}&after=${page.nextAfter}`);
assert.equal(next.cursor,'6'); assert.equal(next.hasMore,false); assert.equal(next.notes.length,0);
const tail = await request(`/sync/pull?cursor=${page.cursor}`);
assert.equal(tail.changes.length,2); assert.equal(tail.changes[0].revision,6); assert.ok(tail.changes[1].deletedAt);
await request('/auth/logout','POST',{});
console.log('PASS deployed health, readiness, capabilities, latest-only pages, stable checkpoint and concurrent edit/delete catchup');
