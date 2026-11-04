(() => {
    const card = document.getElementById('entryControl');
    const toggle = document.getElementById('entryIntakeToggle');
    const label = document.getElementById('entryControlLabel');
    const status = document.getElementById('entryControlStatus');
    const endpoint = '/api/positions/operator-entry-pause';
    let paused = null;
    let busy = false;

    async function update(next) {
        if (busy || card.hidden) return;
        busy = true;
        toggle.disabled = true;
        if (next !== undefined) status.textContent = 'در حال ثبت تغییر…';
        const controller = new AbortController();
        const timeout = setTimeout(() => controller.abort(), 10000);
        try {
            const response = await fetch(endpoint, {
                cache: 'no-store',
                signal: controller.signal,
                ...(next === undefined ? {} : {
                    method: 'POST',
                    headers: { 'Content-Type': 'application/json' },
                    body: JSON.stringify({ paused: next })
                })
            });
            if (response.status === 403) {
                card.hidden = true;
                return;
            }
            if (!response.ok) throw new Error('Entry status unavailable');
            const result = await response.json();
            if (typeof result.paused !== 'boolean') throw new Error('Invalid entry status');
            paused = result.paused;
            card.dataset.state = paused ? 'off' : 'on';
            toggle.setAttribute('aria-checked', String(!paused));
            label.textContent = paused ? 'خاموش' : 'روشن';
            status.textContent = paused
                ? 'سیگنال در صورت رسیدن به ورود منقضی می‌شود.'
                : 'ورود سفارش‌های جدید مجاز است.';
        } catch {
            paused = null;
            card.dataset.state = 'unknown';
            toggle.setAttribute('aria-checked', 'false');
            label.textContent = '—';
            status.textContent = 'وضعیت تأیید نشد. در حال بررسی دوباره…';
        } finally {
            clearTimeout(timeout);
            busy = false;
            toggle.disabled = paused === null;
        }
    }

    function refresh() {
        if (!document.hidden && !card.closest('[hidden]')) update();
    }

    toggle.addEventListener('click', () => {
        if (paused !== null) update(!paused);
    });
    window.addEventListener('phoenix-tab-changed', refresh);
    document.addEventListener('visibilitychange', refresh);
    setInterval(refresh, 5000);
    refresh();
})();
