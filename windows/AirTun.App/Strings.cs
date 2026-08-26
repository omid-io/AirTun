namespace AirTun.App;

public static class Strings
{
    private static bool _isPersian = false;

    public static bool IsPersian
    {
        get => _isPersian;
        set => _isPersian = value;
    }

    public static string FlowDirection => _isPersian ? "RightToLeft" : "LeftToRight";

    public static string AppName => "AirTun";
    public static string Tagline => _isPersian ? "اشتراک بدون مرز اینترنت گوشی با ویندوز" : "Ultra-Fast Phone Internet Sharing";

    // Bottom Navigation Tabs
    public static string TabConnect => _isPersian ? "اتصال" : "Connect";
    public static string TabRouting => _isPersian ? "مسیریابی" : "Routing";
    public static string TabLogs => _isPersian ? "گزارش‌ها" : "Logs";
    public static string TabAbout => _isPersian ? "تنظیمات" : "Settings";
    public static string TabDns => _isPersian ? "DNS" : "DNS";
    public static string TabAi => _isPersian ? "هوش مصنوعی" : "AI";

    public static string DnsTitle => _isPersian ? "سرورهای DNS" : "DNS Servers";
    public static string DnsSubtitle => _isPersian ? "ریزولور انتخابی برای تونل و ویندوز" : "Choose which resolver the tunnel and Windows use";
    public static string DnsTestAll => _isPersian ? "⚡ تست همه" : "⚡ Test All";
    public static string DnsSet => _isPersian ? "✓ تنظیم" : "✓ Set";
    public static string DnsFlush => _isPersian ? "🧹 پاکسازی کش" : "🧹 Flush DNS";
    public static string DnsUnset => _isPersian ? "لغو ✕" : "Unset ✕";
    public static string DnsAddCustom => _isPersian ? "＋ افزودن DNS سفارشی" : "+ Add custom DNS";
    public static string DnsActiveBadge => _isPersian ? "فعال" : "ACTIVE";
    public static string DnsGroupIran => _isPersian ? "🛡 ضد تحریم — ایران" : "🛡 Anti-Sanction — Iran";
    public static string DnsGroupGlobal => _isPersian ? "🌍 جهانی" : "🌍 Global";
    public static string DnsGroupCustom => _isPersian ? "📦 سفارشی" : "📦 Custom";

    // Statuses
    public static string StatusIdle => _isPersian ? "آماده برای اتصال" : "Ready to Connect";
    public static string StatusPreparing => _isPersian ? "در حال ایجاد تونل..." : "Starting Tunnel...";
    public static string StatusConnected => _isPersian ? "متصل به تانل (Connected)" : "Connected (TUN Active)";
    public static string StatusDisconnected => _isPersian ? "قطع شد" : "Disconnected";
    public static string StatusError => _isPersian ? "خطا در اتصال" : "Connection Error";

    // Mode Pill
    public static string StatusModeTun => "TUN";
    public static string StatusModeProxy => "Proxy";

    // Modes
    public static string ModeTunTitle => "⚡ TUN Mode";
    public static string ModeTunSubtitle => _isPersian ? "کل سیستم، گیم و برنامه‌ها" : "System-wide, games & apps";
    public static string ModeProxyTitle => "🌐 Web Proxy";
    public static string ModeProxySubtitle => _isPersian ? "مرورگرها و وب" : "Browsers & web only";

    // Quick Tips
    public static string QuickTipsLabel => _isPersian ? "نکات سریع" : "Quick Tips";
    public static string Tip1 => _isPersian ? "از TUN Mode برای بازی‌ها و تمام سیستم استفاده کنید." : "Use TUN Mode for games and all-system routing.";
    public static string Tip2 => _isPersian ? "از Web Proxy برای ترافیک مرورگر استفاده کنید." : "Use Web Proxy for browser-only traffic.";
    public static string Tip3 => _isPersian ? "مطمئن شوید Hotspot گوشی فعال است." : "Make sure Hotspot is active on your phone.";

    // Routing & Bypass Domestic
    public static string RoutingTitle => _isPersian ? "مسیریابی هوشمند و بای‌پاس" : "Smart Routing Rules";
    public static string RoutingSubtitle => _isPersian ? "اتصال مستقیم سایت‌ها بدون عبور از تانل" : "Direct connection without routing through proxy";
    public static string BypassDomesticTitle => _isPersian ? "بای‌پاس سایت‌های داخلی (.ir)" : "Bypass Domestic Sites (.ir)";
    public static string BypassDomesticDesc => _isPersian
        ? "سایت‌های بانکی، اداری و دامنه‌های ملی"
        : "Iranian banking & national websites";

    public static string BypassLanTitle => _isPersian ? "بای‌پاس شبکه محلی (LAN)" : "Bypass Local Network (LAN)";
    public static string BypassLanDesc => _isPersian
        ? "192.168.x, 10.x, 127.0.0.1 و مودم"
        : "192.168.x, 10.x, 127.0.0.1";

    public static string CustomRulesHeader => _isPersian ? "دامنه‌ها و IPهای سفارشی" : "Custom Bypass Rules";
    public static string CustomRulesDesc => _isPersian ? "افزودن دستی دامنه‌ها برای اتصال مستقیم:" : "Add domains/IPs to bypass the tunnel:";
    public static string AddRuleAction => _isPersian ? "+ افزودن" : "+ Add";
    public static string RulePatternPlaceholder => _isPersian ? "مثال: *.digikala.com یا 1.1.1.1" : "e.g. *.digikala.com or 1.1.1.1";
    public static string RuleActionDirect => _isPersian ? "مستقیم" : "Direct";
    public static string RuleActionProxy => _isPersian ? "از تونل" : "Proxy";
    public static string RuleActionBlock => _isPersian ? "مسدود" : "Block";
    public static string DeleteRuleAction => "✕";

    // Discovery & Connect
    public static string DiscoveredHeader => _isPersian ? "گوشی در دسترس" : "Available Device";
    public static string SearchingDevices => _isPersian ? "در حال جستجو برای گوشی..." : "Searching for Phone...";
    public static string PinHint => _isPersian ? "پین ۴ رقمی امنیتی" : "4-digit Security PIN";
    public static string PinAutoDetected => _isPersian ? "شناسایی خودکار ✓" : "Auto-detected ✓";
    public static string ConnectAction => _isPersian ? "اتصال به گوشی" : "Connect to Phone";
    public static string DisconnectAction => _isPersian ? "قطع اتصال" : "Disconnect";
    public static string RetryAction => _isPersian ? "تلاش مجدد" : "Retry";
    public static string DismissAction => _isPersian ? "بستن" : "Dismiss";

    // Dashboard
    public static string TrafficHeader => _isPersian ? "ترافیک و سرعت لحظه‌ای" : "Live Network Bandwidth";
    public static string LiveTrafficHeader => _isPersian ? "نمودار زنده پهنای باند" : "Live Traffic Waveform";
    public static string TrafficTotal => _isPersian ? "حجم کل" : "Total Data";
    public static string LatencyLabel => _isPersian ? "پینگ:" : "Ping:";
    public static string DurationLabel => _isPersian ? "مدت:" : "Duration:";
    public static string OutboundIpHeader => _isPersian ? "لوکیشن و آی‌پی خروجی" : "Outbound Location";
    public static string FetchingGeo => _isPersian ? "در حال استعلام لوکیشن..." : "Resolving outbound location...";
    public static string RefreshGeoAction => _isPersian ? "بروزرسانی" : "Refresh";

    // Logs
    public static string LogsHeader => _isPersian ? "گزارش‌های سیستم" : "System Logs";
    public static string CopyLogsAction => _isPersian ? "کپی" : "Copy";
    public static string CopyLogsFeedback => _isPersian ? "✓ کپی شد" : "✓ Copied";
    public static string ClearLogsAction => _isPersian ? "پاک‌سازی" : "Clear";

    // Tray & Startup Settings
    public static string SettingsHeader => _isPersian ? "تنظیمات سیستم و Tray" : "System & Tray Settings";
    public static string StartWithWindowsTitle => _isPersian ? "اجرا هنگام روشن شدن ویندوز" : "Start with Windows";
    public static string StartWithWindowsDesc => _isPersian ? "اجرای خودکار برنامه با بوت سیستم" : "Launch automatically on system boot";
    public static string CloseToTrayTitle => _isPersian ? "مینیمایز به Tray هنگام بستن (X)" : "Close to Tray (X button)";
    public static string CloseToTrayDesc => _isPersian ? "برنامه با دکمه ضربدر بسته نشود و در پس‌زمینه بماند" : "Keep running in background when closed";
    public static string MinimizeToTrayTitle => _isPersian ? "مینیمایز به Tray هنگام کوچک کردن (_)" : "Minimize to Tray (_ button)";
    public static string MinimizeToTrayDesc => _isPersian ? "انتقال پنجره به منوی تسک‌بار" : "Send to taskbar tray instead of minimizing";

    // GitHub Card
    public static string GithubCardTitle => _isPersian ? "مخزن گیت‌هاب پروژه" : "GitHub Repository";
    public static string GithubCardSub => "omid-io / AirTun";
    public static string GithubCardAction => "Open ↗";

    // About
    public static string AboutTitle => _isPersian ? "درباره ایر‌تون" : "About AirTun";
    public static string AboutDescription => _isPersian
        ? "نرم‌افزار مدرن، فوق سریع و امن برای اشتراک‌گذاری اینترنت گوشی با سیستم‌های ویندوزی بدون محدودیت اپراتور با پینگ بهینه برای گیمینگ و وبگردی."
        : "Ultra-fast, low-latency and secure phone internet sharing for Windows systems with zero operator restrictions, tailored for online gaming and daily workflows.";
    public static string OpenGithubAction => _isPersian ? "مشاهده پروژه در گیت‌هاب" : "Open Project on GitHub";
    public static string DeveloperTitle => _isPersian ? "توسعه‌دهنده:" : "Developer:";
    public static string DeveloperName => "Omid Zaferi";
    public static string LicenseTitle => _isPersian ? "مجوز:" : "License:";
    public static string LicenseName => "MIT License";

    // Tray Context Menu
    public static string TrayOpen => _isPersian ? "باز کردن AirTun" : "Open AirTun";
    public static string TrayExit => _isPersian ? "خروج کامل" : "Exit AirTun";

    // Errors
    public static string GetErrorTitle(string? code) => code switch
    {
        "ERR_INVALID_PIN" => _isPersian ? "پین‌کد نادرست است" : "Invalid Security PIN",
        "ERR_ELEVATION_DECLINED" => _isPersian ? "دسترسی ادمین تایید نشد" : "Admin Permission Declined",
        "ERR_TUNNEL_START_FAILED" => _isPersian ? "خطا در ساخت کارت شبکه" : "Adapter Setup Failed",
        "ERR_CONNECTION_REFUSED" => _isPersian ? "عدم پاسخ سرور گوشی" : "Phone Unreachable",
        "ERR_PROXY_APPLY_FAILED" => _isPersian ? "خطا در پروکسی ویندوز" : "Proxy Setup Failed",
        _ => _isPersian ? "خطا در اتصال" : "Connection Error",
    };

    public static string GetErrorBody(string? code) => code switch
    {
        "ERR_INVALID_PIN" => _isPersian
            ? "پین‌کد ۴ رقمی وارد شده با پین گوشی مطابقت ندارد."
            : "The 4-digit PIN entered does not match the PIN on the phone screen.",
        "ERR_ELEVATION_DECLINED" => _isPersian
            ? "برای ساخت کارت شبکه WinTun، دسترسی ادمین ویندوز الزامی است."
            : "Administrator privilege is required by Windows to create the virtual network adapter.",
        "ERR_TUNNEL_START_FAILED" => _isPersian
            ? "امکان راه‌اندازی کارت شبکه میسر نشد. سایر برنامه‌های VPN را ببندید و مجدداً تلاش کنید."
            : "Could not create the WinTun adapter. Please close conflicting VPNs and retry.",
        "ERR_CONNECTION_REFUSED" => _isPersian
            ? "گوشی پاسخ نداد. بررسی کنید هات‌اسپات روشن باشد و دکمه Start در اپ گوشی فعال باشد."
            : "Cannot reach the Android device. Ensure the hotspot is active and AirTun is started on your phone.",
        _ => _isPersian
            ? "ارتباط با مشکل مواجه شد. لطفاً وضعیت هات‌اسپات و پین‌کد را بررسی کرده و دوباره امتحان کنید."
            : "An unexpected error occurred. Please verify your hotspot connection and try again.",
    };
}
