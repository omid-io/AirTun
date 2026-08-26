package io.airtun.app.net.socks5

import android.content.Context
import android.util.Log
import io.airtun.app.core.AirTunConfig
import io.airtun.app.net.VpnStatus
import io.airtun.app.net.LocalAddress
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.io.InputStream
import java.io.OutputStream
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.net.SocketTimeoutException
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong

/** True for 127.x.x.x loopback addresses. */
private fun String.isLoopback(): Boolean = startsWith("127.")

class Socks5Server(
    val port: Int = AirTunConfig.DEFAULT_SOCKS_PORT,
    var pinCode: String = "",
    var pinRequired: Boolean = true,
    /** LAN IPv4 (e.g. hotspot ap0 address). When set, the listener binds THIS address
     *  instead of the wildcard, so SYN-ACKs leave via the hotspot NIC even while a
     *  VPN tunnel is capturing all UIDs — fixes "Web Proxy/TUN connect timeout". */
    val lanAddress: InetAddress? = null,
    private val upstreamContext: Context? = null,
    private val bindSocket: ((Socket) -> Boolean)? = null,
    private val bindDatagramSocket: ((DatagramSocket) -> Boolean)? = null,
    private val onTraffic: (bytesUp: Long, bytesDown: Long) -> Unit,
    private val onClientCountChanged: (count: Int) -> Unit,
    private val onLog: (message: String) -> Unit = {},
    /** Fired when a LAN client's TCP connect succeeded but the SOCKS handshake
     *  never completed while a VPN tunnel is capturing this app — the signature of
     *  "VPN app is proxying AirTun itself" (fix: exclude AirTun in the VPN app). */
    private val onVpnCaptureSuspected: (() -> Unit)? = null,
) {
    companion object {
        private const val TAG = "AirTun-Socks5"
    }

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var serverSocket: ServerSocket? = null
    private var acceptJob: Job? = null
    private var udpRelay: Socks5UdpRelay? = null

    val activeConnections = AtomicInteger(0)
    private val activeClients = ConcurrentHashMap<String, AtomicInteger>()
    val uniqueClientCount: Int get() = activeClients.size
    val totalBytesUp = AtomicLong(0)
    val totalBytesDown = AtomicLong(0)

    /** The port actually bound (may differ from [port] if the default was busy). */
    @Volatile
    var actualPort: Int = port
        private set

    private val authenticatedClients = ConcurrentHashMap.newKeySet<String>()

    @Volatile
    var isRunning: Boolean = false
        private set

    @Synchronized
    fun start() {
        if (isRunning) return
        // Probe the phone's local VPN proxy once at startup; refreshed on each start.
        if (upstreamContext != null) {
            UpstreamProxy.detect(upstreamContext)
            if (UpstreamProxy.isAvailable()) {
                Log.i(TAG, "Upstream chaining ENABLED via loopback :${UpstreamProxy.detectedPort}")
                onLog("VPN proxy detected on :${UpstreamProxy.detectedPort} — traffic will be chained")
            } else {
                Log.i(TAG, "No local VPN proxy — direct upstream mode")
            }
        }
        // Bind with fallback: another VPN app (v2rayNG etc.) may already occupy the
        // default port on loopback. Try successive ports and remember which one won —
        // the beacon advertises server.port so Windows clients always find us.
        var server: ServerSocket? = null
        var boundPort = port
        val maxShift = 20
        for (shift in 0..maxShift) {
            val candidate = port + shift
            try {
                val s = ServerSocket()
                s.reuseAddress = true
                // Bind to the hotspot IP when available: replies then egress ap0 and a
                // VPN tunnel cannot hijack the handshake (root cause of connect timeout).
                s.bind(
                    if (lanAddress != null) InetSocketAddress(lanAddress, candidate)
                    else InetSocketAddress(candidate)
                )
                server = s
                boundPort = candidate
                if (candidate != port) {
                    Log.w(TAG, "Default port $port busy — bound to $candidate instead")
                    onLog("Port $port busy — using $candidate")
                    actualPort = candidate
                }
                break
            } catch (_: IOException) {
                if (shift == maxShift) throw IOException("No free port in range $port..${port + maxShift}")
            }
        }
        server = requireNotNull(server)
        // CRITICAL: when the phone VPN is active, replies from this listener must leave
        // via the hotspot interface (ap0), not the VPN tunnel — otherwise LAN clients'
        // handshakes never complete (SYN_SENT forever). Bind listener to the LAN network.
        if (upstreamContext != null) {
            if (!VpnStatus.bindListenerToLocalLan(upstreamContext, server)) {
                Log.e(TAG, "Listener could not be bound to LAN network; continuing unbound")
            }
        }
        serverSocket = server
        isRunning = true

        udpRelay = Socks5UdpRelay(
            upstreamContext = upstreamContext,
            bindDatagramSocket = bindDatagramSocket,
        ) { up, down ->
            recordTraffic(up, down)
        }.also { it.start() }

        onLog("SOCKS5 Server listening on port $boundPort (UDP Relay on ${udpRelay?.boundPort})")
        Log.i(TAG, "SOCKS5 Server started on port $boundPort")

        acceptJob = scope.launch {
            while (isActive) {
                val clientSocket = try {
                    val accepted = server.accept()
                    // Phase-0 diagnostic: proves LAN SYN reaches the app through hotspot+VPN.
                    Log.i(TAG, "ACCEPT from ${accepted.inetAddress?.hostAddress}:${accepted.port}")
                    accepted
                } catch (_: IOException) {
                    break
                }
                launch {
                    handleClient(clientSocket)
                }
            }
        }
    }

    private suspend fun handleClient(client: Socket) {
        val clientIp = client.inetAddress?.hostAddress ?: "unknown"
        activeConnections.incrementAndGet()
        activeClients.computeIfAbsent(clientIp) { AtomicInteger(0) }.incrementAndGet()
        onClientCountChanged(activeClients.size)

        // VPN-capture watchdog: if this is a LAN (non-loopback) client whose TCP
        // connect succeeded but no SOCKS greeting arrives within 5s while a VPN
        // tunnel is active, the VPN is almost certainly proxying AirTun's own
        // traffic — the handshake reply was swallowed by the tunnel.
        var handshakeDone = false
        if (clientIp != "unknown" && !clientIp.isLoopback() && upstreamContext != null &&
            VpnStatus.isVpnActive(upstreamContext)
        ) {
            scope.launch {
                kotlinx.coroutines.delay(5_000)
                if (!handshakeDone && activeConnections.get() > 0) {
                    Log.w(TAG, "Handshake stall from $clientIp with VPN active — VPN capture suspected")
                    onLog("Client connected but handshake stalled — your VPN may be capturing AirTun (exclude AirTun in VPN per-app settings)")
                    onVpnCaptureSuspected?.invoke()
                }
            }
        }

        var clientIn: InputStream? = null
        var clientOut: OutputStream? = null

        try {
            client.soTimeout = AirTunConfig.SOCKET_IDLE_TIMEOUT_MS
            client.tcpNoDelay = true

            clientIn = client.getInputStream()
            clientOut = client.getOutputStream()

            val dataIn = DataInputStream(clientIn)
            val dataOut = DataOutputStream(clientOut)

            val version = dataIn.readUnsignedByte()
            handshakeDone = true
            if (version != 0x05) {
                Log.w(TAG, "Invalid SOCKS version: $version from $clientIp")
                return
            }

            val nMethods = dataIn.readUnsignedByte()
            val methods = ByteArray(nMethods)
            dataIn.readFully(methods)

            val isAlreadyAuth = !pinRequired || authenticatedClients.contains(clientIp)
            val hasUserPass = methods.contains(0x02.toByte())
            val hasNoAuth = methods.contains(0x00.toByte())

            if (isAlreadyAuth && hasNoAuth) {
                dataOut.writeByte(0x05)
                dataOut.writeByte(0x00)
                dataOut.flush()
            } else if (pinRequired && hasUserPass) {
                dataOut.writeByte(0x05)
                dataOut.writeByte(0x02)
                dataOut.flush()

                val authVer = dataIn.readUnsignedByte()
                if (authVer != 0x01) {
                    dataOut.writeByte(0x01)
                    dataOut.writeByte(0xFF)
                    dataOut.flush()
                    return
                }

                val ulen = dataIn.readUnsignedByte()
                val unameBytes = ByteArray(ulen)
                dataIn.readFully(unameBytes)
                val uname = String(unameBytes, Charsets.UTF_8)

                val plen = dataIn.readUnsignedByte()
                val passBytes = ByteArray(plen)
                dataIn.readFully(passBytes)
                val pass = String(passBytes, Charsets.UTF_8)

                val matchesPin = (uname == pinCode || pass == pinCode)
                if (matchesPin) {
                    authenticatedClients.add(clientIp)
                    dataOut.writeByte(0x01)
                    dataOut.writeByte(0x00)
                    dataOut.flush()
                    Log.i(TAG, "Client $clientIp authenticated successfully with PIN")
                } else {
                    dataOut.writeByte(0x01)
                    dataOut.writeByte(0xFF)
                    dataOut.flush()
                    Log.w(TAG, "Client $clientIp failed PIN authentication")
                    return
                }
            } else if (isAlreadyAuth) {
                dataOut.writeByte(0x05)
                dataOut.writeByte(0x00)
                dataOut.flush()
            } else {
                dataOut.writeByte(0x05)
                dataOut.writeByte(0xFF)
                dataOut.flush()
                return
            }

            val reqVer = dataIn.readUnsignedByte()
            if (reqVer != 0x05) return

            val cmd = dataIn.readUnsignedByte()
            val rsv = dataIn.readUnsignedByte()
            val atyp = dataIn.readUnsignedByte()

            val targetHost: String
            val targetAddress: InetAddress?

            when (atyp) {
                0x01 -> {
                    val ipBytes = ByteArray(4)
                    dataIn.readFully(ipBytes)
                    targetAddress = InetAddress.getByAddress(ipBytes)
                    targetHost = targetAddress.hostAddress ?: ""
                }
                0x03 -> {
                    val len = dataIn.readUnsignedByte()
                    val domainBytes = ByteArray(len)
                    dataIn.readFully(domainBytes)
                    targetHost = String(domainBytes, Charsets.US_ASCII)
                    targetAddress = try {
                        InetAddress.getByName(targetHost)
                    } catch (_: Exception) {
                        null
                    }
                }
                0x04 -> {
                    val ipBytes = ByteArray(16)
                    dataIn.readFully(ipBytes)
                    targetAddress = InetAddress.getByAddress(ipBytes)
                    targetHost = targetAddress.hostAddress ?: ""
                }
                else -> {
                    sendReply(dataOut, 0x08)
                    return
                }
            }

            val targetPort = dataIn.readUnsignedShort()

            when (cmd) {
                0x01 -> {
                    val destDescription = if (targetHost.isNotEmpty()) "$targetHost:$targetPort" else "$targetAddress:$targetPort"
                    Log.d(TAG, "Connecting to destination: $destDescription for client $clientIp")

                    // STRATEGY 1 (preferred): chain through the phone's local VPN proxy
                    // (Hiddify/sing-box/...). Loopback traffic bypasses kernel UID routing,
                    // so handshakes with LAN clients always survive an active VPN.
                    // Lazy re-detect handles the VPN app restarting on a new port.
                    if (!UpstreamProxy.isAvailable() && upstreamContext != null) {
                        UpstreamProxy.ensureAvailable(upstreamContext)
                    }
                    val chained = UpstreamProxy.connectThrough(targetHost, targetPort)
                        ?: run {
                            // One retry after re-detect (port may have moved).
                            if (upstreamContext != null && UpstreamProxy.ensureAvailable(upstreamContext)) {
                                UpstreamProxy.connectThrough(targetHost, targetPort)
                            } else null
                        }
                    if (chained == null && UpstreamProxy.isAvailable()) {
                        // Chain was expected to work but failed — port probably stale.
                        UpstreamProxy.invalidate()
                    }

                    if (chained != null) {
                        sendReply(dataOut, 0x00, chained.localAddress, chained.localPort)
                        pipeSockets(clientIn, clientOut, chained, client, destDescription)
                        return
                    }

                    // DNS on the upstream network when a hostname was given (VPN-aware resolve).
                    var resolvedAddress = targetAddress
                    if (resolvedAddress == null && targetHost.isNotEmpty() && upstreamContext != null) {
                        resolvedAddress = VpnStatus.resolveOnUpstream(upstreamContext, targetHost)
                    }
                    if (resolvedAddress == null && targetHost.isNotEmpty()) {
                        resolvedAddress = try { InetAddress.getByName(targetHost) } catch (_: Exception) { null }
                    }

                    // Upstream socket: socketFactory of the bound network (fail-closed).
                    val remoteSocket = try {
                        val created = if (upstreamContext != null) {
                            VpnStatus.createUpstreamTcpSocket(upstreamContext)
                                ?: run { sendReply(dataOut, 0x01); return }
                        } else {
                            Socket().apply { bindSocket?.invoke(this) }
                        }
                        created.apply {
                            soTimeout = AirTunConfig.SOCKET_IDLE_TIMEOUT_MS
                            tcpNoDelay = true
                            if (upstreamContext != null && bindSocket != null) {
                                // legacy hook kept for tests; returns false => fail closed
                                if (!bindSocket.invoke(this)) {
                                    try { close() } catch (_: Exception) {}
                                    sendReply(dataOut, 0x01); return
                                }
                            }
                            if (resolvedAddress != null) {
                                connect(InetSocketAddress(resolvedAddress, targetPort), 10000)
                            } else if (targetHost.isNotEmpty()) {
                                connect(InetSocketAddress(targetHost, targetPort), 10000)
                            } else {
                                sendReply(dataOut, 0x04)
                                return
                            }
                        }
                    } catch (e: Exception) {
                        Log.e(TAG, "Failed connecting to $destDescription: ${e.message}")
                        sendReply(dataOut, 0x05)
                        return
                    }

                    sendReply(dataOut, 0x00, remoteSocket.localAddress, remoteSocket.localPort)
                    pipeSockets(clientIn, clientOut, remoteSocket, client, destDescription)
                }

                0x03 -> {
                    val relay = udpRelay
                    if (relay == null || relay.boundPort <= 0) {
                        sendReply(dataOut, 0x01)
                        return
                    }
                    val bindAddr = (client.localAddress as? Inet4Address)
                        ?: LocalAddress.findAdvertisableIpv4()?.let { try { InetAddress.getByName(it) } catch (_: Exception) { null } }
                        ?: InetAddress.getByName("0.0.0.0")
                    sendReply(dataOut, 0x00, bindAddr, relay.boundPort)

                    try {
                        val dummy = ByteArray(64)
                        while (dataIn.read(dummy) != -1) {
                        }
                    } catch (_: Exception) {}
                }

                else -> {
                    sendReply(dataOut, 0x07)
                }
            }

        } catch (e: SocketTimeoutException) {
            Log.d(TAG, "Client $clientIp socket timeout: ${e.message}")
        } catch (e: IOException) {
            Log.d(TAG, "Client $clientIp IO exception: ${e.message}")
        } finally {
            try { client.close() } catch (_: Exception) {}
            activeConnections.decrementAndGet().coerceAtLeast(0)
            activeClients.computeIfPresent(clientIp) { _, ref ->
                if (ref.decrementAndGet() <= 0) null else ref
            }
            onClientCountChanged(activeClients.size)
        }
    }

    private fun sendReply(
        out: DataOutputStream,
        repCode: Int,
        bndAddr: InetAddress = InetAddress.getByName("0.0.0.0"),
        bndPort: Int = 0,
    ) {
        try {
            out.writeByte(0x05)
            out.writeByte(repCode)
            out.writeByte(0x00)
            if (bndAddr is Inet6Address) {
                out.writeByte(0x04)
                out.write(bndAddr.address)
            } else {
                out.writeByte(0x01)
                out.write(bndAddr.address)
            }
            out.writeShort(bndPort)
            out.flush()
        } catch (_: Exception) {}
    }

    private suspend fun pipeSockets(
        clientIn: InputStream,
        clientOut: OutputStream,
        remote: Socket,
        client: Socket,
        destTag: String,
    ) {
        val remoteIn = remote.getInputStream()
        val remoteOut = remote.getOutputStream()

        val uploadJob = scope.launch {
            val buf = ByteArray(AirTunConfig.BUFFER_SIZE)
            var totalUploaded = 0L
            try {
                while (isActive) {
                    val read = clientIn.read(buf)
                    if (read == -1) break
                    remoteOut.write(buf, 0, read)
                    remoteOut.flush()
                    totalUploaded += read
                    recordTraffic(read.toLong(), 0L)
                }
            } catch (_: Exception) {} finally {
                try { remote.shutdownOutput() } catch (_: Exception) {}
                Log.d(TAG, "Upload stream closed for $destTag ($totalUploaded bytes sent)")
            }
        }

        val downloadJob = scope.launch {
            val buf = ByteArray(AirTunConfig.BUFFER_SIZE)
            var totalDownloaded = 0L
            try {
                while (isActive) {
                    val read = remoteIn.read(buf)
                    if (read == -1) break
                    clientOut.write(buf, 0, read)
                    clientOut.flush()
                    totalDownloaded += read
                    recordTraffic(0L, read.toLong())
                }
            } catch (_: Exception) {} finally {
                try { client.shutdownOutput() } catch (_: Exception) {}
                Log.d(TAG, "Download stream closed for $destTag ($totalDownloaded bytes received)")
            }
        }

        try {
            uploadJob.join()
            downloadJob.join()
        } finally {
            try { remote.close() } catch (_: Exception) {}
            try { client.close() } catch (_: Exception) {}
        }
    }

    private fun recordTraffic(up: Long, down: Long) {
        if (up > 0) totalBytesUp.addAndGet(up)
        if (down > 0) totalBytesDown.addAndGet(down)
        onTraffic(up, down)
    }

    @Synchronized
    fun stop() {
        isRunning = false
        acceptJob?.cancel()
        acceptJob = null
        try {
            serverSocket?.close()
        } catch (_: Exception) {}
        serverSocket = null
        udpRelay?.stop()
        udpRelay = null
        authenticatedClients.clear()
        activeConnections.set(0)
        onClientCountChanged(0)
        scope.cancel()
    }
}
