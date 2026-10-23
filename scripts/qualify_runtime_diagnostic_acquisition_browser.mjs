import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';

const [input, destination] = process.argv.slice(2);
assert.ok(input && destination, 'Usage: node qualify_runtime_diagnostic_acquisition_browser.mjs <MCP result.json> <fresh output>');
const output = path.resolve(destination);
await fs.mkdir(path.dirname(output), { recursive: true });
await fs.mkdir(output);
const inputBytes = await fs.readFile(input), workflow = JSON.parse(inputBytes);
assert.equal(workflow.status, 'passed');
const require = createRequire(import.meta.url);
const bundledModules = path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules');
const { marked } = await import(pathToFileURL(require.resolve('marked', { paths: [process.cwd(), bundledModules] })).href);
let playwright;
try { playwright = require('playwright'); }
catch { playwright = require(process.env.PLAYWRIGHT_MODULE || path.join(bundledModules, 'playwright')); }
const bundled = playwright.chromium.executablePath();
const executablePath = process.env.EXPLORER_BROWSER || (await fs.stat(bundled).then(s => s.isFile(), () => false)
  ? bundled : process.platform === 'win32' ? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' : undefined);
const browser = await playwright.chromium.launch({ headless: true, executablePath });
const sha = bytes => createHash('sha256').update(bytes).digest('hex');
const report = { status: 'running', nativeQualification: false, browser: browser.version(),
  inputSha256: sha(inputBytes), harnessSha256: sha(await fs.readFile(new URL(import.meta.url))),
  checks: [], screenshots: [], pageErrors: [], remoteRequests: [], dialogs: [] };
function check(ok, message) { assert.ok(ok, message); report.checks.push(message); }
try {
  const formats = workflow.exports;
  const source = await fs.readFile(formats.json.artifact.path, 'utf8');
  const document = JSON.parse(source);
  const html = await fs.readFile(formats.html.artifact.path);
  check(sha(source) === formats.json.id && sha(html) === formats.html.id, 'Actual MCP JSON/HTML hashes match receipts');
  check(document.schemaVersion === 'diagnostic-runtime-acquisition.v1', 'New acquisition contract is the actual exported source');
  const page = await browser.newPage();
  page.on('pageerror', error => report.pageErrors.push(error.message));
  page.on('request', request => { if (/^https?:/.test(request.url())) report.remoteRequests.push(request.url()); });
  page.on('dialog', async dialog => { report.dialogs.push(dialog.message()); await dialog.dismiss(); });
  for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    await page.goto(pathToFileURL(formats.html.artifact.path).href);
    const suffix = ` at ${viewport.width}px`;
    check(await page.locator('h1').count() === 1, 'Exactly one title' + suffix);
    check(await page.locator('script,img,iframe,object,embed,a[href]').count() === 0, 'Hostile evidence creates no active/linked nodes' + suffix);
    const text = await page.locator('main').innerText();
    check(text.includes('sequential polling') && text.includes('not simultaneous or scan synchronized') &&
      text.includes('separate from imported before/after'), 'Acquisition uncertainty and composition boundary visible' + suffix);
    check(text.includes('Timing, quality, gaps and capture limits') && text.includes('byteLimit') &&
      text.includes('unsupportedValues') && text.includes('droppedReadResults'), 'Quality and truncation evidence readable' + suffix);
    check(text.includes('Selected signals and retained associations') && text.includes('requestStartedUtc') &&
      text.includes('responseReceivedUtc') && text.includes('hostClockWentBackwards'), 'Exact mapping and per-read times visible' + suffix);
    check(text.includes("Acquisition </pre><script>fetch('https://invalid.example/')</script>"),
      'Hostile reason displayed as literal text' + suffix);
    check(await page.locator('pre code').last().textContent() === source, 'Embedded complete JSON exactly equals original source' + suffix);
    check(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'No page-level horizontal overflow' + suffix);
    check(await page.locator('pre').evaluateAll(rows => rows.every(row => row.tabIndex === 0)), 'Every evidence block is keyboard focusable' + suffix);
    await page.locator('pre').first().focus();
    check(await page.locator('pre').first().evaluate(node => document.activeElement === node), 'Keyboard focus reaches evidence block' + suffix);
    await page.keyboard.press('Tab');
    check(await page.locator('pre').nth(1).evaluate(node => document.activeElement === node), 'Tab advances between evidence blocks' + suffix);
    await page.evaluate(() => { document.activeElement?.blur(); window.scrollTo({ top: 0, behavior: 'instant' }); });
    const screenshot = path.join(output, `acquisition-${viewport.width}.png`);
    await page.screenshot({ path: screenshot }); report.screenshots.push(screenshot);
    await page.getByRole('heading', { name: 'Timing, quality, gaps and capture limits', exact: true }).scrollIntoViewIfNeeded();
    const qualityScreenshot = path.join(output, `quality-${viewport.width}.png`);
    await page.screenshot({ path: qualityScreenshot }); report.screenshots.push(qualityScreenshot);
  }
  const markdown = await fs.readFile(formats.markdown.artifact.path, 'utf8');
  check(sha(markdown) === formats.markdown.id, 'Actual MCP Markdown hash matches receipt');
  const rendered = path.join(output, 'acquisition-markdown.html');
  await fs.writeFile(rendered, '<!doctype html><meta charset="utf-8">' + marked.parse(markdown));
  await page.goto(pathToFileURL(rendered).href);
  check(await page.locator('script,img,iframe,object,embed,a[href]').count() === 0, 'GFM keeps all saved content inert');
  check(await page.locator('pre code').last().textContent() === source + '\n', 'GFM dynamic fence preserves exact complete JSON');
  check(await page.locator('h1').count() === 1, 'Hostile evidence cannot inject Markdown headings');
  check(report.pageErrors.length === 0 && report.remoteRequests.length === 0 && report.dialogs.length === 0,
    'Zero browser errors, remote requests or dialogs');
  report.status = 'passed';
} catch (error) {
  report.status = 'failed'; report.error = String(error); throw error;
} finally {
  await browser.close();
  await fs.writeFile(path.join(output, 'result.json'), JSON.stringify(report, null, 2) + '\n');
}
console.log(`PASS ${report.checks.length} runtime acquisition report browser checks`);
