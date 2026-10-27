import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import vm from 'node:vm';
const source=await readFile(new URL('../public/assets/site-v2.js',import.meta.url),'utf8');
const config={passwordReset:false,emailVerification:false,humanVerification:{provider:'turnstile',siteKey:'0x-unit-test'}};
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
test('anonymous form waits for CAPTCHA; failed login consumes token and requires a fresh challenge',async()=>{
 const p=await page();assert.equal(p.get('#auth-content').hidden,false);assert.equal(p.get('#auth-submit').disabled,true);assert.equal(p.widgets[0].action,'login');
 await p.get('#auth-form').emit('submit');assert.equal(p.calls.some(c=>c.path.endsWith('/auth/login')),false);
 p.widgets[0].callback('unit-test-token');assert.equal(p.get('#auth-submit').disabled,false);
 p.get('#auth-email').value='synthetic@example.com';p.get('#auth-password').value='unit-password';
 await p.get('#auth-form').emit('submit');await settle();
 const login=p.calls.find(c=>c.path.endsWith('/auth/login'));assert.equal(login.options.headers.get('X-Kim-Turnstile'),'unit-test-token');assert.equal(JSON.parse(login.options.body).password,'unit-password');
 assert.equal(p.widgets.length,2);assert.equal(p.get('#auth-submit').disabled,true);assert.equal(p.get('#auth-password').value,'unit-password');
 p.widgets[0].callback('stale-token');assert.equal(p.get('#auth-submit').disabled,true);
 p.widgets[1].callback('fresh-token');assert.equal(p.get('#auth-submit').disabled,false);
 p.widgets[1]['expired-callback']();assert.equal(p.get('#auth-submit').disabled,true);assert.equal(p.get('#verification-retry').hidden,false);
});
test('registration switches challenge action and validates password confirmation before network submission',async()=>{
 const p=await page();p.widgets[0].callback('old-token');
 await p.document.emit('click',{target:{closest:()=>({dataset:{authMode:'register'}})}});await settle();
 assert.equal(p.widgets.at(-1).action,'register');assert.equal(p.get('#auth-submit').disabled,true);
 p.widgets.at(-1).callback('new-token');p.get('#auth-password').value='unit-password';p.get('#auth-confirm').value='different';
 await p.get('#auth-form').emit('submit');assert.equal(p.calls.some(c=>c.path.endsWith('/auth/register')),false);
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
