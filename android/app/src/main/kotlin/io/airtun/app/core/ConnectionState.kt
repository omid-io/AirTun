package io.airtun.app.core

sealed interface ConnectionState {
    val stateName: String

    data object Idle : ConnectionState {
        override val stateName = "Idle"
    }

    data object Preparing : ConnectionState {
        override val stateName = "Preparing"
    }

    data class Advertising(
        val host: String,
        val port: Int,
        val pinCode: String,
        val deviceName: String,
        val bytesUp: Long = 0,
        val bytesDown: Long = 0,
        val reconnecting: Boolean = false,
    ) : ConnectionState {
        override val stateName = "Advertising"
    }

    data class Connected(
        val host: String,
        val port: Int,
        val pinCode: String,
        val deviceName: String,
        val clientCount: Int = 1,
        val bytesUp: Long = 0,
        val bytesDown: Long = 0,
        val reconnecting: Boolean = false,
    ) : ConnectionState {
        override val stateName = "Connected"
    }

    data class Error(val code: ErrorCode) : ConnectionState {
        override val stateName = "Error"
    }
}

enum class ErrorCode {
    HOTSPOT_OFF,
    HOTSPOT_LOST,
    PORT_IN_USE,
    SERVICE_FAILED,
}

enum class WarningCode {
    NO_VPN_ACTIVE,
    VPN_CAPTURES_LOCAL,
}

object ConnectionRules {
    val states = setOf("Idle", "Preparing", "Advertising", "Connected", "Error")
    const val initial = "Idle"

    val transitions: Map<Pair<String, String>, String> = mapOf(
        ("Idle" to "start") to "Preparing",
        ("Preparing" to "ready") to "Advertising",
        ("Preparing" to "failure") to "Error",
        ("Preparing" to "stop") to "Idle",
        ("Advertising" to "clientConnected") to "Connected",
        ("Advertising" to "stop") to "Idle",
        ("Advertising" to "failure") to "Error",
        ("Connected" to "clientCountChanged") to "Connected",
        ("Connected" to "lastClientDisconnected") to "Advertising",
        ("Connected" to "stop") to "Idle",
        ("Connected" to "failure") to "Error",
        ("Error" to "dismiss") to "Idle",
        ("Error" to "retry") to "Preparing",
    )

    fun canTransition(from: String, event: String): Boolean =
        (from to event) in transitions

    fun target(from: String, event: String): String? = transitions[from to event]
}
