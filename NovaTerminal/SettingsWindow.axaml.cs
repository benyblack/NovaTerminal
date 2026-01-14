using Avalonia.Controls;
using NovaTerminal.Core;
using System.Linq;
using SkiaSharp;

namespace NovaTerminal
{
    public partial class SettingsWindow : Window
    {
        private TerminalSettings _settings;

        public SettingsWindow()
        {
            InitializeComponent();
            _settings = TerminalSettings.Load();
            
            PopulateFonts();
            LoadCurrentSettings();

            var btnSave = this.FindControl<Button>("BtnSave");
            var btnCancel = this.FindControl<Button>("BtnCancel");

            if (btnSave != null) btnSave.Click += (s, e) => SaveAndClose();
            if (btnCancel != null) btnCancel.Click += (s, e) => Close();
        }

        private void PopulateFonts()
        {
            var fontList = this.FindControl<ComboBox>("FontList");
            if (fontList == null) return;

            fontList.Items.Clear();
            var fonts = SkiaSharp.SKFontManager.Default.FontFamilies
                .OrderBy(f => f)
                .Select(f => new ComboBoxItem { Content = f })
                .ToList();

            foreach (var f in fonts) fontList.Items.Add(f);
        }

        private void LoadCurrentSettings()
        {
            var fontList = this.FindControl<ComboBox>("FontList");
            var fontSizeInput = this.FindControl<NumericUpDown>("FontSizeInput");
            var themeList = this.FindControl<ComboBox>("ThemeList");

            if (fontSizeInput != null) fontSizeInput.Value = (decimal)_settings.FontSize;

            if (fontList != null)
            {
                // Try to find matching item
                foreach (ComboBoxItem item in fontList.Items.Cast<ComboBoxItem>())
                {
                    if (item.Content?.ToString() == _settings.FontFamily)
                    {
                        fontList.SelectedItem = item;
                        break;
                    }
                }
            }

            if (themeList != null)
            {
                foreach (ComboBoxItem item in themeList.Items.Cast<ComboBoxItem>())
                {
                    if (item.Content?.ToString() == _settings.ThemeName)
                    {
                        themeList.SelectedItem = item;
                        break;
                    }
                }
            }
        }

        private void SaveAndClose()
        {
            var fontList = this.FindControl<ComboBox>("FontList");
            var fontSizeInput = this.FindControl<NumericUpDown>("FontSizeInput");
            var themeList = this.FindControl<ComboBox>("ThemeList");

            if (fontSizeInput != null) _settings.FontSize = (double)(fontSizeInput.Value ?? 14);
            
            if (fontList?.SelectedItem is ComboBoxItem fontItem)
                _settings.FontFamily = fontItem.Content?.ToString() ?? "Consolas";

            if (themeList?.SelectedItem is ComboBoxItem themeItem)
                _settings.ThemeName = themeItem.Content?.ToString() ?? "Default";

            _settings.Save();
            Close(true); // Return true to indicate saved
        }
    }
}
