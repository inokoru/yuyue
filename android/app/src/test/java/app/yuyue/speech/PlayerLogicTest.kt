package app.yuyue.speech

import android.media.AudioManager
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Test

class PlayerLogicTest {

    @Test
    fun sleepTakesPriorityOverSingleSentenceLoop() {
        val done = Player.Spot(section = 1, sentence = 4)
        val next = Player.Spot(section = 1, sentence = 5)

        assertEquals(
            Player.PlayedAction.SleepAt(done),
            Player.nextActionAfterPlayed(done, next, Player.Sleep.Sentence, loop = true, loopRange = null, nowMs = 1_000L),
        )
        assertEquals(
            Player.PlayedAction.SleepAt(done),
            Player.nextActionAfterPlayed(done, next, Player.Sleep.Until(at = 1_000L, minutes = 15), loop = true, loopRange = null, nowMs = 1_000L),
        )
        assertEquals(
            Player.PlayedAction.LoopTo(done),
            Player.nextActionAfterPlayed(done, next, Player.Sleep.Until(at = 5_000L, minutes = 15), loop = true, loopRange = null, nowMs = 1_000L),
        )
    }

    @Test
    fun sleepTakesPriorityOverRangeLoop() {
        val range = Triple(0, 2, 5)
        val endOfRange = Player.Spot(section = 0, sentence = 5)
        val afterRange = Player.Spot(section = 0, sentence = 6)
        val midRange = Player.Spot(section = 0, sentence = 3)
        val midNext = Player.Spot(section = 0, sentence = 4)

        assertEquals(
            Player.PlayedAction.SleepAt(Player.Spot(0, 2)),
            Player.nextActionAfterPlayed(endOfRange, afterRange, Player.Sleep.Until(at = 1_000L, minutes = 15), loop = true, loopRange = range, nowMs = 1_500L),
        )
        assertEquals(
            Player.PlayedAction.SleepAt(midNext),
            Player.nextActionAfterPlayed(midRange, midNext, Player.Sleep.Until(at = 1_000L, minutes = 15), loop = true, loopRange = range, nowMs = 1_500L),
        )
        assertEquals(
            Player.PlayedAction.LoopTo(Player.Spot(0, 2)),
            Player.nextActionAfterPlayed(endOfRange, afterRange, Player.Sleep.Off, loop = true, loopRange = range, nowMs = 1_500L),
        )
        assertEquals(
            Player.PlayedAction.Advance,
            Player.nextActionAfterPlayed(midRange, midNext, Player.Sleep.Off, loop = true, loopRange = range, nowMs = 1_500L),
        )
    }

    @Test
    fun sleepChapterStopsAtChapterEndWithOrWithoutLoop() {
        val lastInChapter = Player.Spot(section = 0, sentence = 9)
        val firstInNext = Player.Spot(section = 1, sentence = 0)

        assertEquals(
            Player.PlayedAction.SleepAt(lastInChapter),
            Player.nextActionAfterPlayed(lastInChapter, firstInNext, Player.Sleep.Chapter, loop = true, loopRange = null, nowMs = 0L),
        )
        assertEquals(
            Player.PlayedAction.SleepAt(firstInNext),
            Player.nextActionAfterPlayed(lastInChapter, firstInNext, Player.Sleep.Chapter, loop = false, loopRange = null, nowMs = 0L),
        )
    }

    @Test
    fun focusLossTransientOnlyArmsAutoResumeWhenActivelyPlayingOrLoading() {
        val fromPlaying = Player.focusTransition(AudioManager.AUDIOFOCUS_LOSS_TRANSIENT, Player.State.Playing, pausedForFocus = false)
        assertEquals(Player.FocusAction.PauseKeepFocus, fromPlaying.action)
        assertTrue(fromPlaying.pausedForFocus)

        val fromLoading = Player.focusTransition(AudioManager.AUDIOFOCUS_LOSS_TRANSIENT, Player.State.Loading, pausedForFocus = false)
        assertEquals(Player.FocusAction.PauseKeepFocus, fromLoading.action)
        assertTrue(fromLoading.pausedForFocus)

        val fromManualPause = Player.focusTransition(AudioManager.AUDIOFOCUS_LOSS_TRANSIENT, Player.State.Paused, pausedForFocus = false)
        assertEquals(Player.FocusAction.None, fromManualPause.action)
        assertFalse(fromManualPause.pausedForFocus)

        val fromStopped = Player.focusTransition(AudioManager.AUDIOFOCUS_LOSS_TRANSIENT, Player.State.Stopped, pausedForFocus = false)
        assertEquals(Player.FocusAction.None, fromStopped.action)
        assertFalse(fromStopped.pausedForFocus)
    }

    @Test
    fun focusGainNeverResumesAfterManualPauseOrPermanentLoss() {
        val resumeAfterTransient = Player.focusTransition(AudioManager.AUDIOFOCUS_GAIN, Player.State.Paused, pausedForFocus = true)
        assertEquals(Player.FocusAction.Resume, resumeAfterTransient.action)
        assertFalse(resumeAfterTransient.pausedForFocus)

        val ignoreAfterManualPause = Player.focusTransition(AudioManager.AUDIOFOCUS_GAIN, Player.State.Paused, pausedForFocus = false)
        assertEquals(Player.FocusAction.None, ignoreAfterManualPause.action)
        assertFalse(ignoreAfterManualPause.pausedForFocus)

        val permanentLossWhileTransient = Player.focusTransition(AudioManager.AUDIOFOCUS_LOSS, Player.State.Paused, pausedForFocus = true)
        assertEquals(Player.FocusAction.None, permanentLossWhileTransient.action)
        assertFalse(permanentLossWhileTransient.pausedForFocus)
    }

    @Test
    fun fadeVolumeIsFullWhenNotFadingAndClampedDuringFadeout() {
        val expired = Player.Sleep.Until(at = 10_000L, minutes = 15)
        assertEquals(1f, Player.fadeVolume(expired, fading = false, nowMs = 15_000L), 0.0001f)
        assertEquals(0.5f, Player.fadeVolume(Player.Sleep.Until(at = 20_000L, minutes = 15), fading = true, nowMs = 10_000L), 0.0001f)
        assertEquals(0f, Player.fadeVolume(expired, fading = true, nowMs = 15_000L), 0.0001f)
        assertEquals(1f, Player.fadeVolume(Player.Sleep.Off, fading = true, nowMs = 15_000L), 0.0001f)
    }
}
