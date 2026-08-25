package io.airtun.app.ui

import android.widget.Toast
import androidx.compose.animation.AnimatedVisibility
import androidx.compose.animation.Crossfade
import androidx.compose.animation.core.InfiniteRepeatableSpec
import androidx.compose.animation.core.RepeatMode
import androidx.compose.animation.core.animateFloat
import androidx.compose.animation.core.rememberInfiniteTransition
import androidx.compose.animation.core.tween
import androidx.compose.animation.expandVertically
import androidx.compose.animation.fadeIn
import androidx.compose.animation.fadeOut
import androidx.compose.animation.shrinkVertically
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.StrokeCap
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.heightIn
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.widthIn
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.selectableGroup
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.draw.scale
import androidx.compose.foundation.Image
import androidx.compose.ui.platform.LocalClipboardManager
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalUriHandler
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.res.pluralStringResource
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.AnnotatedString
import androidx.compose.ui.text.font.FontFamily
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import io.airtun.app.R
import io.airtun.app.core.ConnectionState
import io.airtun.app.core.ErrorCode
import io.airtun.app.core.WarningCode
import io.airtun.app.service.LocalLog
import androidx.compose.foundation.border
import androidx.compose.foundation.Canvas
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Brush
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.draw.shadow
import io.airtun.app.ui.theme.LocalGlass
import io.airtun.app.ui.theme.glassPanel
import io.airtun.app.ui.theme.nmCard
import io.airtun.app.ui.theme.nmSunken

@Composable
fun HomeScreen(
    state: ConnectionState,
    batteryExempt: Boolean,
    warnings: Set<WarningCode>,
    themeMode: String,
    lang: String = "en",
    logs: List<LocalLog.Entry>,
    onStart: () -> Unit,
    onStop: () -> Unit,
    onRetry: () -> Unit,
    onDismissError: () -> Unit,
    onAllowBattery: () -> Unit,
    onDismissWarning: (WarningCode) -> Unit,
    onSetTheme: (String) -> Unit,
    onToggleLang: () -> Unit = {},
    onClearLogs: () -> Unit,
    onShareLogs: () -> Unit = {},
) {
    val isRunning = state is ConnectionState.Advertising || state is ConnectionState.Connected
    val isPreparing = state is ConnectionState.Preparing
    val isError = state is ConnectionState.Error

    val host = when (state) {
        is ConnectionState.Advertising -> state.host
        is ConnectionState.Connected -> state.host
        else -> "192.168.43.1"
    }
    val port = when (state) {
        is ConnectionState.Advertising -> state.port
        is ConnectionState.Connected -> state.port
        else -> 10808
    }
    val pinCode = when (state) {
        is ConnectionState.Advertising -> state.pinCode
        is ConnectionState.Connected -> state.pinCode
        else -> "----"
    }
    val clientCount = when (state) {
        is ConnectionState.Connected -> state.clientCount
        else -> 0
    }
    val bytesUp = when (state) {
        is ConnectionState.Connected -> state.bytesUp
        is ConnectionState.Advertising -> state.bytesUp
        else -> 0L
    }
    val bytesDown = when (state) {
        is ConnectionState.Connected -> state.bytesDown
        is ConnectionState.Advertising -> state.bytesDown
        else -> 0L
    }

    Box(modifier = Modifier.fillMaxSize(), contentAlignment = Alignment.TopCenter) {
        Column(
            modifier = Modifier
                .widthIn(max = 460.dp)
                .fillMaxSize()
                .verticalScroll(rememberScrollState())
                .windowInsetsPadding(WindowInsets.safeDrawing)
                .padding(horizontal = 20.dp, vertical = 20.dp),
            horizontalAlignment = Alignment.CenterHorizontally,
        ) {
            Header(lang = lang, onToggleLang = onToggleLang)
            Spacer(Modifier.height(16.dp))

            WarningBanners(warnings, onDismissWarning)

            if (isError && state is ConnectionState.Error) {
                ErrorPanel(state.code, onRetry, onDismissError)
                Spacer(Modifier.height(16.dp))
            }

            // Giant Neumorphic Power Button
            GiantPowerButton(
                isRunning = isRunning,
                isPreparing = isPreparing,
                onClick = {
                    if (isRunning || isPreparing) onStop() else onStart()
                },
            )

            Spacer(Modifier.height(18.dp))

            // Connection PIN Card
            ConnectionPinCard(
                host = host,
                port = port,
                pinCode = pinCode,
                isRunning = isRunning,
            )

            Spacer(Modifier.height(14.dp))

            // 2-Column Metrics Grid
            MetricsGrid(
                clientCount = clientCount,
                bytesUp = bytesUp,
                bytesDown = bytesDown,
                isRunning = isRunning,
            )

            Spacer(Modifier.height(16.dp))

            // Windows Client Required Card
            WindowsClientRequiredCard()

            Spacer(Modifier.height(16.dp))

            if (!batteryExempt) {
                BatteryBanner(onAllowBattery)
                Spacer(Modifier.height(14.dp))
            }

            Spacer(Modifier.height(16.dp))

        }
    }
}

@Composable
private fun Header(lang: String = "en", onToggleLang: () -> Unit = {}) {
    val glass = LocalGlass.current
    Row(
        modifier = Modifier.fillMaxWidth(),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Image(
            painter = painterResource(id = R.drawable.brand_wordmark),
            contentDescription = "AirTun",
            modifier = Modifier.height(24.dp),
        )

        Box(
            modifier = Modifier
                .clip(RoundedCornerShape(10.dp))
                .nmCard(10.dp)
                .clickable(onClick = onToggleLang)
                .padding(horizontal = 14.dp, vertical = 7.dp),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                text = if (lang == "fa") "EN" else "FA",
                color = glass.accent,
                fontSize = 12.sp,
                fontWeight = FontWeight.ExtraBold,
                letterSpacing = 1.sp,
            )
        }
    }
}

@Composable
private fun GiantPowerButton(
    isRunning: Boolean,
    isPreparing: Boolean,
    onClick: () -> Unit,
) {
    val glass = LocalGlass.current
    val infiniteTransition = rememberInfiniteTransition(label = "powerPulse")
    val pulseScale by infiniteTransition.animateFloat(
        initialValue = 1f,
        targetValue = 1.35f,
        animationSpec = InfiniteRepeatableSpec(
            animation = tween(2000),
            repeatMode = RepeatMode.Restart,
        ),
        label = "pulseScale",
    )
    val pulseAlpha by infiniteTransition.animateFloat(
        initialValue = 0.6f,
        targetValue = 0f,
        animationSpec = InfiniteRepeatableSpec(
            animation = tween(2000),
            repeatMode = RepeatMode.Restart,
        ),
        label = "pulseAlpha",
    )

    Box(
        modifier = Modifier
            .fillMaxWidth()
            .padding(vertical = 12.dp),
        contentAlignment = Alignment.Center,
    ) {
        // Animated glowing ring when running
        if (isRunning) {
            Box(
                modifier = Modifier
                    .size(148.dp)
                    .scale(pulseScale)
                    .clip(CircleShape)
                    .border(1.5.dp, glass.accent.copy(alpha = pulseAlpha), CircleShape),
            )
        }

        // Outer Raised Neumorphic Circle
        // HTML: box-shadow: 14px 14px 28px rgba(0,0,0,0.65), -8px -8px 20px rgba(255,255,255,0.045)
        // Running HTML: box-shadow: 0 0 40px rgba(0,229,255,0.35) + above
        Box(
            modifier = Modifier
                .size(148.dp)
                .shadow(
                    elevation = if (isRunning) 18.dp else 10.dp,
                    shape = CircleShape,
                    clip = false,
                    spotColor = if (isRunning) glass.accent.copy(alpha = 0.35f) else Color(0x8C000000),
                    ambientColor = if (isRunning) glass.accent.copy(alpha = 0.15f) else Color(0x33000000),
                )
                .clip(CircleShape)
                // Raised gradient: lighter top-left → darker bottom-right
                .background(
                    Brush.linearGradient(
                        colors = listOf(Color(0xFF181D28), Color(0xFF10141D)),
                    )
                )
                // Idle: subtle white edge rgba(255,255,255,0.07); Running: cyan at 50%
                .border(
                    width = 1.5.dp,
                    color = if (isRunning) glass.accent.copy(alpha = 0.5f) else Color(0x12FFFFFF),
                    shape = CircleShape,
                )
                .clickable(role = Role.Button, onClick = onClick),
            contentAlignment = Alignment.Center,
        ) {
            // Inner Sunken Circle
            // HTML idle:  background: #090c12, box-shadow: inset 6px 6px 14px rgba(0,0,0,0.85)
            // HTML running: background: radial-gradient(ellipse, rgba(0,229,255,0.12), #06080d 70%)
            Box(
                modifier = Modifier
                    .size(106.dp)
                    .clip(CircleShape)
                    .background(
                        if (isRunning)
                            Brush.radialGradient(
                                colors = listOf(
                                    Color(0xFF00E5FF).copy(alpha = 0.12f),
                                    Color(0xFF06080D),
                                ),
                            )
                        else
                            Brush.radialGradient(
                                colors = listOf(Color(0xFF0D1018), Color(0xFF090C12)),
                            )
                    )
                    // Asymmetric inset border: dark top-left / subtle light bottom-right
                    .border(
                        1.dp,
                        Brush.linearGradient(
                            colors = listOf(Color(0x99000000), Color(0x0DFFFFFF)),
                            start = Offset.Zero,
                            end = Offset.Infinite,
                        ),
                        CircleShape,
                    ),
                contentAlignment = Alignment.Center,
            ) {
                Column(
                    horizontalAlignment = Alignment.CenterHorizontally,
                    verticalArrangement = Arrangement.Center,
                ) {
                    // Power icon — matches HTML SVG exactly:
                    // <path d="M18.36 6.64a9 9 0 1 1-12.73 0"/> (arc, gap at 12 o'clock)
                    // <line x1="12" y1="2" x2="12" y2="12"/>     (vertical stem)
                    // Canvas: 34dp, mapped from 24×24 SVG viewBox
                    val iconColor = if (isRunning) glass.accent else glass.textTertiary
                    Canvas(modifier = Modifier.size(34.dp)) {
                        val sw = 2.4.dp.toPx()
                        val cx = size.width / 2f
                        val cy = size.height / 2f
                        // r = 9/24 = 37.5% — exact match to HTML arc radius
                        val r = size.width * (9f / 24f)
                        // Arc: gap at 12 o'clock (top center).
                        // Compose convention: 0°=3h(right), 90°=6h(bottom), 270°=12h(top).
                        // Right endpoint (18.36,6.64) in 24px → ≈320° in Compose
                        // Left  endpoint (5.63, 6.64) in 24px → ≈220° in Compose
                        // Gap 220°→320° through 270° = 100° centered at 12 o'clock ✓
                        drawArc(
                            color = iconColor,
                            startAngle = 320f,   // upper-right (≈1:30 o'clock)
                            sweepAngle = 260f,   // CW: 3h → 6h → 9h → upper-left (≈10:30)
                            useCenter = false,
                            topLeft = Offset(cx - r, cy - r), // centered, no vertical offset
                            size = Size(r * 2f, r * 2f),
                            style = Stroke(width = sw, cap = StrokeCap.Round),
                        )
                        // Stem: y=2 to y=12 in 24px viewBox → 8.3% to 50% of canvas height
                        drawLine(
                            color = iconColor,
                            start = Offset(cx, size.height * (2f / 24f)),
                            end = Offset(cx, cy),
                            strokeWidth = sw,
                            cap = StrokeCap.Round,
                        )
                    }
                    Spacer(Modifier.height(6.dp))
                    Text(
                        text = if (isPreparing) stringResource(R.string.phone_power_preparing) else if (isRunning) stringResource(R.string.phone_power_stop) else stringResource(R.string.phone_power_start),
                        fontSize = 11.sp,
                        fontWeight = FontWeight.ExtraBold,
                        color = if (isRunning) glass.accent else glass.textTertiary,
                        letterSpacing = 1.5.sp,
                    )
                }
            }
        }
    }
}

@Composable
private fun ConnectionPinCard(
    host: String,
    port: Int,
    pinCode: String,
    isRunning: Boolean,
) {
    val glass = LocalGlass.current
    val clipboardManager = LocalClipboardManager.current
    val context = LocalContext.current

    Row(
        modifier = Modifier
            .fillMaxWidth()
            .nmCard(20.dp)
            .clickable {
                if (pinCode != "----") {
                    clipboardManager.setText(AnnotatedString(pinCode))
                    Toast.makeText(context, context.getString(R.string.phone_pin_copied, pinCode), Toast.LENGTH_SHORT).show()
                }
            }
            .padding(16.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
        verticalAlignment = Alignment.CenterVertically,
    ) {
        Column {
            Text(
                text = stringResource(R.string.phone_pin_card_title),
                style = MaterialTheme.typography.titleMedium,
                fontWeight = FontWeight.ExtraBold,
                color = glass.textPrimary,
            )
            Spacer(Modifier.height(2.dp))
            Text(
                text = if (isRunning) "$host:$port" else "192.168.43.1:10808",
                style = MaterialTheme.typography.bodySmall,
                color = glass.textSecondary,
                fontFamily = FontFamily.Monospace,
            )
        }

        Box(
            modifier = Modifier
                .clip(RoundedCornerShape(12.dp))
                .nmSunken(12.dp)
                .padding(horizontal = 14.dp, vertical = 6.dp),
            contentAlignment = Alignment.Center,
        ) {
            Text(
                text = pinCode,
                fontFamily = FontFamily.Monospace,
                fontSize = 22.sp,
                fontWeight = FontWeight.Black,
                color = if (pinCode != "----") glass.accent else glass.textTertiary,
                letterSpacing = 4.sp,
            )
        }
    }
}

@Composable
private fun MetricsGrid(
    clientCount: Int,
    bytesUp: Long,
    bytesDown: Long,
    isRunning: Boolean,
) {
    val glass = LocalGlass.current
    val totalBytes = bytesUp + bytesDown
    val speedText = if (isRunning && totalBytes > 0) "${formatBytes(totalBytes)}/s" else "0.0 KB/s"

    Row(
        modifier = Modifier.fillMaxWidth(),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        // Connected Devices
        Column(
            modifier = Modifier
                .weight(1f)
                .nmCard(16.dp)
                .padding(14.dp),
        ) {
            Text(
                text = stringResource(R.string.phone_metric_devices),
                style = MaterialTheme.typography.labelSmall,
                color = glass.textSecondary,
                fontWeight = FontWeight.SemiBold,
            )
            Spacer(Modifier.height(4.dp))
            Text(
                text = pluralStringResource(R.plurals.status_connected, clientCount, clientCount),
                fontFamily = FontFamily.Monospace,
                fontSize = 18.sp,
                fontWeight = FontWeight.ExtraBold,
                color = glass.textPrimary,
            )
        }

        // Live Speed
        Column(
            modifier = Modifier
                .weight(1f)
                .nmCard(16.dp)
                .padding(14.dp),
        ) {
            Text(
                text = stringResource(R.string.phone_metric_speed),
                style = MaterialTheme.typography.labelSmall,
                color = glass.textSecondary,
                fontWeight = FontWeight.SemiBold,
            )
            Spacer(Modifier.height(4.dp))
            Text(
                text = speedText,
                fontFamily = FontFamily.Monospace,
                fontSize = 18.sp,
                fontWeight = FontWeight.ExtraBold,
                color = glass.accent,
            )
        }
    }
}

@Composable
private fun WindowsClientRequiredCard() {
    val glass = LocalGlass.current
    val uriHandler = LocalUriHandler.current
    Column(
        modifier = Modifier
            .fillMaxWidth()
            .nmCard(20.dp)
            .padding(16.dp),
    ) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            // SVG Info Circle — Canvas drawn, matches HTML stroke icon
            Canvas(modifier = Modifier.size(18.dp)) {
                val sw = 2.dp.toPx()
                val r = size.width / 2f - sw / 2f
                drawCircle(
                    color = glass.accent,
                    radius = r,
                    style = Stroke(width = sw),
                )
                // Question mark dot at bottom (12,17)
                drawCircle(
                    color = glass.accent,
                    radius = sw * 0.6f,
                    center = Offset(size.width / 2f, size.height * 0.72f),
                )
                // Question mark top arc approximated as small arc
                drawArc(
                    color = glass.accent,
                    startAngle = 210f,
                    sweepAngle = 200f,
                    useCenter = false,
                    topLeft = Offset(size.width * 0.3f, size.height * 0.2f),
                    size = Size(size.width * 0.4f, size.height * 0.32f),
                    style = Stroke(width = sw, cap = StrokeCap.Round),
                )
            }
            Spacer(Modifier.width(8.dp))
            Text(
                text = stringResource(R.string.phone_help_card_title),
                style = MaterialTheme.typography.titleSmall,
                color = glass.textPrimary,
                fontWeight = FontWeight.ExtraBold,
            )
        }
        Spacer(Modifier.height(6.dp))
        Text(
            text = stringResource(R.string.phone_help_card_desc),
            style = MaterialTheme.typography.bodySmall,
            color = glass.textSecondary,
        )
        Spacer(Modifier.height(12.dp))
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .nmSunken(10.dp)
                .clickable { uriHandler.openUri("https://github.com/omid-io/AirTun") }
                .padding(horizontal = 12.dp, vertical = 10.dp),
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = stringResource(R.string.phone_download_label),
                color = glass.textSecondary,
                style = MaterialTheme.typography.labelSmall,
            )
            Text(
                text = "github.com/omid-io/AirTun ↗",
                color = glass.accent,
                style = MaterialTheme.typography.labelSmall,
                fontWeight = FontWeight.Bold,
            )
        }
    }
}


@Composable
private fun ErrorPanel(
    code: ErrorCode,
    onRetry: () -> Unit,
    onDismiss: () -> Unit,
) {
    val glass = LocalGlass.current
    val (title, body) = when (code) {
        ErrorCode.HOTSPOT_OFF ->
            stringResource(R.string.error_hotspot_off_title) to stringResource(R.string.error_hotspot_off_body)
        ErrorCode.HOTSPOT_LOST ->
            stringResource(R.string.error_hotspot_lost_title) to stringResource(R.string.error_hotspot_lost_body)
        ErrorCode.PORT_IN_USE ->
            stringResource(R.string.error_port_in_use_title) to stringResource(R.string.error_port_in_use_body)
        ErrorCode.SERVICE_FAILED ->
            stringResource(R.string.error_service_failed_title) to stringResource(R.string.error_service_failed_body)
    }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .glassPanel(radius = 28.dp)
            .padding(24.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        Text(text = "⚠️", fontSize = 40.sp)
        Spacer(Modifier.height(12.dp))
        Text(
            text = title,
            style = MaterialTheme.typography.titleLarge,
            color = glass.error,
            fontWeight = FontWeight.Bold,
        )
        Spacer(Modifier.height(8.dp))
        Text(
            text = body,
            style = MaterialTheme.typography.bodyMedium,
            color = glass.textSecondary,
            textAlign = TextAlign.Center,
        )
        Spacer(Modifier.height(24.dp))
        Row(
            modifier = Modifier.fillMaxWidth(),
            horizontalArrangement = Arrangement.spacedBy(12.dp),
        ) {
            Box(
                modifier = Modifier
                    .weight(1f)
                    .height(44.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .background(glass.fillRaised)
                    .clickable(role = Role.Button, onClick = onDismiss),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    text = stringResource(R.string.action_dismiss),
                    color = glass.textPrimary,
                    style = MaterialTheme.typography.bodyMedium,
                )
            }
            Box(
                modifier = Modifier
                    .weight(1f)
                    .height(44.dp)
                    .clip(RoundedCornerShape(12.dp))
                    .background(glass.accent)
                    .clickable(role = Role.Button, onClick = onRetry),
                contentAlignment = Alignment.Center,
            ) {
                Text(
                    text = stringResource(R.string.action_retry),
                    color = glass.onAccent,
                    style = MaterialTheme.typography.bodyMedium,
                    fontWeight = FontWeight.Bold,
                )
            }
        }
    }
}

@Composable
private fun WarningBanners(
    warnings: Set<WarningCode>,
    onDismiss: (WarningCode) -> Unit,
) {
    val glass = LocalGlass.current
    warnings.forEach { warning ->
        val (title, body) = when (warning) {
            WarningCode.NO_VPN_ACTIVE ->
                stringResource(R.string.warning_no_vpn_title) to stringResource(R.string.warning_no_vpn_body)
            WarningCode.VPN_CAPTURES_LOCAL ->
                stringResource(R.string.warning_vpn_captures_title) to stringResource(R.string.warning_vpn_captures_body)
        }
        Column(
            modifier = Modifier
                .fillMaxWidth()
                .padding(bottom = 12.dp)
                .glassPanel(radius = 16.dp)
                .padding(16.dp),
        ) {
            Row(
                modifier = Modifier.fillMaxWidth(),
                horizontalArrangement = Arrangement.SpaceBetween,
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(text = "💡 $title", color = glass.warning, fontWeight = FontWeight.Bold)
                Text(
                    text = "✕",
                    color = glass.textTertiary,
                    modifier = Modifier.clickable { onDismiss(warning) },
                )
            }
            Spacer(Modifier.height(4.dp))
            Text(text = body, color = glass.textSecondary, style = MaterialTheme.typography.labelSmall)
        }
    }
}

@Composable
private fun BatteryBanner(onAllow: () -> Unit) {
    val glass = LocalGlass.current
    Row(
        modifier = Modifier
            .fillMaxWidth()
            .glassPanel(radius = 16.dp)
            .padding(16.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Column(modifier = Modifier.weight(1f)) {
            Text(
                text = stringResource(R.string.battery_banner_title),
                style = MaterialTheme.typography.bodyMedium,
                color = glass.textPrimary,
                fontWeight = FontWeight.Bold,
            )
            Spacer(Modifier.height(2.dp))
            Text(
                text = stringResource(R.string.battery_banner_body),
                style = MaterialTheme.typography.labelSmall,
                color = glass.textSecondary,
            )
        }
        Spacer(Modifier.width(12.dp))
        Box(
            modifier = Modifier
                .clip(RoundedCornerShape(10.dp))
                .background(glass.accent)
                .clickable(role = Role.Button, onClick = onAllow)
                .padding(horizontal = 12.dp, vertical = 8.dp),
        ) {
            Text(
                text = stringResource(R.string.battery_banner_allow),
                color = glass.onAccent,
                style = MaterialTheme.typography.labelSmall,
                fontWeight = FontWeight.Bold,
            )
        }
    }
}

@Composable
private fun AdvancedSection(
    themeMode: String,
    logs: List<LocalLog.Entry>,
    onSetTheme: (String) -> Unit,
    onClearLogs: () -> Unit,
    onShareLogs: () -> Unit,
) {
    val glass = LocalGlass.current
    var expanded by rememberSaveable { mutableStateOf(false) }

    Column(
        modifier = Modifier
            .fillMaxWidth()
            .glassPanel(radius = 20.dp)
            .padding(16.dp),
    ) {
        Row(
            modifier = Modifier
                .fillMaxWidth()
                .clickable { expanded = !expanded },
            horizontalArrangement = Arrangement.SpaceBetween,
            verticalAlignment = Alignment.CenterVertically,
        ) {
            Text(
                text = "⚙️ ${stringResource(R.string.advanced)}",
                style = MaterialTheme.typography.bodyMedium,
                color = glass.textPrimary,
                fontWeight = FontWeight.SemiBold,
            )
            Text(text = if (expanded) "▲" else "▼", color = glass.textTertiary)
        }

        AnimatedVisibility(
            visible = expanded,
            enter = expandVertically() + fadeIn(),
            exit = shrinkVertically() + fadeOut(),
        ) {
            Column(modifier = Modifier.padding(top = 16.dp)) {
                Text(
                    text = stringResource(R.string.advanced_theme),
                    style = MaterialTheme.typography.labelSmall,
                    color = glass.textSecondary,
                )
                Spacer(Modifier.height(8.dp))
                Row(
                    modifier = Modifier
                        .fillMaxWidth()
                        .selectableGroup(),
                    horizontalArrangement = Arrangement.spacedBy(8.dp),
                ) {
                    listOf("system" to R.string.theme_system, "dark" to R.string.theme_dark, "light" to R.string.theme_light).forEach { (mode, resId) ->
                        val selected = themeMode == mode
                        Box(
                            modifier = Modifier
                                .weight(1f)
                                .height(36.dp)
                                .clip(RoundedCornerShape(8.dp))
                                .background(if (selected) glass.accent else glass.fill)
                                .selectable(selected = selected, onClick = { onSetTheme(mode) }),
                            contentAlignment = Alignment.Center,
                        ) {
                            Text(
                                text = stringResource(resId),
                                color = if (selected) glass.onAccent else glass.textSecondary,
                                style = MaterialTheme.typography.labelSmall,
                                fontWeight = if (selected) FontWeight.Bold else FontWeight.Normal,
                            )
                        }
                    }
                }

                Spacer(Modifier.height(16.dp))

                Row(
                    modifier = Modifier.fillMaxWidth(),
                    horizontalArrangement = Arrangement.SpaceBetween,
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(
                        text = stringResource(R.string.advanced_logs),
                        style = MaterialTheme.typography.labelSmall,
                        color = glass.textSecondary,
                    )
                    Row {
                        Text(
                            text = stringResource(R.string.advanced_logs_clear),
                            color = glass.textTertiary,
                            style = MaterialTheme.typography.labelSmall,
                            modifier = Modifier.clickable { onClearLogs() },
                        )
                        Spacer(Modifier.width(12.dp))
                        Text(
                            text = stringResource(R.string.advanced_logs_share),
                            color = glass.accent,
                            style = MaterialTheme.typography.labelSmall,
                            modifier = Modifier.clickable { onShareLogs() },
                        )
                    }
                }

                Spacer(Modifier.height(8.dp))

                Box(
                    modifier = Modifier
                        .fillMaxWidth()
                        .heightIn(max = 180.dp)
                        .clip(RoundedCornerShape(10.dp))
                        .background(glass.fillRaised)
                        .padding(10.dp)
                        .verticalScroll(rememberScrollState()),
                ) {
                    if (logs.isEmpty()) {
                        Text(
                            text = stringResource(R.string.advanced_logs_empty),
                            color = glass.textTertiary,
                            style = MaterialTheme.typography.labelSmall,
                        )
                    } else {
                        Column(verticalArrangement = Arrangement.spacedBy(4.dp)) {
                            logs.forEach { entry ->
                                Text(
                                    text = "${entry.formattedTime}: ${entry.message}",
                                    color = glass.textSecondary,
                                    fontFamily = FontFamily.Monospace,
                                    fontSize = 11.sp,
                                )
                            }
                        }
                    }
                }
            }
        }
    }
}

private fun formatBytes(bytes: Long): String {
    return when {
        bytes >= 1_000_000_000 -> "%.1f GB".format(bytes / 1_000_000_000.0)
        bytes >= 1_000_000 -> "%.1f MB".format(bytes / 1_000_000.0)
        bytes >= 1_000 -> "%.1f KB".format(bytes / 1_000.0)
        else -> "$bytes B"
    }
}
