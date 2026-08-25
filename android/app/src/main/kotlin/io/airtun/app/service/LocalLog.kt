package io.airtun.app.service

import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update
import java.text.SimpleDateFormat
import java.util.Date
import java.util.Locale

object LocalLog {
    data class Entry(val timestamp: Long, val message: String) {
        val formattedTime: String
            get() = SimpleDateFormat("HH:mm:ss", Locale.US).format(Date(timestamp))
    }

    private const val MAX_ENTRIES = 200
    private val _entries = MutableStateFlow<List<Entry>>(emptyList())
    val entries: StateFlow<List<Entry>> = _entries.asStateFlow()

    fun add(message: String) {
        android.util.Log.i("AirTun", message)
        val entry = Entry(System.currentTimeMillis(), message)
        _entries.update { list ->
            (list + entry).takeLast(MAX_ENTRIES)
        }
    }

    fun clear() {
        _entries.value = emptyList()
    }
}
