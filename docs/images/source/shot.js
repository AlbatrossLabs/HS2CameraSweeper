const { chromium } = require('playwright');
(async () => {
  const b = await chromium.launch();
  for (const [layout, w, h, dpr, out] of [['banner',1600,400,2,'banner.png'],['social',1280,640,1,'social-preview.png']]) {
    const p = await b.newPage({ viewport: { width: w, height: h }, deviceScaleFactor: dpr });
    await p.goto('file://' + __dirname + '/banner.html?layout=' + layout);
    await p.waitForTimeout(300);
    await p.screenshot({ path: out, clip: { x: 0, y: 0, width: w, height: h } });
  }
  await b.close();
})();
