(async () => {
  const analysis = location.pathname.startsWith('/analysis');
  const response = await fetch(analysis ? '/api/analysis/auth/me' : '/api/auth/me', { cache: 'no-store' });
  if (!response.ok) return;
  const session = await response.json();
  if (!session.viewerOnly) return;
  document.body.classList.add('viewer-only');
  const style = document.createElement('style');
  style.textContent = `.viewer-only .approve,.viewer-only #finalConfirm,.viewer-only .batch-box button,.viewer-only #signalForm,.viewer-only .remove,.viewer-only #securityButton,.viewer-only #usersButton{display:none!important}.viewer-notice{margin:10px 18px;padding:9px 12px;border:1px solid #7b5a1f;border-radius:8px;background:#e8b84b12;color:#e8c66b;text-align:center;font:11px Vazirmatn,Tahoma,sans-serif}`;
  document.head.append(style);
  const notice = document.createElement('div');
  notice.className = 'viewer-notice';
  notice.textContent = 'حساب فقط مشاهده‌گر — امکان ثبت، تأیید، حذف یا تغییر اطلاعات غیرفعال است.';
  const header = document.querySelector('header');
  if (header) header.insertAdjacentElement('afterend', notice);
})();
