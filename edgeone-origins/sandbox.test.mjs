import {test} from 'node:test';import assert from 'node:assert/strict';import worker from './sandbox.mjs';
const base='https://origin-edge-sandbox.kimlulu.com/__kim-sandbox/';
test('synthetic users have distinct private responses and unauthenticated requests fail',async()=>{
 for(const user of ['test-a','test-b']){
  const login=await worker.fetch(new Request(base+'login',{method:'POST',body:JSON.stringify({user,password:'synthetic-only'})}));
  assert.equal(login.status,200);assert.match(login.headers.get('set-cookie'),new RegExp(user));assert.equal(login.headers.get('cache-control'),'private, no-store');
  const save=await worker.fetch(new Request(base+'sync',{method:'POST',headers:{cookie:`kim_synthetic=${user}`},body:JSON.stringify({note:`synthetic-${user}`})}));assert.equal(save.status,200);assert.equal(save.headers.get('cache-control'),'private, no-store');
  const sync=await worker.fetch(new Request(base+'sync',{headers:{cookie:`kim_synthetic=${user}; kim_synthetic_note=synthetic-${user}`}}));const data=await sync.json();assert.equal(data.user,user);assert.equal(data.notes[0].text,`synthetic-${user}`);assert.equal(sync.headers.get('cache-control'),'private, no-store');
 }
 const cross=await worker.fetch(new Request(base+'sync',{headers:{cookie:'kim_synthetic=test-a; kim_synthetic_note=synthetic-test-b'}}));assert.equal((await cross.json()).notes.length,0);
 assert.equal((await worker.fetch(new Request(base+'sync'))).status,401);
 assert.equal((await worker.fetch(new Request(base+'login',{method:'POST',body:JSON.stringify({user:'actual',password:'synthetic-only'})}))).status,401);
 assert.equal((await worker.fetch(new Request('https://origin-edge-sandbox.kimlulu.com/'))).status,404);
});
