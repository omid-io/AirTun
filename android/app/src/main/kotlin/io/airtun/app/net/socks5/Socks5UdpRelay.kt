package io.airtun.app.net.socks5

import android.content.Context
import android.util.Log
import io.airtun.app.core.AirTunConfig
import io.airtun.app.net.VpnStatus
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
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.Inet6Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketAddress
import java.nio.ByteBuffer
import java.util.concurrent.ConcurrentHashMap
import java.util.concurrent.atomic.AtomicLong

class Socks5UdpRelay(
    private val upstreamContext: Context? = null,
    private val bindDatagramSocket: ((DatagramSocket) -> Boolean)? = null,
    private val onTraffic: (bytesUp: Long, bytesDown: Long) -> Unit,
) {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var relaySocket: DatagramSocket? = null
    private var relayJob: Job? = null

    var boundPort: Int = -1
        private set

    private val activeClients = ConcurrentHashMap<SocketAddress, Long>()

    fun start(): Int {
        // Listener stays FREE of the VPN. When a phone VPN is active it gets bound to the
        // LAN (hotspot) network instead, so UDP replies reach LAN clients directly.
        val socket = DatagramSocket(0)
        if (upstreamContext != null) {
            if (!VpnStatus.bindListenerToLocalLan(upstreamContext, socket)) {
                Log.e("AirTun-UdpRelay", "UDP listener not bound to LAN; continuing unbound")
            }
        }
        relaySocket = socket
        boundPort = socket.localPort

        relayJob = scope.launch {
            val buffer = ByteArray(AirTunConfig.BUFFER_SIZE)
            while (isActive) {
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(packet)
                } catch (_: IOException) {
                    break
                }

                val senderAddress = packet.socketAddress
                activeClients[senderAddress] = System.currentTimeMillis()

                val dataLength = packet.length
                if (dataLength < 10) continue

                val byteBuffer = ByteBuffer.wrap(packet.data, packet.offset, dataLength)
                val rsv = byteBuffer.short
                val frag = byteBuffer.get()
                if (frag.toInt() != 0) {
                    continue
                }

                val atyp = byteBuffer.get().toInt() and 0xFF
                val targetAddress: InetAddress?
                val targetPort: Int

                when (atyp) {
                    0x01 -> {
                        val ipBytes = ByteArray(4)
                        byteBuffer.get(ipBytes)
                        targetAddress = InetAddress.getByAddress(ipBytes)
                        targetPort = byteBuffer.short.toInt() and 0xFFFF
                    }
                    0x03 -> {
                        val len = byteBuffer.get().toInt() and 0xFF
                        val domainBytes = ByteArray(len)
                        byteBuffer.get(domainBytes)
                        val domain = String(domainBytes, Charsets.US_ASCII)
                        targetAddress = try {
                            InetAddress.getByName(domain)
                        } catch (_: Exception) {
                            null
                        }
                        targetPort = byteBuffer.short.toInt() and 0xFFFF
                    }
                    0x04 -> {
                        val ipBytes = ByteArray(16)
                        byteBuffer.get(ipBytes)
                        targetAddress = InetAddress.getByAddress(ipBytes)
                        targetPort = byteBuffer.short.toInt() and 0xFFFF
                    }
                    else -> continue
                }

                if (targetAddress == null) continue

                val payloadLength = byteBuffer.remaining()
                val payload = ByteArray(payloadLength)
                byteBuffer.get(payload)

                onTraffic(payloadLength.toLong(), 0L)

                scope.launch {
                    forwardAndListen(
                        targetAddress = targetAddress,
                        targetPort = targetPort,
                        payload = payload,
                        clientEndpoint = senderAddress,
                    )
                }
            }
        }
        return boundPort
    }

    private fun forwardAndListen(
        targetAddress: InetAddress,
        targetPort: Int,
        payload: ByteArray,
        clientEndpoint: SocketAddress,
    ) {
        try {
            // STRATEGY 1: when the loopback VPN proxy is available, send UDP through a
            // TCP-tunneled DNS-like path is complex; instead route UDP payloads over the
            // chained SOCKS5 server's UDP ASSOCIATE. Simpler robust path: wrap payload
            // into a SOCKS5 datagram and send via the upstream's relay socket.
            if (UpstreamProxy.isAvailable()) {
                forwardViaUpstream(targetAddress, targetPort, payload, clientEndpoint)
                return
            }

            // STRATEGY 2 (no VPN proxy): direct datagram on the bound network.
            DatagramSocket().use { remoteSocket ->
                var bound = true
                if (upstreamContext != null && bindDatagramSocket != null) {
                    bound = bindDatagramSocket.invoke(remoteSocket)
                }
                if (!bound) {
                    android.util.Log.e("AirTun-UdpRelay", "Upstream bind failed; dropping UDP to $targetAddress:$targetPort")
                    return
                }
                remoteSocket.soTimeout = 10000
                val outgoingPacket = DatagramPacket(payload, payload.size, targetAddress, targetPort)
                remoteSocket.send(outgoingPacket)

                val responseBuffer = ByteArray(AirTunConfig.BUFFER_SIZE)
                val incomingPacket = DatagramPacket(responseBuffer, responseBuffer.size)
                remoteSocket.receive(incomingPacket)

                val respLength = incomingPacket.length
                onTraffic(0L, respLength.toLong())

                val respAddress = incomingPacket.address
                val respPort = incomingPacket.port

                val headerBuffer = ByteBuffer.allocate(32)
                headerBuffer.putShort(0.toShort())
                headerBuffer.put(0.toByte())
                if (respAddress is Inet4Address) {
                    headerBuffer.put(0x01.toByte())
                    headerBuffer.put(respAddress.address)
                } else if (respAddress is Inet6Address) {
                    headerBuffer.put(0x04.toByte())
                    headerBuffer.put(respAddress.address)
                }
                headerBuffer.putShort(respPort.toShort())

                val headerBytes = ByteArray(headerBuffer.position())
                headerBuffer.flip()
                headerBuffer.get(headerBytes)

                val fullResponse = ByteArray(headerBytes.size + respLength)
                System.arraycopy(headerBytes, 0, fullResponse, 0, headerBytes.size)
                System.arraycopy(incomingPacket.data, incomingPacket.offset, fullResponse, headerBytes.size, respLength)

                relaySocket?.send(DatagramPacket(fullResponse, fullResponse.size, clientEndpoint))
            }
        } catch (_: Exception) {
        }
    }

    /**
     * Sends a UDP payload through the loopback VPN proxy using SOCKS5 UDP ASSOCIATE.
     * Opens one control TCP connection, requests UDP ASSOCIATE, then relays the wrapped
     * datagram over the returned relay endpoint and waits for the response.
     */
    private fun forwardViaUpstream(
        targetAddress: InetAddress,
        targetPort: Int,
        payload: ByteArray,
        clientEndpoint: SocketAddress,
    ) {
        var control: java.net.Socket? = null
        try {
            control = java.net.Socket()
            control.tcpNoDelay = true
            control.soTimeout = 10000
            control.connect(java.net.InetSocketAddress("127.0.0.1", UpstreamProxy.detectedPort), 3000)

            val out = DataOutputStream(control.getOutputStream())
            val inp = DataInputStream(control.getInputStream())

            // greeting
            out.write(byteArrayOf(0x05, 0x01, 0x00)); out.flush()
            val ver = inp.readUnsignedByte(); val method = inp.readUnsignedByte()
            if (ver != 0x05 || method != 0x00) return

            // UDP ASSOCIATE — BND.ADDR/PORT 0 means "you choose"
            out.writeByte(0x05); out.writeByte(0x03); out.writeByte(0x00)
            out.writeByte(0x01); out.write(ByteArray(4)); out.writeShort(0)
            out.flush()

            val rVer = inp.readUnsignedByte()
            val rRep = inp.readUnsignedByte()
            inp.readUnsignedByte()                       // rsv
            val atyp = inp.readUnsignedByte()
            val relayAddr: java.net.InetAddress = when (atyp) {
                0x01 -> { val b = ByteArray(4); inp.readFully(b); java.net.InetAddress.getByAddress(b) }
                0x04 -> { val b = ByteArray(16); inp.readFully(b); java.net.InetAddress.getByAddress(b) }
                else -> { val n = inp.readUnsignedByte(); val b = ByteArray(n); inp.readFully(b); targetAddress }
            }
            val relayPort = inp.readUnsignedShort()

            if (rVer != 0x05 || rRep != 0x00) {
                android.util.Log.e("AirTun-UdpRelay", "Upstream UDP ASSOCIATE failed rep=$rRep")
                return
            }

            // Relay the datagram (loopback socket — no kernel routing involved)
            DatagramSocket().use { udp ->
                udp.soTimeout = 10000
                val header = ByteBuffer.allocate(4 + 16 + 2)
                header.putShort(0); header.put(0.toByte())
                if (targetAddress is Inet4Address) { header.put(0x01); header.put(targetAddress.address) }
                else { header.put(0x04); header.put(targetAddress.address) }
                header.putShort(targetPort.toShort())
                val hb = ByteArray(header.position()); header.flip(); header.get(hb)

                val packetBytes = hb + payload
                udp.send(DatagramPacket(packetBytes, packetBytes.size, relayAddr, relayPort))

                onTraffic(payload.size.toLong(), 0L)

                val respBuf = ByteArray(AirTunConfig.BUFFER_SIZE)
                val respPkt = DatagramPacket(respBuf, respBuf.size)
                udp.receive(respPkt)

                // strip upstream SOCKS5 header from response
                val bb = ByteBuffer.wrap(respBuf, 0, respPkt.length)
                bb.short; bb.get()                        // rsv + frag
                val raTyp = bb.get().toInt() and 0xFF
                when (raTyp) { 0x01 -> bb.position(bb.position() + 4); 0x04 -> bb.position(bb.position() + 16); else -> bb.position(bb.position() + 1 + bb.get(bb.position()).toInt()) }
                val rPort = bb.short.toInt() and 0xFFFF
                val respPayloadLen = bb.remaining()
                val respPayload = ByteArray(respPayloadLen); bb.get(respPayload)

                onTraffic(0L, respPayloadLen.toLong())

                // wrap back into OUR reply format for the LAN client
                val hdr = ByteBuffer.allocate(32)
                hdr.putShort(0); hdr.put(0.toByte())
                hdr.put(0x01.toByte()); hdr.put(targetAddress.address)
                hdr.putShort(rPort.toShort())
                val hArr = ByteArray(hdr.position()); hdr.flip(); hdr.get(hArr)
                val full = hArr + respPayload

                relaySocket?.send(DatagramPacket(full, full.size, clientEndpoint))
            }
        } catch (e: Exception) {
            android.util.Log.e("AirTun-UdpRelay", "forwardViaUpstream(${targetAddress.hostAddress}:$targetPort): ${e.message}")
        } finally {
            try { control?.close() } catch (_: Exception) {}
        }
    }

    fun stop() {
        relayJob?.cancel()
        relayJob = null
        try {
            relaySocket?.close()
        } catch (_: Exception) {}
        relaySocket = null
        scope.cancel()
    }
}
