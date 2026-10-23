import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';

// Read actual downloaded MCP exports and validate their exact bytes in a real browser.
const [input, destination] = process.argv.slice(2);
assert.ok(input && destination, 'Usage: node qualify_repeated_diagnostic_experiment_browser.mjs <MCP result.json> <fresh output>');
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
const sha = value => createHash('sha256').update(value).digest('hex');
const report = { status: 'running', nativeQualification: false, browser: browser.version(),
  inputSha256: sha(inputBytes), harnessSha256: sha(await fs.readFile(new URL(import.meta.url))),
  checks: [], screenshots: [], pageErrors: [], remoteRequests: [], dialogs: [] };
function check(value, message) { assert.ok(value, message); report.checks.push(message); }
try {
  const formats = workflow.exports;
  const source = await fs.readFile(formats.json.artifact.path, 'utf8');
  const html = await fs.readFile(formats.html.artifact.path);
  check(sha(source) === formats.json.id && sha(html) === formats.html.id, 'Actual MCP JSON and HTML hashes match receipts');
  const page = await browser.newPage();
  page.on('pageerror', error => report.pageErrors.push(error.message));
  page.on('request', request => { if (/^https?:/.test(request.url())) report.remoteRequests.push(request.url()); });
  page.on('dialog', async dialog => { report.dialogs.push(dialog.message()); await dialog.dismiss(); });
  for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
    await page.setViewportSize(viewport);
    await page.goto(pathToFileURL(formats.html.artifact.path).href);
    const suffix = ' at ' + viewport.width + 'px';
    check(await page.locator('h1').count() === 1, 'One report title' + suffix);
    check(await page.locator('script,img,iframe,object,embed,a[href]').count() === 0, 'Saved evidence creates no executable or linked content' + suffix);
    check(/no statistical significance, causal effect or successful fix/.test(await page.locator('aside').innerText()), 'Limits visible before observations' + suffix);
    const text = await page.locator('main').innerText();
    check(text.includes('Repeated-run observations') && text.includes('Saved experiment') && text.includes('Explicit lineage'), 'Evidence and lineage sections rendered' + suffix);
    check(text.includes('Window verdict') && text.includes('Quality limits') && text.includes('unresolved'), 'Window quality and unresolved verdicts readable' + suffix);
    check(text.includes("</td><script>alert('offline')</script>"), 'Hostile report displayed as literal text' + suffix);
    check(await page.locator('pre').textContent() === source, 'Complete combined JSON remains exact' + suffix);
    check(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth), 'No page-level horizontal overflow' + suffix);
    if (viewport.width === 390) {
      check(await page.locator('.table-scroll').evaluateAll(tables =>
        tables.some(table => table.scrollWidth > table.clientWidth) && tables.every(table => getComputedStyle(table).overflowX === 'auto')),
        'Wide evidence tables scroll inside bounded mobile containers');
      await page.locator('.table-scroll').first().focus();
      await page.keyboard.press('ArrowRight');
      await page.waitForFunction(() => document.querySelector('.table-scroll').scrollLeft > 0);
      check(await page.locator('.table-scroll').first().evaluate(table => document.activeElement === table && table.scrollLeft > 0),
        'Mobile tables support keyboard horizontal scrolling');
      await page.locator('.table-scroll').first().evaluate(table => { table.blur(); table.scrollLeft = 0; });
    }
    check(await page.locator('th').evaluateAll(headers => headers.every(header => header.clientWidth >= 100)),
      'Evidence columns preserve readable minimum width' + suffix);
    await page.evaluate(() => window.scrollTo({ top: 0, behavior: 'instant' }));
    await page.waitForFunction(() => window.scrollY === 0);
    const screenshot = path.join(output, `combined-${viewport.width}.png`);
    await page.screenshot({ path: screenshot });
    report.screenshots.push(screenshot);
    await page.getByRole('heading', { name: 'before requested window', exact: true }).first().scrollIntoViewIfNeeded();
    const windowScreenshot = path.join(output, `window-${viewport.width}.png`);
    await page.screenshot({ path: windowScreenshot });
    report.screenshots.push(windowScreenshot);
  }
  const markdown = await fs.readFile(formats.markdown.artifact.path, 'utf8');
  check(sha(markdown) === formats.markdown.id, 'Actual MCP Markdown hash matches receipt');
  const rendered = path.join(output, 'combined-markdown.html');
  await fs.writeFile(rendered, '<!doctype html><meta charset="utf-8">' + marked.parse(markdown));
  await page.goto(pathToFileURL(rendered).href);
  check(await page.locator('script,img,iframe,object,embed,a[href]').count() === 0, 'GFM rendering keeps saved content inert');
  check(await page.locator('pre code').textContent() === source + '\n', 'GFM dynamic fence retains exact full combined JSON');
  check(await page.locator('h1').count() === 1, 'Saved hostile text cannot inject Markdown headings');
  check(report.pageErrors.length === 0 && report.remoteRequests.length === 0 && report.dialogs.length === 0, 'Zero browser errors, remote requests or dialogs');
  report.status = 'passed';
} catch (error) {
  report.status = 'failed'; report.error = String(error); throw error;
} finally {
  await browser.close();
  await fs.writeFile(path.join(output, 'result.json'), JSON.stringify(report, null, 2) + '\n');
}
console.log(`PASS ${report.checks.length} repeated diagnostic report browser checks`);
