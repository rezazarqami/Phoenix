# Review delivery performance

The image endpoint previously called `PendingAsync`, which deserialized the entire base64 image queue and could run a similarity migration under the review gate. Every review refresh also requested `/api/signals`, whose leverage refresh can wait for the exchange. These are code paths that can stall an otherwise healthy browser; they do not establish that every reported disconnect has the same cause.

Chart delivery now reads an immutable proposal snapshot, filtered by owner. Warm image reads bypass the review gate. Atomic queue writes replace the snapshot only after the file is persisted; external changes invalidate it by path, file timestamp and length. A cold or changed file still requires a gated disk read. The image endpoint never invokes similarity or exchange operations. Private ETag revalidation checks the session and owner before returning 304.

`/api/signals/active-count` uses local stored state and the same terminal/removed filters as the former browser counter. It does not refresh leverage or similarity. Review polling runs every five seconds while the pane is visible, with one queue read and one counter read in flight. Read timeouts are eight seconds. Existing proposals and drafts survive failed refreshes.

The browser fetches the current chart and prefetches the next two with at most two active requests. Foreground charts take queue priority and can cancel speculative work. Completed images use page-lifetime blob URLs, with at most twelve completed entries retained, and no persistent application cache. A changed selection cannot install the prior chart in the new DOM. Failed, timed-out or invalid responses can be retried; returning online retries a visible failed chart. Back-forward cache restores retain their URLs; final navigation revokes them.

Trading POSTs are unchanged and are never automatically retried. A transport failure after submission remains an uncertain outcome to be checked in the queue, not permission to submit again.

Validation covers concurrent decisions and draft preservation, prefetch reuse, bounded transport and memory, aborted requests, authentication and invalid image responses, retry, back-forward restoration, owner isolation, external queue invalidation and conditional image requests. A regression test holds the review gate while reading a warm chart, demonstrating that similarity/decision work cannot block that read.

This reduces known application bottlenecks. It cannot guarantee availability through every ISP/VPN route. Production latency percentiles, resource pressure, application restart history and failing request traces still require server-side evidence before attributing residual outages to hosting or connectivity.
