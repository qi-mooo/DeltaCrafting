#include "ui_resume.h"
#include <assert.h>
#include <string>
#include <limits>
#include <algorithm>

struct Store {
    struct File {
        Store *store;
        bool writing, exists;
        size_t position = 0;
        explicit operator bool() const { return exists; }
        size_t size() const { return store->saved.size(); }
        bool available() const { return position < size(); }
        int read() { return available() ? store->saved[position++] : -1; }
        size_t write(const uint8_t *byte, size_t) {
            if (store->temporary.size() >= store->failAfter) return 0;
            store->temporary.push_back(*byte); return 1;
        }
        bool finish() { return !store->failFlush; }
        void close() {}
    };
    std::vector<uint8_t> temporary, saved;
    size_t failAfter = std::numeric_limits<size_t>::max();
    bool failFlush = false, failCommit = false, failClear = false;
    File create() { temporary.clear(); return {this, true, true}; }
    File open() { return {this, false, !saved.empty()}; }
    bool commit(bool ok) {
        if (!ok || failCommit) { temporary.clear(); return false; }
        saved = std::move(temporary); return true;
    }
    bool clear() { if (failClear) return false; saved.clear(); temporary.clear(); return true; }
};
using State = UiResume::State<std::string>;

State populated()
{
    State s;
    s.ui.home = 1;
    s.ui.customMode = true;
    s.ui.tool = 2;
    s.ui.itemCount = 254;
    for (unsigned i = 0; i < 254; ++i) {
        s.items.names.push_back("物品 " + std::to_string(i));
        s.items.labels.push_back("5级 " + s.items.names.back());
    }
    s.items.selected = s.items.names[130];
    s.tools.tool = 2;
    s.tools.page = 7;
    s.tools.pages = 50;
    s.tools.next = true;
    s.tools.battlefield = true;
    s.tools.detail = "缓存日期 2026-10-08";
    UiResume::ToolEntry<std::string> entry;
    entry.id = "640001"; entry.title = "测试枪械"; entry.author = "作者";
    entry.code = "不应自动复制的改枪码"; entry.price = "123456";
    entry.password = "1234"; entry.date = "2026-10-08"; entry.category = "步枪";
    entry.lines = {"第一行", "第二行"};
    s.tools.entries.assign(12, entry);
    s.weapons.entries.assign(200, entry);
    s.ui.toolCount = 12;
    s.toolSelected = 8;
    s.gunBattlefield = true;
    s.gunCategory = "步枪"; s.gunWeapon = "测试枪械";
    s.toolLines = {"详情标题", "第1行", "第2行", "第3行"};
    s.ui.detailCount = s.toolLines.size();
    s.imageId = "640001";
    for (unsigned i = 0; i < 96 * 96; ++i) s.image.push_back(i * 37);
    return s;
}

int main()
{
    auto s = populated();
    Store store;
    // Every page and every selectable row, including bottom toolbars and the
    // last item/return row of a 254-item list, retain exact navigation state.
    for (unsigned page = 0; page <= unsigned(UiPage::AutoSleep); ++page) {
        s.ui.page = static_cast<UiPage>(page);
        for (unsigned row = 0; row < s.ui.count(); ++row) {
            s.ui.row = row;
            if (s.ui.page == UiPage::Home) s.ui.home = row;
            assert(UiResume::save(store, s));
            State restored;
            assert(UiResume::restore(store, true, restored));
            assert(restored.ui.page == s.ui.page && restored.ui.row == row && restored.ui.home == s.ui.home);
            assert(restored.ui.customMode && !restored.ui.hourlyMode);
            assert(restored.items.names == s.items.names && restored.items.labels == s.items.labels);
            assert(restored.items.selected == s.items.selected && restored.items.error.empty());
            assert(restored.tools.page == 7 && restored.tools.pages == 50 && restored.tools.next);
            assert(restored.tools.entries[8].code == s.tools.entries[8].code);
            assert(restored.tools.entries[8].lines == s.tools.entries[8].lines);
            assert(restored.tools.entries[8].password == "1234" && restored.tools.entries[8].price == "123456");
            assert(restored.weapons.entries.size() == 200);
            assert(restored.gunBattlefield && restored.gunCategory == s.gunCategory && restored.gunWeapon == s.gunWeapon);
            assert(restored.toolSelected == 8 && restored.toolLines == s.toolLines && restored.image == s.image);
            assert(store.saved.empty());
            assert(!UiResume::restore(store, true, restored)); // consumed once
            assert(restored.ui.page == UiPage::Home);
        }
    }
    // Password/market details and failed lists have different row counts.
    for (uint8_t tool = 0; tool < 4; ++tool) {
        s = populated(); s.ui.tool = s.tools.tool = tool;
        s.ui.page = tool < 3 ? UiPage::ToolDetail : UiPage::ToolList;
        s.ui.row = s.ui.count() - 1;
        assert(UiResume::save(store, s));
        State restored;
        assert(UiResume::restore(store, true, restored));
        assert(restored.ui.count() == s.ui.count() && restored.ui.row == s.ui.row);
        s.ui.page = UiPage::ToolList; s.tools.entries.clear(); s.ui.toolCount = 0;
        s.tools.error = "查询失败"; s.ui.toolFailed = true; s.ui.row = s.ui.count() - 1;
        assert(UiResume::save(store, s) && UiResume::restore(store, true, restored));
        assert(restored.ui.toolFailed && restored.tools.error == "查询失败");
    }
    s = populated(); s.ui.page = UiPage::Items; s.items.names.clear(); s.items.labels.clear();
    s.items.error = "加载失败"; s.ui.itemCount = 1; s.ui.row = 1;
    State restored;
    assert(UiResume::save(store, s) && UiResume::restore(store, true, restored));
    assert(restored.items.error == s.items.error && restored.ui.row == 1);

    s = populated(); s.ui.page = UiPage::Items; s.ui.row = 140;
    assert(UiResume::save(store, s));
    assert(!UiResume::restore(store, false, restored)); // RESET/power-on/OTA
    assert(store.saved.empty());
    assert(!UiResume::restore(store, true, restored));

    assert(UiResume::save(store, s));
    const auto good = store.saved;
    // Corruption, incomplete writes and unknown versions must never produce
    // out-of-range navigation or a partially restored view.
    for (size_t i : {size_t(0), size_t(4), size_t(100), good.size() - 1}) {
        store.saved = good; store.saved[i] ^= 0x80;
        assert(!UiResume::restore(store, true, restored));
        assert(restored.ui.page == UiPage::Home && restored.tools.entries.empty());
    }
    for (size_t size : {size_t(1), size_t(11), good.size() / 2, good.size() - 1}) {
        store.saved.assign(good.begin(), good.begin() + size);
        assert(!UiResume::restore(store, true, restored) && store.saved.empty());
    }
    store.saved = good; store.saved.push_back(1);
    assert(!UiResume::restore(store, true, restored));
    store.saved.resize(UiResume::MaxBytes + 1);
    assert(!UiResume::restore(store, true, restored));
    store.saved = good; store.failClear = true;
    assert(!UiResume::restore(store, true, restored));
    store.failClear = false; store.clear();
    for (size_t offset : {size_t(0), size_t(100), good.size() - 1}) {
        store.failAfter = offset;
        assert(!UiResume::save(store, s) && store.saved.empty());
    }
    store.failAfter = std::numeric_limits<size_t>::max();
    store.failFlush = true;
    assert(!UiResume::save(store, s) && store.saved.empty());
    store.failFlush = false; store.failCommit = true;
    assert(!UiResume::save(store, s) && store.saved.empty());
    store.failCommit = false;
    for (unsigned invalid = 0; invalid < 9; ++invalid) {
        s = populated(); s.ui.page = UiPage::ToolDetail;
        switch (invalid) {
        case 0: s.ui.page = static_cast<UiPage>(255); break;
        case 1: s.ui.home = 6; break;
        case 2: s.ui.row = 254; break;
        case 3: s.ui.tool = 4; break;
        case 4: s.toolSelected = 12; break;
        case 5: s.ui.detailCount = 200; break;
        case 6: s.items.labels.pop_back(); break;
        case 7: s.image.pop_back(); break;
        case 8: s.tools.entries.resize(201); break;
        }
        assert(!UiResume::save(store, s));
    }
    s = populated(); s.tools.entries[0].title.assign(4097, 'a');
    assert(!UiResume::save(store, s));
}
