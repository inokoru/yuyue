package app.yuyue.update

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test
import java.io.File
import java.nio.file.Files
import java.security.MessageDigest

class UpdatesTest {

    private fun sha256Hex(bytes: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(bytes).joinToString("") { "%02x".format(it) }

    @Test
    fun validateCachedApkChecksSizeHashAndSignerAndDeletesOnMismatch() {
        val dir = Files.createTempDirectory("yuyue-updates-cache").toFile()
        try {
            val payload = "fake-apk-bytes-123456".toByteArray()
            val digest = sha256Hex(payload)

            val good = File(dir, "good.apk").apply { writeBytes(payload) }
            assertTrue(UpdateVerifier.validateCachedApk(good, payload.size.toLong(), digest.uppercase()) { true })
            assertTrue(good.exists())

            val badSigner = File(dir, "bad-signer.apk").apply { writeBytes(payload) }
            assertFalse(UpdateVerifier.validateCachedApk(badSigner, payload.size.toLong(), digest) { false })
            assertFalse("Invalid signer cached APK must be deleted", badSigner.exists())

            val badHash = File(dir, "bad-hash.apk").apply { writeBytes(payload) }
            assertFalse(UpdateVerifier.validateCachedApk(badHash, payload.size.toLong(), "0".repeat(64)) { true })
            assertFalse("Corrupt hash cached APK must be deleted", badHash.exists())

            val badSize = File(dir, "bad-size.apk").apply { writeBytes(payload) }
            assertFalse(UpdateVerifier.validateCachedApk(badSize, payload.size.toLong() + 1L, digest) { true })
            assertFalse("Wrong-size cached APK must be deleted", badSize.exists())
        } finally {
            dir.deleteRecursively()
        }
    }

    @Test
    fun validateForInstallReverifiesIntegrityAndSignerAndDeletesBadFile() {
        val dir = Files.createTempDirectory("yuyue-updates-install").toFile()
        try {
            val payload = "apk-content-for-install".toByteArray()
            val digest = sha256Hex(payload)

            val ok = File(dir, "ok.apk").apply { writeBytes(payload) }
            assertEquals(
                UpdateVerifier.InstallCheckResult.Ok,
                UpdateVerifier.validateForInstall(ok, payload.size.toLong(), digest) { true },
            )
            assertTrue(ok.exists())

            val wrongSigner = File(dir, "wrong-signer.apk").apply { writeBytes(payload) }
            assertEquals(
                UpdateVerifier.InstallCheckResult.WrongSigner,
                UpdateVerifier.validateForInstall(wrongSigner, payload.size.toLong(), digest) { false },
            )
            assertFalse(wrongSigner.exists())

            val tampered = File(dir, "tampered.apk").apply { writeBytes("tampered-bytes".toByteArray()) }
            assertEquals(
                UpdateVerifier.InstallCheckResult.MissingOrCorrupt,
                UpdateVerifier.validateForInstall(tampered, payload.size.toLong(), digest) { true },
            )
            assertFalse(tampered.exists())

            // Even without offer metadata, signer check still runs.
            val noOfferBadSigner = File(dir, "no-offer-bad.apk").apply { writeBytes(payload) }
            assertEquals(
                UpdateVerifier.InstallCheckResult.WrongSigner,
                UpdateVerifier.validateForInstall(noOfferBadSigner, null, null) { false },
            )
            assertFalse(noOfferBadSigner.exists())
        } finally {
            dir.deleteRecursively()
        }
    }
}
