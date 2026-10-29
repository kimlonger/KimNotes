import {test} from 'node:test';
import assert from 'node:assert/strict';
import {readFile,stat} from 'node:fs/promises';
import {JSDOM} from 'jsdom';
const html = await readFile(new URL('../public/index.html',import.meta.url),'utf8');

test('homepage keeps downloads, web notes and Linux DO in a link area that accepts more entries',() => {
  const document = new JSDOM(html).window.document;
  assert.equal(document.querySelector('#hero-title').textContent,'把想法，留在眼前。');
  assert.equal(document.querySelectorAll('.hero-actions a[download]').length,1);
  assert.equal(document.querySelector('.hero-actions .text-link').getAttribute('href'),'/notes/');
  const links = document.querySelector('[aria-label="社区与朋友"]');
  assert.equal(links.querySelector('a[href="https://linux.do"]').textContent,'LINUX DO');
  for (let i=0;i<8;i++) { const link=document.createElement('a');link.href=`https://example.com/${i}`;link.textContent=`朋友 ${i}`;links.append(link); }
  assert.equal(links.querySelectorAll('a').length,11);
  assert.equal(document.querySelector('#guide').open,false,'detailed setup is available on demand');
});
test('hero uses complete high-resolution product assets rather than a flattened mockup of the website',async() => {
  const document = new JSDOM(html).window.document;
  const images = [...document.querySelectorAll('.hero-showcase img')];
  assert.equal(images.length,3);
  for (const image of images) {
    assert.equal(image.width,672);assert.equal(image.height,768);
    assert.ok((await stat(new URL('../public'+image.getAttribute('src'),import.meta.url))).size>10000);
  }
});
