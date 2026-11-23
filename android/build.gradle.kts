plugins {
    id("com.android.application") version "9.4.1" apply false
    id("org.jetbrains.kotlin.plugin.compose") version "2.4.20" apply false
    // 界面快照: Compose Preview Screenshot Testing (the experimental plugin). AGP 9.4 is below
    // the 9.5 test-suite integration, so this is the plugin the docs still describe for that line.
    id("com.android.compose.screenshot") version "0.0.1-alpha16" apply false
}
