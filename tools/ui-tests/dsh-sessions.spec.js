const { test, expect } = require('@playwright/test');

// A DeepSeek Harness session on the AI sessions widget: its glyph, its title and the "local session" token note.
test('AI sessions widget renders a DeepSeek Harness session', async ({ page }) => {
  await page.goto('/panel/style.css');
  await page.evaluate(() => {
    localStorage.clear();
    localStorage.setItem('pb-demo-config', JSON.stringify({
      night: { enabled: false }, rules: [], shortcuts: [],
      display: { language: 'en' },
      pages: [{ id: 'one', title: 'One', widgets: [{ id: 's', type: 'sessions', x: 0, y: 0, w: 7, h: 6, options: {} }] }],
    }));
  });
  await page.goto('/panel/');
  await page.evaluate(async () => {
    const { D, emit } = await import('/panel/main.js');
    window.onAppPause(true);
    D.work = {
      sessions: [{
        id: 'dsh:3942fa18', tool: 'dsh', name: 'phonebox-src-3942', project: 'phonebox-src',
        status: 'busy', waitingFor: null, since: Date.now() - 10_000, startedAt: Date.now() - 120_000,
        model: 'deepseek-v4.1-flash', totalTokens: 1684119, inputTokens: 77457, outputTokens: 28452,
        cachedInputTokens: 1578210, lastInputTokens: 108503, contextWindow: 1000000,
        title: 'Ollama Cloud widget', turnStartedAt: Date.now() - 30_000,
      }],
      github: { user: null, notifications: 0, reviewRequests: [], myPrs: [], error: null },
    };
    emit('work');
  });

  const row = page.locator('button.sess');
  await expect(row).toHaveCount(1);
  await expect(row.locator('.tl.dsh')).toHaveText('❖');
  await expect(row.locator('.nm')).toHaveText('Ollama Cloud widget');
  await expect(row.locator('.sess-meta')).toContainText('deepseek-v4.1-flash');

  await row.click();
  await expect(page.locator('.session-panel')).toContainText('DeepSeek Harness');
  await expect(page.locator('.session-panel')).toContainText('108.5k / 1.0M');
  await expect(page.locator('.session-panel')).toContainText('Token totals are for this local session');
});
