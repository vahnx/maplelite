# Companion panels, CSV and market storage

Open **Open companion panel** in the main sidebar. Choose Chat History, Loot or XP Tracker.
Open another panel to keep chat and a tracker visible together. **Side / bottom** switches
between a narrow side panel and a wide chat panel. **Follow game** follows a windowed game's
position, tries the left side when the right is full, and reports when there is insufficient
space. Resize or move the game yourself, or turn following off and place the panel manually.
These are separate native windows, not an embedded game client. Fullscreen docking is not supported.
Views reuse running plugins; closing a panel stops its display timer, not logging.

## Shop Card editing

Selling and Buying each have independent search/type filters for **On your card**. Selling also
filters by source. These filters only change the list view. Click Name, Qty or Price to cycle
ascending, descending and original list order; list sorting does not reorder the exported card.

**Edit card** beside the preview opens live name, quantity, base price, stats and slots fields.
They edit the same saved entries as the lists. Up/down arrows select **Manual** card order,
which persists across restarts. Manual order displays individual entries without combining
stacks, category grouping or column packing; Selling still precedes Buying. Choose another
Order in Design to resume automatic ordering and the usual stack/group/packing settings.
The preview uses the existing price multipliers; the editor changes the base price.

**Sync** asks before updating visible Inventory, Storage and own-shop items. It does not wipe
the card or clear manual additions, unseen entries or Buying selections. It updates scanned
quantities and can remove scanned items confirmed gone from visible slots. Retained entries
keep recorded stats and custom prices; removed entries may need their details read again.
Open each game tab and scroll to capture everything. Cancel makes no changes.

## Shop Card CSV

The current Shop Card builder has **Export CSV…** and **Import CSV…** beside its image export
controls under the preview. CSV contains the complete item list, including Included/Buying
flags, item ID/name, quantity, price, stats, slots, stack count and item price percentage.
Prices are the current list prices before category/card-wide multipliers. An automatic
reference price becomes a fixed imported price. Images, layout, seller information and global
design/discount settings are not in this item-list format.

Import validates the whole file before changing items. It merges entries as manual cards;
existing inventory/shop cards remain intact. Reimporting the same exported keys updates earlier
CSV imports. Keep the exported header and keys. Duplicate keys and malformed rows are rejected.
Quoted commas, quotes and multiline stats round-trip. Formula-like text is escaped for spreadsheet use.

## SQLite price archive (first migration stage)

**Market database** browses the latest 500 archived rows, optionally filtered by item ID, and
opens the database folder. The file is `<MapleLite data>/market/prices.sqlite`.
Turn on **Settings → Sidebar → Show Market database** to show it below **Shop Card**.
The implementation uses Microsoft.Data.Sqlite:
https://learn.microsoft.com/en-us/dotnet/standard/data/sqlite/

The archive runs on a background worker every 30 seconds and after plugin shutdown. It indexes
changed `plugins/salecard/fm-prices.json` and `site-prices.json` files transactionally, preserves
the originals, and deduplicates identical observations. SQLite WAL allows external readers.
Use the `observations` table, `fm_prices` view, and `website_prices` view in a SQLite database app.
Columns retain source, observation time, unit asking price, bundle, seller, stats, remaining slots,
last sale, three-sale average, ten-sale median and typical price.

This is an archive, not yet the authoritative pricing store. Shop Card still reads its existing
price stores and owns the FM/browser readers. External database edits do not feed back into cards.
The old files contain no server information: archived rows explicitly use `unknown`, rather than
assuming the current card's server. A site's retained metric can have an older source age than its
snapshot timestamp. Intermediate website snapshots overwritten between archive cycles cannot be recovered.
Chat, loot and XP journals retain their existing storage formats.

## Next stage: independent pricing plugin and crowdsourcing

Move FM/browser readers and pricing queries behind a shared Market service, then expose its own
plugin page. Sale Card should consume that service and keep only user offers and card settings.
Move existing observations with a transactional, idempotent migration; retain backups and verify
counts before making SQLite authoritative. New observations need an explicit world/server,
source-specific metric timestamp, exact bundle price, OCR quality and equipment profile.

For a hosted service, send versioned observation batches over authenticated HTTPS to an API;
do not upload the live SQLite file or distribute database credentials. Add a durable outbox,
server-side validation/rate limits, idempotent ingestion, duplicate-observation handling across
contributors, and price freshness/sample-count displays. Keep asking prices distinct from confirmed
sales. Shared uploads should exclude private chat, inventory, card offers and player identifiers
unless separately selected. A hosted endpoint/account has not been selected or deployed in this change.

## Resource changes and verification

Capture now follows the highest subscriber request up to the configured cap; unchanged capture
status is notified at most once per second. Default OCR concurrency is at most two. Windows reuses
its large staging byte buffer. Game preview bitmaps are disposed on resize/detach. Shop Card
unsubscribes UI update/render handlers while detached and disposes replaced preview bitmaps.
These address concrete allocation/rendering waste, not a proven root cause of system-wide hangs.
Live-game resource profiling and mixed-DPI docking verification remain outstanding.

Verified against `D:\Programming\MapleLite`: Windows build, 282 model/core tests and three existing
Shop Card UI tests. An existing recording timing test failed once in the full run, then passed in
isolation and in the subsequent full run. The user's existing running app was not restarted.
