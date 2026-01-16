using Avalonia.Controls;
using NovaTerminal.Core;
using System;
using System.Linq;
using SkiaSharp;

namespace NovaTerminal
{
    public partial class SettingsWindow : Window
    {
        private TerminalSettings _settings;
        public event Action<double>? OnOpacityChanged;
        public event Action<string>? OnBlurChanged;

        public SettingsWindow()
        {
            InitializeComponent();
            _settings = TerminalSettings.Load();

            PopulateFonts();
            LoadCurrentSettings();
            ApplyTheme();

            var btnSave = this.FindControl<Button>("BtnSave");
            var btnCancel = this.FindControl<Button>("BtnCancel");
            var opacitySlider = this.FindControl<Slider>("WindowOpacitySlider");
            var opacityDisplay = this.FindControl<TextBlock>("OpacityValueDisplay");
            var blurList = this.FindControl<ComboBox>("BlurList");

            // Update opacity display when slider changes
            if (opacitySlider != null && opacityDisplay != null)
            {
                // Set initial value
                opacityDisplay.Text = $"{(int)(opacitySlider.Value * 100)}%";

                // Update on change
                opacitySlider.PropertyChanged += (s, e) =>
                {
                    if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
                    {
                        opacityDisplay.Text = $"{(int)(opacitySlider.Value * 100)}%";
                        OnOpacityChanged?.Invoke(opacitySlider.Value);
                    }
                };
            }

            if (blurList != null)
            {
                blurList.SelectionChanged += (s, e) =>
                {
                    if (blurList.SelectedItem is ComboBoxItem item && item.Content != null)
                    {
                        OnBlurChanged?.Invoke(item.Content.ToString() ?? "Acrylic");
                    }
                };
            }

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
            var scrollbackInput = this.FindControl<NumericUpDown>("ScrollbackInput");
            var themeList = this.FindControl<ComboBox>("ThemeList");
            var opacitySlider = this.FindControl<Slider>("WindowOpacitySlider");
            var opacityDisplay = this.FindControl<TextBlock>("OpacityValueDisplay");

            if (fontSizeInput != null) fontSizeInput.Value = (decimal)_settings.FontSize;
            if (scrollbackInput != null) scrollbackInput.Value = (decimal)_settings.MaxHistory;
            if (opacitySlider != null)
            {
                opacitySlider.Value = _settings.WindowOpacity;
                if (opacityDisplay != null)
                    opacityDisplay.Text = $"{(int)(_settings.WindowOpacity * 100)}%";
            }

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
            var blurList = this.FindControl<ComboBox>("BlurList");
            if (blurList != null)
            {
                // Select currently configured item
                foreach (ComboBoxItem item in blurList.Items.Cast<ComboBoxItem>())
                {
                    if (item.Content?.ToString() == _settings.BlurEffect)
                    {
                        blurList.SelectedItem = item;
                        break;
                    }
                }

                // If nothing selected (e.g. first run or invalid value), default to Acrylic
                if (blurList.SelectedItem == null && blurList.ItemCount > 0)
                {
                    blurList.SelectedIndex = 0;
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

        private void ApplyTheme()
        {
            var theme = (_settings.ThemeName == "Solarized Dark") ? TerminalTheme.SolarizedDark : TerminalTheme.Dark;
            this.Background = new Avalonia.Media.SolidColorBrush(theme.Background);
            this.Foreground = new Avalonia.Media.SolidColorBrush(theme.Foreground);
        }

        private void SaveAndClose()
        {
            var fontList = this.FindControl<ComboBox>("FontList");
            var fontSizeInput = this.FindControl<NumericUpDown>("FontSizeInput");
            var scrollbackInput = this.FindControl<NumericUpDown>("ScrollbackInput");
            var themeList = this.FindControl<ComboBox>("ThemeList");
            var opacitySlider = this.FindControl<Slider>("WindowOpacitySlider");

            if (fontSizeInput != null) _settings.FontSize = (double)(fontSizeInput.Value ?? 14);
            if (scrollbackInput != null) _settings.MaxHistory = (int)(scrollbackInput.Value ?? 10000);
            if (opacitySlider != null) _settings.WindowOpacity = opacitySlider.Value;

            if (fontList?.SelectedItem is ComboBoxItem fontItem)
                _settings.FontFamily = fontItem.Content?.ToString() ?? "Consolas";

            if (themeList?.SelectedItem is ComboBoxItem themeItem)
                _settings.ThemeName = themeItem.Content?.ToString() ?? "Default";

            var blurList = this.FindControl<ComboBox>("BlurList");
            if (blurList?.SelectedItem is ComboBoxItem blurItem)
                _settings.BlurEffect = blurItem.Content?.ToString() ?? "Acrylic";

            _settings.Save();
            Close(true); // Return true to indicate saved
        }

        public partial class Helper
        {
            // This method added via partial update manually
        }
    }
}
