#if TORCH
using VRageMath;

namespace InventoryManagerLight
{
    // Lightweight data-transfer struct describing a single row of sprite content for an LCD panel.
    // Built on the scan/game thread; coordinates are computed by LcdManager on the game thread.
    internal struct LcdSpriteRow
    {
        public enum Kind { Header, Separator, Item, Bar, Stat, Footer }

        public Kind   RowKind;
        public string Text;         // display text (null = no text)
        public string IconSprite;   // "MyObjectBuilder_Ingot/Iron" or null
        public Color  TextColor;
        public bool   ShowAlert;    // render amber "!" at right edge of this row
        public float  BarFill;      // 0-1 fill fraction, only used when RowKind == Bar
        public Color  BarFillColor; // filled-portion colour, only used when RowKind == Bar
    }
}
#endif
