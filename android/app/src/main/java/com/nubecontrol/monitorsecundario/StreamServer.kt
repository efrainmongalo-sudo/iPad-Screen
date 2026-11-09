package com.nubecontrol.monitorsecundario

import java.io.BufferedInputStream
import java.io.DataInputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.ServerSocket
import java.net.Socket
import java.util.concurrent.Executors
import kotlin.concurrent.thread

/**
 * Servidor TCP que escucha en el teléfono. La PC se conecta por el cable USB (adb forward).
 * Protocolo:
 *  - PC -> dispositivo: [4 bytes longitud big-endian][datos JPEG] repetido.
 *  - dispositivo -> PC: líneas de texto "D x y", "M x y" o "U x y" (x, y entre 0 y 1) para los toques,
 *    "A" por cada cuadro recibido y "R pxAncho pxAlto ptAncho ptAlto phone|pad" con el tamaño de la pantalla.
 */
class StreamServer(private val port: Int, private val listener: Listener) {

    interface Listener {
        /** Llega en un hilo de fondo. */
        fun onFrame(data: ByteArray)

        /** Llega en un hilo de fondo. */
        fun onState(connected: Boolean, text: String)
    }

    @Volatile
    private var running = false

    @Volatile
    private var serverSocket: ServerSocket? = null

    @Volatile
    private var current: Socket? = null

    private val writer = Executors.newSingleThreadExecutor()

    fun start() {
        if (running) return
        running = true
        thread(name = "stream-accept", isDaemon = true) { acceptLoop() }
    }

    fun stop() {
        running = false
        try { current?.close() } catch (_: IOException) { }
        try { serverSocket?.close() } catch (_: IOException) { }
        writer.shutdownNow()
    }

    fun send(text: String) {
        val socket = current ?: return
        try {
            writer.execute {
                try {
                    val out = socket.getOutputStream()
                    out.write(text.toByteArray(Charsets.US_ASCII))
                    out.flush()
                } catch (_: IOException) {
                    // La conexión se cerró: el hilo de lectura se encarga de avisar.
                }
            }
        } catch (_: java.util.concurrent.RejectedExecutionException) {
            // Ya se detuvo el servidor.
        }
    }

    private fun acceptLoop() {
        while (running) {
            try {
                val server = ServerSocket()
                server.reuseAddress = true
                server.bind(InetSocketAddress(port))
                serverSocket = server
                listener.onState(false, "Esperando a la PC…")

                while (running) {
                    val client = server.accept()
                    client.tcpNoDelay = true
                    try { current?.close() } catch (_: IOException) { } // una sola PC a la vez
                    current = client
                    thread(name = "stream-read", isDaemon = true) { serve(client) }
                }
            } catch (e: IOException) {
                if (!running) return
                listener.onState(false, "No se pudo abrir el puerto $port: ${e.message}")
                try { Thread.sleep(1000) } catch (_: InterruptedException) { return }
            } finally {
                try { serverSocket?.close() } catch (_: IOException) { }
                serverSocket = null
            }
        }
    }

    private fun serve(client: Socket) {
        listener.onState(true, "Conectado")
        try {
            val input = DataInputStream(BufferedInputStream(client.getInputStream(), 64 * 1024))
            while (running) {
                val length = input.readInt()
                if (length <= 0 || length > 16 * 1024 * 1024) break
                val data = ByteArray(length)
                input.readFully(data)
                send("A\n") // confirma el cuadro para que la PC no acumule retraso
                listener.onFrame(data)
            }
        } catch (_: IOException) {
            // Se desconectó el cable o la PC cerró la conexión.
        } finally {
            try { client.close() } catch (_: IOException) { }
            if (current === client) {
                current = null
                if (running) listener.onState(false, "Esperando a la PC…")
            }
        }
    }
}
