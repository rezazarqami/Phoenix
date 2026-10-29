(async () => {
  const session = await fetch('/api/auth/me', {cache:'no-store'}).then(r => r.json());
  if (!session.isAdmin) return;
  document.querySelector('#bulkControls').hidden = false;
  const status = document.querySelector('#bulkStatus');
  const resume = document.querySelector('#resumeEntriesButton');
  const buttons = [...document.querySelectorAll('#bulkControls button')];
  async function request(url) {
    const response = await fetch(url, {method:'POST', headers:{'Content-Type':'application/json'}});
    const data = await response.json().catch(() => ({}));
    if (response.status === 401) throw new Error('نشست شما منقضی شده است؛ دوباره وارد شوید.');
    if (response.status === 403) throw new Error(data.error || 'این حساب مجوز لغو گروهی ندارد؛ با حساب مدیر وارد شوید.');
    if (!response.ok) throw new Error(data.error || 'عملیات ناموفق بود؛ وضعیت را بررسی کنید.');
    return data;
  }
  async function paused() {
    const data = await fetch('/api/positions/entry-pause', {cache:'no-store'}).then(r => r.json());
    resume.hidden = !data.paused;
    if (data.paused && !status.textContent) status.textContent = 'ورودهای جدید متوقف است؛ پایش پوزیشن‌های موجود ادامه دارد.';
  }
  async function run(action) {
    buttons.forEach(b => b.disabled = true);
    status.textContent = 'در حال انجام عملیات…';
    try { await action(); }
    catch (error) { status.textContent = error.message; }
    finally {
      buttons.forEach(b => b.disabled = false);
      try { await paused(); await refreshSignals(); await refreshHistory(); window.dispatchEvent(new Event('phoenix-positions-refresh')); }
      catch { status.textContent += '\nبه‌روزرسانی نمایش ناموفق بود؛ صفحه را تازه کنید.'; }
    }
  }
  document.querySelector('#closePositionsButton').onclick = () => run(async () => {
    const preview = await request('/api/positions/close-preview');
    if (!preview.positions.length) { status.textContent = 'پوزیشن باز USDT در این حساب وجود ندارد.'; return; }
    const list = preview.positions.map(p => p.symbol + ' · ' + p.side + ' · حجم ' + p.size).join('\n');
    if (!await phoenixConfirm('حساب: ' + preview.account + '\n' + list +
        '\n\nهمه پوزیشن‌های USDT بالا (حتی دستی) با سفارش بازار بسته شوند؟ نتیجه پس از تأیید بسته‌شدن در صرافی ثبت می‌شود.')) { status.textContent = 'عملیات انجام نشد.'; return; }
    const data = await request('/api/positions/close-all/' + preview.id);
    status.textContent = data.items.map(x => x.symbol + ': ' + (x.submitted
      ? 'درخواست بستن ارسال شد؛ بسته‌شدن هنوز باید در صرافی تأیید شود.'
      : x.error)).join('\n') + '\nبرای حجم باقی‌مانده یا خطا، وضعیت صرافی را بررسی و دوباره اقدام کنید.';
  });
  resume.onclick = () => run(async () => {
    if (!await phoenixConfirm('ورودهای جدید استراتژی اصلی دوباره فعال شوند؟')) { status.textContent = 'عملیات انجام نشد.'; return; }
    await request('/api/positions/resume-entries');
    status.textContent = 'ورودهای جدید دوباره فعال شدند.';
  });
  await paused();
})();
