const { test, expect } = require('@playwright/test');

// The Ollama widget reads /api/state.ollama, which the agent fills from ollama.com with the key from the settings.
// These tests drive it with a fixture so they never touch the network.
async function panel(page, widget) {
  await page.goto('/panel/style.css');
  await page.evaluate((w) => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { language: 'en' },
      pages: [{ id: 'one', title: 'One', widgets: [{ id: 'o', type: 'ollama', x: 0, y: 0, ...w, options: {} }] }],
    }));
  }, widget);
  await page.goto('/panel/');
  await page.evaluate(async () => { await import('/panel/main.js'); window.onAppPause(true); });
}

test('the Ollama widget shows the balance, the plan and the run rate', async ({ page }) => {
  await panel(page, { w: 6, h: 3 });
  await page.evaluate(async () => {
    const { D, emit } = await import('/panel/main.js');
    D.state = {
      ...D.state,
      ollama: {
        plan: 'Free', allowanceUsd: 0, includedLeftUsd: 0, purchasedUsd: 19.39, availableUsd: 19.39,
        dayUsd: 0.59, weekUsd: 0.59, requests24h: 391, requests7d: 391, burnPerDay: 0.0843, runwayDays: 230,
        hours: [0, 0.2, 0.1], updatedAt: Date.now() - 3000, error: null,
      },
    };
    emit('state');
  });

  const box = page.locator('.w-ollama');
  // the panel formats money with Intl.NumberFormat, so the exact symbol follows the phone's locale
  await expect(box.locator('.ol-bal')).toContainText('19.39');
  await expect(box.locator('.ol-plan')).toHaveText('Free');
  await expect(box.locator('.ol-stat').nth(2).locator('b')).toHaveText('230');
  await expect(box.locator('.ol-stat').nth(3).locator('b')).toHaveText('391');
  await expect(box.locator('.ol-foot')).toContainText('/ 24h');
});

test('a full-page Ollama widget draws the 24-hour strip', async ({ page }) => {
  await panel(page, { w: 12, h: 6 });
  await page.evaluate(async () => {
    const { D, emit } = await import('/panel/main.js');
    D.state = {
      ...D.state,
      ollama: {
        plan: 'Pro', allowanceUsd: 60, includedLeftUsd: 45, purchasedUsd: 0, availableUsd: 45,
        dayUsd: 1.5, weekUsd: 7, requests24h: 120, requests7d: 700, burnPerDay: 1, runwayDays: 45,
        hours: [0, 0.4, 0.1, 1.2], updatedAt: Date.now(), error: null,
      },
    };
    emit('state');
  });

  const box = page.locator('.w-ollama');
  await expect(box.locator('.ol-cap')).toContainText('45.00');
  await expect(box.locator('.ol-cap')).toContainText('60.00');
  await expect(box.locator('.ol-bars i')).toHaveCount(4);
  // the tallest hour fills the strip, and an hour without spend stays a flat marker
  const heights = await box.locator('.ol-bars i').evaluateAll((nodes) => nodes.map((n) => n.style.height));
  expect(heights[3]).toBe('100%');
  await expect(box.locator('.ol-bars i.zero')).toHaveCount(1);
});

test('without an API key the widget says what to do instead of showing numbers', async ({ page }) => {
  await panel(page, { w: 6, h: 3 });
  await page.evaluate(async () => {
    const { D, emit } = await import('/panel/main.js');
    D.state = { ...D.state, ollama: null };
    emit('state');
  });

  await expect(page.locator('.w-ollama .ol-empty')).toHaveText('Add an Ollama API key in Settings › General');
  await expect(page.locator('.w-ollama .ol-bal')).toBeHidden();
});
