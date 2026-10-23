import fs from 'node:fs/promises';
import path from 'node:path';
import os from 'node:os';
import assert from 'node:assert/strict';
import { createHash } from 'node:crypto';
import { createRequire } from 'node:module';
import { pathToFileURL } from 'node:url';

// Consume actual MCP receipts; do not substitute hand-authored HTML fixtures.
const [input, destination] = process.argv.slice(2);
assert.ok(input && destination, 'Usage: node qualify_diagnostic_report_browser.mjs <MCP result.json> <fresh output directory>');
const output = path.resolve(destination);
await fs.mkdir(path.dirname(output), { recursive: true });
await fs.mkdir(output);
const bytes = await fs.readFile(input), workflow = JSON.parse(bytes);
assert.equal(workflow.status, 'passed');
const require = createRequire(import.meta.url);
const bundledModules = path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules');
const { marked } = await import(pathToFileURL(require.resolve('marked', { paths: [process.cwd(), bundledModules] })).href);
let playwright;
try { playwright = require('playwright'); }
catch { playwright = require(process.env.PLAYWRIGHT_MODULE || path.join(os.homedir(), '.cache/codex-runtimes/codex-primary-runtime/dependencies/node/node_modules/playwright')); }
const bundled = playwright.chromium.executablePath();
const executablePath = process.env.EXPLORER_BROWSER || (await fs.stat(bundled).then(s => s.isFile(), () => false) ? bundled : process.platform === 'win32' ? 'C:\\Program Files\\Google\\Chrome\\Application\\chrome.exe' : undefined);
const browser = await playwright.chromium.launch({ headless: true, executablePath });
const sha = data => createHash('sha256').update(data).digest('hex');
const report = { status: 'running', evidenceKind: 'supplied_diagnostic_report_browser', nativeQualification: false,
  browser: browser.version(), inputSha256: sha(bytes), checks: [], screenshots: [], artifacts: [] };
const errors = [], network = [], dialogs = [];
try {
  const page = await browser.newPage();
  page.on('pageerror', error => errors.push(error.message));
  page.on('request', request => { if (/^https?:/.test(request.url())) network.push(request.url()); });
  page.on('dialog', async dialog => { dialogs.push(dialog.message()); await dialog.dismiss(); });
  assert.ok(Object.keys(workflow.reportExports).length > 0);
  for (const name of Object.keys(workflow.reportExports)) {
    const formats = workflow.reportExports[name];
    const source = await fs.readFile(formats.json.artifact.path, 'utf8');
    const html = await fs.readFile(formats.html.artifact.path);
    assert.equal(sha(html), formats.html.artifact.sha256);
    assert.equal(sha(source), formats.html.sourceReportId);
    report.artifacts.push({ name, htmlSha256: sha(html), sourceSha256: sha(source) });
    for (const viewport of [{ width: 1440, height: 1000 }, { width: 390, height: 844 }]) {
      await page.setViewportSize(viewport);
      await page.goto(pathToFileURL(formats.html.artifact.path).href);
      assert.equal(await page.locator('h1').count(), 1);
      assert.equal(await page.locator('script, img, iframe, object, embed, a[href]').count(), 0);
      assert.match(await page.locator('aside').innerText(), /does not prove causality/);
      assert.match(await page.locator('h2').allTextContents().then(v => v.join('|')), /Result overview/);
      assert.equal(await page.locator('details code').textContent(), source);
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
      if (name === 'hostile') assert.match(await page.locator('main').innerText(), /Über conveyor \| <img src=/);
      if (name === 'capture') {
        assert.match(await page.locator('main').innerText(), /sequential reads/);
        assert.match(await page.locator('main').innerText(), /unresolved/);
      }
      const summary = page.locator('summary');
      await summary.focus(); await page.keyboard.press('Enter');
      assert.equal(await page.locator('details').getAttribute('open'), '');
      assert.ok(await page.evaluate(() => document.documentElement.scrollWidth <= window.innerWidth));
      await page.keyboard.press('Enter');
      await page.evaluate(() => window.scrollTo(0, 0));
      const shot = path.join(output, `${name}-${viewport.width}.png`);
      await page.screenshot({ path: shot });
      report.screenshots.push(shot);
      report.checks.push(`${name} ${viewport.width}: source hash and exact JSON, inert content, limits, no page overflow, keyboard disclosure`);
    }
    const markdown = await fs.readFile(formats.markdown.artifact.path, 'utf8');
    assert.equal(sha(markdown), formats.markdown.artifact.sha256);
    const renderedMarkdown = marked.parse(markdown);
    const markdownFile = path.join(output, `${name}-markdown.html`);
    await fs.writeFile(markdownFile, '<!doctype html><meta charset="utf-8">' + renderedMarkdown);
    await page.goto(pathToFileURL(markdownFile).href);
    assert.equal(await page.locator('script, img, iframe, object, embed, a[href]').count(), 0);
    assert.equal(await page.locator('pre code').last().textContent(), source + '\n');
    assert.equal(await page.locator('h1').count(), 1);
    if (name === 'hostile') assert.match(await page.locator('body').innerText(), /Über conveyor \| <img src=/);
    report.checks.push(`${name}: GFM rendering keeps hostile content inert and complete JSON exact`);
  }
  assert.deepEqual(errors, []); assert.deepEqual(network, []); assert.deepEqual(dialogs, []);
  report.checks.push('Zero page errors, remote requests or dialogs across all renders');
  report.status = 'passed';
} catch (error) {
  report.status = 'failed'; report.error = String(error); throw error;
} finally {
  await browser.close();
  await fs.writeFile(path.join(output, 'result.json'), JSON.stringify(report, null, 2) + '\n');
}
console.log(`PASS ${report.checks.length} diagnostic report browser checks`);
