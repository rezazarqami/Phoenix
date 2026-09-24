(() => {
  const routes = {
    crypto: '/analysis/coins?v=20260902-1&embed=1',
    signal: '/analysis/signals?v=20260827-4&embed=1',
    elliott: '/analysis?v=20260827-4&embed=1',
    strategy: '/strategy2.html?embed=1'
  };
  const tabs = [...document.querySelectorAll('.home-tab')];
  const settingsToggle = document.querySelector('#settingsToggle');
  const settingsMenu = document.querySelector('#settingsMenu');

  function fitFrame(frame) {
    let observer;
    let queued = false;
    const measure = () => {
      if (queued) return;
      queued = true;
      requestAnimationFrame(() => {
        queued = false;
        const doc = frame.contentDocument;
        if (!doc?.body) return;
        const height = Math.ceil(Math.max(doc.body.scrollHeight, doc.documentElement.scrollHeight));
        if (height > 0 && Math.abs(frame.getBoundingClientRect().height - height) > 2)
          frame.style.height = `${height}px`;
      });
    };
    frame.addEventListener('load', () => {
      observer?.disconnect();
      try {
        const doc = frame.contentDocument;
        if (!doc?.body) return;
        observer = new ResizeObserver(measure);
        observer.observe(doc.body);
        measure();
      } catch { /* External navigation cannot be measured. */ }
    });
    window.addEventListener('resize', measure);
  }

  function activate(name, updateHash = true) {
    if (name !== 'home' && !routes[name]) name = 'home';
    for (const tab of tabs) {
      const selected = tab.dataset.panel === name;
      tab.classList.toggle('active', selected);
      tab.setAttribute('aria-selected', String(selected));
      tab.tabIndex = selected ? 0 : -1;
      const panel = document.querySelector('#panel-' + tab.dataset.panel);
      panel.hidden = !selected;
      if (selected && routes[name] && !panel.firstElementChild) {
        const frame = document.createElement('iframe');
        frame.src = routes[name];
        frame.title = tab.textContent.trim();
        frame.className = 'home-content-frame';
        fitFrame(frame);
        panel.append(frame);
      }
    }
    if (updateHash && location.hash !== '#' + name) history.replaceState(null, '', '#' + name);
  }
  tabs.forEach((tab, index) => {
    tab.addEventListener('click', () => activate(tab.dataset.panel));
    tab.addEventListener('keydown', event => {
      if (!['ArrowLeft', 'ArrowRight', 'Home', 'End'].includes(event.key)) return;
      event.preventDefault();
      const next = event.key === 'Home' ? 0 : event.key === 'End' ? tabs.length - 1 :
        (index + (event.key === 'ArrowLeft' ? 1 : -1) + tabs.length) % tabs.length;
      tabs[next].focus();
      activate(tabs[next].dataset.panel);
    });
  });
  window.addEventListener('hashchange', () => activate(location.hash.slice(1), false));
  window.addEventListener('message', event => {
    if (event.origin !== location.origin || event.data?.type !== 'phoenix-open-signal') return;
    const symbol = String(event.data.symbol || '').toUpperCase();
    if (!/^[A-Z0-9]{2,24}$/.test(symbol)) return;
    activate('signal');
    const frame = document.querySelector('#panel-signal iframe');
    frame.src = routes.signal + '&symbol=' + encodeURIComponent(symbol);
  });
  activate(location.hash.slice(1), false);

  function closeSettings() {
    settingsMenu.hidden = true;
    settingsToggle.setAttribute('aria-expanded', 'false');
  }
  settingsToggle.addEventListener('click', () => {
    settingsMenu.hidden = !settingsMenu.hidden;
    settingsToggle.setAttribute('aria-expanded', String(!settingsMenu.hidden));
  });
  settingsMenu.addEventListener('click', event => {
    if (event.target.closest('button')) closeSettings();
  });
  document.addEventListener('click', event => {
    if (!event.target.closest('.header-actions')) closeSettings();
  });
  document.addEventListener('keydown', event => {
    if (event.key === 'Escape' && !settingsMenu.hidden) {
      closeSettings();
      settingsToggle.focus();
    }
  });
})();
