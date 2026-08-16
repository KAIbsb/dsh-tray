using System;
using System.Windows.Forms;

// Unified DPI layout helper for hand-built forms. The settings dialog deliberately uses
// AutoScaleMode.None + this helper: TableLayoutPanel Absolute row/column styles do not follow
// AutoScaleMode.Dpi, and mixing manual Sp() with AutoScaleMode.Dpi double-scales explicit
// control sizes on real high-DPI screens. Keeping one manual scaling path makes --ui-preview
// (dpiOverride) behave the same as a real scaled desktop.
static class Ui
{
    static float scale = 1f;

    public static void SetScale(float s)
    {
        scale = (s > 0f) ? s : 1f;
    }

    public static void Init(Control c)
    {
        SetScale(c.DeviceDpi / 96f);
    }

    // generic layout pixel (width/height/margin/padding)
    public static int Px(int px)
    {
        return Math.Max(1, (int)Math.Round(px * scale));
    }

    // row height (design value at 100% DPI)
    public static int RowH(int base96)
    {
        return Px(base96);
    }

    // spacing/gap (design value at 100% DPI)
    public static int Gap(int base96)
    {
        return Px(base96);
    }

    // separator lines are always 1 physical pixel, never scaled
    public static int Line()
    {
        return 1;
    }
}
