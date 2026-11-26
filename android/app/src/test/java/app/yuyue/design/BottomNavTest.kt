package app.yuyue.design

import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

class BottomNavTest {

    @Test
    fun parseNormalizesEmptyMissingMeDuplicatesAndBounds() {
        assertEquals(BottomNav.DEFAULT, BottomNav.parse(null))
        assertEquals(BottomNav.DEFAULT, BottomNav.parse(""))
        assertEquals(BottomNav.DEFAULT, BottomNav.parse("unknown,bogus"))

        // Missing Me gets appended automatically
        assertEquals(
            listOf(BottomTab.Shelf, BottomTab.Podcasts, BottomTab.Me),
            BottomNav.parse("shelf,podcasts,shelf"),
        )

        // Single Me gets fallback tab prepended to keep minimum 2 tabs
        assertEquals(
            listOf(BottomTab.Shelf, BottomTab.Me),
            BottomNav.parse("me"),
        )

        // More than MAX_TABS truncates while guaranteeing Me stays
        assertEquals(
            listOf(BottomTab.Shelf, BottomTab.Notes, BottomTab.Podcasts, BottomTab.Audio, BottomTab.Me),
            BottomNav.parse("shelf,notes,podcasts,audio,progress,search,me"),
        )
    }

    @Test
    fun removeRespectsPinnedMeAndMinimumTwoTabs() {
        val minimal = listOf(BottomTab.Shelf, BottomTab.Me)
        assertFalse(BottomNav.canRemove(minimal, BottomTab.Me))
        assertFalse(BottomNav.canRemove(minimal, BottomTab.Shelf))
        assertEquals(minimal, BottomNav.remove(minimal, BottomTab.Shelf))

        val three = listOf(BottomTab.Shelf, BottomTab.Notes, BottomTab.Me)
        assertTrue(BottomNav.canRemove(three, BottomTab.Notes))
        assertFalse(BottomNav.canRemove(three, BottomTab.Me))
        assertEquals(minimal, BottomNav.remove(three, BottomTab.Notes))
    }

    @Test
    fun addInsertsBeforeTrailingSearchAndRespectsMax() {
        val base = listOf(BottomTab.Shelf, BottomTab.Me, BottomTab.Search)
        assertEquals(
            listOf(BottomTab.Shelf, BottomTab.Me, BottomTab.Podcasts, BottomTab.Search),
            BottomNav.add(base, BottomTab.Podcasts),
        )

        // Explicit insertion index places entry at exact slot
        assertEquals(
            listOf(BottomTab.Shelf, BottomTab.Audio, BottomTab.Me, BottomTab.Search),
            BottomNav.add(base, BottomTab.Audio, atIndex = 1),
        )

        val full = listOf(BottomTab.Shelf, BottomTab.Notes, BottomTab.Podcasts, BottomTab.Me, BottomTab.Search)
        assertEquals(full, BottomNav.add(full, BottomTab.Audio))
    }

    @Test
    fun replaceSwapsInPlaceAndExchangesActiveSlots() {
        val base = listOf(BottomTab.Shelf, BottomTab.Notes, BottomTab.Me, BottomTab.Search)
        // Replace hidden entry in-place (e.g. swap Notes -> Podcasts)
        assertEquals(
            listOf(BottomTab.Shelf, BottomTab.Podcasts, BottomTab.Me, BottomTab.Search),
            BottomNav.replace(base, BottomTab.Notes, BottomTab.Podcasts),
        )
        // Replace Podcasts -> Shelf when Shelf was hidden
        val noShelf = listOf(BottomTab.Podcasts, BottomTab.Notes, BottomTab.Me)
        assertEquals(
            listOf(BottomTab.Shelf, BottomTab.Notes, BottomTab.Me),
            BottomNav.replace(noShelf, BottomTab.Podcasts, BottomTab.Shelf),
        )
        // Swapping two active entries exchanges their positions (even involving Me)
        assertEquals(
            listOf(BottomTab.Me, BottomTab.Notes, BottomTab.Shelf, BottomTab.Search),
            BottomNav.replace(base, BottomTab.Shelf, BottomTab.Me),
        )
        // Cannot replace pinned Me with a hidden entry
        assertEquals(base, BottomNav.replace(base, BottomTab.Me, BottomTab.Audio))
    }

    @Test
    fun moveAndDetachedSearchOrbRules() {
        val base = listOf(BottomTab.Shelf, BottomTab.Notes, BottomTab.Me, BottomTab.Search)
        assertTrue(BottomNav.usesDetachedSearchOrb(base))

        val movedSearch = BottomNav.move(base, 3, 1)
        assertEquals(listOf(BottomTab.Shelf, BottomTab.Search, BottomTab.Notes, BottomTab.Me), movedSearch)
        assertFalse(BottomNav.usesDetachedSearchOrb(movedSearch))

        // Only 2 tabs with Search last stays combined in one balanced pill
        assertFalse(BottomNav.usesDetachedSearchOrb(listOf(BottomTab.Me, BottomTab.Search)))
    }

    @Test
    fun shelfFoldMutualExclusionAndAutoMinimalSwitcher() {
        val defaultCfg = BottomNav.config(BottomNav.DEFAULT_RAW, BottomNav.DEFAULT_HIDDEN_SHELF_RAW)
        assertEquals(listOf(BottomTab.Progress, BottomTab.Podcasts, BottomTab.Audio), BottomNav.foldedInShelf(defaultCfg))
        assertEquals(emptyList<BottomTab>(), BottomNav.hiddenEntries(defaultCfg))
        assertTrue(BottomNav.shelfHasSwitcher(defaultCfg, hasCollections = false))
        assertEquals("default", BottomNav.activePresetId(defaultCfg))

        // Promoting 播客 to bottom bar removes it automatically from 书架内收起
        val withPodcast = BottomNav.insert(defaultCfg, BottomTab.Podcasts, atIndex = 1)
        assertEquals(listOf(BottomTab.Shelf, BottomTab.Podcasts, BottomTab.Notes, BottomTab.Me, BottomTab.Search), withPodcast.tabs)
        assertEquals(listOf(BottomTab.Progress, BottomTab.Audio), BottomNav.foldedInShelf(withPodcast))

        // Folding 播客 back returns it to 书架内收起
        val foldedBack = BottomNav.foldIntoShelf(withPodcast, BottomTab.Podcasts)
        assertEquals(defaultCfg.tabs, foldedBack.tabs)
        assertEquals(listOf(BottomTab.Progress, BottomTab.Podcasts, BottomTab.Audio), BottomNav.foldedInShelf(foldedBack))

        // 极简 preset hides all shelf sub-places -> switcher hides automatically when 0 collections, stays on when collections exist
        val minimal = BottomNav.applyPreset(BottomNav.presets.first { it.id == "minimal" })
        assertEquals(listOf(BottomTab.Shelf, BottomTab.Me), minimal.tabs)
        assertEquals(emptyList<BottomTab>(), BottomNav.foldedInShelf(minimal))
        assertEquals(listOf(BottomTab.Notes, BottomTab.Podcasts, BottomTab.Audio, BottomTab.Progress, BottomTab.Search), BottomNav.hiddenEntries(minimal))
        assertFalse(BottomNav.shelfHasSwitcher(minimal, hasCollections = false))
        assertTrue(BottomNav.shelfHasSwitcher(minimal, hasCollections = true))
        assertEquals("minimal", BottomNav.activePresetId(minimal))

        // 播客 preset promotes 播客 and automatically folds 笔记 safely into 书架内收起
        val podPreset = BottomNav.applyPreset(BottomNav.presets.first { it.id == "podcast" })
        assertEquals(listOf(BottomTab.Progress, BottomTab.Audio, BottomTab.Notes), BottomNav.foldedInShelf(podPreset))
        assertEquals(emptyList<BottomTab>(), BottomNav.hiddenEntries(podPreset))

        // Folding 笔记 directly from default bottom bar moves it cleanly into 书架内收起
        val notesFolded = BottomNav.foldIntoShelf(defaultCfg, BottomTab.Notes)
        assertEquals(listOf(BottomTab.Shelf, BottomTab.Me, BottomTab.Search), notesFolded.tabs)
        assertEquals(listOf(BottomTab.Progress, BottomTab.Podcasts, BottomTab.Audio, BottomTab.Notes), BottomNav.foldedInShelf(notesFolded))

        // Replacing a promoted shelfSub on the bar returns it cleanly to 书架内收起 when 书架 is present
        val replacedBack = BottomNav.replace(podPreset, BottomTab.Podcasts, BottomTab.Notes)
        assertEquals(defaultCfg.tabs, replacedBack.tabs)
        assertTrue(BottomTab.Podcasts in BottomNav.foldedInShelf(replacedBack))

        // Hiding 书架 disables foldedInShelf and moves shelf sub-places to hidden recovery
        val noShelf = BottomNav.hide(defaultCfg, BottomTab.Shelf)
        assertFalse(BottomTab.Shelf in noShelf.tabs)
        assertEquals(emptyList<BottomTab>(), BottomNav.foldedInShelf(noShelf))
        assertFalse(BottomNav.shelfHasSwitcher(noShelf, hasCollections = true))
        assertNull(BottomNav.activePresetId(noShelf))
    }

    @Test
    fun parseAndSerializeHiddenShelfSubsFiltersNonShelfSubIds() {
        val parsed = BottomNav.parseHiddenShelfSubs("audio,notes,me,progress,search,unknown,audio")
        assertEquals(setOf(BottomTab.Progress, BottomTab.Audio, BottomTab.Notes), parsed)
        assertEquals("progress,audio,notes", BottomNav.serializeHiddenShelfSubs(parsed))
    }

    @Test
    fun podcastsUsesMacOsRadioIconAndFilledVariant() {
        assertEquals(Icons.radio, BottomTab.Podcasts.icon)
        assertEquals(Icons.radioFill, BottomTab.Podcasts.iconOn)
        assertEquals(Icons.radio, app.yuyue.shelf.PlaceIcons.podcasts)
        assertEquals(Icons.radioFill, app.yuyue.shelf.PlaceIcons.filled(Icons.radio))
    }
}
