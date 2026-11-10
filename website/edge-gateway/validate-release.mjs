import { readFileSync, statSync } from 'node:fs';
import { createHash } from 'node:crypto';
import path from 'node:path';

const root = process.argv[2];
if (!root) throw new Error('missing website public directory');
const update = JSON.parse(readFileSync(path.join(root, 'update.json'), 'utf8'));
const release = JSON.parse(readFileSync(path.join(root, 'downloads', 'release.json'), 'utf8'));
if (!/^\d+\.\d+\.\d+$/.test(update.version) || update.version !== release.version) {
  throw new Error('update and download versions do not match');
}
if (release.file !== `KimNotes-v${release.version}.zip`) throw new Error('unexpected download filename');
const archive = path.join(root, 'downloads', release.file);
const hash = createHash('sha256').update(readFileSync(archive)).digest('hex');
const checksum = readFileSync(archive.replace(/\.zip$/, '.sha256'), 'utf8').trim();
if (statSync(archive).size !== release.bytes || hash !== release.sha256 || checksum !== `${hash}  ${release.file}`) {
  throw new Error('release package size or checksum does not match');
}
const index = readFileSync(path.join(root, 'index.html'), 'utf8');
if (!index.includes(`/downloads/${release.file}`) || !index.includes(`"softwareVersion":"${release.version}"`)) {
  throw new Error('website homepage does not match release version');
}
console.log(`Validated KimNotes ${release.version} download and update manifest`);
