#if TORCH
using VRageMath;

namespace InventoryManagerLight
{
    // Lightweight data-transfer struct describing a single row of sprite content for an LCD panel.
    // Built on the scan/game thread; coordinates are computed by LcdManager on the game thread.
    internal struct LcdSpriteRow
    {
        public enum Kind { Header, Separator, Item, Bar, Stat, Footer, ItemBar }

        public Kind   RowKind;
        public string Text;         // left-side display text (null = no text)
        public string StatText;     // right-side stat text for ItemBar rows (e.g. "10%  192/2,000")
        public string IconSprite;   // "MyObjectBuilder_Ingot/Iron" or null
        public Color  TextColor;
        public bool   ShowAlert;    // render amber text / amber bar fill when true
        public float  BarFill;      // 0-1 fill fraction (Bar and ItemBar rows)
        public Color  BarFillColor; // filled-portion colour (Bar and ItemBar rows)
    }
}
#endif
