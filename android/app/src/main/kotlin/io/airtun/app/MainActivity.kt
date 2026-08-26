package io.airtun.app

import android.Manifest
import android.content.Intent
import android.content.res.Configuration
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import androidx.activity.ComponentActivity
import androidx.activity.compose.LocalActivityResultRegistryOwner
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.activity.result.contract.ActivityResultContracts
import androidx.activity.viewModels
import androidx.compose.runtime.CompositionLocalProvider
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.platform.LocalConfiguration
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLayoutDirection
import androidx.compose.ui.unit.LayoutDirection
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.compose.LocalLifecycleOwner
import androidx.lifecycle.repeatOnLifecycle
import io.airtun.app.service.DiagnosticReport
import io.airtun.app.ui.HomeScreen
import io.airtun.app.ui.MainViewModel
import io.airtun.app.ui.theme.AirTunBackground
import io.airtun.app.ui.theme.AirTunTheme
import java.util.Locale

class MainActivity : ComponentActivity() {

    private val viewModel: MainViewModel by viewModels()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent {
            val themeMode by viewModel.themeMode.collectAsState()
            var lang by remember { mutableStateOf("en") }

            // Capture Activity-owned locals BEFORE overriding LocalContext
            val activityContext = LocalContext.current
            val activityResultRegistryOwner = LocalActivityResultRegistryOwner.current
            val lifecycleOwner = LocalLifecycleOwner.current

            // rememberLauncherForActivityResult MUST be called before LocalContext override
            val notificationPermission = rememberLauncherForActivityResult(
                ActivityResultContracts.RequestPermission(),
            ) { viewModel.startSharing() }

            // Build localized context for string resources (RTL/FA support)
            val locale = remember(lang) { Locale(lang) }
            val localizedConfiguration = remember(lang) {
                Configuration(activityContext.resources.configuration).apply {
                    setLocale(locale)
                    setLayoutDirection(locale)
                }
            }
            val localizedContext = remember(lang) {
                activityContext.createConfigurationContext(localizedConfiguration)
            }
            val layoutDirection = if (lang == "fa") LayoutDirection.Rtl else LayoutDirection.Ltr

            CompositionLocalProvider(
                LocalContext provides localizedContext,
                LocalConfiguration provides localizedConfiguration,
                LocalLayoutDirection provides layoutDirection,
                // Re-provide Activity-owned locals so they survive the context override
                LocalActivityResultRegistryOwner provides activityResultRegistryOwner!!,
                LocalLifecycleOwner provides lifecycleOwner,
            ) {
                AirTunTheme(themeMode = themeMode) {
                    AirTunBackground {
                        val state by viewModel.state.collectAsState()
                        val batteryExempt by viewModel.batteryExempt.collectAsState()
                        val warnings by viewModel.warnings.collectAsState()
                        val speedBps by viewModel.speedBps.collectAsState()
                        val logs by viewModel.logs.collectAsState()

                        LaunchedEffect(lifecycleOwner) {
                            lifecycleOwner.lifecycle.repeatOnLifecycle(Lifecycle.State.RESUMED) {
                                viewModel.refreshBatteryExempt()
                            }
                        }

                        HomeScreen(
                            state = state,
                            batteryExempt = batteryExempt,
                            warnings = warnings,
                            themeMode = themeMode,
                            speedBps = speedBps,
                            lang = lang,
                            logs = logs,
                            onStart = {
                                if (Build.VERSION.SDK_INT >= 33) {
                                    notificationPermission.launch(Manifest.permission.POST_NOTIFICATIONS)
                                } else {
                                    viewModel.startSharing()
                                }
                            },
                            onStop = viewModel::stopSharing,
                            onRetry = viewModel::retry,
                            onDismissError = viewModel::dismissError,
                            onAllowBattery = ::requestBatteryExemption,
                            onDismissWarning = viewModel::dismissWarning,
                            onSetTheme = viewModel::setThemeMode,
                            onToggleLang = {
                                lang = if (lang == "en") "fa" else "en"
                            },
                            onClearLogs = viewModel::clearLogs,
                            onShareLogs = {
                                val version = runCatching {
                                    packageManager.getPackageInfo(packageName, 0).versionName
                                }.getOrNull() ?: "1.0.0"
                                val report = DiagnosticReport.build(state, logs, version)
                                startActivity(DiagnosticReport.shareIntent(this@MainActivity, report))
                            },
                        )
                    }
                }
            }
        }
    }

    private fun requestBatteryExemption() {
        val direct = Intent(
            Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS,
            Uri.parse("package:$packageName"),
        )
        try {
            startActivity(direct)
        } catch (_: Exception) {
            runCatching {
                startActivity(Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS))
            }
        }
    }
}
