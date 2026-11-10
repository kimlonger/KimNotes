import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';
const source=await readFile(new URL('../public/assets/site-v2.js',import.meta.url),'utf8');
const config={passwordReset:false,emailVerification:false};
function element() {
 const events=new Map();
 return {hidden:false,disabled:false,value:'',textContent:'',dataset:{},clientWidth:352,firstElementChild:{},events,
  classList:{add(){},remove(){},toggle(){return false;}},setAttribute(){},replaceChildren(){},append(){},focus(){},setCustomValidity(){},reportValidity(){return true;},closest(){return element();},
  addEventListener(name,callback){events.set(name,callback);},async emit(name,event={}){return events.get(name)?.({preventDefault(){},...event});}};
}
const settle=()=>new Promise(resolve=>setImmediate(resolve));
async function page({home=false,url='https://note.kimlulu.com/login/',meStatus=401,handler}={}) {
 const nodes=new Map();const get=selector=>{if(!nodes.has(selector))nodes.set(selector,element());return nodes.get(selector);};
 const document=element(),window=element(),calls=[],redirects=[],widgets=[],channels=[];
 document.body={dataset:{page:home?'home':'auth'}};document.hidden=false;
 document.querySelector=selector=>['[data-nav-toggle]','#site-nav','#image-preview'].includes(selector)||selector==='.site-nav .login-link'&&!home?null:get(selector);
 document.querySelectorAll=()=>[];document.createElement=element;document.createTextNode=element;document.head=element();
 get('#auth-content').hidden=true;get('#auth-session-retry').hidden=true;
 const link=get('.site-nav .login-link');link.textContent='登录';link.href='/login/';
 window.turnstile={render(_selector,options){widgets.push(options);return widgets.length;},remove(){}};
 class Channel {constructor(){this.events=new Map();channels.push(this);}addEventListener(name,callback){this.events.set(name,callback);}postMessage(){}close(){}}
 const location=new URL(url);location.assign=path=>redirects.push({kind:'assign',path});location.replace=path=>redirects.push({kind:'replace',path});
 const context=vm.createContext({document,window,location,history:{replaceState(){}},Headers,URL,Blob,BroadcastChannel:Channel,AbortSignal,setTimeout,clearTimeout,
  fetch:async(path,options)=>{calls.push({path,options});if(handler){const response=await handler(path,options);if(response)return response;}
   if(path.endsWith('/auth/me'))return Response.json(meStatus===200?{user:{id:'synthetic'}}:{message:'请先登录。'},{status:meStatus});
   if(path.endsWith('/config'))return Response.json(config);
   return Response.json({message:'密码错误'},{status:401});}});
 vm.runInContext(source,context);await settle();await settle();
 return {nodes,get,window,document,calls,redirects,widgets,channels};
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
test('registration validates confirmation and then submits without CAPTCHA',async()=>{
 const p=await page({handler:path=>path.endsWith('/auth/register')?Response.json({user:{id:'fixture'}}):null});
 await p.document.emit('click',{target:{closest:()=>({dataset:{authMode:'register'}})}});await settle();
 assert.equal(p.get('#auth-submit').disabled,false);assert.equal(p.widgets.length,0);
 p.get('#auth-email').value='fixture@example.com';p.get('#auth-password').value='unit-password';p.get('#auth-confirm').value='different';
 await p.get('#auth-form').emit('submit');assert.equal(p.calls.some(c=>c.path.endsWith('/auth/register')),false);
 p.get('#auth-confirm').value='unit-password';await p.get('#auth-form').emit('submit');assert.equal(p.redirects.at(-1).path,'/notes/');
 assert.equal(p.calls.find(c=>c.path.endsWith('/auth/register')).options.headers.get('X-Kim-Turnstile'),null);
});
test('successful password login keeps session handling and redirects to notes',async()=>{
 const p=await page({handler:path=>path.endsWith('/auth/login')?Response.json({user:{id:'fixture'}}):null});
 p.get('#auth-email').value='fixture@example.com';p.get('#auth-password').value='unit-password';
 await p.get('#auth-form').emit('submit');assert.equal(p.redirects.at(-1).path,'/notes/');assert.equal(p.get('#auth-password').value,'');assert.equal(p.widgets.length,0);
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
