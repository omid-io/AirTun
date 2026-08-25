package io.airtun.app.net

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.util.Log
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.ServerSocket
import java.net.Socket

object VpnStatus {
    private const val TAG = "AirTun-VPN"

    fun isVpnActive(context: Context): Boolean {
        return getActiveVpnNetwork(context) != null
    }

    fun getActiveVpnNetwork(context: Context): Network? {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return null
        val active = cm.activeNetwork
        if (active != null) {
            val caps = cm.getNetworkCapabilities(active)
            Log.i(TAG, "Active network: $active, caps: $caps")
            if (caps?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true) {
                return active
            }
        }
        for (network in cm.allNetworks) {
            val caps = cm.getNetworkCapabilities(network)
            Log.i(TAG, "Checking network: $network, caps: $caps")
            if (caps?.hasTransport(NetworkCapabilities.TRANSPORT_VPN) == true) {
                return network
            }
        }
        return null
    }

    fun bindSocketToUpstream(context: Context, socket: Socket): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return false
        val vpnNetwork = getActiveVpnNetwork(context)
        Log.i(TAG, "bindSocketToUpstream: target=${socket.remoteSocketAddress}, vpnNetwork=$vpnNetwork, activeNetwork=${cm.activeNetwork}")
        // Fail-closed: on bind failure the socket must NOT silently leak via the default route.
        return if (vpnNetwork != null) {
            try {
                vpnNetwork.bindSocket(socket)
                Log.i(TAG, "Successfully bound TCP socket to VPN network: $vpnNetwork")
                true
            } catch (e: Exception) {
                Log.e(TAG, "Failed to bind TCP socket to VPN network: ${e.message}", e)
                false
            }
        } else {
            // No VPN active: default route IS the desired upstream, nothing to do.
            true
        }
    }

    /**
     * Creates a TCP socket bound to the upstream (VPN when active, otherwise default).
     * Uses Network.socketFactory — the canonical way; the socket is born on the right network.
     * Returns null when no upstream is available (caller must fail-closed).
     */
    fun createUpstreamTcpSocket(context: Context): Socket? {
        val network = getActiveVpnNetwork(context) ?: run {
            val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager
            cm?.activeNetwork
        }
        return try {
            val s = network?.socketFactory?.createSocket() ?: Socket()
            Log.i(TAG, "Created TCP socket via ${network ?: "default"}")
            s
        } catch (e: Exception) {
            Log.e(TAG, "Failed creating upstream TCP socket: ${e.message}", e)
            null
        }
    }

    /** Resolves a hostname on the upstream network (VPN-aware), never the process-default DNS. */
    fun resolveOnUpstream(context: Context, host: String): InetAddress? {
        val network = getActiveVpnNetwork(context)
        return try {
            if (network != null) {
                val addrs = network.getAllByName(host)
                Log.i(TAG, "Resolved $host on VPN network $network -> ${addrs.firstOrNull()}")
                addrs.firstOrNull()
            } else {
                InetAddress.getByName(host)
            }
        } catch (e: Exception) {
            Log.e(TAG, "Resolve failed for $host on upstream: ${e.message}")
            null
        }
    }

    fun bindDatagramSocketToUpstream(context: Context, socket: DatagramSocket): Boolean {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return false
        val vpnNetwork = getActiveVpnNetwork(context)
        if (vpnNetwork != null) {
            return try {
                vpnNetwork.bindSocket(socket)
                Log.i(TAG, "Successfully bound UDP socket to VPN network: $vpnNetwork")
                true
            } catch (e: Exception) {
                Log.e(TAG, "Failed to bind UDP socket to VPN network: ${e.message}", e)
                false
            }
        }
        // No VPN active: default route is fine.
        return true
    }

    /**
     * Finds the LOCAL network (Wi-Fi / hotspot interface like ap0/wlan) — the one LAN
     * clients connect through. Traffic answered on a socket bound to this network exits
     * via the local interface, NOT the VPN tunnel, so handshakes with LAN clients survive
     * an active phone VPN that captures all UIDs (e.g. Hiddify/sing-box).
     */
    fun getLocalLanNetwork(context: Context): Network? {
        val cm = context.getSystemService(Context.CONNECTIVITY_SERVICE) as? ConnectivityManager ?: return null
        var best: Network? = null
        val request = android.net.NetworkRequest.Builder()
            .addTransportType(NetworkCapabilities.TRANSPORT_WIFI)
            .build()
        try {
            // Synchronous scan over currently-known networks (no callback wait needed).
            for (network in cm.allNetworks) {
                val caps = cm.getNetworkCapabilities(network) ?: continue
                if (!caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI)) continue
                if (!caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED) &&
                    !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_LOCAL_NETWORK) &&
                    android.os.Build.VERSION.SDK_INT < 33) continue
                // Prefer the network owning our advertised host address (hotspot ap0).
                val lp = cm.getLinkProperties(network) ?: continue
                val hasOurHost = lp.linkAddresses.any { it.address is java.net.Inet4Address }
                if (hasOurHost) {
                    best = network
                    Log.i(TAG, "LAN network found: $network ifaces=${lp.interfaceName} addrs=${lp.linkAddresses}")
                }
            }
        } catch (e: Exception) {
            Log.e(TAG, "LAN network scan failed: ${e.message}")
        }
        return best
    }

    /**
     * Binds the LISTENER socket (ServerSocket or UDP listener) to the local LAN network so
     * its replies leave via the hotspot interface and are never swallowed by the phone VPN.
     * When no VPN is active this is unnecessary; binding is skipped and true returned.
     */
    fun bindListenerToLocalLan(context: Context, serverSocket: ServerSocket): Boolean =
        bindListenerImpl(context) { net ->
            // Network.bindSocket has no ServerSocket overload — go through the file descriptor.
            val fd = serverSocket.getChannel()?.let { ch ->
                try {
                    val m = ch.javaClass.methods.firstOrNull { it.name == "getFD" }
                    m?.invoke(ch) as? java.io.FileDescriptor
                } catch (_: Exception) { null }
            } ?: throw IllegalStateException("ServerSocket channel FD unavailable")
            net.bindSocket(fd)
        }

    fun bindListenerToLocalLan(context: Context, socket: DatagramSocket): Boolean =
        bindListenerImpl(context) { net -> net.bindSocket(socket) }

    private fun bindListenerImpl(context: Context, doBind: (Network) -> Unit): Boolean {
        val vpn = getActiveVpnNetwork(context)
        if (vpn == null) {
            Log.i(TAG, "No VPN active — listener left unbound (default OK)")
            return true
        }
        val lan = getLocalLanNetwork(context)
        if (lan == null) {
            Log.e(TAG, "VPN active but no LAN network found for listener bind!")
            return false
        }
        return try {
            doBind(lan)
            Log.i(TAG, "Listener bound to LAN network $lan — replies will bypass VPN")
            true
        } catch (e: Exception) {
            Log.e(TAG, "Failed binding listener to LAN network: ${e.message}", e)
            false
        }
    }
}
