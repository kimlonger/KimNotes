import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {createHash,webcrypto} from 'node:crypto';
import vm from 'node:vm';
const source=await readFile(new URL('../public/assets/site-v2.js',import.meta.url),'utf8');
const config={passwordReset:false,emailVerification:false};
function element() {
 const events=new Map();
 return {hidden:false,disabled:false,value:'',textContent:'',dataset:{},clientWidth:352,firstElementChild:{},events,
  classList:{add(){},remove(){},toggle(){return false;}},setAttribute(){},removeAttribute(){},replaceChildren(){},append(){},focus(){},setCustomValidity(){},reportValidity(){return true;},closest(){return element();},
  addEventListener(name,callback){events.set(name,callback);},async emit(name,event={}){return events.get(name)?.({preventDefault(){},...event});}};
}
const settle=()=>new Promise(resolve=>setImmediate(resolve));
async function page({home=false,notes=false,channelSupport='available',url='https://note.kimlulu.com/login/',meStatus=401,handler,tabStorage=new Map()}={}) {
 const nodes=new Map();const get=selector=>{if(!nodes.has(selector))nodes.set(selector,element());return nodes.get(selector);};
 const document=element(),window=element(),calls=[],redirects=[],widgets=[],channels=[];
 document.body={dataset:{page:notes?'notes':home?'home':'auth'}};document.hidden=false;
 document.querySelector=selector=>['[data-nav-toggle]','#site-nav','#image-preview'].includes(selector)||selector==='.site-nav .login-link'&&!home?null:get(selector);
 document.querySelectorAll=()=>[];document.createElement=tag=>{
  if(tag!=='canvas')return element();
  return {getContext:()=>({drawImage(){}}),toDataURL:()=> 'data:image/png;base64,cHJldmlldw=='};
 };document.createTextNode=element;document.head=element();
 const avatarField=get('#avatar-field'), custom=get('input[value=custom]');custom.value='custom';
 avatarField.querySelector=selector=>selector==='input[name=avatar]:checked'&&custom.checked?{value:'custom'}:get(selector);
 avatarField.querySelectorAll=()=>[custom];
 const imageFile=get('[data-avatar-file]');imageFile.files=[];
 class TestURL extends URL {static createObjectURL(){return 'blob:avatar-source';}static revokeObjectURL(){}}
 class TestImage {naturalWidth=600;naturalHeight=400;async decode(){}}
 get('#auth-content').hidden=true;get('#auth-session-retry').hidden=true;
 const link=get('.site-nav .login-link');link.textContent='登录';link.href='/login/';
 window.turnstile={render(_selector,options){widgets.push(options);return widgets.length;},remove(){}};
 class Channel {constructor(){this.events=new Map();channels.push(this);}addEventListener(name,callback){this.events.set(name,callback);}postMessage(){}close(){}}
 const location=new URL(url);location.assign=path=>redirects.push({kind:'assign',path});location.replace=path=>redirects.push({kind:'replace',path});location.reload=()=>redirects.push({kind:'reload'});
 const storageWrites=[];
 const localStorage={setItem(key,value){storageWrites.push([key,value]);},removeItem(){}};
 const sessionStorage={getItem:key=>tabStorage.get(key)??null,setItem:(key,value)=>tabStorage.set(key,value),removeItem:key=>tabStorage.delete(key)};
 const history={replaceState(_state,_title,path){location.href=new URL(path,location.href).href;}};
 const channelAPI=channelSupport==='missing'?{}:{BroadcastChannel:channelSupport==='blocked'?class{constructor(){throw Error('blocked');}}:Channel};
 const context=vm.createContext({document,window,location,localStorage,history,sessionStorage,URLSearchParams,crypto:webcrypto,TextEncoder,btoa,Headers,URL:TestURL,Image:TestImage,Blob,...channelAPI,AbortSignal,setTimeout,clearTimeout,
  fetch:async(path,options)=>{calls.push({path,options});if(handler){const response=await handler(path,options);if(response)return response;}
   if(path.endsWith('/auth/me'))return Response.json(meStatus===200?{user:{id:'synthetic'}}:{message:'请先登录。'},{status:meStatus});
   if(path.endsWith('/config'))return Response.json(config);
   return Response.json({message:'密码错误'},{status:401});}});
 vm.runInContext(source,context);await settle();await settle();
 return {nodes,get,window,document,calls,redirects,widgets,channels,storageWrites,location};
}
test('saved session goes straight to notes without loading CAPTCHA or exposing the login form',async()=>{
 const p=await page({meStatus:200});assert.equal(p.redirects[0].path,'/notes/');assert.equal(p.redirects[0].kind,'replace');assert.equal(p.widgets.length,0);assert.equal(p.calls.length,1);assert.equal(p.get('#auth-content').hidden,true);
 assert.equal(p.calls[0].options.credentials,'same-origin');assert.equal(p.calls[0].options.cache,'no-store');
});
test('unavailable session check preserves state and offers retry instead of asking for a password',async()=>{
 let available=false;
 const p=await page({handler:path=>path.endsWith('/auth/me')?Response.json({user:{id:'synthetic'},message:'暂时不可用'},{status:available?200:503}):null});
 assert.equal(p.get('#auth-content').hidden,true);assert.equal(p.get('#auth-session-retry').hidden,false);assert.equal(p.widgets.length,0);
 available=true;await p.get('#auth-session-retry').emit('click');assert.equal(p.redirects[0].path,'/notes/');
});
test('login is immediately usable without CAPTCHA and password errors can be retried',async()=>{
 const p=await page();assert.equal(p.get('#auth-content').hidden,false);assert.equal(p.get('#auth-submit').disabled,false);assert.equal(p.widgets.length,0);
 p.get('#auth-email').value='synthetic@example.com';p.get('#auth-password').value='unit-password';
 await p.get('#auth-form').emit('submit');await settle();
 const login=p.calls.find(c=>c.path.endsWith('/auth/login'));assert.equal(login.options.headers.get('X-Kim-Turnstile'),null);assert.equal(JSON.parse(login.options.body).password,'unit-password');
 assert.equal(p.get('#auth-submit').disabled,false);assert.equal(p.get('#auth-status').textContent,'密码错误');assert.equal(p.get('#auth-password').value,'unit-password');assert.equal(p.widgets.length,0);
 await p.get('#auth-form').emit('submit');assert.equal(p.calls.filter(c=>c.path.endsWith('/auth/login')).length,2);
});
test('registration requires matching passwords, a trimmed nickname and an explicit avatar without CAPTCHA',async()=>{
 const p=await page({handler:path=>path.endsWith('/auth/register')?Response.json({user:{id:'fixture'}}):null});
 await p.document.emit('click',{target:{closest:()=>({dataset:{authMode:'register'}})}});await settle();
 assert.equal(p.get('#auth-submit').disabled,false);assert.equal(p.widgets.length,0);
 p.get('#auth-email').value='fixture@example.com';p.get('#auth-password').value='unit-password';p.get('#auth-confirm').value='different';
 await p.get('#auth-form').emit('submit');assert.equal(p.calls.some(c=>c.path.endsWith('/auth/register')),false);
 p.get('#auth-confirm').value='unit-password';
 for(const invalidName of ['   ','羊'.repeat(25)]) {
  p.get('#auth-nickname').value=invalidName; await p.get('#auth-form').emit('submit');
  assert.match(p.get('#auth-status').textContent,/昵称/);assert.equal(p.calls.some(c=>c.path.endsWith('/auth/register')),false);
 }
 p.get('#auth-nickname').value='  开开心心  ';await p.get('#auth-form').emit('submit');assert.match(p.get('#auth-status').textContent,/头像/);
 p.get('input[name=avatar]:checked').value='mint';
 await p.get('#auth-form').emit('submit');assert.equal(p.redirects.at(-1).path,'/notes/');
 const register=p.calls.find(c=>c.path.endsWith('/auth/register'));
 assert.equal(register.options.headers.get('X-Kim-Turnstile'),null);
 assert.deepEqual(JSON.parse(register.options.body),{email:'fixture@example.com',password:'unit-password',profileVersion:1,nickname:'开开心心',avatarId:'mint'});
});
test('registration uploads a prepared custom image and rejects unsupported files before submission',async()=>{
 const p=await page({url:'https://note.kimlulu.com/login/?mode=register',
  handler:path=>path.endsWith('/auth/register')?Response.json({user:{id:'fixture'}}):null});
 const file=p.get('[data-avatar-file]');
 file.files=[{name:'vector.svg',type:'image/svg+xml',size:100}];await file.emit('change');
 assert.match(p.get('[data-avatar-message]').textContent,/JPEG/);
 assert.equal(p.get('input[value=custom]').checked,undefined);
 file.files=[{name:'portrait.png',type:'image/png',size:100}];await file.emit('change');
 assert.equal(p.get('input[value=custom]').checked,true);
 assert.equal(p.get('[data-avatar-photo]').hidden,false);
 p.get('#auth-email').value='fixture@example.com';
 p.get('#auth-password').value=p.get('#auth-confirm').value='unit-password';
 p.get('#auth-nickname').value='头像测试';
 await p.get('#auth-form').emit('submit');
 const register=p.calls.find(c=>c.path.endsWith('/auth/register'));
 assert.deepEqual(JSON.parse(register.options.body),{
  email:'fixture@example.com',password:'unit-password',profileVersion:1,nickname:'头像测试',
  avatarId:'custom',avatarImage:'cHJldmlldw=='
 });
});
test('successful password login keeps session handling and redirects to notes',async()=>{
 const p=await page({handler:path=>path.endsWith('/auth/login')?Response.json({user:{id:'fixture'}}):null});
 p.get('#auth-email').value='fixture@example.com';p.get('#auth-password').value='unit-password';
 await p.get('#auth-form').emit('submit');assert.equal(p.redirects.at(-1).path,'/notes/');assert.equal(p.get('#auth-password').value,'');assert.equal(p.widgets.length,0);
});
test('only the known profile destination survives login',async()=>{
 const signedIn=await page({url:'https://note.kimlulu.com/login/?next=profile',meStatus:200});
 assert.deepEqual(signedIn.redirects,[{kind:'replace',path:'/notes/?panel=profile'}]);
 const p=await page({url:'https://note.kimlulu.com/login/?next=profile',handler:path=>path.endsWith('/auth/login')?Response.json({user:{id:'fixture'}}):null});
 p.get('#auth-email').value='fixture@example.com';p.get('#auth-password').value='unit-password';
 await p.get('#auth-form').emit('submit');assert.deepEqual(p.redirects,[{kind:'assign',path:'/notes/?panel=profile'}]);
 const unrelated=await page({url:'https://note.kimlulu.com/login/?next=https://example.invalid',meStatus:200});
 assert.deepEqual(unrelated.redirects,[{kind:'replace',path:'/notes/'}]);
});
const desktopEntry='https://note.kimlulu.com/login/?desktop=profile';
const validState='s'.repeat(43), validCode='c'.repeat(43);
test('desktop profile opens for A even if the browser was signed in as B',async()=>{
 const tabStorage=new Map();
 const launch=await page({url:desktopEntry+'#port=34567&state='+validState,tabStorage,meStatus:200});
 assert.equal(launch.location.hash,'');
 for(let i=0;!launch.redirects.length&&i<100;i++) await new Promise(resolve=>setTimeout(resolve,2));
 const {kind,path}=launch.redirects[0], loopback=new URL(path);
 assert.equal(kind,'replace');
 assert.equal(loopback.origin,'http://127.0.0.1:34567');
 assert.equal(loopback.pathname,'/profile/'+validState);
 assert.deepEqual([...loopback.searchParams.keys()],['challenge']);
 const {verifier}=JSON.parse(tabStorage.get('kimnotes-desktop-profile-verifier'));
 assert.match(verifier,/^[A-Za-z0-9_-]{43}$/);
 assert.equal(loopback.searchParams.get('challenge'),createHash('sha256').update(verifier,'ascii').digest('base64url'));
 assert.equal(launch.calls.length,0);
 const finish=await page({url:desktopEntry+'#code='+validCode,tabStorage,meStatus:200,
  handler:path=>path.endsWith('/auth/browser-handoff/consume')?Response.json({user:{id:'account-a'}}):null});
 assert.equal(finish.location.hash,'');
 assert.deepEqual(finish.redirects,[{kind:'replace',path:'/notes/?panel=profile'}]);
 assert.equal(finish.calls.length,1);
 const {path:api,options}=finish.calls[0];
 assert.equal(api,'/api/v1/apps/kimnotes/auth/browser-handoff/consume');
 assert.equal(options.method,'POST');assert.equal(options.credentials,'same-origin');
 assert.equal(options.headers.has('X-Kim-Expected-User'),false);
 assert.deepEqual(JSON.parse(options.body),{code:validCode,verifier});
 assert.equal(tabStorage.has('kimnotes-desktop-profile-verifier'),false);
 assert.equal(JSON.parse(tabStorage.get('kimnotes-desktop-profile-identity')).id,'account-a');
 assert.deepEqual(finish.storageWrites.map(([key])=>key),['kimnotes-session']);
});
test('expired, missing, replayed and rejected desktop links never use a prior web session',async()=>{
 for(const [hash,pending,requests] of [
  ['port=1023&state='+validState,null,0],['port=65536&state='+validState,null,0],
  ['port=2000&state=bad',null,0],['code='+validCode,null,0],
  ['code='+validCode,{verifier:validState,expires:Date.now()-1},0],
  ['code='+validCode,{verifier:validState,expires:Date.now()+90000},1]
 ]) {
  const tabStorage=new Map();
  if(pending)tabStorage.set('kimnotes-desktop-profile-verifier',JSON.stringify(pending));
  const p=await page({url:desktopEntry+'#'+hash,meStatus:200,tabStorage,
    handler:path=>path.endsWith('/auth/browser-handoff/consume')?Response.json({},{status:403}):null});
  assert.equal(p.calls.length,requests);
  assert.equal(p.calls.some(({path})=>path.endsWith('/auth/me')),false);
  assert.equal(p.location.hash,'');assert.deepEqual(p.redirects,[]);
  assert.equal(p.get('#auth-content').hidden,true);assert.match(p.get('#auth-session-message').textContent,/回到桌面便签/);
  assert.equal(tabStorage.size,0);assert.equal(p.storageWrites.length,0);
 }
});
test('if the browser cookie belongs to B after exchange, the handoff cannot open B’s profile',async()=>{
 const tabStorage=new Map([['kimnotes-desktop-profile-identity',JSON.stringify({id:'account-a',expires:Date.now()+90000})]]);
 const p=await page({notes:true,url:'https://note.kimlulu.com/notes/?panel=profile',tabStorage,
  handler:path=>path.endsWith('/auth/me')?Response.json({user:{id:'account-b'}}):null});
 assert.deepEqual(p.redirects,[{kind:'replace',path:'/login/?desktop=profile'}]);
 assert.equal(p.calls.length,1);
});
test('login markup and script do not load a third-party challenge',async()=>{
 const html=await readFile(new URL('../public/login/index.html',import.meta.url),'utf8');
 assert.doesNotMatch(html,/human-verification|turnstile|verification-retry/);assert.doesNotMatch(source,/challenges\.cloudflare\.com|loadTurnstile/);
});
test('back navigation and cross-tab login restore the existing session',async()=>{
 let signedIn=false;
 const p=await page({handler:path=>path.endsWith('/auth/me')?Response.json({user:{id:'synthetic'}},{status:signedIn?200:401}):null});
 signedIn=true;await p.window.emit('pageshow',{persisted:true});await settle();assert.equal(p.redirects.at(-1).path,'/notes/');
 await p.channels[0].events.get('message')();assert.equal(p.redirects.length,2);
});
test('homepage account entry tracks existing session, logout and transient service failures',async()=>{
 let state=200;
 const p=await page({home:true,handler:path=>path.endsWith('/auth/me')?Response.json({user:{id:'synthetic'}},{status:state}):null});
 assert.equal(p.get('.site-nav .login-link').textContent,'我的便签');assert.equal(p.get('.site-nav .login-link').href,'/notes/');
 state=503;await p.window.emit('pageshow');assert.equal(p.get('.site-nav .login-link').href,'/notes/');
 state=401;await p.channels[0].events.get('message')();assert.equal(p.get('.site-nav .login-link').textContent,'登录');assert.equal(p.get('.site-nav .login-link').href,'/login/');
});
test('a cross-tab session change wins over a delayed account check on the home and login pages',async()=>{
 for (const home of [true,false]) {
  let completeOldCheck, count=0;
  const p=await page({home,handler:path=>{
   if(!path.endsWith('/auth/me'))return null;
   if(count++===0)return new Promise(resolve=>{completeOldCheck=resolve;});
   return Response.json({user:{id:'current'}},{status:home?401:200});
  }});
  await p.channels[0].events.get('message')();
  completeOldCheck(Response.json({user:{id:'previous'}},{status:home?200:401}));await settle();await settle();
  assert.equal(count,2);
  if(home)assert.equal(p.get('.site-nav .login-link').href,'/login/');
  else {assert.deepEqual(p.redirects,[{kind:'replace',path:'/notes/'}]);assert.equal(p.get('#auth-content').hidden,true);}
 }
});
test('notes gate follows a cross-tab login and ignores a superseded first account response',async()=>{
 for (const pending of [false,true]) {
  let finish;
  const p=await page({notes:true,url:'https://note.kimlulu.com/notes/',handler:path=>
   path.endsWith('/auth/me') && pending ? new Promise(resolve=>{finish=resolve;}) : null});
  p.channels[0].events.get('message')();assert.deepEqual(p.redirects,[{kind:'reload'}]);
  if(pending){finish(Response.json({user:{id:'previous'}}));await settle();await settle();}
  assert.equal(p.calls.length,1);assert.equal(p.get('#notes-gate').hidden,false);
 }
});
test('home, login and guest notes keep working when the browser does not provide session broadcasts',async()=>{
 const auth=await page({channelSupport:'missing',handler:path=>path.endsWith('/auth/login')?Response.json({user:{id:'fixture'}}):null});
 assert.equal(auth.get('#auth-content').hidden,false);
 auth.get('#auth-email').value='fixture@example.test';auth.get('#auth-password').value='fixture-password';
 await auth.get('#auth-form').emit('submit');
 assert.deepEqual(auth.redirects,[{kind:'assign',path:'/notes/'}]);
 const key=auth.storageWrites[0][0];
 let signedIn=false;
 const handler=path=>path.endsWith('/auth/me')?Response.json({user:{id:'fixture'}},{status:signedIn?200:401}):null;
 const home=await page({home:true,channelSupport:'blocked',handler});
 const guest=await page({notes:true,channelSupport:'missing',handler});
 await guest.window.emit('focus');assert.deepEqual(guest.redirects,[]);
 signedIn=true;
 await home.window.emit('storage',{key,newValue:'changed'});await settle();
 assert.equal(home.get('.site-nav .login-link').href,'/notes/');
 await guest.window.emit('focus');assert.deepEqual(guest.redirects,[{kind:'reload'}]);
});
