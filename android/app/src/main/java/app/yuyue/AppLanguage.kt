package app.yuyue

import android.app.Activity
import android.app.LocaleManager
import android.content.Context
import android.content.res.Resources
import android.os.Build
import android.os.LocaleList
import app.yuyue.design.Prefs

/**
 * 通用 › 语言: 跟随系统, 简体中文 or English, for 语阅 alone. From Android 13 it is the system's own
 * app language (the same as 设置 › 应用 › 语阅 › 语言); before that, a setting of ours. Chosen, the
 * screen is made again in the new words at once, with the ink line passing over it.
 */
object AppLanguage {
    const val SYSTEM = "system"
    const val ZH = "zh"
    const val EN = "en"
    private const val KEY = "app.language"

    /** The screen was just made again for a new language: sweep it, and stay on 通用. */
    var sweepPending = false
    var reopenGeneral = false

    fun chosen(context: Context): String {
        if (Build.VERSION.SDK_INT >= 33) {
            val locales = context.getSystemService(LocaleManager::class.java).applicationLocales
            if (locales.isEmpty) return SYSTEM
            return if (locales[0].language == "zh") ZH else EN
        }
        return Prefs.string(KEY, SYSTEM)
    }

    /** Whether the system itself speaks Chinese, under any choice of ours. */
    fun systemChinese(context: Context): Boolean {
        val system = if (Build.VERSION.SDK_INT >= 33) context.getSystemService(LocaleManager::class.java).systemLocales
                     else Resources.getSystem().configuration.locales
        return (system[0]?.language ?: "zh") == "zh"
    }

    /** The language the words are in: what was chosen, or the system's. */
    fun chinese(context: Context): Boolean = when (if (Build.VERSION.SDK_INT >= 33) SYSTEM else Prefs.string(KEY, SYSTEM)) {
        ZH -> true
        EN -> false
        // From 13 the process's own locale is already the app language.
        else -> (LocaleList.getDefault()[0]?.language ?: "zh") == "zh"
    }

    fun choose(activity: Activity, value: String) {
        if (value == chosen(activity)) return
        val before = L10n.chinese
        if (Build.VERSION.SDK_INT >= 33) {
            val tags = when (value) { ZH -> "zh-CN"; EN -> "en"; else -> "" }
            Prefs.set(KEY, value)
            activity.getSystemService(LocaleManager::class.java).applicationLocales = LocaleList.forLanguageTags(tags)
            // The system makes the screen again when the words change; when they don't, there is
            // nothing to redo.
            val after = if (value == SYSTEM) systemChinese(activity) else value == ZH
            if (after != before) { sweepPending = true; reopenGeneral = true }
        } else {
            Prefs.set(KEY, value)
            val after = if (value == SYSTEM) systemChinese(activity) else value == ZH
            if (after != before) {
                sweepPending = true
                reopenGeneral = true
                activity.recreate()
            }
        }
    }
}
