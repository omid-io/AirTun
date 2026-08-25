package io.airtun.app.service

import io.airtun.app.core.ConnectionRules
import io.airtun.app.core.ConnectionState
import io.airtun.app.core.WarningCode
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

object ConnectionRepository {

    private val _state = MutableStateFlow<ConnectionState>(ConnectionState.Idle)
    val state: StateFlow<ConnectionState> = _state.asStateFlow()

    private val _warnings = MutableStateFlow<Set<WarningCode>>(emptySet())
    val warnings: StateFlow<Set<WarningCode>> = _warnings.asStateFlow()

    fun setWarning(code: WarningCode, active: Boolean) {
        _warnings.update { if (active) it + code else it - code }
    }

    fun clearWarnings() {
        _warnings.value = emptySet()
    }

    @Synchronized
    fun updateTraffic(bytesUp: Long, bytesDown: Long, clientCount: Int) {
        _state.update { current ->
            when (current) {
                is ConnectionState.Connected -> current.copy(
                    bytesUp = bytesUp,
                    bytesDown = bytesDown,
                    clientCount = clientCount,
                )
                is ConnectionState.Advertising -> {
                    if (clientCount > 0) {
                        ConnectionState.Connected(
                            host = current.host,
                            port = current.port,
                            pinCode = current.pinCode,
                            deviceName = current.deviceName,
                            clientCount = clientCount,
                            bytesUp = bytesUp,
                            bytesDown = bytesDown,
                            reconnecting = current.reconnecting,
                        )
                    } else {
                        current.copy(bytesUp = bytesUp, bytesDown = bytesDown)
                    }
                }
                else -> current
            }
        }
    }

    @Synchronized
    fun setClientCount(clientCount: Int) {
        _state.update { current ->
            when (current) {
                is ConnectionState.Connected -> {
                    if (clientCount == 0) {
                        ConnectionState.Advertising(
                            host = current.host,
                            port = current.port,
                            pinCode = current.pinCode,
                            deviceName = current.deviceName,
                            bytesUp = current.bytesUp,
                            bytesDown = current.bytesDown,
                            reconnecting = current.reconnecting,
                        )
                    } else {
                        current.copy(clientCount = clientCount)
                    }
                }
                is ConnectionState.Advertising -> {
                    if (clientCount > 0) {
                        ConnectionState.Connected(
                            host = current.host,
                            port = current.port,
                            pinCode = current.pinCode,
                            deviceName = current.deviceName,
                            clientCount = clientCount,
                            bytesUp = current.bytesUp,
                            bytesDown = current.bytesDown,
                            reconnecting = current.reconnecting,
                        )
                    } else current
                }
                else -> current
            }
        }
    }

    @Synchronized
    fun annotateReconnecting(active: Boolean) {
        _state.update { current ->
            when (current) {
                is ConnectionState.Connected -> current.copy(reconnecting = active)
                is ConnectionState.Advertising -> current.copy(reconnecting = active)
                else -> current
            }
        }
    }

    @Synchronized
    fun dispatch(event: String, build: (ConnectionState) -> ConnectionState): Boolean {
        val current = _state.value
        val target = ConnectionRules.target(current.stateName, event) ?: return false
        val next = build(current)
        check(next.stateName == target) {
            "Event '$event' from '${current.stateName}' must produce '$target', got '${next.stateName}'"
        }
        _state.value = next
        return true
    }
}
