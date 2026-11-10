# Dark styling for the tray menu.
#
# WinForms' stock ContextMenuStrip is always light on .NET Framework -- it has no
# idea about Windows dark mode -- so it sits next to Explorer's dark menus looking
# like a leftover. This repaints it with the same palette Windows 11 uses, but
# only when the user has actually chosen dark mode; a light desktop keeps the
# stock menu.

function Test-WindowsDarkTheme {
    try {
        $key = Get-ItemProperty -Path 'HKCU:\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize' -ErrorAction Stop
        if ($null -eq $key.AppsUseLightTheme) { return $false }
        return ([int]$key.AppsUseLightTheme -eq 0)
    } catch { return $false }
}

function Set-DarkTrayMenu {
    param([System.Windows.Forms.ContextMenuStrip]$Menu)
    if (-not $Menu) { return $false }
    if (-not (Test-WindowsDarkTheme)) { return $false }

    if (-not ('DarkMenuRenderer' -as [type])) {
        Add-Type -ReferencedAssemblies 'System.Windows.Forms', 'System.Drawing' -TypeDefinition @'
using System.Drawing;
using System.Windows.Forms;

public class DarkMenuColorTable : ProfessionalColorTable
{
    public static readonly Color Surface = Color.FromArgb(44, 44, 46);
    public static readonly Color Hover   = Color.FromArgb(64, 64, 68);
    public static readonly Color Line    = Color.FromArgb(72, 72, 76);

    public override Color ToolStripDropDownBackground { get { return Surface; } }
    public override Color ImageMarginGradientBegin { get { return Surface; } }
    public override Color ImageMarginGradientMiddle { get { return Surface; } }
    public override Color ImageMarginGradientEnd { get { return Surface; } }
    public override Color MenuBorder { get { return Line; } }
    public override Color MenuItemBorder { get { return Hover; } }
    public override Color MenuItemSelected { get { return Hover; } }
    public override Color MenuItemSelectedGradientBegin { get { return Hover; } }
    public override Color MenuItemSelectedGradientEnd { get { return Hover; } }
    public override Color MenuItemPressedGradientBegin { get { return Hover; } }
    public override Color MenuItemPressedGradientEnd { get { return Hover; } }
    public override Color SeparatorDark { get { return Line; } }
    public override Color SeparatorLight { get { return Surface; } }
    public override Color CheckBackground { get { return Hover; } }
    public override Color CheckSelectedBackground { get { return Hover; } }
    public override Color CheckPressedBackground { get { return Hover; } }
}

public class DarkMenuRenderer : ToolStripProfessionalRenderer
{
    public DarkMenuRenderer() : base(new DarkMenuColorTable()) { }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        // The stock renderer keeps near-black text, which disappears on the
        // dark surface, so drive the colour from the item state instead.
        if (!e.Item.Enabled) { e.TextColor = Color.FromArgb(128, 128, 128); }
        else if (e.Item.Selected || e.Item.Pressed) { e.TextColor = Color.White; }
        else { e.TextColor = Color.FromArgb(240, 240, 240); }
        base.OnRenderItemText(e);
    }
}
'@
    }

    $Menu.Renderer = New-Object DarkMenuRenderer
    $Menu.BackColor = [DarkMenuColorTable]::Surface
    $Menu.ForeColor = [System.Drawing.Color]::FromArgb(240, 240, 240)
    foreach ($item in $Menu.Items) {
        $item.ForeColor = [System.Drawing.Color]::FromArgb(240, 240, 240)
    }
    return $true
}
