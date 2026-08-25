package io.airtun.app.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.Build
import android.os.IBinder
import android.os.PowerManager
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import io.airtun.app.MainActivity
import io.airtun.app.R
import io.airtun.app.core.AirTunConfig
import io.airtun.app.core.ConnectionRules
import io.airtun.app.core.ConnectionState
import io.airtun.app.core.ErrorCode
import io.airtun.app.core.PinCode
import io.airtun.app.core.WarningCode
import io.airtun.app.net.AirTunBeacon
import io.airtun.app.net.LocalAddress
import io.airtun.app.net.NetworkDiagnostics
import io.airtun.app.net.VpnStatus
import io.airtun.app.net.socks5.Socks5Server
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.collectLatest
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.IOException
import java.net.DatagramSocket

class SharingService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Default)
    private var socksServer: Socks5Server? = null
    private var beacon: AirTunBeacon? = null
    private var wakeLock: PowerManager.WakeLock? = null
    private var currentHost: String? = null
    private var currentPin: String? = null
    private var networkWatcher: Job? = null

    private val notificationManager by lazy {
        getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
    }

    override fun onBind(intent: Intent?): IBinder? = null

    override fun onCreate() {
        super.onCreate()
        createNotificationChannel()
        scope.launch {
            ConnectionRepository.state.collectLatest { state ->
                if (state !is ConnectionState.Idle) {
                    notificationManager.notify(NOTIFICATION_ID, buildNotification(state))
                }
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_START, null -> {
                startInForeground()
                scope.launch {
                    if (ConnectionRepository.state.value is ConnectionState.Idle) {
                        startSharing()
                    }
                }
            }
            ACTION_STOP -> stopSharing()
        }
        return START_STICKY
    }

    override fun onDestroy() {
        teardown()
        ConnectionRepository.dispatch("stop") { ConnectionState.Idle }
        scope.cancel()
        super.onDestroy()
    }

    private fun startInForeground() {
        val type = if (Build.VERSION.SDK_INT >= 34) {
            ServiceInfo.FOREGROUND_SERVICE_TYPE_SPECIAL_USE
        } else {
            0
        }
        ServiceCompat.startForeground(
            this,
            NOTIFICATION_ID,
            buildNotification(ConnectionRepository.state.value),
            type,
        )
    }

    private fun startSharing() {
        if (!ConnectionRepository.dispatch("start") { ConnectionState.Preparing }) return
        LocalLog.add("Starting AirTun Engine")
        ConnectionRepository.clearWarnings()

        ConnectionRepository.setWarning(
            WarningCode.NO_VPN_ACTIVE,
            !VpnStatus.isVpnActive(this),
        )

        val host = LocalAddress.findAdvertisableIpv4()
        if (host == null) {
            LocalLog.add("No usable Wi-Fi/Hotspot interface found")
            fail(ErrorCode.HOTSPOT_OFF)
            return
        }

        currentHost = host
        val pin = PinCode.draw()
        currentPin = pin
        val deviceName = "${Build.MANUFACTURER} ${Build.MODEL}".trim()

        try {
            val server = Socks5Server(
                port = AirTunConfig.DEFAULT_SOCKS_PORT,
                pinCode = pin,
                pinRequired = true,
                upstreamContext = applicationContext,
                bindSocket = { socket ->
                    VpnStatus.bindSocketToUpstream(applicationContext, socket)
                },
                bindDatagramSocket = { datagramSocket ->
                    VpnStatus.bindDatagramSocketToUpstream(applicationContext, datagramSocket)
                },
                onTraffic = { up, down ->
                    ConnectionRepository.updateTraffic(
                        socksServer?.totalBytesUp?.get() ?: 0L,
                        socksServer?.totalBytesDown?.get() ?: 0L,
                        socksServer?.uniqueClientCount ?: 0,
                    )
                },
                onClientCountChanged = { count ->
                    ConnectionRepository.setClientCount(count)
                    manageWakeLock(count > 0)
                },
                onLog = { msg ->
                    LocalLog.add(msg)
                },
            )
            server.start()
            socksServer = server
            LocalLog.add("SOCKS5 Server active on $host:${server.port} with PIN: $pin")
        } catch (e: IOException) {
            LocalLog.add("Failed to start SOCKS5 Server on port ${AirTunConfig.DEFAULT_SOCKS_PORT}: ${e.message}")
            fail(ErrorCode.PORT_IN_USE)
            return
        } catch (e: Exception) {
            LocalLog.add("Error starting server: ${e.message}")
            fail(ErrorCode.SERVICE_FAILED)
            return
        }

        val announcer = AirTunBeacon(
            deviceName = deviceName,
            socksPort = AirTunConfig.DEFAULT_SOCKS_PORT,
            pin = pin,
            pinRequired = true,
        ).also { it.start() }
        beacon = announcer
        LocalLog.add("UDP Beacon broadcasting on port ${AirTunConfig.DEFAULT_BEACON_PORT}")

        ConnectionRepository.dispatch("ready") {
            ConnectionState.Advertising(
                host = host,
                port = AirTunConfig.DEFAULT_SOCKS_PORT,
                pinCode = pin,
                deviceName = deviceName,
            )
        }

        scope.launch {
            NetworkDiagnostics.runDiagnostic(applicationContext)
        }

        startNetworkWatcher()
    }

    private fun startNetworkWatcher() {
        networkWatcher?.cancel()
        networkWatcher = scope.launch {
            while (isActive) {
                delay(3000)
                val current = LocalAddress.findAdvertisableIpv4()
                if (current == null) {
                    LocalLog.add("Lost local network interface")
                    fail(ErrorCode.HOTSPOT_LOST)
                    break
                }
            }
        }
    }

    private fun manageWakeLock(hold: Boolean) {
        if (hold) {
            if (wakeLock == null) {
                val powerManager = getSystemService(Context.POWER_SERVICE) as PowerManager
                wakeLock = powerManager.newWakeLock(
                    PowerManager.PARTIAL_WAKE_LOCK,
                    "AirTun:TransferWakeLock",
                ).apply { acquire(10 * 60 * 1000L) }
            }
        } else {
            wakeLock?.let {
                if (it.isHeld) it.release()
            }
            wakeLock = null
        }
    }

    private fun fail(error: ErrorCode) {
        teardown()
        val current = ConnectionRepository.state.value.stateName
        if (ConnectionRules.canTransition(current, "failure")) {
            ConnectionRepository.dispatch("failure") { ConnectionState.Error(error) }
        }
    }

    private fun stopSharing() {
        LocalLog.add("Stopping AirTun service")
        teardown()
        if (ConnectionRepository.state.value !is ConnectionState.Idle) {
            ConnectionRepository.dispatch("stop") { ConnectionState.Idle }
        }
        stopForeground(STOP_FOREGROUND_REMOVE)
        stopSelf()
    }

    private fun teardown() {
        networkWatcher?.cancel()
        networkWatcher = null
        beacon?.stop()
        beacon = null
        socksServer?.stop()
        socksServer = null
        manageWakeLock(false)
    }

    private fun buildNotification(state: ConnectionState): Notification {
        val launchIntent = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_IMMUTABLE,
        )

        val (title, text) = when (state) {
            is ConnectionState.Idle ->
                getString(R.string.status_idle) to ""
            is ConnectionState.Preparing ->
                getString(R.string.notification_starting) to ""
            is ConnectionState.Advertising ->
                getString(R.string.notification_waiting) to "PIN: ${state.pinCode}"
            is ConnectionState.Connected ->
                resources.getQuantityString(
                    R.plurals.notification_connected,
                    state.clientCount,
                    state.clientCount,
                ) to "↑ ${formatBytes(state.bytesUp)}  ↓ ${formatBytes(state.bytesDown)}"
            is ConnectionState.Error ->
                getString(R.string.notification_error) to ""
        }

        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setContentTitle(title)
            .setContentText(text)
            .setSmallIcon(R.drawable.ic_notification)
            .setContentIntent(launchIntent)
            .setOngoing(state !is ConnectionState.Idle && state !is ConnectionState.Error)
            .setCategory(NotificationCompat.CATEGORY_SERVICE)
            .build()
    }

    private fun createNotificationChannel() {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            val channel = NotificationChannel(
                CHANNEL_ID,
                getString(R.string.notification_channel),
                NotificationManager.IMPORTANCE_LOW,
            ).apply {
                description = "Shows AirTun active proxy status"
            }
            notificationManager.createNotificationChannel(channel)
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

    companion object {
        const val ACTION_START = "io.airtun.app.START"
        const val ACTION_STOP = "io.airtun.app.STOP"
        private const val CHANNEL_ID = "airtun_sharing_channel"
        private const val NOTIFICATION_ID = 10101

        fun start(context: Context) {
            val intent = Intent(context, SharingService::class.java).apply { action = ACTION_START }
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
                context.startForegroundService(intent)
            } else {
                context.startService(intent)
            }
        }

        fun stop(context: Context) {
            context.startService(Intent(context, SharingService::class.java).apply { action = ACTION_STOP })
        }
    }
}
