import org.gradle.process.ExecOperations
import java.nio.ByteBuffer
import java.nio.ByteOrder
import java.util.Properties
import java.util.zip.ZipFile

plugins {
    id("com.android.application")
    id("org.jetbrains.kotlin.plugin.compose")
    id("com.android.compose.screenshot")
}

android {
    namespace = "app.yuyue"
    compileSdk = 37
    buildToolsVersion = "36.1.0"

    defaultConfig {
        applicationId = "app.yuyue"
        minSdk = 26
        targetSdk = 36
        versionCode = 9
        versionName = "0.1.7"
        ndk { abiFilters += listOf("arm64-v8a", "armeabi-v7a") }
        // 反馈. android/feedback.properties is not committed (ntfy topic).
        val feedbackFile = rootProject.file("feedback.properties")
        val feedback = Properties()
        if (feedbackFile.exists()) feedbackFile.inputStream().use { feedback.load(it) }
        fun cfg(value: String) = "\"" + value.replace("\\", "\\\\").replace("\"", "\\\"") + "\""
        buildConfigField("String", "FEEDBACK_TOPIC", cfg(feedback.getProperty("topic", "")))
        // 检查更新: the APK from GitHub updates itself; Google Play's (`-Pplay`) leaves it to the store.
        buildConfigField("boolean", "SELF_UPDATE", (!project.hasProperty("play")).toString())
    }

    signingConfigs {
        // The store key lives outside the repo: android/keystore.properties (storeFile, storePassword,
        // keyAlias, keyPassword). Without it a release build is signed with the debug key, to try on a phone.
        val props = rootProject.file("keystore.properties")
        if (props.exists()) {
            val p = Properties().apply { props.inputStream().use { load(it) } }
            create("store") {
                storeFile = rootProject.file(p.getProperty("storeFile"))
                storePassword = p.getProperty("storePassword")
                keyAlias = p.getProperty("keyAlias")
                keyPassword = p.getProperty("keyPassword")
            }
        }
    }

    buildTypes {
        release {
            signingConfig = signingConfigs.findByName("store") ?: signingConfigs.getByName("debug")
            isMinifyEnabled = true
            isShrinkResources = true
            proguardFiles(getDefaultProguardFile("proguard-android-optimize.txt"), "proguard-rules.pro")
        }
    }

    buildFeatures {
        compose = true
        buildConfig = true
    }

    // 界面快照 source set (src/screenshotTest). The flag in gradle.properties turns the feature on.
    experimentalProperties["android.experimental.enableScreenshotTest"] = true

    // The reader bundle (`cd reader && pnpm build`) is served to the WebView as /app/….
    sourceSets["main"].assets.directories.add("../../reader/dist")
    // Wi‑Fi 传书's page for the computer, shared with Apple.
    sourceSets["main"].assets.directories.add("../../shared")
    // 点词's dictionary (scripts/ecdict-android.py).
    sourceSets["main"].assets.directories.add("src/main/assets-dict")

    // Google Play's build (`-Pplay`): no permission to install apps.
    if (project.hasProperty("play")) sourceSets["release"].manifest.srcFile("src/play/AndroidManifest.xml")

    packaging { jniLibs { useLegacyPackaging = false } }

    testOptions {
        unitTests.all { test ->
            System.getProperty("jna.library.path")?.let { test.systemProperty("jna.library.path", it) }
        }
    }
}

kotlin { jvmToolchain(21) }

/** English for the interface, from the catalog the Apple app uses (scripts/l10n-android.py). */
abstract class L10nTask : DefaultTask() {
    @get:InputFile abstract val catalog: RegularFileProperty
    @get:InputFile abstract val extra: RegularFileProperty
    @get:InputFile abstract val script: RegularFileProperty
    @get:OutputDirectory abstract val outputDir: DirectoryProperty
    @get:javax.inject.Inject abstract val exec: ExecOperations

    @TaskAction
    fun run() {
        exec.exec { commandLine("python3", script.get().asFile.path, outputDir.get().file("l10n/en.json").asFile.path) }
    }
}

val l10n = tasks.register<L10nTask>("l10n") {
    catalog.set(file("../../apple/Yuyue/Localizable.xcstrings"))
    extra.set(file("src/main/l10n/extra-en.json"))
    script.set(file("../../scripts/l10n-android.py"))
    outputDir.set(layout.buildDirectory.dir("generated/l10n"))
}

androidComponents {
    onVariants { variant -> variant.sources.assets?.addGeneratedSourceDirectory(l10n, L10nTask::outputDir) }
}

// Fail fast if `reader/dist` hasn't been built yet, instead of shipping an APK whose WebView renders blank.
val checkReaderDist = tasks.register("checkReaderDist") {
    val js = file("../../reader/dist/reader.js")
    val css = file("../../reader/dist/reader.css")
    doLast {
        if (!js.isFile || !css.isFile) {
            throw GradleException("Missing reader/dist/reader.js or reader.css. Run `cd reader && pnpm install && pnpm build` before building Android.")
        }
    }
}

tasks.configureEach {
    if (name.startsWith("merge") && name.endsWith("Assets")) {
        dependsOn(checkReaderDist)
    }
}

// Guard against R8 9.4.x emitting invalid 8-bit register operands (VerifyError at startup)
// when a single DEX method crosses 255 registers (e.g. a huge Compose lambda).
val verifyReleaseDexRegisters = tasks.register("verifyReleaseDexRegisters") {
    val apkProvider = layout.buildDirectory.file("outputs/apk/release/app-release.apk")
    doLast {
        val apk = apkProvider.get().asFile
        if (!apk.isFile) return@doLast
        val offenders = mutableListOf<String>()
        var maxRegs = 0
        var maxMethod = ""
        ZipFile(apk).use { zip ->
            val dexEntries = zip.entries().asSequence()
                .filter { it.name.matches(Regex("""classes\d*\.dex""")) }
                .toList()
            for (entry in dexEntries) {
                val bytes = zip.getInputStream(entry).readBytes()
                val buf = ByteBuffer.wrap(bytes).order(ByteOrder.LITTLE_ENDIAN)
                if (bytes.size < 112 || String(bytes, 0, 4) != "dex\n") continue
                val stringIdsOff = buf.getInt(0x3C)
                val typeIdsOff = buf.getInt(0x44)
                val methodIdsOff = buf.getInt(0x5C)
                val classDefsSize = buf.getInt(0x60)
                val classDefsOff = buf.getInt(0x64)

                fun readUleb128(pos: IntArray): Int {
                    var result = 0
                    var shift = 0
                    while (true) {
                        val b = bytes[pos[0]++].toInt() and 0xFF
                        result = result or ((b and 0x7F) shl shift)
                        if ((b and 0x80) == 0) break
                        shift += 7
                    }
                    return result
                }

                fun readString(idx: Int): String {
                    val dataOff = buf.getInt(stringIdsOff + idx * 4)
                    val p = intArrayOf(dataOff)
                    readUleb128(p) // UTF-16 length
                    var end = p[0]
                    while (end < bytes.size && bytes[end] != 0.toByte()) end++
                    return String(bytes, p[0], end - p[0], Charsets.UTF_8)
                }

                fun methodName(methodIdx: Int): String {
                    val off = methodIdsOff + methodIdx * 8
                    val clsIdx = buf.getShort(off).toInt() and 0xFFFF
                    val nameIdx = buf.getInt(off + 4)
                    val clsDesc = readString(buf.getInt(typeIdsOff + clsIdx * 4))
                    val name = readString(nameIdx)
                    return "$clsDesc->$name"
                }

                for (i in 0 until classDefsSize) {
                    val classDataOff = buf.getInt(classDefsOff + i * 32 + 24)
                    if (classDataOff == 0) continue
                    val p = intArrayOf(classDataOff)
                    val staticFields = readUleb128(p)
                    val instanceFields = readUleb128(p)
                    val directMethods = readUleb128(p)
                    val virtualMethods = readUleb128(p)
                    repeat(staticFields + instanceFields) {
                        readUleb128(p)
                        readUleb128(p)
                    }
                    for (count in intArrayOf(directMethods, virtualMethods)) {
                        var methodIdx = 0
                        repeat(count) {
                            methodIdx += readUleb128(p)
                            readUleb128(p) // access_flags
                            val codeOff = readUleb128(p)
                            if (codeOff != 0) {
                                val regs = buf.getShort(codeOff).toInt() and 0xFFFF
                                if (regs > maxRegs) {
                                    maxRegs = regs
                                    maxMethod = "${entry.name}: ${methodName(methodIdx)}"
                                }
                                if (regs >= 256) {
                                    offenders += "${entry.name}: ${methodName(methodIdx)} (registers=$regs)"
                                }
                            }
                        }
                    }
                }
            }
        }
        logger.lifecycle("verifyReleaseDexRegisters: max registers = $maxRegs ($maxMethod)")
        if (offenders.isNotEmpty()) {
            throw GradleException(
                "Release DEX contains method(s) with >= 256 registers, which triggers an R8 8-bit register truncation VerifyError on ART:\n" +
                    offenders.joinToString("\n") { "  - $it" } +
                    "\nSplit the offending composable/function into smaller helpers so every method stays under 256 registers."
            )
        }
    }
}

tasks.configureEach {
    if (name == "assembleRelease") {
        finalizedBy(verifyReleaseDexRegisters)
    }
}

dependencies {
    val bom = platform("androidx.compose:compose-bom:2026.09.00")
    implementation(bom)
    implementation("androidx.compose.ui:ui")
    implementation("androidx.compose.foundation:foundation")
    implementation("androidx.compose.material3:material3")
    implementation("androidx.compose.material:material-icons-extended")
    // 底栏 blurs the page under it (Android 12+; below that, a translucent paper colour).
    implementation("dev.chrisbanes.haze:haze:2.0.1")
    implementation("dev.chrisbanes.haze:haze-blur:2.0.1")
    // 液态玻璃底栏 (Kyant0/AndroidLiquidGlass, Apache-2.0): real-time lens refraction + chromatic dispersion.
    implementation("io.github.kyant0:backdrop:2.0.1")
    implementation("androidx.compose.ui:ui-tooling-preview")
    debugImplementation("androidx.compose.ui:ui-tooling")
    implementation("androidx.activity:activity-compose:1.11.0")
    implementation("androidx.lifecycle:lifecycle-viewmodel-compose:2.9.4")
    implementation("androidx.navigation:navigation-compose:2.9.5")
    implementation("androidx.webkit:webkit:1.14.0")
    implementation("androidx.media3:media3-session:1.8.0")
    implementation("androidx.media3:media3-exoplayer:1.8.0")
    // 强制对齐: Meta's MMS alignment model, run on the device (the model itself is downloaded).
    implementation("com.microsoft.onnxruntime:onnxruntime-android:1.30.0")
    implementation("androidx.documentfile:documentfile:1.1.0")
    implementation("androidx.exifinterface:exifinterface:1.4.1")
    implementation("androidx.security:security-crypto:1.1.0")
    implementation("org.jetbrains.kotlinx:kotlinx-coroutines-android:1.10.2")
    // WebDAV needs PROPFIND and MKCOL, which the platform client refuses.
    implementation("com.squareup.okhttp3:okhttp:4.12.0")
    // 同步 · Google Drive: the phone's Google account allows the app's hidden folder.
    implementation("com.google.android.gms:play-services-auth:21.3.0")
    // UniFFI's Kotlin bindings call the core through JNA.
    implementation("net.java.dev.jna:jna:5.18.1@aar")
    // Anonymous usage analytics (AirTraffic / PostHog; pinned to 3.77.1 for /batch compatibility).
    implementation("com.posthog:posthog-android:3.77.1")
    testImplementation("junit:junit:4.13.2")
    // Desktop JNA JAR for host JVM conformance tests against libyuyue_core.
    testImplementation("net.java.dev.jna:jna:5.18.1")
    // @PreviewTest renders on the host JVM (layoutlib), not on a device. Previews live in
    // src/screenshotTest and call the app's composables with fixture data.
    screenshotTestImplementation(bom)
    screenshotTestImplementation("androidx.compose.ui:ui-tooling")
    screenshotTestImplementation("androidx.compose.ui:ui-tooling-preview")
    screenshotTestImplementation("com.android.tools.screenshot:screenshot-validation-api:0.0.1-alpha16")
}
