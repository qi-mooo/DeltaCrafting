#pragma once

namespace UiListLayout {
// Long lists keep their focus in the middle, including the first/last row.
// A signed offset allows empty space at either end instead of moving the focus.
constexpr int scroll(int selected, int rows, int rowHeight, int viewport)
{
    return rows * rowHeight > viewport ? selected * rowHeight - (viewport - rowHeight) / 2 : 0;
}
}
