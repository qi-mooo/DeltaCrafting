#pragma once
#include <stdint.h>
#include <stddef.h>
#include <vector>
#include <utility>
#include "ui_state.h"

// The snapshot contains display/navigation data only: never commands, settings
// writes, live API status, credentials or an in-progress firmware transaction.
namespace UiResume {
constexpr size_t MaxBytes = 256 * 1024;
template<class Text> struct ToolEntry {
    Text id, title, code, password, date, price, author, category;
    std::vector<Text> lines;
};
template<class Text> struct ToolData {
    std::vector<ToolEntry<Text>> entries;
    Text detail, error;
    uint8_t tool = 0;
    uint16_t page = 1, pages = 1;
    bool next = false, battlefield = false;
};
template<class Text> struct ItemList {
    std::vector<Text> names, labels;
    Text selected, error;
};
template<class Text> struct State {
    State() { weapons.tool = 3; }
    UiState ui;
    ToolData<Text> tools, weapons;
    ItemList<Text> items;
    bool gunBattlefield = false;
    Text gunCategory, gunWeapon, imageId, imageError;
    uint8_t toolSelected = 0;
    std::vector<Text> toolLines;
    std::vector<uint16_t> image;
};

// Streaming encoding keeps large lists/images off the task stack and avoids a
// second JSON document. Bounds apply before allocation; the checksum covers all
// fields. The format is versioned independently of the firmware release.
template<class Stream, bool Reading> class Codec {
public:
    explicit Codec(Stream &stream) : stream_(stream) {}
    bool number(uint32_t &value, size_t bytes)
    {
        if (Reading) value = 0;
        for (size_t i = 0; i < bytes; ++i) {
            uint8_t byte = value >> (8 * i);
            if (!transfer(byte, true)) return false;
            if (Reading) value |= uint32_t(byte) << (8 * i);
        }
        return true;
    }
    template<class Number> bool field(Number &value, size_t bytes = 1)
    {
        uint32_t raw = static_cast<uint32_t>(value);
        if (!number(raw, bytes)) return false;
        if (Reading) value = static_cast<Number>(raw);
        return true;
    }
    bool flag(bool &value)
    {
        uint32_t raw = value;
        if (!number(raw, 1) || raw > 1) return false;
        value = raw != 0;
        return true;
    }
    template<class Text> bool text(Text &value)
    {
        uint32_t length = value.length();
        if (!number(length, 2) || length > 4096) return false;
        if (Reading) { value = ""; value.reserve(length); }
        for (uint32_t i = 0; i < length; ++i) {
            uint8_t byte = Reading ? 0 : value[i];
            if (!transfer(byte, true) || byte == 0) return false;
            if (Reading) value += char(byte);
        }
        return value.length() == length;
    }
    template<class Values, class Visit> bool list(Values &values, size_t maximum, Visit visit)
    {
        uint32_t count = values.size();
        if (!number(count, 2) || count > maximum) return false;
        if (Reading) values.resize(count);
        for (auto &value : values) if (!visit(value)) return false;
        return true;
    }
    template<class Text> bool texts(std::vector<Text> &values, size_t maximum)
    { return list(values, maximum, [this](Text &value) { return text(value); }); }
    template<class Text> bool tools(ToolData<Text> &data)
    {
        return field(data.tool) && data.tool <= 3 && field(data.page, 2) && data.page > 0
            && field(data.pages, 2) && flag(data.next) && flag(data.battlefield)
            && text(data.detail) && text(data.error)
            && list(data.entries, 200, [this](ToolEntry<Text> &e) {
                return text(e.id) && text(e.title) && text(e.code) && text(e.password)
                    && text(e.date) && text(e.price) && text(e.author) && text(e.category)
                    && texts(e.lines, 253);
            });
    }
    bool finish()
    {
        uint32_t expected = checksum_, stored = 0;
        for (unsigned i = 0; i < 4; ++i) {
            uint8_t byte = expected >> (8 * i);
            if (!transfer(byte, false)) return false;
            stored |= uint32_t(byte) << (8 * i);
        }
        return stored == expected && (!Reading || !stream_.available());
    }
private:
    bool transfer(uint8_t &byte, bool hash)
    {
        if (++bytes_ > MaxBytes) return false;
        if (Reading) {
            int value = stream_.read();
            if (value < 0) return false;
            byte = value;
        } else if (stream_.write(&byte, 1) != 1) return false;
        if (hash) checksum_ = (checksum_ ^ byte) * 16777619u;
        return true;
    }
    Stream &stream_;
    size_t bytes_ = 0;
    uint32_t checksum_ = 2166136261u;
};

template<class Text> bool valid(const State<Text> &s)
{
    const auto &u = s.ui;
    if (uint8_t(u.page) > uint8_t(UiPage::AutoSleep) || u.home >= 6 || u.tool > 3
        || (u.customMode && u.hourlyMode) || s.items.names.size() != s.items.labels.size()
        || s.weapons.tool != 3 || (!s.image.empty() && s.image.size() != 96 * 96)) return false;
    if (u.page == UiPage::Items && u.itemCount != (s.items.error.length() ? 1 : s.items.names.size())) return false;
    if (u.page == UiPage::ToolList || u.page == UiPage::ToolDetail) {
        if (u.tool != s.tools.tool || u.toolCount != s.tools.entries.size()
            || u.toolFailed != (s.tools.error.length() != 0)) return false;
        if (u.page == UiPage::ToolDetail && (u.tool == 3 || s.toolSelected >= s.tools.entries.size()
            || u.detailCount != s.toolLines.size())) return false;
    }
    return u.count() != 0 && (u.page == UiPage::Home || u.row < u.count());
}

template<class Stream, bool Reading, class Text> bool encode(Stream &stream, State<Text> &s)
{
    Codec<Stream, Reading> c(stream);
    uint32_t magic = 0x31525344; // DSR1
    auto &u = s.ui;
    return c.number(magic, 4) && magic == 0x31525344
        && c.field(u.page) && c.field(u.home) && c.field(u.row)
        && c.flag(u.customMode) && c.flag(u.hourlyMode) && c.field(u.itemCount)
        && c.field(u.tool) && c.field(u.toolCount) && c.field(u.detailCount) && c.flag(u.toolFailed)
        && c.texts(s.items.names, 254) && c.texts(s.items.labels, 254)
        && c.text(s.items.selected) && c.text(s.items.error)
        && c.tools(s.tools) && c.tools(s.weapons)
        && c.flag(s.gunBattlefield) && c.text(s.gunCategory) && c.text(s.gunWeapon)
        && c.field(s.toolSelected) && c.texts(s.toolLines, 253)
        && c.text(s.imageId) && c.text(s.imageError)
        && c.list(s.image, 96 * 96, [&c](uint16_t &pixel) { return c.field(pixel, 2); })
        && valid(s) && c.finish();
}

// Store supplies atomic commit and one-shot consumption. Even a rejected or
// RESET-discarded snapshot is removed, so it cannot reappear on a later wake.
template<class Store, class Text> bool save(Store &store, State<Text> &state)
{
    if (!valid(state)) return false;
    auto file = store.create();
    bool ok = file && encode<decltype(file), false>(file, state) && file.finish();
    file.close();
    return store.commit(ok);
}
template<class Store, class Text> bool restore(Store &store, bool deepWake, State<Text> &state)
{
    if (!deepWake) { store.clear(); return false; }
    auto file = store.open();
    bool ok = file && file.size() <= MaxBytes && encode<decltype(file), true>(file, state);
    file.close();
    if (!store.clear()) ok = false;
    if (!ok) state = State<Text>{};
    return ok;
}
}
