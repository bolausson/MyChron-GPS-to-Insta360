using System.Globalization;
using System.Windows.Forms;

namespace MyChron2Insta360.App;

/// <summary>
/// A NumericUpDown that takes a dot or a comma as its decimal point, and whose value is always the one
/// it shows. The stock box reads typing by Windows' number format alone, so under a German format
/// "-3.452", as the README and the help write it, read as -3452 and was capped at -600. And it rounds
/// only what it displays: "-3,452" in a box with one decimal showed "-3,5" while -3.452 was used.
/// </summary>
internal sealed class NumberUpDown : NumericUpDown
{
    /// <summary>
    /// Read typed text. In a box with decimals, either mark is the decimal point: none of these boxes
    /// needs a thousands separator, so "1.234,5" with both is not a number. A whole-number box reads
    /// the text as Windows does, so "1.000" stays a thousand under a German format.
    /// </summary>
    internal static decimal? Parse(string? text, int decimalPlaces, CultureInfo culture)
    {
        string t = (text ?? "").Trim();
        if (t.Length == 0) return null;
        var style = NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite |
                    NumberStyles.AllowLeadingSign | NumberStyles.AllowDecimalPoint;
        if (decimalPlaces > 0)
        {
            string point = culture.NumberFormat.NumberDecimalSeparator;
            t = t.Replace(",", point).Replace(".", point);
        }
        else style |= NumberStyles.AllowThousands;
        return decimal.TryParse(t, style, culture, out var v) ? v : null;
    }

    /// <summary>Show a saved value as if it had been typed: kept in range, to the box's decimals.</summary>
    public void SetValue(double v)
    {
        if (double.IsFinite(v)) Value = Fit((decimal)Math.Clamp(v, (double)Minimum, (double)Maximum));
    }

    private decimal Fit(decimal v) => Math.Clamp(Math.Round(v, DecimalPlaces, MidpointRounding.AwayFromZero), Minimum, Maximum);

    // The stock box parses typing in a method it does not let a subclass replace, which it reaches only
    // through these four. Each reads the text here first, which leaves nothing for it to parse.
    protected override void ValidateEditText()
    {
        ReadTyped();
        UpdateEditText();
    }

    protected override void UpdateEditText()
    {
        ReadTyped();
        base.UpdateEditText();
    }

    public override void UpButton()
    {
        ReadTyped();
        base.UpButton();
    }

    public override void DownButton()
    {
        ReadTyped();
        base.DownButton();
    }

    private void ReadTyped()
    {
        if (!UserEdit) return;
        UserEdit = false;   // first, since setting Value comes back through UpdateEditText
        if (Parse(Text, DecimalPlaces, CultureInfo.CurrentCulture) is decimal v) Value = Fit(v);
        // Then show the value in force, whatever became of the typing. The stock box redraws only when
        // the value changes, so typing that rounds or clamps to the value already there (0,04 in a box
        // at 0,0, then the down arrow) stayed on show; and it leaves an emptied box empty.
        ChangingText = true;
        Text = Value.ToString((ThousandsSeparator ? "N" : "F") + DecimalPlaces, CultureInfo.CurrentCulture);
        ChangingText = false;   // when the text was already that, no change event cleared it
    }

    protected override void OnTextBoxKeyPress(object? source, KeyPressEventArgs e)
    {
        // The stock box beeps at any mark outside Windows' number format: a dot under a French or a
        // Swedish one, a comma under a Swiss one, a minus typed as '-' where the format's sign is '−'.
        if (e.KeyChar == '-' || (DecimalPlaces > 0 && e.KeyChar is '.' or ',')) return;
        base.OnTextBoxKeyPress(source, e);
    }
}
