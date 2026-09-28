#pragma once
#include <stdint.h>

enum class UiPage : uint8_t { Home, Facility, Global, CraftMode, AfterRun, Items };

struct UiState {
    UiPage page = UiPage::Home;
    uint8_t home = 0, row = 0;
    bool customMode = false;
    uint8_t itemCount = 0;

    uint8_t count() const
    {
        return page == UiPage::Home ? 5 : page == UiPage::Items ? itemCount + 1
            : page == UiPage::Facility ? (customMode ? 4 : 3) : page == UiPage::Global ? 7 : 4;
    }

    void move(int direction)
    {
        uint8_t &selected = page == UiPage::Home ? home : row;
        selected = (selected + count() + direction) % count();
    }

    void open(UiPage next, uint8_t selected = 0) { page = next; row = selected; }

    template<class Items, class Text>
    void openItems(const Items &items, const Text &selected)
    {
        itemCount = static_cast<uint8_t>(items.size());
        open(UiPage::Items);
        for (uint8_t i = 0; i < itemCount; ++i)
            if (items[i] == selected) { row = i; break; }
    }

    int initialScroll() const { return row > 1 ? -29 * (row - 1) : 0; }

    uint8_t facility() const
    {
        constexpr uint8_t order[] = {3, 0, 1, 2};
        return order[home < 4 ? home : 0];
    }
};
