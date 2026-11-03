using Microsoft.Win32;

namespace LocaLizard.App;

/// <summary>
/// Applies a consistent light, dark, or Windows-following palette to the WinForms UI.
/// The choice is stored independently from build profiles so profile imports never
/// unexpectedly change the application's appearance.
/// </summary>
internal static class UiThemeManager
{
    private const string RegistryPath = @"Software\LocaLizard";
    private static readonly Color Accent = Color.FromArgb(35, 139, 82);

    private readonly record struct Palette(
        Color Window,
        Color Surface,
        Color Input,
        Color Border,
        Color Text,
        Color Muted,
        Color AccentText);

    public static string Load()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RegistryPath, writable: false);
            var value = key?.GetValue("UiTheme") as string;
            return value is "Light" or "Dark" ? value : "System";
        }
        catch
        {
            return "System";
        }
    }

    public static void Save(string theme)
    {
        if (theme is not ("System" or "Light" or "Dark")) return;
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RegistryPath, writable: true);
            key?.SetValue("UiTheme", theme, RegistryValueKind.String);
        }
        catch
        {
            // Appearance preferences are non-critical; a registry restriction must
            // never prevent the application from opening.
        }
    }

    public static void Apply(Control root, string theme)
    {
        var dark = theme == "Dark" || (theme == "System" && IsWindowsDarkMode());
        var palette = dark
            ? new Palette(
                Color.FromArgb(28, 32, 36),
                Color.FromArgb(37, 42, 47),
                Color.FromArgb(25, 29, 33),
                Color.FromArgb(62, 70, 77),
                Color.FromArgb(235, 239, 242),
                Color.FromArgb(164, 174, 181),
                Color.White)
            : new Palette(
                Color.FromArgb(245, 247, 246),
                Color.White,
                Color.FromArgb(250, 251, 250),
                Color.FromArgb(220, 226, 222),
                Color.FromArgb(35, 44, 39),
                Color.FromArgb(103, 114, 107),
                Color.White);

        ApplyRecursive(root, palette);
        root.Invalidate(true);
    }

    private static void ApplyRecursive(Control control, Palette palette)
    {
        if (control.Tag is string tag && tag == "brand")
        {
            control.BackColor = Accent;
            control.ForeColor = Color.White;
        }
        else if (control is Form or TabControl or TabPage)
        {
            control.BackColor = palette.Window;
            control.ForeColor = palette.Text;
        }
        else if (control is GroupBox)
        {
            control.BackColor = palette.Surface;
            control.ForeColor = palette.Text;
        }
        else if (control is TextBoxBase textBox)
        {
            textBox.BackColor = palette.Input;
            textBox.ForeColor = palette.Text;
            textBox.BorderStyle = BorderStyle.FixedSingle;
        }
        else if (control is ComboBox comboBox)
        {
            comboBox.BackColor = palette.Input;
            comboBox.ForeColor = palette.Text;
            comboBox.FlatStyle = FlatStyle.Flat;
        }
        else if (control is Button button)
        {
            button.UseVisualStyleBackColor = false;
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = palette.Border;
            button.FlatAppearance.BorderSize = 1;
            button.BackColor = palette.Surface;
            button.ForeColor = palette.Text;
            if (button.AccessibleName == "primary")
            {
                button.BackColor = Accent;
                button.ForeColor = palette.AccentText;
                button.FlatAppearance.BorderSize = 0;
            }
        }
        else if (control is Label label)
        {
            label.BackColor = Color.Transparent;
            label.ForeColor = control.Tag as string == "muted" ? palette.Muted : palette.Text;
        }
        else if (control is CheckBox checkBox)
        {
            checkBox.BackColor = Color.Transparent;
            checkBox.ForeColor = palette.Text;
        }
        else if (control is Panel or TableLayoutPanel or FlowLayoutPanel)
        {
            control.BackColor = palette.Window;
            control.ForeColor = palette.Text;
        }
        else
        {
            control.BackColor = palette.Surface;
            control.ForeColor = palette.Text;
        }

        foreach (Control child in control.Controls)
            ApplyRecursive(child, palette);
    }

    private static bool IsWindowsDarkMode()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                writable: false);
            return key?.GetValue("AppsUseLightTheme") is int value && value == 0;
        }
        catch
        {
            return false;
        }
    }
}
