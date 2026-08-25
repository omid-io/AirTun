package io.airtun.app.net

import android.content.Context
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.os.Environment
import android.util.Log
import io.airtun.app.service.LocalLog
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import java.io.BufferedReader
import java.io.File
import java.io.InputStreamReader
import java.io.OutputStreamWriter
import java.net.InetSocketAddress
import java.net.Socket
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

object NetworkDiagnostics {
    private const val TAG = "AirTun-Diag"

    suspend fun runDiagnostic(context: Context) = withContext(Dispatchers.IO) {
        val report = StringBuilder()
        val timestamp = SimpleDateFormat("yyyy-MM-dd HH:mm:ss", Locale.US).format(Date())
        report.appendLine("=== AirTun Android Network Diagnostic ($timestamp) ===")

        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
        if (cm == null) {
            report.appendLine("[ERROR] ConnectivityManager is null")
            saveAndLog(report.toString())
            return@withContext
        }

        val activeNetwork = cm.activeNetwork
        val activeCaps = activeNetwork?.let { cm.getNetworkCapabilities(it) }
        report.appendLine("[NET] Active Network: $activeNetwork")
        report.appendLine("[NET] Active Capabilities: $activeCaps")

        var vpnFound = false
        for (net in cm.allNetworks) {
            val caps = cm.getNetworkCapabilities(net) ?: continue
            val isVpn = caps.hasTransport(NetworkCapabilities.TRANSPORT_VPN)
            val isCellular = caps.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR)
            val isWifi = caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)
            report.appendLine("[NET] Network $net -> VPN=$isVpn, WiFi=$isWifi, Cellular=$isCellular")
            if (isVpn) vpnFound = true
        }

        if (!vpnFound) {
            report.appendLine("[WARN] No TRANSPORT_VPN interface detected on device!")
        } else {
            report.appendLine("[OK] Active VPN interface detected.")
        }

        try {
            report.appendLine("[TEST 1] Testing Default Outbound Socket to api.ipify.org...")
            val (ip1, err1) = queryOutboundIp(null)
            if (ip1 != null) {
                report.appendLine("[TEST 1 SUCCESS] Default Outbound IP: $ip1")
            } else {
                report.appendLine("[TEST 1 FAILED] Error: $err1")
            }
        } catch (e: Exception) {
            report.appendLine("[TEST 1 ERROR] ${e.message}")
        }

        try {
            report.appendLine("[TEST 2] Testing Outbound Socket with VpnStatus.bindSocket...")
            val (ip2, err2) = queryOutboundIp { sock ->
                VpnStatus.bindSocketToUpstream(context, sock)
            }
            if (ip2 != null) {
                report.appendLine("[TEST 2 SUCCESS] Outbound IP with bindSocket: $ip2")
                if (ip2.startsWith("5.122.") || ip2.startsWith("2.144.") || ip2.startsWith("188.") || ip2.startsWith("89.")) {
                    report.appendLine("[DIAGNOSIS] IP belongs to Iranian Mobile ISP. Traffic did not enter VPN tunnel.")
                } else {
                    report.appendLine("[DIAGNOSIS] Foreign VPN IP detected! VPN tunnel routing is ACTIVE.")
                }
            } else {
                report.appendLine("[TEST 2 FAILED] Error: $err2")
            }
        } catch (e: Exception) {
            report.appendLine("[TEST 2 ERROR] ${e.message}")
        }

        saveAndLog(report.toString())
    }

    private fun queryOutboundIp(bindAction: ((Socket) -> Unit)?): Pair<String?, String?> {
        var socket: Socket? = null
        try {
            socket = Socket()
            socket.soTimeout = 7000
            socket.tcpNoDelay = true
            bindAction?.invoke(socket)
            socket.connect(InetSocketAddress("api.ipify.org", 80), 7000)

            val writer = OutputStreamWriter(socket.getOutputStream(), "UTF-8")
            writer.write("GET / HTTP/1.1\r\nHost: api.ipify.org\r\nConnection: close\r\nUser-Agent: AirTun/1.1.0\r\n\r\n")
            writer.flush()

            val reader = BufferedReader(InputStreamReader(socket.getInputStream(), "UTF-8"))
            var line: String?
            var isBody = false
            val body = StringBuilder()
            while (reader.readLine().also { line = it } != null) {
                if (line.isNullOrEmpty()) {
                    isBody = true
                    continue
                }
                if (isBody) {
                    body.append(line?.trim())
                    break
                }
            }
            val ip = body.toString().trim()
            return if (ip.isNotEmpty()) Pair(ip, null) else Pair(null, "Empty response body")
        } catch (e: Exception) {
            return Pair(null, "${e.javaClass.simpleName}: ${e.message}")
        } finally {
            try { socket?.close() } catch (_: Exception) {}
        }
    }

    private fun saveAndLog(content: String) {
        Log.i(TAG, content)
        for (line in content.lines()) {
            if (line.isNotBlank()) {
                LocalLog.add(line)
            }
        }

        try {
            val downloadDir = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
            if (downloadDir != null && downloadDir.exists()) {
                val file = File(downloadDir, "airtun_diag.txt")
                file.writeText(content)
                Log.i(TAG, "Saved diagnostic report to ${file.absolutePath}")
            }
        } catch (e: Exception) {
            Log.e(TAG, "Could not write to external storage: ${e.message}")
        }
    }
}
