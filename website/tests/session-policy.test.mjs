import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const {createActivity,drafts} = createRequire(import.meta.url)('../public/assets/session-client.js');
function clock({fail} = {}) {
 let time=100000,visible=true,sends=0,expired=0,timerCleared=false;
 const state=createActivity({send:async()=>{sends++;if(fail)throw Object.assign(new Error('expired'),{status:fail});},onExpired:()=>expired++,now:()=>time,visible:()=>visible,setTimer:()=>1,clearTimer:()=>timerCleared=true});
 return {state,get sends(){return sends;},get expired(){return expired;},get cleared(){return timerCleared;},advance:ms=>time+=ms,hide:()=>visible=false,show:()=>visible=true};
}
test('background timer, synthetic events and hidden tab never extend an idle session',async()=>{
 const c=clock();for(let i=0;i<40;i++){c.advance(60000);await c.state.tick();}assert.equal(c.sends,0);
 c.state.interact({isTrusted:false});await c.state.tick();assert.equal(c.sends,0);
 c.hide();c.state.interact({isTrusted:true});await c.state.tick();assert.equal(c.sends,0);
 c.show();await c.state.tick();assert.equal(c.sends,0);c.state.stop();assert.equal(c.cleared,true);
});
test('only recent real interaction is reported, throttled to once a minute',async()=>{
 const c=clock();c.state.interact({isTrusted:true});await c.state.tick();assert.equal(c.sends,1);
 c.advance(10000);c.state.interact({isTrusted:true});await c.state.tick();assert.equal(c.sends,1);
 c.advance(50000);c.state.interact({isTrusted:true});await c.state.tick();assert.equal(c.sends,2);
 c.advance(180000);await c.state.tick();assert.equal(c.sends,2);c.state.stop();
});
test('activity 401 ends reporting; transient service failures do not log the user out',async()=>{
 const c=clock({fail:401});c.state.interact({isTrusted:true});await c.state.tick();assert.equal(c.expired,1);c.advance(60000);c.state.interact({isTrusted:true});await c.state.tick();assert.equal(c.sends,1);c.state.stop();
 const offline=clock({fail:503});offline.state.interact({isTrusted:true});await offline.state.tick();assert.equal(offline.expired,0);offline.state.stop();
});
test('unsaved drafts are isolated by verified account ID and never contain authentication data',()=>{
 const data=new Map(),store=drafts({setItem:(key,value)=>data.set(key,value),getItem:key=>data.get(key),removeItem:key=>data.delete(key)});
 store.save('account-a',{editingID:'note-a',document:{title:'A',text:'Unsent contents',attachments:[]}});
 assert.equal(store.read('account-b'),null);assert.equal(store.read('account-a').document.text,'Unsent contents');
 data.set('kimnotes-draft:account-b',data.get('kimnotes-draft:account-a'));assert.equal(store.read('account-b'),null);
 assert.doesNotMatch([...data.values()].join(''),/token|password|authorization/i);store.remove('account-a');assert.equal(store.read('account-a'),null);
});
test('draft storage quota and invalid JSON fail safely',()=>{
 const store=drafts({setItem(){throw new Error('quota');},getItem(){return '{broken';},removeItem(){throw new Error('blocked');}});
 assert.equal(store.save('a',{document:{text:'x'}}),false);assert.equal(store.read('a'),null);assert.doesNotThrow(()=>store.remove('a'));
});
