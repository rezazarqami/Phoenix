# Loading and retained history

The journal restores up to 400 previously viewed rows per source/tab from account-scoped local storage after validating the current identity (or reusing the authenticated same-origin parent identity). It renders cached data before awaiting the history response. The authoritative archive remains on the server with its existing older-page pagination; browser storage limits do not remove archived rows.

History responses carry a private, owner-scoped weak ETag derived from the complete page, including wallet recovery values. Unchanged polls return 304 without a JSON body or DOM replacement. New events and changed wallets merge by event ID. Cached focus links still request their selected event if it is absent locally. Storage errors fall back to normal network operation. Unauthorized responses clear the displayed/stored journal, and explicit web logout removes journal caches.

The home archive used to request up to 1,000 rows every 5 seconds. It now polls every 30 seconds while the home panel is visible, refreshes on return/explicit invalidation, and skips unchanged DOM rendering. Slow status, signals and history requests cannot accumulate overlapping polls. Live visible status/signals retain their existing 1/5-second intervals. Hidden journal iframes stop polling and refresh on return.

Authenticated JS/CSS receive compression. Versioned static assets may be retained privately for one hour; unversioned assets revalidate with ETags instead of unconditional redownloads. HTML/authentication/transaction APIs remain uncached. Only the read-only canonical journal API joins asset compression. Asset version strings were bumped for changed files.

Validation includes account-isolated persistence, delayed network/304 DOM retention, wallet changes, expiry cleanup, hidden polling, overlapping-request prevention, HTTP compression/revalidation, routing/authentication and the complete existing C# regression suite. No end-user latency multiplier is claimed; actual improvement depends on connection latency, stored history and server load. Local Chromium layout tests could not launch because the environment lacks the GLES shared library; CI retains the existing browser suite.

## ساخت پیشنهاد — اسکن محدود و استفادهٔ دوباره از کندل‌ها

اسکنِ هر دستهٔ هشت‌تایی اکنون حداکثر سه رمزارز را هم‌زمان بررسی می‌کند. ترتیب ورودی پیش از رتبه‌بندی حفظ می‌شود، بنابراین انتخاب نزدیک‌ترین گزینه و ترتیب شکستن تساوی‌ها تغییر نمی‌کند. مجموع درخواست‌های اسکن (قوانین نماد و کندل‌ها) حداکثر شش درخواست هم‌زمان و حداکثر بیست شروع درخواست در ثانیه دارد تا فضای پایش و حساب حفظ شود. دریافت قوانین نماد و کندل‌هایش نیز هم‌زمان انجام می‌شود.

کندل‌های کامل همان اسکن به تحلیل حمایت و مقاومت داده می‌شوند؛ دادهٔ مشترک دوباره دانلود نمی‌شود. نمودار نیز از دادهٔ اسکن یا کش کوتاه تحلیل استفاده می‌کند. زمان مشاهده و حذف کندل‌های بسته‌نشده همچنان در مسیر تحلیل رعایت می‌شود؛ پنجره‌ها، معیارهای انتخاب، فیلترهای سیگنال و معیار شباهت عوض نشده‌اند. در آزمون با دادهٔ یکسان، ویژگی‌های فنی دقیقاً یکسان باقی ماندند؛ درخواست‌های اضافی تحلیل در نمونهٔ همهٔ تایم‌فریم‌ها از شش به سه کاهش یافت و دانلود تکراری نمودار حذف شد.

کش تحلیل پیش‌تر پس از انقضای ۴۵ ثانیه صرفاً از استفاده کنار گذاشته می‌شد، اما آرایهٔ کندل‌ها در حافظه می‌ماند. اکنون ورودی‌های منقضی هنگام درج دادهٔ جدید حذف می‌شوند و حداکثر ۱۲۸ تاریخچه نگه داشته می‌شود. حذف شرطی مانع پاک‌کردن تصادفی داده‌ای می‌شود که هم‌زمان تازه شده است.

زمان اسکن دسته و آماده‌سازی پیشنهاد در گزارش سرور و فیلدهای `lastScanDurationMs` و `lastPreparationDurationMs` وضعیت صف ثبت می‌شود. این اعداد برای تشخیص کندی واقعی دریافت داده در برابر تحلیل به کار می‌روند؛ کاهش تعداد درخواست در آزمون، تضمین زمان ثابت برای پاسخ صرافی نیست.
