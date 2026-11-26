# Shop Card design QA

final result: passed

Scope: the selected second mockup's two-list selection workflow, implemented in the existing Avalonia application,
with the same interaction pattern on Buying. Checked November 20, 2026.

## Visual evidence

Reference: selected mockup `exec-f55d8da5-dbfd-4743-9f69-294120f257f2.png` from the ideation set.
Rendered production component screenshots: `artifacts/ui-review/selling.png`, `buying.png`, `selling-narrow.png`.
These are actual Avalonia controls rendered by Skia in the headless UI test host with isolated fixture data;
they are not captures of a running game or the full desktop shell. Wide content viewport: 1240 × 1040.
Narrow content viewport: 700 × 1040. The reference includes the app sidebar; the component captures exclude it.
Source and implementation images were opened together for comparison. The existing sidebar and export renderer
are deliberately preserved rather than replacing them with the mockup's invented artwork and card format.

## Findings resolved

- Preview originally clipped the card vertically. It now fits the complete exported card and offers Maximize.
- Available-row Add buttons were too close to the scrollbar. Added content margin so controls remain clear.
- Added explicit Qty and Price / each column labels and source labels on selected Selling rows.
- Narrow layout stacks the two panes rather than squeezing the selected-item editors.
- Filters only affect available items. Selected rows remain visible while searching, and quantity/price edits
  keep focus and bindings instead of rebuilding the selected list on every search or monitor update.

## Required fidelity surfaces

- Typography: native Fluent typography retained; 18px section headings, readable item names and muted source lines.
  Longer item names truncate within their column. The mockup's larger marketing-scale typography is intentionally
  adapted to the existing desktop app density.
- Spacing: two equally weighted panes, consistent row separators, visible page-level sync/add actions, preview below.
  Five Selling fixture results fit at the wide viewport; longer lists scroll. Short windows scroll the whole page.
- Colors: existing Midnight theme tokens and orange accent retained, including built-in light/high-contrast support.
- Images: bundled game icons used rather than generated approximations. Export preview renders real selected items.
  Fixture lacks a player portrait/map; this is test data, not removal of those supported features.
- Copy: Available items versus On your card is explicit; Buying uses Item database. Manual source is distinct.
  Sync describes visible game content and does not promise access to unopened inventory tabs.

## Interaction verification

Headless pointer and control tests passed for Selling add/remove, source filtering, search without changing selection,
quantity editing, sync without a captured game, narrow layout, Buying add/remove, search, quantity and offer price,
expanding wanted details and adding a stat requirement. Model tests cover shop selection surviving rescans,
manual-source filtering, manual persistence and shop correction persistence/sold detection.

## Limits and follow-up

Live-game OCR sync was not exercised against a newly captured inventory in this pass. Existing game readers are reused;
Sync now invokes inventory, storage and shop readers. Full card designs remain user-configurable, so dense exported
cards may need Maximize to inspect small text. No additional P0/P1/P2 layout findings remain in the reviewed states.

# XP Tracker design QA

final result: passed

Compared the selected first XP Tracker mockup with the rendered Avalonia Party Quests tab at 1440 × 1024 using the same seven sample runs. The comparison crops the mockup window chrome. The 360px capture verifies stacked quest and run panels in a narrow companion viewport. Captures are under `artifacts/ui-review/xp-tracker/`.

The first comparison exposed a saturated selection fill and a run total that was hard to find in the breakdown. The updated rows use a muted accent fill, a visible selection edge, and separators. The breakdown heading now contains the selected run total. The revised capture retains the two-column proportions, app-native typography, theme colors, and color-coded breakdown from the reference. The compact typography and shorter panels fit the host window. The tracker uses native app chrome and contains no additional image assets.

Verified quest and run selection by pointer, date filtering, typed quest names, start and end controls, reward classification, persisted run boundaries, and Live and History tab navigation. Live game capture was outside the fixture.
