# Ice login and Crypto Markets interface

The web and bundled Android login use Phoenix's supplied blue logo, local Persian font and existing alpine artwork. Authentication still posts to `/api/auth/login` and redirects every role to `/phoenix`. Android keeps its package and signing identity; version 1.9.5 (15) is prepared for the next update.

Crypto Markets has six exclusive tabs above the two market counters: create signals, approved signals, historical data, review archive, statistics and coin search. Existing section nodes, IDs, API requests and controls are retained. The unused `freeCount` display is removed; search filters and market calculations remain available.

The Telegram queue form retains timed/count modes, duration, per-signal USDT size, direction, chart type, timeframe and start/stop controls. Number steppers obey the input bounds and disabled state. Tab changes preserve entered settings and historical filters. Keyboard navigation, tab/panel relationships and visible focus styles are included.

The home BTC card is hidden outside the home section. Crypto Markets replaces the home navigation strip with its own six tabs; the header menu provides a return to the home screen. Other main sections remain accessible from home.

Validation: existing batch/cancellation/market tests, new jsdom tab/input/login tests for web and Android HTML, and real Chromium renders at widths 360, 412, 691 and 1440. Preview counters and status are fixtures; no live queue was started. Production deployment and APK publication are not triggered by this design branch.
