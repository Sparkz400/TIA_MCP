import fs from 'node:fs/promises';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import { createRequire } from 'node:module';
const require = createRequire(import.meta.url);
const M = require('./model.js');
const directory = path.dirname(fileURLToPath(import.meta.url));
export async function pack(snapshot, outputPath) {
  const document = snapshot == null ? null : M.validateSnapshot(snapshot);
  const names = ['index.template.html', 'style.css', 'model.js', 'client.js', 'explorer.js'];
  const contents = await Promise.all(names.map(name => fs.readFile(path.join(directory, name), 'utf8')));
  const serialized = JSON.stringify(document).replace(/</g, '\\u003c').replace(/>/g, '\\u003e').replace(/&/g, '\\u0026').replace(/\u2028/g, '\\u2028').replace(/\u2029/g, '\\u2029');
  // Function replacements keep literal "$&" and "$`" project labels intact.
  const html = contents[0].replace('__EXPLORER_STYLE__', () => contents[1]).replace('__PROJECT_MODEL_JSON__', () => serialized).replace('__EXPLORER_SCRIPT__', () => contents.slice(2).join('\n').replace(/<\/script/gi, '<\\/script'));
  await fs.mkdir(path.dirname(path.resolve(outputPath)), { recursive: true }); await fs.writeFile(outputPath, html, 'utf8');
  const manifest = { schemaVersion: 'project-explorer-bundle.v1', generatedAtUtc: new Date().toISOString(), entryPoint: path.basename(outputPath), snapshotId: document?.snapshotId || null, networkRequired: false, bytes: Buffer.byteLength(html), sha256: crypto.createHash('sha256').update(html).digest('hex'), assets: names.map((name, i) => ({ path: name, bytes: Buffer.byteLength(contents[i]), sha256: crypto.createHash('sha256').update(contents[i]).digest('hex') })) };
  await fs.writeFile(outputPath + '.manifest.json', JSON.stringify(manifest, null, 2) + '\n', 'utf8'); return manifest;
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const [input, output] = process.argv.slice(2);
  if (!input || !output) { console.error('Usage: node pack.mjs <snapshot.json | --empty> <output.html>'); process.exitCode = 2; }
  else { const snapshot = input === '--empty' ? null : JSON.parse(await fs.readFile(input, 'utf8')); console.log(JSON.stringify(await pack(snapshot, output), null, 2)); }
}
