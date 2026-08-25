# AirTun — لیست کامل و نهایی کارها (فازبندی شده)
تاریخ بهروزرسانی: 2026-08-26 | baseline tag: plan-v2-baseline

> این سند مرجع واحد است. BACKLOG.md قدیمی با این ادغام شد.

## 🔴 فاز ۰ — فیکسهای پایداری (کوچک، قبل از هر UI)

| # | کار | منشأ | فایل |
|---|---|---|---|
| 0.1 | WakeLock بدون timeout + گره به حضور کلاینت (الگوی Relay: acquire() بدون مدت، release وقتی آخرین کلاینت رفت یا teardown) | قطع ترافیک بعد خاموشی صفحه | SharingService.kt |
| 0.2 | ممیزی شمارنده per-IP کلاینتها — باگ Relay: decrement بدون increment روی کانکشنهای شکسته → wake lock وسط دانلود آزاد میشد | گزارش Relay | Socks5Server.kt finally |
| 0.3 | معافیت Doze/باتری: مجوز REQUEST_IGNORE_BATTERY_OPTIMIZATIONS هست ولی UI راهنما ندارد → onboarding + نمایش وضعیت isIgnoringBatteryOptimizations | گزارش Relay | HomeScreen + WarningCode |
| 0.4 | ممیزی deadlineهای مطلق در airtun-tun/main.go — ریشه واقعی «قطع ۵ دقیقهای» Relay آنجا بود (SetDeadline مطلق)؛ باید مطمئن شویم tun2socks ما همین باگ را ندارد | گزارش Relay | main.go |
| 0.5 | «Live Speed» اندروید: نمایش `/s` روی حجم تجمعی غلط است → یا اسم به «حجم مصرفی» یا سرعت واقعی delta-based (تصمیم کاربر) | تست کاربر | HomeScreen.kt:463 |

## 🔴 فاز ۱ — Rail Navigation ویندوز (تصمیم تاییدشده: Rail سمت راست)
| # | کار | توضیح |
|---|---|---|
| 1.1 | تبدیل nav پایین ۴ تبه به rail عمودی راست ~۵۶px با ۶ تب | اتصال، DNS، AI Access، مسیریابی، گزارشها، تنظیمات |
| 1.2 | گسترش SelectTab به ۶ مورد + دو آیکن SVG جدید (globe=DNS, monitor-bot=AI) | MainWindow.xaml.cs:698 الگوی موجود |
| 1.3 | تست بصری عرض 440px با rail — اگر فشرده بود افزایش به ~500 | ConfigureWindow(440,700) |
| 1.4 | RTL: rail خودکار سمت راست میافتد (ColumnDefinition اول) — verify | XAML |

## 🟠 فاز ۲ — تب DNS ویندوز (پشت feature flag)
| # | کار |
|---|---|
| 2.1 | DnsStore: JSON persistence لیست سرورها (builtin از تحقیق ۱۱ سرویس + سفارشی کاربر) |
| 2.2 | DnsEngine: سه resolver (UDP raw / DoH / System) همه bound به شبکه upstream |
| 2.3 | UI تب DNS: لیست + add/edit/delete + radio-select فعال + دکمه تست |
| 2.4 | تستر ۳ مرحلهای: latency ×۳ median → resolution صحیح → bypass-check (مقایسه با system) |
| 2.5 | اعمال انتخاب: TUN mode → پاس DoH endpoint به airtun-tun (به جای hardcode)؛ Proxy mode → ست DNS سیستم |
| 2.6 | قانون resolve: زنجیره فعال = remote-resolve حفظ (رفتار فعلی)؛ فقط direct حالا از DNS کاربر |

## 🟣 فاز ۳ — تب AI Access ویندوز (پشت همان flag)
| # | کار |
|---|---|
| 3.1 | IPv6 leak test: هنگام اتصال AAAA resolve چک شود + toggle block-AAAA در tun2socks |
| 3.2 | IP reputation check بعد از connect: ip-api از داخل تونل → کشور/ISP/proxy-flag → توصیه («این کانفیگ برای Gemini مناسب نیست») |
| 3.3 | Google-Fix routing rules: جدول دامنههای AI (gemini.google.com, *.googleapis.com, ai.google.dev, openai.com, claude.ai, chatgpt.com) → DNS ضدتحریم انتخابی (pure function قابل تست) |
| 3.4 | راهنمای WARP (متن + لینک vpndada الگو) |

## 🟡 فاز ۴ — باگهای باقیمانده P1
| # | کار | توضیح |
|---|---|---|
| 4.1 | Web Proxy mode وصل نمیشود (TUN سالم) — دیباگ ProxySession/WinINetStore؛ چک پورت صحیح بعد port-fallback | باگ تأیید کاربر |
| 4.2 | تداخل پورت معکوس: انتقال پورت پیشفرض AirTun از 10808 به رنج غیرمتعارف (~27510) که با هیچ VPNای تداخل نداشته باشد؛ بیکون/UI/ویندوز از actualPort میخوانند (زیرساخت آماده) | درخواست صریح کاربر |

## 🧹 فاز ۵ — پاکسازی
| # | کار |
|---|---|
| 5.1 | تغییر کامیتنشده AppController.cs (حذف health-check) — تصمیم نهایی: نگه داشتن IsRunning چک یا revert |
| 5.2 | حذف crash.log قدیمی و scratch_check.py / scroll_screenshot.py از ریشه ریپو |
| 5.3 | InsecureSkipVerify در DoH client (main.go:190) — حداقل لاگ هشدار |

## 📋 سناریوی تست رگرسیون ثابت (بعد از هر فاز اجرا شود)
START گوشی → connect ویندوز TUN → ipify خارجی ✅ → سایت ایرانی دایرکت ✅ → disconnect تمیز ✅

## قواعد ایمنی
- مسیر حیاتی (handshake/PIN/tunnel protocol) دست نمیخورد؛ تغییرات additive
- DNS/AI پشت feature flag `enable_dns_features` (default off تا رگرسیون صفر)
- هر فاز مستقل شippable؛ شکست تست رگرسیون = revert فاز
