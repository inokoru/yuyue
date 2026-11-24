package app.yuyue.reader

import android.view.InputDevice
import android.view.KeyEvent
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Test

/** Xiaomi Focus Pen Pro gestures (#7875) reuse gamepad keycodes 194-197. */
class XiaomiPenKeysTest {
    @Test
    fun `slide up and down are pen gestures only while the engine is registered`() {
        val pen = InputDevice.SOURCE_KEYBOARD
        assertNull(XiaomiPenKeys.nameFor(KeyEvent.KEYCODE_BUTTON_9, pen, false))
        assertEquals("XiaomiSlideUp", XiaomiPenKeys.nameFor(KeyEvent.KEYCODE_BUTTON_9, pen, true))
        assertEquals("XiaomiSlideDown", XiaomiPenKeys.nameFor(KeyEvent.KEYCODE_BUTTON_10, pen, true))
    }

    @Test
    fun `squeeze, double tap and other gamepad buttons are never pen gestures`() {
        val pen = InputDevice.SOURCE_KEYBOARD
        assertNull(XiaomiPenKeys.nameFor(KeyEvent.KEYCODE_BUTTON_7, pen, true))
        assertNull(XiaomiPenKeys.nameFor(KeyEvent.KEYCODE_BUTTON_8, pen, true))
        assertNull(XiaomiPenKeys.nameFor(KeyEvent.KEYCODE_BUTTON_R1, pen, true))
    }

    @Test
    fun `a gamepad or joystick never sends pen gestures`() {
        assertNull(XiaomiPenKeys.nameFor(KeyEvent.KEYCODE_BUTTON_9, InputDevice.SOURCE_GAMEPAD, true))
        assertNull(XiaomiPenKeys.nameFor(KeyEvent.KEYCODE_BUTTON_10, InputDevice.SOURCE_JOYSTICK, true))
    }

    @Test
    fun `slide up turns back and slide down turns forward`() {
        assertEquals(false, XiaomiPenKeys.forward(KeyEvent.KEYCODE_BUTTON_9))
        assertEquals(true, XiaomiPenKeys.forward(KeyEvent.KEYCODE_BUTTON_10))
        assertNull(XiaomiPenKeys.forward(KeyEvent.KEYCODE_BUTTON_7))
    }
}
