package com.nubecontrol.monitorsecundario

import android.app.Activity
import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.graphics.Bitmap
import android.graphics.BitmapFactory
import android.graphics.Color
import android.graphics.Paint
import android.graphics.RectF
import android.graphics.Typeface
import android.graphics.drawable.GradientDrawable
import android.os.BatteryManager
import android.os.Build
import android.os.Bundle
import android.text.Spannable
import android.text.SpannableString
import android.text.style.RelativeSizeSpan
import android.text.style.StyleSpan
import android.view.Gravity
import android.view.MotionEvent
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.View
import android.view.ViewGroup
import android.view.WindowManager
import android.view.animation.OvershootInterpolator
import android.widget.FrameLayout
import android.widget.LinearLayout
import android.widget.TextView
import java.util.Locale
import java.util.concurrent.ArrayBlockingQueue
import kotlin.concurrent.thread
import kotlin.math.roundToInt

/**
 * Muestra en pantalla completa la imagen que manda la PC y devuelve los toques.
 * Al conectarse y al girar el teléfono avisa su tamaño para que Windows cree una pantalla de esa resolución.
 */
class MainActivity : Activity(), SurfaceHolder.Callback, StreamServer.Listener {

    companion object {
        const val PORT = 5800
    }

    private lateinit var surfaceView: SurfaceView
    private lateinit var overlay: LinearLayout
    private lateinit var statusText: TextView
    private lateinit var batteryBadge: TextView
    private lateinit var server: StreamServer

    // Solo se conserva el último cuadro recibido: si llegan varios, los viejos se descartan.
    private val frames = ArrayBlockingQueue<ByteArray>(1)
    private var decoder: Thread? = null
    private val drawLock = Any()
    private val paint = Paint(Paint.FILTER_BITMAP_FLAG)

    @Volatile
    private var lastBitmap: Bitmap? = null

    @Volatile
    private var imageRect = RectF()

    private var connected = false
    private var surfaceW = 0
    private var surfaceH = 0
    private var lastSentW = 0
    private var lastSentH = 0

    // Aviso de batería baja: círculo arriba a la derecha que se oculta al tocarlo.
    private var badgeVisible = false
    private var badgeDismissed = false

    private val batteryReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            updateBattery(intent)
        }
    }

    private fun dp(value: Int): Int = (value * resources.displayMetrics.density).roundToInt()

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        if (Build.VERSION.SDK_INT >= 28) {
            val params = window.attributes
            params.layoutInDisplayCutoutMode = WindowManager.LayoutParams.LAYOUT_IN_DISPLAY_CUTOUT_MODE_SHORT_EDGES
            window.attributes = params
        }

        buildUi()
        hideSystemBars()

        decoder = thread(name = "decoder", isDaemon = true) { decodeLoop() }
        server = StreamServer(PORT, this)
        server.start()
    }

    override fun onResume() {
        super.onResume()
        val sticky = registerReceiver(batteryReceiver, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        if (sticky != null) updateBattery(sticky)
    }

    override fun onPause() {
        try { unregisterReceiver(batteryReceiver) } catch (_: IllegalArgumentException) { }
        super.onPause()
    }

    override fun onDestroy() {
        server.stop()
        decoder?.interrupt()
        super.onDestroy()
    }

    override fun onWindowFocusChanged(hasFocus: Boolean) {
        super.onWindowFocusChanged(hasFocus)
        if (hasFocus) hideSystemBars()
    }

    @Suppress("DEPRECATION")
    private fun hideSystemBars() {
        window.decorView.systemUiVisibility = View.SYSTEM_UI_FLAG_LAYOUT_STABLE or
            View.SYSTEM_UI_FLAG_LAYOUT_HIDE_NAVIGATION or
            View.SYSTEM_UI_FLAG_LAYOUT_FULLSCREEN or
            View.SYSTEM_UI_FLAG_HIDE_NAVIGATION or
            View.SYSTEM_UI_FLAG_FULLSCREEN or
            View.SYSTEM_UI_FLAG_IMMERSIVE_STICKY
    }

    // ---------------------------------------------------------------- Interfaz

    private fun buildUi() {
        val root = FrameLayout(this)
        root.setBackgroundColor(Color.BLACK)

        surfaceView = SurfaceView(this)
        surfaceView.holder.addCallback(this)
        surfaceView.setOnTouchListener { _, event ->
            handleTouch(event)
            true
        }
        root.addView(surfaceView, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))

        overlay = LinearLayout(this)
        overlay.orientation = LinearLayout.VERTICAL
        overlay.gravity = Gravity.CENTER
        overlay.setBackgroundColor(Color.rgb(14, 18, 24))
        overlay.setPadding(dp(24), dp(24), dp(24), dp(24))

        val title = TextView(this)
        title.text = "Monitor secundario"
        title.setTextColor(Color.WHITE)
        title.textSize = 28f
        title.typeface = Typeface.DEFAULT_BOLD
        title.gravity = Gravity.CENTER

        val steps = TextView(this)
        steps.text = "1. Conecta este dispositivo a la PC con el cable USB.\n" +
            "2. Activa la depuración USB y acepta el aviso que aparece en el teléfono.\n" +
            "3. En Windows abre «Monitor secundario» e inicia la transmisión."
        steps.setTextColor(Color.rgb(190, 190, 190))
        steps.textSize = 17f
        steps.gravity = Gravity.CENTER
        steps.setPadding(0, dp(20), 0, dp(20))

        statusText = TextView(this)
        statusText.text = "Iniciando…"
        statusText.setTextColor(Color.rgb(240, 180, 60))
        statusText.textSize = 20f
        statusText.typeface = Typeface.DEFAULT_BOLD
        statusText.gravity = Gravity.CENTER

        overlay.addView(title)
        overlay.addView(steps)
        overlay.addView(statusText)
        root.addView(overlay, FrameLayout.LayoutParams(ViewGroup.LayoutParams.MATCH_PARENT, ViewGroup.LayoutParams.MATCH_PARENT))

        // Aviso de batería baja (siempre al final para quedar por encima de todo).
        val badgeBackground = GradientDrawable()
        badgeBackground.shape = GradientDrawable.OVAL
        badgeBackground.setColor(Color.rgb(230, 69, 77))
        badgeBackground.setStroke(dp(3), Color.WHITE)

        batteryBadge = TextView(this)
        batteryBadge.gravity = Gravity.CENTER
        batteryBadge.setTextColor(Color.WHITE)
        batteryBadge.textSize = 11f
        batteryBadge.background = badgeBackground
        batteryBadge.elevation = dp(8).toFloat()
        batteryBadge.visibility = View.GONE
        batteryBadge.setOnClickListener {
            badgeDismissed = true
            hideBadge()
        }
        val size = dp(92)
        val badgeParams = FrameLayout.LayoutParams(size, size, Gravity.TOP or Gravity.END)
        badgeParams.setMargins(dp(16), dp(16), dp(16), dp(16))
        root.addView(batteryBadge, badgeParams)

        setContentView(root)
    }

    // ---------------------------------------------------------------- StreamServer.Listener

    override fun onFrame(data: ByteArray) {
        frames.clear()
        frames.offer(data)
    }

    override fun onState(connected: Boolean, text: String) {
        runOnUiThread {
            this.connected = connected
            statusText.text = text
            overlay.visibility = if (connected) View.GONE else View.VISIBLE
            if (connected) {
                lastSentW = 0 // al conectar siempre se informa el tamaño actual
                lastSentH = 0
                sendSizeIfNeeded()
            }
        }
    }

    // ---------------------------------------------------------------- Imagen

    private fun decodeLoop() {
        try {
            while (true) {
                val data = frames.take()
                val bitmap = BitmapFactory.decodeByteArray(data, 0, data.size) ?: continue
                lastBitmap = bitmap
                draw(bitmap)
            }
        } catch (_: InterruptedException) {
            // La app se cierra.
        }
    }

    private fun draw(bitmap: Bitmap) {
        synchronized(drawLock) {
            val holder = surfaceView.holder
            val canvas = try { holder.lockCanvas() } catch (_: Exception) { null }
            if (canvas == null) return
            try {
                canvas.drawColor(Color.BLACK)
                val w = canvas.width.toFloat()
                val h = canvas.height.toFloat()
                val scale = minOf(w / bitmap.width, h / bitmap.height)
                val dw = bitmap.width * scale
                val dh = bitmap.height * scale
                val rect = RectF((w - dw) / 2, (h - dh) / 2, (w + dw) / 2, (h + dh) / 2)
                imageRect = rect
                canvas.drawBitmap(bitmap, null, rect, paint)
            } finally {
                holder.unlockCanvasAndPost(canvas)
            }
        }
    }

    override fun surfaceCreated(holder: SurfaceHolder) {
    }

    override fun surfaceChanged(holder: SurfaceHolder, format: Int, width: Int, height: Int) {
        surfaceW = width
        surfaceH = height
        lastBitmap?.let { draw(it) }
        sendSizeIfNeeded() // al girar, la PC ajusta la resolución de la pantalla virtual
    }

    override fun surfaceDestroyed(holder: SurfaceHolder) {
    }

    private fun sendSizeIfNeeded() {
        if (!connected || surfaceW <= 0 || surfaceH <= 0) return
        if (surfaceW == lastSentW && surfaceH == lastSentH) return
        lastSentW = surfaceW
        lastSentH = surfaceH

        val density = resources.displayMetrics.density
        val ptW = (surfaceW / density).roundToInt()
        val ptH = (surfaceH / density).roundToInt()
        val idiom = if (resources.configuration.smallestScreenWidthDp >= 600) "pad" else "phone"
        server.send("R $surfaceW $surfaceH $ptW $ptH $idiom\n")
    }

    // ---------------------------------------------------------------- Toques

    private fun handleTouch(event: MotionEvent) {
        val phase = when (event.actionMasked) {
            MotionEvent.ACTION_DOWN -> "D"
            MotionEvent.ACTION_MOVE -> "M"
            MotionEvent.ACTION_UP, MotionEvent.ACTION_CANCEL -> "U"
            else -> return
        }
        val rect = imageRect
        if (rect.width() <= 0f || rect.height() <= 0f) return

        val x = ((event.x - rect.left) / rect.width()).coerceIn(0f, 1f)
        val y = ((event.y - rect.top) / rect.height()).coerceIn(0f, 1f)
        server.send(String.format(Locale.US, "%s %.4f %.4f\n", phase, x, y))
    }

    // ---------------------------------------------------------------- Aviso de batería baja

    private fun updateBattery(intent: Intent) {
        val level = intent.getIntExtra(BatteryManager.EXTRA_LEVEL, -1)
        val scale = intent.getIntExtra(BatteryManager.EXTRA_SCALE, -1)
        if (level < 0 || scale <= 0) return

        val percent = (level * 100f / scale).roundToInt()
        val status = intent.getIntExtra(BatteryManager.EXTRA_STATUS, -1)
        val charging = status == BatteryManager.BATTERY_STATUS_CHARGING || status == BatteryManager.BATTERY_STATUS_FULL

        if (charging || percent > 25) {
            // Cargando o con batería suficiente: se oculta y se rearma para la próxima vez.
            badgeDismissed = false
            hideBadge()
            return
        }

        if (percent <= 20) {
            val percentText = "$percent%"
            val text = SpannableString("$percentText\nbatería baja")
            text.setSpan(RelativeSizeSpan(2.1f), 0, percentText.length, Spannable.SPAN_EXCLUSIVE_EXCLUSIVE)
            text.setSpan(StyleSpan(Typeface.BOLD), 0, text.length, Spannable.SPAN_EXCLUSIVE_EXCLUSIVE)
            batteryBadge.text = text
            if (!badgeDismissed) showBadge()
        }
    }

    private fun showBadge() {
        if (badgeVisible) return
        badgeVisible = true
        batteryBadge.visibility = View.VISIBLE
        batteryBadge.alpha = 0f
        batteryBadge.scaleX = 0.3f
        batteryBadge.scaleY = 0.3f
        batteryBadge.animate()
            .alpha(1f)
            .scaleX(1f)
            .scaleY(1f)
            .setDuration(450)
            .setInterpolator(OvershootInterpolator())
            .start()
    }

    private fun hideBadge() {
        if (!badgeVisible) return
        badgeVisible = false
        batteryBadge.animate()
            .alpha(0f)
            .scaleX(0.5f)
            .scaleY(0.5f)
            .setDuration(250)
            .withEndAction {
                if (!badgeVisible) batteryBadge.visibility = View.GONE
            }
            .start()
    }
}
