# Phoenix ice homepage

The `/phoenix` homepage uses the supplied blue phoenix logo unchanged, an icy alpine header, six functional navigation cards, a live BTC ticker with four candle intervals, and three mutually exclusive Signal Center panels (new, active, history). Existing form IDs, order actions, permissions, dialogs, history, lazy-loaded lab pages, and statistics are preserved. Viewer accounts default to the active-signals panel.

`/api/market/overview` returns 50 BTCUSDT candle closes for 1, 60, D or W; it does not invoke Elliott analysis. The chart and changes use these actual readings, with missing-data states rather than fixture values. Change is measured against the first candle in the selected range. The bell opens the actual active-signal summary; the menu contains existing administration controls.

Assets:
- `Phoenix.Web/wwwroot/analysis-assets/phoenix-ice-logo.png`: supplied logo, unchanged bytes.
- `Phoenix.Web/wwwroot/analysis-assets/phoenix-ice-sky.webp`: generated hero artwork packaged for this project with the built-in image tool; logo is not regenerated.

Final hero edit prompt:
> Edit this wide landscape header illustration. Keep the same blue white silver phoenix, snowy alpine mountains, clouds and airy pale blue sunny palette. Change COMPOSITION ONLY for website header use: make the phoenix significantly smaller and position it in the LOWER RIGHT of the landscape; its HEAD must be at about 60 percent of the canvas height and at about 82 percent of the canvas width. Its raised wings rise to about 20 percent of image height, its feet and perch continue below the bottom edge (partial body crop is desired). Leave the entire top 20 percent of the canvas and the entire left half as clean pale blue sky with light clouds, so a navigation bar and logo can sit there unobstructed. Mountain peaks and clouds run across the bottom quarter. No text, no interface, no logo, no watermark. Wide horizontal panoramic image.

Validation includes real Chromium mobile/desktop renders with API fixtures, exclusive tabs, retained form values, keyboard focus, notification navigation, interval requests, chart failures, and viewer behavior. Browser fixtures are test-only and never shipped as market data.
