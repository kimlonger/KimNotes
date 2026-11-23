import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {JSDOM} from 'jsdom';
const html=await readFile(new URL('../public/notes/index.html',import.meta.url),'utf8');
const script=await readFile(new URL('../public/assets/site-v2.js',import.meta.url),'utf8');
const sessions=await readFile(new URL('../public/assets/session-client.js',import.meta.url),'utf8');
const timeScript=await readFile(new URL('../public/assets/notes-time.js',import.meta.url),'utf8');
const settle=async()=>{for(let i=0;i<6;i++)await new Promise(resolve=>setImmediate(resolve));};
const note=(id,extra={})=>({id,revision:1,updatedAt:'2026-10-23T07:00:00Z',document:{title:`便签 ${id}`,text:'第一段\n\n第二段',themeId:2,attachments:[]},...extra});
async function page({demo=false,user='account-a',profile={},notes=[note('plain'),note('image',{document:{title:'图文便签',text:'图片说明',themeId:1,attachments:['private-1']}}),note('desktop',{document:{title:'桌面富文本',text:'保留桌面格式',themeId:0,attachments:[],rtfGzip:'compressed'}}),note('deleted',{deletedAt:'2026-10-23T08:00:00Z'})],storage={},preferences={},handler,panel=''}={}) {
 const dom=new JSDOM(html,{url:`https://note.kimlulu.com/notes/${demo?'?demo=1':panel?'?panel='+panel:''}`,runScripts:'outside-only',pretendToBeVisual:true});
 const w=dom.window,calls=[],timers=[],observed=new Set(),revoked=[],created=[];
 w.Headers=Headers;w.AbortSignal=AbortSignal;w.AbortController=AbortController;w.structuredClone=structuredClone;w.confirm=()=>true;
 w.URL.createObjectURL=blob=>{created.push(blob);return `blob:synthetic-${created.length}`;};w.URL.revokeObjectURL=url=>revoked.push(url);
 w.setInterval=(callback,ms)=>{timers.push({callback,ms});return timers.length;};w.clearInterval=()=>{};
 w.BroadcastChannel=class {postMessage(){}close(){}addEventListener(){}};
 w.IntersectionObserver=class {constructor(callback){this.callback=callback;w.imageObserver=this;}observe(image){observed.add(image);}unobserve(image){observed.delete(image);}};
 w.HTMLDialogElement.prototype.showModal=function(){this.open=true;};w.HTMLDialogElement.prototype.close=function(){this.open=false;this.dispatchEvent(new w.Event('close'));};
 Object.entries(storage).forEach(([key,value])=>w.sessionStorage.setItem(key,value));
 Object.entries(preferences).forEach(([key,value])=>w.localStorage.setItem(key,value));
 w.fetch=async(path,options={})=>{
  calls.push({path,options});if(handler){const response=await handler(path,options);if(response)return response;}
  if(path.endsWith('/auth/me'))return Response.json({user:{id:user,email:'synthetic@example.invalid',...profile},session:{id:'current-web',clientKind:'web',protocolVersion:2}});
  if(path.includes('/sync/pull'))return Response.json({changes:notes,cursor:'4',hasMore:false});
  if(path.endsWith('/auth/sessions'))return Response.json({sessions:[{id:'current-web',appId:'kimnotes',clientKind:'web',deviceName:'浏览器',createdAt:'2026-10-23T07:00:00Z',current:true},{id:'desktop-device',appId:'kimnotes',clientKind:'desktop',deviceName:'工作电脑',createdAt:'2026-10-23T07:00:00Z',current:false}]});
  if(path.includes('/attachments/'))return new Response(new Uint8Array([1,2,3]),{headers:{'Content-Type':'image/png'}});
  if(options.method==='DELETE')return Response.json({ok:true,current:false});
  return Response.json({ok:true});
 };
 w.eval(sessions);w.eval(timeScript);w.eval(script);await settle();
 const $=selector=>w.document.querySelector(selector),all=selector=>[...w.document.querySelectorAll(selector)];
 return {w,dom,$,all,calls,timers,observed,revoked,created,close:()=>w.close(),async click(selector){$(selector).click();await settle();},async input(selector,value){$(selector).value=value;$(selector).dispatchEvent(new w.Event('input',{bubbles:true}));await settle();},storage(){return Object.fromEntries(Object.keys(w.sessionStorage).map(key=>[key,w.sessionStorage.getItem(key)]));}};
}
test('demo opens on a themed card overview; reader is only opened by selection and closes cleanly',async()=>{
 const p=await page({demo:true});try {
  assert.equal(p.all('.note-item').length,6);assert.equal(p.$('#reader-panel').open,false);assert.equal(p.calls.length,0);
  assert.equal(p.$('[data-notes-view=todos]').hidden,true);
  assert.ok(p.$('.note-item').style.getPropertyValue('--note-theme'));assert.equal(p.all('.note-card-image').length,2);
  await p.click('[data-note-id=daily]');assert.equal(p.$('#reader-panel').open,true);assert.equal(p.$('.reader-title').textContent,'今天的小计划');
  await p.click('#reader-expand');assert.equal(p.$('#reader-panel').classList.contains('is-expanded'),true);
  await p.click('#reader-close');assert.equal(p.$('#reader-panel').open,false);assert.equal(p.w.document.activeElement.dataset.noteId,'daily');
  await p.click('[data-notes-view=images]');assert.equal(p.all('.note-item').length,2);assert.equal(p.$('[data-notes-view=images]').getAttribute('aria-current'),'page');
  await p.input('#note-search','不存在的内容');assert.equal(p.all('.note-item').length,0);await p.click('#clear-search');assert.equal(p.all('.note-item').length,6);
 } finally{p.close();}
});
test('desktop todos load all authenticated pages on demand and retain separate search',async()=>{
 const finished={id:'done',text:'发出记录',done:true,doneAt:'2026-11-10T15:59:00Z',repeat:0};
 let first={id:'first',text:'检查计划 <b>正文</b>',dueAt:'2000-01-01T00:00:00Z',done:false,repeat:2};
 const p=await page({handler:path=>{
  if(path.endsWith('/todos'))return Response.json({todos:[first],nextCursor:'a/b'});
  if(path.endsWith('/todos?cursor=a%2Fb'))return Response.json({todos:[finished]});
 }});try{
  assert.equal(p.calls.some(c=>c.path.includes('/todos')),false);
  await p.input('#note-search','图文');await p.click('[data-notes-view=todos]');
  assert.equal(p.$('#notes-layout').hidden,true);assert.equal(p.$('#new-note').hidden,true);
  assert.equal(p.$('#note-search').value,'');assert.equal(p.all('.todo-item').length,2);
  assert.deepEqual(p.all('.todos-group h2').map(h=>h.textContent),['进行','完成']);
  assert.equal(p.$('.todo-overdue').textContent,'过期');assert.match(p.$('.todo-meta').textContent,/工作日/);
  assert.equal(p.$('.todo-text').textContent,first.text);assert.equal(p.$('.todo-text b'),null);
  assert.ok(p.calls.filter(c=>c.path.includes('/todos')).every(c=>c.options.headers.get('X-Kim-Expected-User')==='account-a' && !c.options.method));
  await p.input('#note-search','发出');assert.equal(p.all('.todo-item').length,1);
  await p.click('[data-notes-view=all]');assert.equal(p.$('#note-search').value,'图文');assert.equal(p.all('.note-item').length,1);
  await p.click('[data-notes-view=todos]');assert.equal(p.$('#note-search').value,'发出');
  await p.click('#clear-search');first={...first,text:'更新后的待办'};
  await p.click('#sync-notes');assert.equal(p.$('.todo-text').textContent,'更新后的待办');
 }finally{p.close();}
});
test('an interrupted todo read shows retry and signing out clears loaded todos',async()=>{
 let failure=true;
 const p=await page({handler:path=>{
  if(path.endsWith('/todos'))return Response.json({todos:[],nextCursor:'second'});
  if(path.endsWith('/todos?cursor=second'))return failure?Response.json({message:'稍后重试'},{status:503}):Response.json({todos:[{id:'t',text:'个人待办',done:false}]});
 }});try{
  await p.click('[data-notes-view=todos]');assert.equal(p.$('#todos-retry').hidden,false);
  assert.equal(p.all('.todo-item').length,0);
  failure=false;await p.click('#todos-retry');
  assert.equal(p.$('.todo-text').textContent,'个人待办');
  p.all('#demo-account .account-panel button').find(button=>button.textContent==='登录设备').click();
  await settle();await p.click('#logout-all');
  assert.equal(p.$('#notes-workspace').hidden,true);assert.equal(p.all('.todo-item').length,0);
 }finally{p.close();}
});
test('account menu renders the Notes profile and QQ group while points are absent',async()=>{
 const p=await page({profile:{nickname:'小羊测试',avatarId:'sky'}});try{
  assert.equal(p.$('#demo-account .account-display-name').textContent,'小羊测试');
  assert.equal(p.$('#demo-account .avatar-chip').dataset.avatar,'sky');
  assert.doesNotMatch(p.$('#demo-account .account-panel').textContent,/积分|签到/);
  await p.click('[data-open-qq]');assert.equal(p.$('#qq-group').open,true);assert.match(p.$('#qq-group').textContent,/1135382915/);
  await p.click('#qq-group [data-close-dialog]');assert.equal(p.$('#qq-group').open,false);
 }finally{p.close();}
 const old=await page();try{
  assert.equal(old.$('#demo-account .account-display-name'),null);
  assert.equal(old.$('#demo-account .avatar-chip').dataset.avatar,'sheep');
  assert.match(old.$('#demo-account .account-panel').textContent,/synthetic@example.invalid/);
 }finally{old.close();}
});
test('a signed-out profile deep link sends the user to the restricted profile login return',async()=>{
 const p=await page({panel:'profile',handler:path=>path.endsWith('/auth/me')?Response.json({}, {status:401}):null});try{
  assert.equal(p.$('#profile-dialog').open,false);
  assert.equal(p.$('.notes-gate .button').getAttribute('href'),'/login/?next=profile');
 }finally{p.close();}
});
test('profile edit opens from the deep link, saves for the current Notes account, and refreshes its display',async()=>{
 const p=await page({panel:'profile',profile:{nickname:'原昵称',avatarId:'sky'},handler:(path,options)=>{
  if(path.endsWith('/profile'))return Response.json({user:{id:'account-a',email:'synthetic@example.invalid',...JSON.parse(options.body)}});
 }});try{
  assert.equal(p.$('#profile-dialog').open,true);assert.equal(p.$('#profile-nickname').value,'原昵称');
  assert.equal(p.$('input[name=avatar]:checked').value,'sky');
  await p.input('#profile-nickname','  新昵称  ');p.$('input[name=avatar][value=peach]').checked=true;
  p.$('#profile-form').dispatchEvent(new p.w.Event('submit',{bubbles:true,cancelable:true}));await settle();
  const save=p.calls.find(c=>c.path.endsWith('/profile'));
  assert.equal(save.options.method,'PUT');assert.equal(save.options.headers.get('X-Kim-Expected-User'),'account-a');
  assert.deepEqual(JSON.parse(save.options.body),{nickname:'新昵称',avatarId:'peach'});
  assert.equal(p.$('#profile-dialog').open,false);assert.equal(p.$('#account-identity strong').textContent,'新昵称');
  assert.equal(p.$('#demo-account .avatar-chip').dataset.avatar,'peach');
  await p.click('#demo-account .account-panel button:first-of-type');
  assert.equal(p.$('#profile-nickname').value,'新昵称');
 }finally{p.close();}
});
test('authenticated overview excludes deleted notes, lazy-loads private thumbnails and releases temporary URLs',async()=>{
 const p=await page();try {
  assert.equal(p.all('.note-item').length,3);assert.equal(p.calls.filter(c=>c.path.includes('/attachments/')).length,0);
  const image=[...p.observed][0];p.w.imageObserver.callback([{target:image,isIntersecting:true}]);await settle();
  assert.equal(p.created.length,1);assert.equal(p.calls.find(c=>c.path.includes('/attachments/')).options.credentials,'same-origin');
  await p.click('[data-notes-view=deleted]');assert.equal(p.all('.note-item').length,1);assert.equal(p.$('.note-item').dataset.noteId,'deleted');assert.deepEqual(p.revoked,['blob:synthetic-1']);
  await p.click('[data-note-id=deleted]');assert.ok(p.$('.reader-actions').textContent.includes('恢复'));
  assert.ok(p.calls.every(c=>c.path.includes('/attachments/')||c.options.headers.get('X-Kim-Auth-Version')==='2'));
 } finally{p.close();}
});
test('initial bootstrap downloads current notes and recycle bin then catches concurrent changes without replaying history',async()=>{
 const p=await page({handler:path=>{
  if(path.includes('/sync/bootstrap'))return path.includes('&after=')
   ?Response.json({notes:[note('later',{revision:4,document:{title:'较新正文',text:'保留最新快照',attachments:[]}}),note('deleted',{revision:2,deletedAt:'2026-10-23T08:00:00Z'})],cursor:'20',includesDeleted:true,hasMore:false,nextAfter:''})
   :Response.json({notes:[note('first',{revision:10})],cursor:'20',includesDeleted:true,hasMore:true,nextAfter:'first'});
  if(path.includes('/sync/pull'))return Response.json({changes:[note('later',{revision:3}),note('first',{revision:11,deletedAt:'2026-10-23T09:00:00Z'})],cursor:'22',hasMore:false});
 }});try{
  assert.equal(p.all('.note-item').length,1);assert.equal(p.$('.note-item').dataset.noteId,'later');
  const bootstrap=p.calls.filter(c=>c.path.includes('/sync/bootstrap'));assert.equal(bootstrap.length,2);assert.ok(bootstrap.every(c=>c.path.includes('includeDeleted=true')));assert.ok(bootstrap[1].path.includes('&after=first&cursor=20'));
  assert.ok(p.calls.find(c=>c.path.includes('/sync/pull')).path.includes('cursor=20'));
  await p.click('[data-note-id=later]');assert.equal(p.$('.reader-title').textContent,'较新正文');
  await p.click('#reader-close');await p.click('[data-notes-view=deleted]');assert.equal(p.all('.note-item').length,2);
  await p.click('#sync-notes');assert.equal(p.calls.filter(c=>c.path.includes('/sync/bootstrap')).length,2);assert.ok(p.calls.filter(c=>c.path.includes('/sync/pull')).at(-1).path.includes('cursor=22'));
 }finally{p.close();}
});
test('old live-only bootstrap falls back to the full stream so recently deleted notes remain recoverable',async()=>{
 const p=await page({handler:path=>path.includes('/sync/bootstrap')?Response.json({notes:[note('live')],cursor:'99',hasMore:false}):null});try{
  assert.equal(p.all('.note-item').length,3);assert.ok(p.calls.find(c=>c.path.includes('/sync/pull')).path.includes('cursor=0'));
  await p.click('[data-notes-view=deleted]');assert.equal(p.all('.note-item').length,1);assert.equal(p.$('.note-item').dataset.noteId,'deleted');
 }finally{p.close();}
});
test('missing bootstrap endpoint remains compatible with the original server',async()=>{
 const p=await page({handler:path=>path.includes('/sync/bootstrap')?Response.json({message:'not found'},{status:404}):null});try{
  assert.equal(p.all('.note-item').length,3);assert.ok(p.calls.find(c=>c.path.includes('/sync/pull')).path.includes('cursor=0'));
 }finally{p.close();}
});
test('interrupted bootstrap does not advance the checkpoint or expose a partial list and retries from the first page',async()=>{
 let fail=true;
 const p=await page({handler:path=>{
  if(path.includes('/sync/bootstrap'))return path.includes('&after=')
   ?fail?Response.json({message:'temporary error'},{status:503}):Response.json({notes:[note('deleted',{deletedAt:'2026-10-23T08:00:00Z'})],cursor:'9',includesDeleted:true,hasMore:false})
   :Response.json({notes:[note('first')],cursor:'9',includesDeleted:true,hasMore:true,nextAfter:'first'});
  if(path.includes('/sync/pull'))return Response.json({changes:[],cursor:'9',hasMore:false});
 }});try{
  assert.equal(p.all('.note-item').length,0);assert.equal(p.calls.filter(c=>c.path.includes('/sync/pull')).length,0);
  fail=false;await p.click('#sync-notes');assert.equal(p.all('.note-item').length,1);assert.equal(p.calls.filter(c=>c.path.includes('/sync/bootstrap')&&!c.path.includes('&after=')).length,2);
  assert.ok(p.calls.find(c=>c.path.includes('/sync/pull')).path.includes('cursor=9'));
  await p.click('[data-notes-view=deleted]');assert.equal(p.all('.note-item').length,1);
 }finally{p.close();}
});
test('desktop compressed RTF remains read-only in the editor and submit cannot replace it',async()=>{
 const p=await page();try {
  await p.click('[data-note-id=desktop]');p.all('.reader-actions button').find(b=>b.textContent==='编辑').click();await settle();
  assert.equal(p.$('#edit-title').readOnly,true);assert.equal(p.$('#edit-text').readOnly,true);assert.equal(p.$('#edit-save').disabled,true);assert.equal(p.$('#edit-image').disabled,true);
  p.$('#edit-form').dispatchEvent(new p.w.Event('submit',{bubbles:true,cancelable:true}));await settle();assert.equal(p.calls.filter(c=>c.path.endsWith('/sync/push')).length,0);
 } finally{p.close();}
});
test('sync polling does not report user activity and session expiry preserves only the current account draft',async()=>{
 let expiry=false;const p=await page({handler:path=>expiry&&path.includes('/sync/pull')?Response.json({code:'session_expired',message:'登录已过期'},{status:401}):null});
 let saved;try {
  await p.click('#new-note');await p.input('#edit-title','未保存的草稿');await p.input('#edit-text','离线内容');
  for(const timer of p.timers)await timer.callback();await settle();assert.equal(p.calls.filter(c=>c.path.endsWith('/auth/activity')).length,0);
  expiry=true;await p.click('#sync-notes');assert.equal(p.$('#notes-workspace').hidden,true);assert.equal(p.$('#note-editor').open,false);saved=p.storage();
  assert.ok(saved['kimnotes-draft:account-a'].includes('离线内容'));assert.equal(p.calls.filter(c=>c.path.endsWith('/sync/push')).length,0);
 } finally{p.close();}
 const same=await page({storage:saved});try{assert.equal(same.$('#note-editor').open,true);assert.equal(same.$('#edit-text').value,'离线内容');assert.equal(same.calls.filter(c=>c.path.endsWith('/sync/push')).length,0);}finally{same.close();}
 const other=await page({user:'account-b',storage:saved});try{assert.equal(other.$('#note-editor').open,false);assert.equal(other.$('#edit-text').value,'');assert.equal(other.calls.filter(c=>c.path.endsWith('/sync/push')).length,0);}finally{other.close();}
});
test('device manager lists web/desktop sessions, revokes one device and supports logout all',async()=>{
 const p=await page({panel:'devices'});try {
  assert.equal(p.$('#devices-dialog').open,true);assert.equal(p.all('.device-item').length,2);
  p.all('.device-item button')[1].click();await settle();assert.ok(p.calls.some(c=>c.path.endsWith('/auth/sessions/desktop-device')&&c.options.method==='DELETE'));
  await p.click('#logout-all');assert.ok(p.calls.some(c=>c.path.endsWith('/auth/logout-all')&&c.options.method==='POST'));assert.equal(p.$('#notes-workspace').hidden,true);
 } finally{p.close();}
});
test('failed save retains the draft and retries the same operation; successful save clears only this account draft',async()=>{
 const operations=[];let fail=true;
 const p=await page({handler:(path,options)=>{
  if(!path.endsWith('/sync/push'))return null;
  const operation=JSON.parse(options.body).operations[0];operations.push(operation);
  return fail?Response.json({message:'连接暂时不可用'},{status:503}):Response.json({results:[{status:'applied',note:note(operation.noteId,{document:operation.document})}]});
 }});try {
  await p.click('#new-note');await p.input('#edit-title','仅保存在当前账号');await p.input('#edit-text','请保留我的草稿');
  const submit=async()=>{p.$('#edit-form').dispatchEvent(new p.w.Event('submit',{bubbles:true,cancelable:true}));await settle();};
  await submit();assert.equal(p.$('#note-editor').open,true);assert.equal(p.$('#edit-save').disabled,false);assert.ok(p.storage()['kimnotes-draft:account-a'].includes('请保留我的草稿'));
  fail=false;await submit();assert.equal(p.$('#note-editor').open,false);assert.equal(operations.length,2);assert.deepEqual(operations[0],operations[1]);assert.equal(p.storage()['kimnotes-draft:account-a'],undefined);
  const push=p.calls.find(c=>c.path.endsWith('/sync/push'));assert.equal(push.options.headers.get('X-Kim-Expected-User'),'account-a');assert.equal(push.options.credentials,'same-origin');assert.equal(p.w.localStorage.length,0);
 }finally{p.close();}
});
test('reading drawer shows latest content without history and retains delete and recovery',async()=>{
 const p=await page({handler:(path,options)=>{
  if(!path.endsWith('/sync/push'))return null;
  const operation=JSON.parse(options.body).operations[0];return Response.json({results:[{status:'applied',note:note(operation.noteId,{revision:operation.baseRevision+1,deletedAt:operation.action==='delete'?'2026-10-23T09:00:00Z':null})}]});
 }});try {
  await p.click('[data-note-id=plain]');assert.ok(p.$('.reader-title').textContent);assert.equal(p.$('#history-dialog'),null);
  assert.ok(p.all('.reader-actions button').every(b=>!b.textContent.includes('历史版本')));assert.ok(p.calls.every(c=>!c.path.endsWith('/versions')));
  p.all('.reader-actions button').find(b=>b.textContent==='删除').click();await settle();assert.equal(p.all('.note-item').length,2);assert.equal(p.$('#reader-panel').open,false);
  await p.click('[data-notes-view=deleted]');await p.click('[data-note-id=plain]');p.all('.reader-actions button').find(b=>b.textContent==='恢复').click();await settle();
  assert.equal(p.all('.note-item').length,1);await p.click('[data-notes-view=all]');assert.equal(p.all('.note-item').length,3);
 }finally{p.close();}
});
test('an image-only note can copy its title without failing on an empty paragraph',async()=>{
 const p=await page({notes:[note('empty',{document:{title:'只有图片',text:'',themeId:0,attachments:['image-id']}})]});try {
  await p.click('[data-note-id=empty]');await p.click('.copy-note-button');assert.equal(p.$('#copy-preview').open,true);assert.equal(p.$('#copy-content').value.trim(),'只有图片');
 }finally{p.close();}
});
test('calendar controls switch and remember grouping; collapsing releases images and search reveals matches',async()=>{
 const p=await page({notes:[note('october',{document:{title:'本月记录',text:'独特关键词',attachments:['private-1'],contentUpdatedAt:'2026-10-20T04:00:00Z'}}),note('september',{document:{title:'上月记录',text:'另一张',attachments:[],contentUpdatedAt:'2026-09-20T04:00:00Z'}})]});try {
  assert.equal(p.$('#note-time-group').value,'month');
  assert.deepEqual(p.all('.note-time-group').map(group=>group.dataset.groupKey),['2026-10','2026-09']);
  await p.click('.note-group-toggle');assert.equal(p.$('.note-group-grid').hidden,true);assert.equal(p.all('.note-item').length,1);
  await p.input('#note-search','独特关键词');assert.equal(p.all('.note-time-group').length,0);assert.equal(p.$('.note-item').dataset.noteId,'october');
  await p.click('#clear-search');assert.equal(p.$('.note-group-grid').hidden,true);
  await p.click('.note-group-toggle');assert.equal(p.all('.note-item').length,2);
  p.$('#note-time-group').value='year';p.$('#note-time-group').dispatchEvent(new p.w.Event('change',{bubbles:true}));await settle();assert.equal(p.all('.note-time-group').length,1);assert.equal(p.w.localStorage.getItem('kimnotes-time-group'),'year');
 }finally{p.close();}
 const restored=await page({demo:true,preferences:{'kimnotes-time-group':'week'}});try{assert.equal(restored.$('#note-time-group').value,'week');assert.equal(restored.all('.note-time-group').length,2);}finally{restored.close();}
});
test('content edits move a note between calendar groups exactly once; theme activity preserves its group and creation',async()=>{
 let revision=1,modified='2026-09-30T04:00:00Z',themeId=0;
 const p=await page({handler:path=>path.includes('/sync/pull')?Response.json({changes:[note('moving',{revision,createdAt:'2026-09-01T04:00:00Z',updatedAt:'2026-10-29T04:00:00Z',document:{title:'持续更新',text:'内容',themeId,attachments:[],contentUpdatedAt:modified}})],cursor:String(revision),hasMore:false}):null});try {
  assert.equal(p.$('.note-time-group').dataset.groupKey,'2026-09');
  revision++;themeId=4;await p.click('#sync-notes');assert.equal(p.$('.note-time-group').dataset.groupKey,'2026-09');
  revision++;modified='2026-10-29T04:00:00Z';await p.click('#sync-notes');assert.equal(p.$('.note-time-group').dataset.groupKey,'2026-10');assert.equal(p.all('.note-item').length,1);
  await p.click('[data-note-id=moving]');const dates=p.all('.reader-time time');assert.equal(dates[0].dateTime,'2026-09-01T04:00:00.000Z');assert.equal(dates[1].dateTime,'2026-10-29T04:00:00.000Z');
 }finally{p.close();}
});
test('missing legacy dates are shown as unknown instead of a fabricated epoch date',async()=>{
 const p=await page({notes:[note('undated',{updatedAt:'invalid-date'})]});try {
  assert.equal(p.$('.note-time-group').dataset.groupKey,'unknown');assert.equal(p.$('.note-item time').textContent,'日期未记录');
 }finally{p.close();}
});
