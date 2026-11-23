package app.yuyue.speech

import app.yuyue.companion.Companion
import app.yuyue.companion.CompanionError
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext
import uniffi.yuyue_core.YuyueCore

/**
 * 整理文字版, as on Apple: a 文字版's homophones and names put right by the reader's own model
 * (书伴), a couple of thousand characters a request. The core keeps only fixes that read the
 * same as what they replace, so the recording still lines up.
 */
object TextPolish {
    data class Outcome(val asked: Int, val fixed: Int, val failed: Int)

    /** Runs every request of `book` (a 文字版), `progress` told after each: (done, of, fixed so far). */
    suspend fun run(core: YuyueCore, book: String, progress: (Int, Int, Int) -> Unit): Outcome {
        val config = app.yuyue.reader.DramaWork.config ?: throw CompanionError(app.yuyue.t("书伴还没接上大模型：在设置 · 书伴里添加一个服务商"))
        val asks = withContext(Dispatchers.IO) { core.audiobookPolishAsks(book) }
        var fixed = 0
        var failed = 0
        for ((i, ask) in asks.withIndex()) {
            try {
                val answer = Companion.answer(config, ask.system, ask.user, 2048, fast = true, source = app.yuyue.companion.CompanionUsage.Source.Polish)
                fixed += withContext(Dispatchers.IO) { core.audiobookPolishApply(book, ask.id, answer) }.toInt()
            } catch (e: CompanionError) {
                // The key or the money: no use going on.
                throw e
            } catch (e: kotlinx.coroutines.CancellationException) {
                throw e
            } catch (e: Exception) {
                failed++
            }
            progress(i + 1, asks.size, fixed)
        }
        return Outcome(asks.size, fixed, failed)
    }
}

/** 整理错字 as the banner shows it: one run per book at a time; how many it fixed is kept (by
 *  book, on this phone), and [finished] tells an open page to load its words again. */
object TextPolishRuns {
    sealed interface State {
        data class Running(val done: Int, val of: Int, val fixed: Int) : State
        data class Failed(val why: String) : State
    }

    val states = androidx.compose.runtime.mutableStateMapOf<String, State>()
    private val jobs = mutableMapOf<String, kotlinx.coroutines.Job>()
    val finished = kotlinx.coroutines.flow.MutableSharedFlow<String>(extraBufferCapacity = 4)
    private val prefs get() = app.yuyue.YuyueApp.instance.getSharedPreferences("textPolish", android.content.Context.MODE_PRIVATE)
    private val fixedCounts = androidx.compose.runtime.mutableStateMapOf<String, Int>()

    fun fixed(book: String): Int? = fixedCounts[book] ?: prefs.getInt(book, -1).takeIf { it >= 0 }

    fun start(library: app.yuyue.library.Library, book: String) {
        if (jobs[book]?.isActive == true) return
        states[book] = State.Running(0, 0, 0)
        jobs[book] = library.scope.launch {
            try {
                val out = TextPolish.run(library.core, book) { done, of, fixed -> states[book] = State.Running(done, of, fixed) }
                prefs.edit().putInt(book, out.fixed).apply()
                fixedCounts[book] = out.fixed
                states.remove(book)
            } catch (e: kotlinx.coroutines.CancellationException) {
                states.remove(book)
            } catch (e: Exception) {
                states[book] = State.Failed(e.message ?: e.toString())
            }
            finished.tryEmit(book)
        }
    }

    fun stop(book: String) {
        jobs[book]?.cancel()
    }
}
