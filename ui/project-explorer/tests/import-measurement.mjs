// Measure the user file-import path. Large objects passed through evaluate() leave
// automation deserialization/GC work in the interval before the next painted frame.
export async function observeImports(page) {
  await page.evaluate(() => {
    globalThis.explorerImports = [];
    const input = document.querySelector('.file-input');
    let pending;
    input.addEventListener('change', () => {
      pending = { startedAt: performance.now(), fileBytes: input.files[0]?.size };
      explorerImports.push(pending);
    }, true);
    const load = projectExplorer.load;
    projectExplorer.load = async function (...args) {
      const measurement = pending;
      const result = await load.apply(this, args);
      if (measurement) {
        measurement.loadedAt = performance.now();
        requestAnimationFrame(() => requestAnimationFrame(() => {
          measurement.firstViewportMs = performance.now() - measurement.startedAt;
          measurement.snapshotId = this.snapshot.snapshotId;
          measurement.renderedRows = document.querySelectorAll('.tree-row').length;
          measurement.done = true;
        }));
      }
      return result;
    };
    const error = projectExplorer.error;
    projectExplorer.error = function (value) {
      if (pending && !pending.done) {
        pending.error = value.code || value.name || 'Error';
        pending.errorMessage = value.message;
        pending.done = true;
      }
      return error.call(this, value);
    };
  });
}

export async function importSnapshot(page, file) {
  const count = await page.evaluate(() => explorerImports.length);
  await page.locator('.file-input').setInputFiles(file);
  await page.waitForFunction(count => explorerImports.length === count + 1 && explorerImports[count].done, count);
  return page.evaluate(count => explorerImports[count], count);
}

export function meetsViewportTarget(run) {
  return !run.error && Number.isFinite(run.firstViewportMs) && run.firstViewportMs >= 0 && run.firstViewportMs < 1000 &&
    Number.isInteger(run.renderedRows) && run.renderedRows > 0 && run.renderedRows < 70;
}
