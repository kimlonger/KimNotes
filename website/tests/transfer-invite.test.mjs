import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile} from 'node:fs/promises';
import {JSDOM} from 'jsdom';

const html = await readFile(new URL('../public/transfer/index.html',import.meta.url),'utf8');
const script = await readFile(new URL('../public/transfer/invite.js',import.meta.url),'utf8');
test('desktop pickup links copy the eight-digit code or the original legacy invitation',async()=>{
 const prefix='https://note.kimlulu.com/transfer/#', legacy='A'.repeat(43);
 for (const [fragment,expected] of [['2ab3cd4e','2AB3CD4E'],[legacy,prefix+legacy],['00000000',null],['A'.repeat(42)+'1',null],['',null]]) {
  const dom=new JSDOM(html,{url:prefix+fragment,runScripts:'outside-only'}), w=dom.window, copied=[];
  try {
   Object.defineProperty(w.navigator,'clipboard',{value:{writeText:async value=>copied.push(value)}});
   w.eval(script);
   const button=w.document.querySelector('#copy-invitation');
   assert.equal(button.hidden,expected===null);button.click();await Promise.resolve();
   assert.deepEqual(copied,expected?[expected]:[]);
   assert.doesNotMatch(w.document.querySelector('.auth-preview').textContent,/打开邀请/);
  }finally{w.close();}
 }
});
