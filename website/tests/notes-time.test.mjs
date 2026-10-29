import {test} from 'node:test';
import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
const time = createRequire(import.meta.url)('../public/assets/notes-time.js');
process.env.TZ = 'Asia/Shanghai';
const note = (id,created,modified,extra = {}) => ({id,createdAt:created,updatedAt:'2026-10-29T12:00:00Z',document:{contentUpdatedAt:modified},...extra});

test('yesterday creation edited today belongs to the content-edit month, not creation or server activity',() => {
  const value = note('edited','2026-09-30T12:00:00Z','2026-10-01T02:00:00Z');
  assert.equal(time.groups([value],'month')[0].key,'2026-10');
  assert.equal(time.created(value).toISOString(),'2026-09-30T12:00:00.000Z');
  assert.equal(time.label(value,new Date('2026-10-01T04:00:00Z')),'今天更新');
  value.updatedAt = '2027-01-01T12:00:00Z';
  assert.equal(time.groups([value],'month')[0].key,'2026-10','theme or sync metadata does not change a content group');
});
test('groups and cards are newest-first, with one entry per note and deterministic equal-time ordering',() => {
  const values = [note('old','2025-01-01','2025-12-31T03:00:00Z'),note('b','2026-01-01','2026-10-01T03:00:00Z'),note('a','2026-01-01','2026-10-01T03:00:00Z')];
  const groups = time.groups(values,'year');
  assert.deepEqual(groups.map(value => value.key),['2026','2025']);
  assert.deepEqual(groups[0].notes.map(value => value.id),['a','b']);
  assert.equal(groups.flatMap(value => value.notes).length,values.length);
  assert.equal(values[0].id,'old','rendering does not mutate its source');
});
test('Monday weeks remain whole across months and years in the user timezone',() => {
  assert.deepEqual(time.calendar(new Date('2027-01-01T02:00:00Z'),'week'),{key:'2026-12-28',label:'2026年12月28日 – 2027年1月3日'});
  assert.equal(time.calendar(new Date('2026-11-01T16:15:00Z'),'week').key,'2026-11-02','local Monday differs from UTC Sunday');
  assert.equal(time.calendar(new Date('2026-10-31T16:15:00Z'),'month').key,'2026-11','local month boundary');
});
test('calendar grouping survives daylight-saving changes',() => {
  const previous = process.env.TZ; process.env.TZ = 'America/New_York';
  try { assert.equal(time.calendar(new Date('2026-11-01T17:00:00Z'),'week').key,'2026-10-26'); }
  finally { process.env.TZ = previous; }
});
test('legacy timestamps fall back without inventing dates and deleted view groups by deletion',() => {
  assert.equal(time.modified({updatedAt:'2026-09-03T04:00:00Z'}).toISOString(),'2026-09-03T04:00:00.000Z');
  assert.equal(time.groups([{id:'missing'}])[0].key,'unknown');
  assert.equal(time.groups([note('deleted','2026-01-01','2026-09-01T00:00:00Z',{deletedAt:'2026-10-29T00:00:00Z'})],'month',true)[0].key,'2026-10');
  assert.equal(time.label({date:'2026-10-28',time:'09:00'},new Date('2026-10-29T04:00:00Z')),'昨天更新');
});
