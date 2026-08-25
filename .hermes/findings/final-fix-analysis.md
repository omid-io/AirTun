# AirTun — تحلیل ریشه باگ شیرنت + طراحی راهحل نهایی (مستقل، 2026-08-25)

## ۱. جمعبندی شواهد آزمایشگاهی (بدون حدس)

| # | مشاهده | منبع |
|---|---|---|
| ۱ | هیدیفای گوشی تونلش UidRange کامل دارد: `{0-10705, 10707-20705, 20707-99999}`؛ AirTun appId=10735 → داخل کپچر | dumpsys connectivity |
| ۲ | SYN ویندوز به :10808 میرسد ولی جواب SYN-ACK وارد tun0 میشود → handshake هرگز کامل نمیشود | netstat + ss |
| ۳ | پورتهای سیستمی گوشی (53/adbd) از LAN جواب میدهند؛ فقط اپها بلاکاند | تست تفکیکی پورت |
| ۴ | پروکسی لوکال هیدیفای `127.0.0.1:12334` SOCKS5 no-auth سالم است؛ CONNECT+HTTP از داخل آن IP خارجی داد (162.35.231.140) | nc دستی روی گوشی |
| ۵ | نسخه Upstream-Chain نصب شد و ACCEPTها شروع شدند (SYN_SENT حل) اما نتیجه نهایی دیده نشد (بافر rotate شد) | logcat |
| ۶ | ap0 (hotspot) هیچ Network object در ConnectivityManager ندارد → bindListenerToLocalLan همیشه fail میشود | grep dumpsys = 0 |

## ۲. باگهای قطعی پیدا شده در بازبینی سورس نسخه chain

### باگ A — UDP relay بدون upstreamContext ساخته میشود
در Socks5Server.start():
`udpRelay = Socks5UdpRelay(bindDatagramSocket = bindDatagramSocket) { ... }`
پارامتر اول constructor همان upstreamContext است که positional جا افتاده؛ UDP chain شکسته است.

### باگ B — detect فقط در start()؛ اگر هیدیفای بعداً روشن/ریستارت شود تا stop/start بعدی پورت تازه نمیگیرد.

### باگ D — connectThrough برای IP literal آن را به صورت domain (ATYP=3) میفرستد؛ باید ATYP=1 برای IPv4.

## ۳. مقایسه با مرجعها
ساختار کلی استاندارد است (جداسازی core/service، fail-closed، resolve bound). دو باگ A و B باید بسته شوند و chain مسیر اصلی شود.

## ۴. محدودیتهای ایران لحاظ شده
- UDP بینالمللی فیلتر است → chain برای UDP ضروری (تا داخل تونل رمز برود)
- DNS آلوده → ATYP=domain یعنی resolve سمت هیدیفای، صفر leak
- UID-capture هیدیفای → loopback از fwmark عبور نمیکند، chain همیشه کار میکند

## ۵. راهحل نهایی (فاز R4)
1. رفع باگ A: named args + پاس دادن upstreamContext به رله UDP
2. رفع باگ B: lazy re-detect با cooldown وقتی connectThrough شکست خورد
3. رفع باگ D: ATYP صحیح برای IP literal
4. حذف fail سخت bindListenerToLocalLan (best-effort)
5. Unit test JVM برای chain handshake
6. بیلد APK + دستورالعمل تست تکمرحلهای

## ۶. نتیجه تست زنده نهایی (2026-08-26 01:29–01:33) ✅

با گوشی متصل، تست انتها-به-انتها از ویندوز انجام شد:

| مرحله | نتیجه |
|---|---|
| START روی گوشی (از طریق UI automation) | سرور روی 10808 بالا آمد، PIN 9142 |
| TCP connect از ویندوز (حتی با VPN روشن ویندوز) | CONNECT_OK |
| ACCEPT روی گوشی از 10.155.44.203 | ثبت شد |
| SOCKS5 کامل: curl --socks5-hostname -U :9142 → api.ipify.org | 185.112.82.145 (IP خارجی VPN — نه ایران!) |
| HTTPS از داخل تونل | همان IP خارجی |
| Google.com از داخل تونل | HTTP 200 |
| Beacon/discovery | probeها پاس میشوند |

**نتیجه: مشکل «برگشت IP ایران» حل شد — ترافیک ویندوز از تونل فیلترشکن گوشی عبور میکند.**

### یادداشتهای باقیمانده (محدودیت شناختهشده)
- /proc/net/tcp روی اندروید جدید EACCES میدهد → اسکن پورت داینامیک غیرفعال؛ لیست ثابت کاندیدها + lastKnownGood استفاده میشود. اگر هیدیفای پورت تصادفی بگذارد و در لیست نباشد، chain فعال نمیشود (fallback مستقیم). بهبود آینده: تنظیم دستی پورت upstream در UI.
- تداخل پورت v2rayNG/AirTun روی 10808 با port-fallback (10809+) حل شد.
- TUN mode ویندوز همان مسیر SOCKS سرور را استفاده میکند؛ فیکس سمت سرور برای هر دو مود اثر دارد.
