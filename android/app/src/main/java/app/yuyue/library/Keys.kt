package app.yuyue.library

import android.content.Context
import android.content.SharedPreferences
import androidx.security.crypto.EncryptedSharedPreferences
import androidx.security.crypto.MasterKey

/** API keys, tokens and passwords, as Apple's keychain keeps them: encrypted with a key the
 *  system's keystore holds, only on this device, never in a snapshot or a sync. */
object Keys {
    private lateinit var appContext: Context
    private val prefs: SharedPreferences by lazy {
        val master = MasterKey.Builder(appContext).setKeyScheme(MasterKey.KeyScheme.AES256_GCM).build()
        EncryptedSharedPreferences.create(
            appContext, "yuyue-keys", master,
            EncryptedSharedPreferences.PrefKeyEncryptionScheme.AES256_SIV,
            EncryptedSharedPreferences.PrefValueEncryptionScheme.AES256_GCM,
        )
    }

    fun init(context: Context) {
        appContext = context.applicationContext
    }

    fun read(key: String): String? = prefs.getString(key, null)

    fun write(key: String, value: String) = prefs.edit().putString(key, value).apply()

    fun delete(key: String) = prefs.edit().remove(key).apply()
}
