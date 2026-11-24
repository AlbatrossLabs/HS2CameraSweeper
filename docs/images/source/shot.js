// Renders banner.png (1600x400) and discord.png (1200x800, 3:2) at 2x from banner.html.
const { chromium } = require('playwright');
(async () => {
  const b = await chromium.launch();
  for (const [layout, w, h, out] of [['banner', 1600, 400, 'banner.png'], ['discord', 1200, 800, 'discord.png']]) {
    const p = await b.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: 2 });
    await p.goto('file://' + __dirname + '/banner.html?layout=' + layout);
    await p.waitForLoadState('networkidle'); await p.waitForTimeout(400);
    await p.screenshot({ path: out, clip: { x: 0, y: 0, width: w, height: h } });
  }
  await b.close();
})();
