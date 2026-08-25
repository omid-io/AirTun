package io.airtun.app.net.socks5

import android.content.Context
import android.util.Log
import io.airtun.app.core.AirTunConfig
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.Socket

/**
 * Detects and connects to a local (loopback) SOCKS5 upstream proxy — the phone's
 * own VPN/proxy app such as Hiddify, sing-box, v2rayNG or Clash.
 *
 * Why: when a VPN app captures all UIDs, AirTun's direct outbound sockets get their
 * replies routed into the tunnel, breaking LAN handshakes. Chaining through the local
 * proxy avoids kernel routing entirely — traffic goes over loopback, which no VPN
 * can intercept.
 */
object UpstreamProxy {

    private const val TAG = "AirTun-Upstream"

    /** Common loopback SOCKS5 ports across popular VPN/proxy apps. */
    val CANDIDATE_PORTS = intArrayOf(2080, 12334, 7890, 1080, 7891, 9090, 10808)

    @Volatile
    var detectedPort: Int = -1
        private set

    /** Remember the last working port to prefer it on re-detect (VPN restarts). */
    @Volatile
    private var lastKnownGoodPort: Int = -1

    /** Cooldown so a burst of failures does not hammer the probe. */
    @Volatile
    private var lastRedetectAt: Long = 0L
    private const val REDETECT_COOLDOWN_MS = 5_000L

    /**
     * Probes candidate loopback ports for a live SOCKS5 server (method 0x00/0x02 accepted).
     * Prefers the last known good port first.
     */
    fun detect(@Suppress("UNUSED_PARAMETER") context: Context?): Int {
        detectedPort = -1
        val ordered = if (lastKnownGoodPort > 0) {
            intArrayOf(lastKnownGoodPort) + CANDIDATE_PORTS.filter { it != lastKnownGoodPort }.toIntArray()
        } else CANDIDATE_PORTS
        for (port in ordered) {
            if (probePort(port)) {
                detectedPort = port
                lastKnownGoodPort = port
                Log.i(TAG, "Detected loopback SOCKS5 upstream on port $port")
                return port
            }
        }
        Log.i(TAG, "No loopback SOCKS5 upstream found")
        return -1
    }

    private fun probePort(port: Int): Boolean = try {
        Socket().use { s ->
            s.connect(InetSocketAddress("127.0.0.1", port), 800)
            s.soTimeout = 1200
            val out = DataOutputStream(s.getOutputStream())
            val inp = DataInputStream(s.getInputStream())
            out.write(byteArrayOf(0x05, 0x01, 0x00)); out.flush()
            val ver = inp.readUnsignedByte()
            val method = inp.readUnsignedByte()
            ver == 0x05 && (method == 0x00 || method == 0x02)
        }
    } catch (_: Exception) {
        false
    }

    fun isAvailable(): Boolean = detectedPort > 0

    /**
     * Ensures an upstream is available, lazily re-detecting with a cooldown.
     * Call before every chain attempt — handles Hiddify restarting on a new port.
     */
    fun ensureAvailable(context: Context?): Boolean {
        if (isAvailable()) return true
        val now = System.currentTimeMillis()
        if (now - lastRedetectAt < REDETECT_COOLDOWN_MS) return false
        lastRedetectAt = now
        Log.i(TAG, "Upstream lost — re-detecting loopback proxy...")
        detect(context)
        return isAvailable()
    }

    fun invalidate() {
        Log.w(TAG, "Upstream invalidated (connection failure)")
        detectedPort = -1
    }

    /**
     * Opens a CONNECT to host:port THROUGH the detected loopback upstream.
     * Returns the established Socket ready for piping, or null on failure.
     * Domains are sent unresolved (ATYP=domain) so the upstream does its own DNS —
     * zero DNS leak from our process.
     */
    fun connectThrough(host: String, port: Int): Socket? {
        if (detectedPort <= 0) return null
        return try {
            val s = Socket()
            s.tcpNoDelay = true
            s.soTimeout = AirTunConfig.SOCKET_IDLE_TIMEOUT_MS
            s.connect(InetSocketAddress("127.0.0.1", detectedPort), 3000)
            val out = DataOutputStream(s.getOutputStream())
            val inp = DataInputStream(s.getInputStream())

            // greeting
            out.write(byteArrayOf(0x05, 0x01, 0x00)); out.flush()
            val ver = inp.readUnsignedByte(); val method = inp.readUnsignedByte()
            if (ver != 0x05 || method != 0x00) { s.close(); return null }

            // CONNECT — correct ATYP per RFC 1928:
            //   IPv4 literal -> ATYP=1 + 4 raw bytes; otherwise domain -> ATYP=3.
            out.writeByte(0x05); out.writeByte(0x01); out.writeByte(0x00)
            val ip4 = runCatching { InetAddress.getByName(host) }.getOrNull()
            if (ip4 is Inet4Address) {
                out.writeByte(0x01)
                out.write(ip4.address)
            } else {
                out.writeByte(0x03)
                val d = host.toByteArray(Charsets.US_ASCII)
                out.writeByte(d.size); out.write(d)
            }
            out.writeShort(port)
            out.flush()

            val rVer = inp.readUnsignedByte()
            val rRep = inp.readUnsignedByte()
            inp.readUnsignedByte()                       // rsv
            val atyp = inp.readUnsignedByte()
            when (atyp) {
                0x01 -> { val b = ByteArray(4); inp.readFully(b) }
                0x04 -> { val b = ByteArray(16); inp.readFully(b) }
                0x03 -> { val n = inp.readUnsignedByte(); val b = ByteArray(n); inp.readFully(b) }
            }
            inp.readUnsignedShort()                      // bnd.port

            if (rVer != 0x05 || rRep != 0x00) {
                Log.w(TAG, "Upstream CONNECT to $host:$port failed rep=$rRep")
                s.close(); return null
            }
            Log.i(TAG, "Chained via loopback :$detectedPort -> $host:$port")
            s
        } catch (e: Exception) {
            Log.e(TAG, "connectThrough($host:$port) failed: ${e.message}")
            null
        }
    }
}
