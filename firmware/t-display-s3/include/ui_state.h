#pragma once
#include <stdint.h>

enum class UiPage : uint8_t { Home, Facility, Global, CraftMode, AfterRun };

struct UiState {
    UiPage page = UiPage::Home;
    uint8_t home = 0, row = 0;

    uint8_t count() const
    {
        return page == UiPage::Home ? 5 : page == UiPage::Facility ? 3 : 4;
    }

    void move(int direction)
    {
        uint8_t &selected = page == UiPage::Home ? home : row;
        selected = (selected + count() + direction) % count();
    }

    void open(UiPage next, uint8_t selected = 0) { page = next; row = selected; }

    uint8_t facility() const
    {
        constexpr uint8_t order[] = {3, 0, 1, 2};
        return order[home < 4 ? home : 0];
    }
};
