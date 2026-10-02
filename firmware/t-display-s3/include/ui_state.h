#pragma once
#include <stdint.h>

enum class UiPage : uint8_t { Home, Facility, Global, CraftMode, AfterRun, Items, Tools, ToolList, ToolDetail, GunMode, GunQuery, Firmware, Brightness, AutoSleep };

struct UiState {
    UiPage page = UiPage::Home;
    uint8_t home = 0, row = 0;
    bool customMode = false;
    bool hourlyMode = false;
    uint8_t itemCount = 0;
    uint8_t tool = 0, toolCount = 0, detailCount = 0;
    bool toolFailed = false;

    bool settingsHint() const { return page == UiPage::Home && home == 4; }
    UiPage homeDestination() const { return home == 5 ? UiPage::Tools : home == 4 ? UiPage::Global : UiPage::Facility; }

    uint8_t count() const
    {
        return page == UiPage::Home ? 6 : page == UiPage::Tools ? 5
            : page == UiPage::Brightness ? 11
            : page == UiPage::AutoSleep ? 7
            : page == UiPage::GunMode || page == UiPage::GunQuery || page == UiPage::Firmware ? 3
            : page == UiPage::ToolList ? toolCount + (tool == 2 ? 4 : tool == 0 || toolFailed ? 2 : 1)
            : page == UiPage::ToolDetail ? (tool == 2 ? detailCount + 2 : 1)
            : page == UiPage::Items ? itemCount + 1
            : page == UiPage::Facility ? (customMode || hourlyMode ? 4 : 3) : page == UiPage::Global ? 11 : 4;
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
