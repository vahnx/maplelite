# Item recognition assets

`items.json` maps MapleStory Classic item IDs to names and Kerning links.
The 1,690 PNGs in `icons/` were refreshed from
`https://kerning.gg/game/items/{id}.png` on 2026-11-14.
MapleStory artwork belongs to Nexon; Kerning Exchange is a fan project.

Keep the icon source consistent with this Classic catalog. Other MapleStory
versions can associate different artwork with the same ID. The previous bundle
caused very confident but incorrect matches, including Cutlus as Viking Sword,
Sapphire Earrings as Weighted Earrings, and Wooden Buckler as Mystic Shield.

The matcher allows three pixels of extra translation beyond the slot bounds.
Some inventory icons are clipped by the 32x32 capture even when their PNG is
smaller than the slot. Do not require templates to fit entirely inside the crop.

Validation: a captured Equip tab now recognizes all seven occupied slots
(Meba, Cutlus, Forked Spear, Blue Lappy Boots, Sapphire Earrings, Dark Guise,
Wooden Buckler), with scores from 0.984 to 1.000. The saved `inventory-use.png`
capture recognizes 28 of 29 occupied slots, versus 24 previously. These captures
do not establish accuracy for every item. Visually identical icons and items
absent from the catalog may still need manual naming.
