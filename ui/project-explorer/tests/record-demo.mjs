// Records docs/assets/demo.gif: the actual Project Explorer UI driven by Playwright
// on the synthetic packaging-line fixture. No TIA Portal, PLCSIM or Host is involved.
// Usage: npm run demo:record [-- --output <gif>] [--frames <dir>]
// Requires Playwright with Chromium (or Chrome; EXPLORER_BROWSER overrides) and a
// Python with Pillow for GIF encoding (PYTHON overrides the interpreter name).
import fs from 'node:fs/promises';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { createRequire } from 'node:module';
import { fileURLToPath, pathToFileURL } from 'node:url';
import { pack } from '../pack.mjs';
const require = createRequire(import.meta.url);
const { fixture } = require('./fixture.cjs');
const playwright = require('playwright');
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../../..');
const argument = (name, fallback) => { const i = process.argv.indexOf(name); return i < 0 ? fallback : path.resolve(process.argv[i + 1]); };
const gifPath = argument('--output', path.join(root, 'docs/assets/demo.gif'));
const work = argument('--frames', path.join(root, 'output/demo'));
const python = process.env.PYTHON || (process.platform === 'win32' ? 'python' : 'python3');
const probe = spawnSync(python, ['-c', 'import PIL'], { encoding: 'utf8' });
if (probe.status !== 0) throw new Error(`GIF encoding needs Python with Pillow; "${python} -c \"import PIL\"" failed. Set PYTHON to an interpreter that has Pillow.`);

const width = 960, height = 720, band = 40;
await fs.rm(work, { recursive: true, force: true }); await fs.mkdir(path.join(work, 'frames'), { recursive: true });
const viewer = path.join(work, 'demo-viewer.html'); await pack(fixture(), viewer);
const bundled = playwright.chromium.executablePath();
const executablePath = process.env.EXPLORER_BROWSER || (await fs.stat(bundled).then(() => bundled, () => process.platform === 'win32' ? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' : undefined));
const browser = await playwright.chromium.launch({ headless: true, executablePath, args: ['--allow-file-access-from-files'] });
const frames = [];
try {
  const page = await browser.newPage({ viewport: { width, height }, colorScheme: 'light', reducedMotion: 'reduce', locale: 'en-US', timezoneId: 'UTC' });
  const captionPage = await browser.newPage({ viewport: { width, height: band } });
  const errors = []; page.on('pageerror', e => errors.push(e.message));
  page.on('request', r => { if (/^https?:/.test(r.url())) errors.push('Unexpected network request ' + r.url()); });
  // Caption band is rendered separately and stacked below the app, so no app pixels are covered.
  const caption = async text => { await captionPage.setContent(`<body style="margin:0;height:${band}px;display:flex;align-items:center;gap:14px;padding:0 16px;background:#171717;color:#f2f0e8;font:600 15px Segoe UI,Arial,sans-serif"><span style="background:#dfff00;color:#171717;padding:3px 8px;font-size:12px;font-weight:800;letter-spacing:.5px">SYNTHETIC SAMPLE DATA</span><span>${text}</span></body>`); const file = path.join(work, `caption-${frames.length}.png`); await captionPage.screenshot({ path: file }); return file; };
  let current = null;
  const shot = async (seconds, text, still) => {
    if (text) current = await caption(text);
    await page.evaluate(() => { const toast = document.querySelector('.toast'); if (toast) toast.hidden = true; });
    await page.waitForTimeout(120);
    const file = path.join(work, 'frames', String(frames.length).padStart(3, '0') + '.png');
    await page.screenshot({ path: file }); frames.push({ file, caption: current, ms: Math.round(seconds * 1000), still });
  };
  await page.goto(pathToFileURL(viewer).href); await page.locator('.project-name').waitFor();
  await shot(2.8, 'Actual Project Explorer UI · offline project-model snapshot (packaging line)');
  const search = page.getByRole('searchbox', { name: 'Search project' }); await search.focus();
  const query = 'MotorControl';
  for (let i = 2; i <= query.length; i += 2) { await search.fill(query.slice(0, i)); await shot(0.18, i === 2 ? 'Search the project for a block' : null); }
  await page.waitForFunction(() => document.querySelectorAll('.tree-row').length === 2);
  await shot(1.8, 'Two matches: same name, distinct PLCs and exact native references', '1-search');
  await page.locator('[data-node="motor-a"]').click(); await page.waitForFunction(() => projectExplorer.details?.reference?.nodeId === 'motor-a');
  await shot(2.6, 'Select MotorControl (FB) in PLC_A · captured block metadata');
  await page.getByRole('tab', { name: 'Relationships', exact: true }).click(); await page.locator('.edge-row').first().waitFor();
  await page.locator('.graph-wrap').scrollIntoViewIfNeeded();
  await shot(3.6, 'Relationships: Main calls MotorControl; I/O association; one unresolved read');
  const callRow = page.locator('.edge-row').filter({ hasText: 'calls' });
  await callRow.scrollIntoViewIfNeeded(); await shot(1.4, 'Each connection keeps its source evidence');
  await callRow.getByRole('button', { name: 'Evidence', exact: true }).click(); await page.locator('#edge-evidence').waitFor();
  await page.locator('#edge-evidence').scrollIntoViewIfNeeded();
  await shot(3.8, 'Evidence for the call: origin, resolution and source location', '2-evidence');
  await page.keyboard.press('Escape'); await page.waitForFunction(() => !document.querySelector('.tree-row.search-row'));
  const tree = page.locator('#tree'); await tree.focus();
  await shot(1.4, 'Clear the search · back to the project tree');
  const steps = [['Home', 'Keyboard: Home jumps to the project root'], ['ArrowDown', 'Arrow Down moves to Hardware inventory'], ['ArrowRight', 'Arrow Right expands it'], ['ArrowDown', null], ['ArrowRight', 'Expand Main cabinet'], ['ArrowDown', 'Digital inputs selected · its relationships follow the selection']];
  for (const [index, [key, text]] of steps.entries()) {
    const last = index === steps.length - 1; await page.keyboard.press(key); await page.waitForTimeout(150);
    if (last) { await page.waitForFunction(() => projectExplorer.selected === 'io'); await page.waitForTimeout(400); await page.locator('.graph-wrap').scrollIntoViewIfNeeded(); }
    await shot(last ? 3.6 : 1.0, text, last ? '3-keyboard' : undefined);
  }
  if (errors.length) throw new Error('Explorer errors during recording: ' + errors.join('; '));
} finally { await browser.close(); }
await fs.writeFile(path.join(work, 'frames.json'), JSON.stringify({ width, height, band, gif: gifPath, stills: path.join(work, 'stills'), frames }, null, 2));

// Encode: stack app frame + caption band, one shared palette, no dithering, per-frame durations.
const encoder = String.raw`
import json, os, sys
from PIL import Image
spec = json.load(open(sys.argv[1], encoding='utf-8'))
w, h, band = spec['width'], spec['height'], spec['band']
images = []
for f in spec['frames']:
    canvas = Image.new('RGB', (w, h + band), '#171717')
    canvas.paste(Image.open(f['file']).convert('RGB'), (0, 0))
    canvas.paste(Image.open(f['caption']).convert('RGB'), (0, h))
    images.append(canvas)
step = max(1, len(images) // 12)
sample = images[::step]
mosaic = Image.new('RGB', (w, (h + band) * len(sample)))
for i, im in enumerate(sample): mosaic.paste(im, (0, i * (h + band)))
palette = mosaic.quantize(colors=128, method=Image.Quantize.MEDIANCUT)
frames = [im.quantize(palette=palette, dither=Image.Dither.NONE) for im in images]
frames[0].save(spec['gif'], save_all=True, append_images=frames[1:], duration=[f['ms'] for f in spec['frames']], loop=0, optimize=False, disposal=1)
os.makedirs(spec['stills'], exist_ok=True)
for i, f in enumerate(spec['frames']):
    if f.get('still'): images[i].save(os.path.join(spec['stills'], f['still'] + '.png'))
print(json.dumps({'gif': spec['gif'], 'frames': len(frames), 'seconds': sum(f['ms'] for f in spec['frames']) / 1000, 'bytes': os.path.getsize(spec['gif'])}))
`;
const result = spawnSync(python, ['-c', encoder, path.join(work, 'frames.json')], { encoding: 'utf8' });
if (result.status !== 0) throw new Error('GIF encoding failed: ' + result.stderr);
process.stdout.write(result.stdout);
