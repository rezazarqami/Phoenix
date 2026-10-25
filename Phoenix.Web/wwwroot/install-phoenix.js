(() => {
  const button = document.getElementById('installPhoenixButton');
  const dialog = document.getElementById('installPhoenixDialog');
  const help = document.getElementById('installPhoenixHelp');
  const close = document.getElementById('closeInstallPhoenix');
  if (!button || !dialog || !help || !close) return;

  let installPrompt = null;
  const installed = () => /PhoenixAndroid\//.test(navigator.userAgent) ||
    window.matchMedia('(display-mode: standalone)').matches ||
    window.navigator.standalone === true;
  const refresh = () => { button.hidden = installed(); };
  refresh();

  window.addEventListener('beforeinstallprompt', event => {
    event.preventDefault();
    installPrompt = event;
    refresh();
  });
  window.addEventListener('appinstalled', () => {
    installPrompt = null;
    button.hidden = true;
    if (dialog.open) dialog.close();
  });
  window.matchMedia('(display-mode: standalone)').addEventListener?.('change', refresh);

  button.addEventListener('click', async () => {
    if (installed()) { refresh(); return; }
    if (installPrompt) {
      const prompt = installPrompt;
      installPrompt = null; // A browser prompt may only be used once.
      try { await prompt.prompt(); await prompt.userChoice; }
      catch { /* Use platform instructions when the browser declines the prompt. */ }
      if (installed()) refresh();
      return;
    }
    const ua = navigator.userAgent;
    help.textContent = /iPad|iPhone|iPod/.test(ua)
      ? 'در Safari دکمهٔ اشتراک‌گذاری را بزنید و «Add to Home Screen» را انتخاب کنید.'
      : /Android/.test(ua)
        ? 'از منوی سه‌نقطهٔ مرورگر، «Install app» یا «افزودن به صفحه اصلی» را بزنید و گزینهٔ نصب برنامه را انتخاب کنید. اگر قبلاً میان‌بر Chrome ساخته‌اید، پس از نصب برنامه می‌توانید میان‌بر قبلی را بردارید.'
        : 'از منوی مرورگر گزینهٔ «Install Phoenix» یا «Install app» را انتخاب کنید.';
    dialog.showModal();
  });
  close.addEventListener('click', () => dialog.close());
  dialog.addEventListener('click', event => {
    if (event.target === dialog) dialog.close();
  });
})();
