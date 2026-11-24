package app.yuyue.reader

import android.view.InputDevice
import android.view.KeyEvent

/**
 * Xiaomi Focus Pen Pro gestures (#7875) arrive as KEYCODE_BUTTON_7..10 only
 * while HyperOS's pen engine has registered the app. There is no separate
 * "learn mode" in 语阅: these reuse the same page-turn handler as volume keys.
 * Only slide up/down are used — squeeze and double tap are left out.
 */
object XiaomiPenKeys {
    private val gestures = mapOf(
        KeyEvent.KEYCODE_BUTTON_9 to "XiaomiSlideUp",
        KeyEvent.KEYCODE_BUTTON_10 to "XiaomiSlideDown",
    )

    /** The native key name, or null when this event isn't a registered pen gesture. */
    fun nameFor(keyCode: Int, source: Int, registered: Boolean): String? {
        if (!registered) return null
        val fromController = source and InputDevice.SOURCE_GAMEPAD == InputDevice.SOURCE_GAMEPAD ||
            source and InputDevice.SOURCE_JOYSTICK == InputDevice.SOURCE_JOYSTICK
        if (fromController) return null
        return gestures[keyCode]
    }

    /** True for a forward page turn (slide down), false for back (slide up), null otherwise. */
    fun forward(keyCode: Int): Boolean? = when (keyCode) {
        KeyEvent.KEYCODE_BUTTON_9 -> false
        KeyEvent.KEYCODE_BUTTON_10 -> true
        else -> null
    }
}
