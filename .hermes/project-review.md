# Project Review — AirTun (2026-08-25)

## پروژه در یک نگاه
- **نام:** AirTun — اشتراک‌گذاری اینترنت/VPN گوشی اندروید با ویندوز
- **نسخه:** v1.1.0 روی شاخه `main` (شاخه مرجع و پایه گسترش)
- **زبانها:** C# (.NET 8 + WinUI 3، ~۵۲۰۰ خط) + Kotlin (Compose، ~۳۰۰۰ خط)
- **مخزن:** github.com/omid-io/AirTun — لایسنس MIT

> ⚠️ شاخههای `dev` و `backup-dev` و تگهای بعد از v1.1.0 مربوط به بررسی اپ Relay دیگران هستند و ربطی به این پروژه ندارند — برای پاکسازی کاندید حذف‌اند.

## معماری (بررسی شده روی main)
- **اندروید (سرور):**
  - `Socks5Server.kt` — سرور SOCKS5 با کوروتین؛ TCP CONNECT + UDP ASSOCIATE، شمارش کلاینت یکتا بر اساس IP، پایش ترافیک
  - `Socks5UdpRelay.kt` — رله UDP مستقل با پورت bind خودش
  - `AirTunBeacon.kt` — بیکون UDP پورت 47880 (broadcast + پاسخ به probe)، پیلود JSON شامل device/port/pin
  - `PinCode.kt` — پین ۴ رقمی با SecureRandom
- **ویندوز (کلاینت):**
  - `LanDiscovery.cs` — گوش دادن بیکون + ارسال probe به broadcast/gatewayها، انقضای دستگاههای قدیمی
  - `WinTunTunnelSession.cs` — اجرای airtun-tun.exe با elevation (runas) و Named Pipe؛ پروتکل READY / NO-HANDSHAKE / END-CONFIG
  - `AppController.cs` — ماشین حالت اتصال، آمار اینترفیس، GeoIP، تنظیمات
  - `RoutingManager.cs` / `TunRoutingManager.cs` — بایپس ایران/LAN/قوانین سفارشی در هر دو مود proxy و TUN
  - `ProxySession.cs` + `WinInetProxyStore` + `FileBackupStore` — ست/بازیابی transactional پروکسی سیستمی

## مسائل امنیتی
1. 🔴 **پین در بیکون plaintext منتشر میشود** (`AirTunBeacon.buildBeaconPayload` فیلد `"pin"`؛ `LanDiscovery` هم آن را میخواند). یعنی احراز هویت پین در LAN عملاً نمایشی است.
   **راهحل پیشنهادی:** حذف pin از بیکون + ورود دستی پین یا تبادل رمزنگاشتهشده (مثلاً HMAC challenge-response).
2. 🟡 تطبیق پین یکطرفه ساده (`uname == pin || pass == pin`) — پس از رفع مورد ۱ قابل قبول برای LAN.
3. 🟡 ترافیک SOCKS5 رمزنگاری ندارد (در شبکه محلی خودِ هاتاسپات قابل قبول).

## تغییر کامیتنشده فعلی
`AppController.cs`: حذف health-check TCP دوره‌ای (`CheckHostHealthAsync`) و جایگزینی با چک `IsRunning` پروسه تونل.
**ریسک:** اگر گوشی از شبکه بیفتد ولی پروسه tun2socks زنده بماند، قطعی دیده نمیشود. پیشنهاد: ترکیب هر دو چک (پروسه زنده + probe سبک UDP به بیکون) — یا revert تا تصمیم نهایی.

## نقاط قوت
- جداسازی تمیز Core/App/Tests و interfaceهای تزریق‌شدنی (IProcessHost, IRouteExecutor, IGatewayFinder) → تستپذیری خوب (۳۶ تست xUnit + تست اندروید)
- Teardown ایمن و بازیابی proxy پس از کرش
- مستندسازی دوزبانه کامل

## نقشه پیشنهادی گسترش
1. **پاکسازی گیت:** حذف شاخههای dev/backup-dev و تگهای متفرقه Relay (با تأیید کاربر)؛ commit تغییر معلق یا revert آن
2. **امنیت:** حذف پین از بیکون (مورد ۱)
3. **پایداری:** health-check ترکیبی در StartStatsPolling
4. **ویژگیهای جدید:** iOS client، multi-device، kill-switch، auto-start همراه هاتاسپات، IPv6
