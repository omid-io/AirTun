# AirTun — پلن جامع فیکسها و تغییرات (تصمیم نهایی: Rail + ۶ تب)
تاریخ: 2026-08-26 | وضعیت گیت: main @ f1cd47b (تمیز)

## 🎯 تصمیمات قطعی شده
- **ناوبری ویندوز:** Rail سمت راست با ۶ تب (ماکت تاییدشده) — جایگزین nav پایین فعلی
- **ترتیب تبها:** اتصال | DNS | AI Access | مسیریابی | گزارشها | تنظیمات
- **اندروید:** فقط شیرکننده میماند؛ تب DNS/AI مال ویندوز است

## فاز ۰ — فیکسهای سریع پایداری (اولویت: قبل از هر UI)
| # | تسک | فایل | حجم |
|---|---|---|---|
| 0.1 | WakeLock بدون timeout + گره به حضور کلاینت (الگوی Relay) | SharingService.kt | ~۱۰ خط |
| 0.2 | چک شمارنده per-IP کلاینتها (باگ decrement بدون increment Relay) | Socks5Server.kt finally | ~۵ خط |
| 0.3 | معافیت Doze: راهنمای UI برای ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS (مجوز هست، UI ندارد) | HomeScreen/WarningCode | متوسط |
| 0.4 | ممیزی deadlineهای مطلق در airtun-tun/main.go (ریشه قطع ۵ دقیقهای Relay) | main.go | تحقیقی |

## فاز ۱ — Rail Navigation در WinUI (زیرساخت دو تب جدید)
- تبدیل nav پایین فعلی (Grid ۴ ستونه pill) به Grid.Column راست با عرض ~۵۶px
- SelectTab(int) به ۶ مورد گسترش یابد؛ آیکنهای SVG موجود حفظ، ۲ آیکن جدید (گlobe برای DNS، ربات/مانیتور برای AI)
- ConfigureWindow(440,700) → عرض به ~500 افزایش یابد (Rail 56px) یا همان بماند و محتوا فشرده شود — تست بصری
- RTL: rail سمت راست = ColumnDefinition اول در RTL خودکار

## فاز ۲ — تب DNS (ویندوز)
محتوا (از طراحی تأییدشده):
- لیست سرورها: builtin (System, Google, Cloudflare, Shecan, 403.online, Electro, RadarGame, Begzar, Quad9...) + سفارشی کاربر
- تست هر DNS: latency ×۳ → resolution → bypass-check (curl --resolve الگو)
- انتخاب فعال → اعمال روی:
  - TUN mode: پاس دادن DoH endpoint انتخابی به airtun-tun/main.go (به جای hardcode 1.1.1.1)
  - Proxy mode: ست DNS ویندوز روی adapter یا راهنما
- ذخیره: JSON در AppData (DnsStore)
- داده builtin: از لیست ۱۱ سرویس تحقیقشده (IP+DoH+DoT کامل موجود)

## فاز ۳ — تب AI Access (ویندوز)
- **IPv6 leak test:** resolve AAAA دامنهها هنگام اتصال → نمایش pass/fail + toggle «block AAAA» در tun2socks
- **IP reputation:** بعد از connect، query به ip-api.com از داخل تونل → کشور/ISP/proxy-flag → توصیه actionable («این کانفیگ برای Gemini مناسب نیست»)
- **Google Fix resolver:** جدول دامنههای AI (gemini.google.com, *.googleapis.com, ai.google.dev, openai.com, claude.ai) → route از طریق DNS ضدتحریم انتخابی
- **راهنمای WARP** (متن + لینک)

## ترتیب اجرا و روش انجام
| مرحله | روش | دلیل |
|---|---|---|
| فاز ۰ (همه موارد) | **تکی** — خودم مستقیم | تغییرات کوچک حساس؛ sub-agent overhead نمیارزد |
| فاز ۱ (Rail) | **تکی** — خودم | XAML layout حساس به جزئیات؛ ماکت مرجع است |
| فاز ۲ (DNS tab) | **تیمی** — ۲ سابایجنت موازی: (الف) DnsStore+builtin data+تست، (ب) UI XAML+ViewModel | مستقل و parallelizable؛ بعد من integrate میکنم |
| فاز ۳ (AI tab) | **تیمی** — بعد از فاز ۲ با همان الگو | وابسته به فاز ۲ برای زیرساخت |

## تعریف «تمام» هر فاز
- build موفق + تستها سبز + checkpoint commit
- فاز ۲/۳ اضافه: نصب APK/بیلد ویندوز و تست زنده با گوشی متصل

## 🛡️ قواعد ایمنی — «اصل کار نباید ضربه بخورد»
مأموریت اصلی: اتصال پایدار گوشی↔ویندوز. هر تغییر باید این را تضمین کند:

1. **منطق حیاتی دستنخورده:** Socks5Server handshake/PIN/pipe، WinTunTunnelSession پروتکل READY، ProxySession transactional rollback — فازهای ۲ و ۳ فقط «اضافه» میکنند؛ هیچ refactor روی این مسیرها.
2. **DNS engine additive است:** نقطه resolve فعلی (resolveOnUpstream → fallback getByName) فقط با wrapper دورش میشود: اگر DnsEngine فعال نبود یا fail کرد = رفتار فعلی. default = off تا رگرسیون نداشته باشیم.
3. **Rail nav صرفاً UI:** SelectTab همان امضا؛ فقط visibility mapping گسترده میشود. هیچ منطق اتصال به nav وابسته نیست (تست: بعد از تغییر، connect/disconnect در تب دیگری هم کار کند).
4. **feature flags:** DNS tab و AI tab پشت تنظیم `enable_dns_features` باشند تا در صورت مشکل، خاموشی سریع بدون rebuild ممکن باشد.
5. **هر فاز مستقل شippable:** بعد از هر فاز برنامه باید مثل قبل connect شود — commit per phase + tag قبل از شروع فاز بعد (rollback نقطه‌ای ممکن).
6. **تست رگرسیون ثابت بعد از هر فاز:** START گوشی → connect ویندوز (TUN) → ipify خارجی → سایت ایرانی دایرکت → disconnect تمیز. هر کدام شکست = revert فاز.
