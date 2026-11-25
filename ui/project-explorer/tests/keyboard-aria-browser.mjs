// Keyboard navigation and ARIA state of the project tree, on its own page so the main browser flow keeps its state.
// The tree is one tab stop (role="tree", aria-activedescendant); rows never take DOM focus.
import assert from 'node:assert/strict';

export async function keyboardAriaBrowser(browser, viewerUrl, check, errors, requests, report) {
  const page = await browser.newPage({ viewport: { width: 1440, height: 1000 } });
  page.on('pageerror', e => errors.push(e.message)); page.on('request', r => { if (/^https?:/.test(r.url())) requests.push(r.url()); });
  await page.goto(viewerUrl); await page.locator('.project-name').waitFor();

  const state = () => page.evaluate(() => {
    const tree = document.querySelector('#tree'), rows = [...tree.querySelectorAll('[role="treeitem"]')];
    const row = r => ({ node: r.dataset.node, id: r.id, selected: r.getAttribute('aria-selected'), expanded: r.getAttribute('aria-expanded'), level: Number(r.getAttribute('aria-level')),
      toggleLabel: r.querySelector('.toggle').getAttribute('aria-label'), toggleDisabled: r.querySelector('.toggle').disabled, toggleTabIndex: r.querySelector('.toggle').tabIndex });
    return { focus: document.activeElement?.id || document.activeElement?.tagName, active: tree.getAttribute('aria-activedescendant'), rows: rows.map(row), model: projectExplorer.selected,
      visible: projectExplorer.visible.map(i => i.node.nodeId), depth: Object.fromEntries(projectExplorer.visible.map(i => [i.node.nodeId, i.depth])),
      names: Object.fromEntries(projectExplorer.visible.map(i => [i.node.nodeId, i.node.name])), detailToken: projectExplorer.detailToken, scrollY, filtering: projectExplorer.filtering() };
  });
  // What must hold after every key: focus stays on the tree, exactly one row is selected, and it is the active descendant.
  const settled = async (expected, label) => {
    await page.waitForFunction(id => projectExplorer.selected === id && document.querySelector('#tree [aria-selected="true"]')?.dataset.node === id, expected, { timeout: 5000 })
      .catch(() => { throw new Error(`${label}: expected selection ${expected}`); });
    const s = await state(), selected = s.rows.filter(r => r.selected === 'true');
    assert.equal(s.focus, 'tree', `${label}: focus stays on the tree`);
    assert.deepEqual(selected.map(r => r.node), [expected], `${label}: exactly one row is aria-selected`);
    assert.equal(s.active, selected[0].id, `${label}: aria-activedescendant names the selected row`);
    assert.ok(s.rows.every(r => r.selected === 'true' || r.selected === 'false'), `${label}: every row states aria-selected`);
    return s;
  };
  const press = async (key, expected, label = key) => { await page.keyboard.press(key); return settled(expected, label); };
  const rowOf = (s, node) => s.rows.find(r => r.node === node);

  // Static roles and names.
  assert.deepEqual(await page.evaluate(() => { const t = document.querySelector('#tree'); return [t.getAttribute('role'), t.getAttribute('aria-label'), t.tabIndex]; }), ['tree', 'Project objects', 0]);
  assert.deepEqual(await page.evaluate(() => { const tabs = [...document.querySelectorAll('.tabs [role="tab"]')], on = tabs.filter(t => t.getAttribute('aria-selected') === 'true');
    return [document.querySelector('.tabs').getAttribute('role'), on.length, on[0]?.id === document.querySelector('#object-panel')?.getAttribute('aria-labelledby'), tabs.every(t => t.getAttribute('aria-controls') === 'object-panel')]; }),
    ['tablist', 1, true, true]);
  check('Tree and object tabs expose tree/tablist roles, names and one selected tab labelling its panel');

  // Home / End / ArrowUp / ArrowDown clamp to the visible rows.
  await page.locator('#tree').focus();
  let s = await press('Home', 'project');
  const first = s.visible[0], last = s.visible.at(-1);
  assert.equal(first, 'project'); assert.equal(rowOf(s, 'project').expanded, 'true'); assert.equal(rowOf(s, 'project').level, 1);
  s = await press('ArrowUp', first, 'ArrowUp on the first row');
  s = await press('End', last);
  s = await press('ArrowDown', last, 'ArrowDown on the last row');
  s = await press('ArrowUp', s.visible.at(-2));
  s = await press('Home', first);
  s = await press('ArrowDown', s.visible[1]);
  assert.equal(s.model, 'hardware');
  for (const r of s.rows) {
    assert.equal(r.level, s.depth[r.node] + 1, `aria-level of ${r.node}`);
    assert.equal(r.toggleTabIndex, -1, 'row toggles are not tab stops');
    if (r.expanded === null) assert.equal(r.toggleDisabled, true, `${r.node}: a row without aria-expanded has a disabled toggle`);
    else assert.equal(r.toggleLabel, `${r.expanded === 'true' ? 'Collapse' : 'Expand'} ${s.names[r.node]}`, `${r.node}: the toggle's name states the action`);
  }
  check('Home, End, ArrowUp and ArrowDown move the single selected row, clamp at both ends and keep focus on the tree');

  // ArrowRight expands a collapsed parent, then enters it; ArrowLeft collapses an expanded parent, then goes to its parent.
  if (rowOf(s, 'hardware').expanded === 'true') { s = await press('ArrowLeft', 'hardware', 'collapse hardware'); }
  assert.equal(rowOf(s, 'hardware').expanded, 'false'); assert.ok(!s.visible.includes('device'));
  s = await press('ArrowRight', 'hardware', 'ArrowRight on a collapsed parent');
  await page.waitForFunction(() => document.querySelector('#tree [data-node="hardware"]')?.getAttribute('aria-expanded') === 'true' && !!document.querySelector('#tree [data-node="device"]'));
  s = await state(); assert.equal(rowOf(s, 'device').level, rowOf(s, 'hardware').level + 1); assert.equal(rowOf(s, 'hardware').toggleLabel, 'Collapse Hardware inventory');
  s = await press('ArrowRight', 'device', 'ArrowRight on an expanded parent');
  if (rowOf(s, 'device').expanded !== 'true') { s = await press('ArrowRight', 'device', 'expand device'); await page.waitForFunction(() => !!document.querySelector('#tree [data-node="io"]')); }
  s = await press('ArrowRight', 'io', 'ArrowRight into the leaf');
  assert.equal(rowOf(s, 'io').expanded, null, 'a leaf has no aria-expanded'); assert.equal(rowOf(s, 'io').toggleDisabled, true); assert.equal(rowOf(s, 'io').level, 4);
  s = await press('ArrowLeft', 'device', 'ArrowLeft on a leaf goes to its parent');
  s = await press('ArrowLeft', 'device', 'ArrowLeft on an expanded parent');
  await page.waitForFunction(() => document.querySelector('#tree [data-node="device"]')?.getAttribute('aria-expanded') === 'false' && !document.querySelector('#tree [data-node="io"]'));
  s = await press('ArrowLeft', 'hardware', 'ArrowLeft on a collapsed parent goes to its parent');
  s = await press('ArrowLeft', 'hardware', 'collapse hardware again');
  await page.waitForFunction(() => document.querySelector('#tree [data-node="hardware"]')?.getAttribute('aria-expanded') === 'false' && !document.querySelector('#tree [data-node="device"]'));
  s = await press('ArrowLeft', 'project', 'ArrowLeft reaches the root');
  check('ArrowRight expands then enters, ArrowLeft collapses then leaves; aria-expanded, aria-level and toggle names follow');

  // ArrowRight on a leaf must not change what ArrowLeft does next: the row shows no expanded state, so one ArrowLeft leaves it.
  await page.evaluate(async () => { await projectExplorer.select('io'); }); await page.locator('#tree').focus(); s = await settled('io', 'select the leaf');
  s = await press('ArrowRight', 'io', 'ArrowRight on a leaf');
  assert.equal(rowOf(s, 'io').expanded, null, 'ArrowRight on a leaf does not give it aria-expanded');
  s = await press('ArrowLeft', 'device', 'ArrowLeft after ArrowRight on a leaf goes to the parent');
  check('ArrowLeft after ArrowRight on a leaf goes to the parent');

  // Enter and Space activate the selected row (reload its details) without moving it or scrolling the page.
  s = await press('Home', 'project');
  for (const key of ['Enter', 'Space']) {
    const before = await state(); await page.keyboard.press(key);
    await page.waitForFunction(token => projectExplorer.detailToken > token, before.detailToken, { timeout: 5000 }).catch(() => { throw new Error(key + ' did not activate the selected row'); });
    const after = await settled('project', key);
    assert.equal(after.scrollY, before.scrollY, key + ' does not scroll the page'); assert.deepEqual(after.visible, before.visible, key + ' does not expand or collapse');
  }
  check('Enter and Space activate the selected row without moving the selection, toggling it or scrolling');

  // A letter key is not bound in the tree; Ctrl+F moves focus to the search box; Escape clears the search.
  const beforeLetter = await state(); await page.keyboard.press('f'); await page.waitForTimeout(100);
  const afterLetter = await state(); assert.equal(afterLetter.focus, 'tree'); assert.equal(afterLetter.model, beforeLetter.model); assert.equal(afterLetter.detailToken, beforeLetter.detailToken);
  await page.keyboard.press('Control+f'); await page.waitForFunction(() => document.activeElement?.id === 'search');
  await page.keyboard.type('MotorControl'); await page.waitForFunction(() => projectExplorer.filtering() && document.querySelectorAll('#tree [role="treeitem"]').length === 2);
  await page.locator('#tree').focus();
  s = await press('Home', (await state()).visible[0], 'Home in search results');
  assert.ok(s.rows.every(r => r.expanded === null || r.expanded === 'true' || r.expanded === 'false'));
  s = await press('ArrowDown', s.visible[1], 'ArrowDown in search results');
  s = await press('ArrowDown', s.visible[1], 'ArrowDown on the last search result');
  const found = s.model;
  await page.keyboard.press('Escape');
  await page.waitForFunction(() => !projectExplorer.filtering() && document.querySelector('#search').value === '' && !!document.querySelector('#tree [data-node="project"]'));
  s = await state();
  assert.equal(s.model, found, 'Escape keeps the selected object'); assert.equal(s.focus, 'tree', 'Escape leaves focus where it was');
  check('Ctrl+F focuses the search box, arrow keys move through search results, Escape clears the search and keeps the selection; a plain letter does nothing');
  await page.close();
}
