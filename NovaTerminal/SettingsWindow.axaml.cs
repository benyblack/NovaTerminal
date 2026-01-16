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
        public event Action<string, double, string>? OnBgImageChanged;
        public event Action<string>? OnFontChanged;
        public event Action<double>? OnFontSizeChanged;
        public event Action<string>? OnThemeChanged;

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

            // Core Settings Controls
            var fontList = this.FindControl<ComboBox>("FontList");
            var fontSizeInput = this.FindControl<NumericUpDown>("FontSizeInput");
            var themeList = this.FindControl<ComboBox>("ThemeList");

            if (fontList != null)
            {
                fontList.SelectionChanged += (s, e) =>
                {
                    if (fontList.SelectedItem is ComboBoxItem item && item.Content != null)
                    {
                        OnFontChanged?.Invoke(item.Content.ToString() ?? "Consolas");
                    }
                };
            }

            if (fontSizeInput != null)
            {
                fontSizeInput.ValueChanged += (s, e) =>
                {
                    if (fontSizeInput.Value.HasValue)
                    {
                        OnFontSizeChanged?.Invoke((double)fontSizeInput.Value.Value);
                    }
                };
            }

            if (themeList != null)
            {
                themeList.SelectionChanged += (s, e) =>
                {
                    if (themeList.SelectedItem is ComboBoxItem item && item.Content != null)
                    {
                        OnThemeChanged?.Invoke(item.Content.ToString() ?? "Default");

                        // Also apply theme to Settings Window itself for preview consistency
                        // (Optional, user might prefer settings window stays static, but it's nice)
                        // Actually, let's keep SettingsWindow static for now to avoid complexity, main window is what matters.
                    }
                };
            }

            // Bg Image Controls
            var bgPathInput = this.FindControl<TextBox>("BgImagePathInput");
            var bgOpacitySlider = this.FindControl<Slider>("BgImageOpacitySlider");
            var bgStretchList = this.FindControl<ComboBox>("BgImageStretchList");
            var bgOpacityDisplay = this.FindControl<TextBlock>("BgImageOpacityDisplay");

            // Define trigger helper
            void TriggerBgUpdate()
            {
                var path = bgPathInput?.Text ?? "";
                var opacity = bgOpacitySlider?.Value ?? 0.5;
                var stretch = (bgStretchList?.SelectedItem as ComboBoxItem)?.Content?.ToString() ?? "UniformToFill";
                OnBgImageChanged?.Invoke(path, opacity, stretch);
            }

            if (bgPathInput != null)
            {
                bgPathInput.PropertyChanged += (s, e) =>
                {
                    if (e.Property == TextBox.TextProperty) TriggerBgUpdate();
                };
            }

            if (bgOpacitySlider != null && bgOpacityDisplay != null)
            {
                bgOpacityDisplay.Text = $"{(int)(bgOpacitySlider.Value * 100)}%";
                bgOpacitySlider.PropertyChanged += (s, e) =>
                {
                    if (e.Property == Avalonia.Controls.Primitives.RangeBase.ValueProperty)
                    {
                        bgOpacityDisplay.Text = $"{(int)(bgOpacitySlider.Value * 100)}%";
                        TriggerBgUpdate();
                    }
                };
            }

            if (bgStretchList != null)
            {
                bgStretchList.SelectionChanged += (s, e) => TriggerBgUpdate();
            }

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

            var btnBrowse = this.FindControl<Button>("BtnBrowseImage");
            if (btnBrowse != null)
            {
                btnBrowse.Click += async (s, e) =>
                {
                    var dlg = new OpenFileDialog();
                    dlg.Filters.Add(new FileDialogFilter { Name = "Images", Extensions = { "png", "jpg", "jpeg", "bmp", "webp" } });
                    dlg.AllowMultiple = false;
                    var result = await dlg.ShowAsync(this);
                    if (result != null && result.Length > 0)
                    {
                        if (bgPathInput != null)
                        {
                            bgPathInput.Text = result[0];
                            TriggerBgUpdate();
                        }
                    }
                };
            }

            var btnClearBg = this.FindControl<Button>("BtnClearImage");
            if (btnClearBg != null)
            {
                btnClearBg.Click += (s, e) =>
                {
                    if (bgPathInput != null)
                    {
                        bgPathInput.Text = "";
                        TriggerBgUpdate();
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

            // Bg Image Controls
            var bgPathInput = this.FindControl<TextBox>("BgImagePathInput");
            var bgOpacitySlider = this.FindControl<Slider>("BgImageOpacitySlider");
            var bgOpacityDisplay = this.FindControl<TextBlock>("BgImageOpacityDisplay");
            var bgStretchList = this.FindControl<ComboBox>("BgImageStretchList");

            if (fontSizeInput != null) fontSizeInput.Value = (decimal)_settings.FontSize;
            if (scrollbackInput != null) scrollbackInput.Value = (decimal)_settings.MaxHistory;
            if (opacitySlider != null)
            {
                opacitySlider.Value = _settings.WindowOpacity;
                if (opacityDisplay != null)
                    opacityDisplay.Text = $"{(int)(_settings.WindowOpacity * 100)}%";
            }

            if (bgPathInput != null) bgPathInput.Text = _settings.BackgroundImagePath;
            if (bgOpacitySlider != null)
            {
                bgOpacitySlider.Value = _settings.BackgroundImageOpacity;
                if (bgOpacityDisplay != null)
                    bgOpacityDisplay.Text = $"{(int)(_settings.BackgroundImageOpacity * 100)}%";
            }

            // ... (font/theme loops omitted for brevity as they are unchanged logic, but we need to keep them if we replace the whole block) ... 
            // Wait, I am replacing a chunk. I should keep the existing logic.
            // Re-implementing existing loops to be safe:

            if (fontList != null)
            {
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

            var blurList = this.FindControl<ComboBox>("BlurList");
            if (blurList != null)
            {
                foreach (ComboBoxItem item in blurList.Items.Cast<ComboBoxItem>())
                {
                    if (item.Content?.ToString() == _settings.BlurEffect)
                    {
                        blurList.SelectedItem = item;
                        break;
                    }
                }
                if (blurList.SelectedItem == null && blurList.ItemCount > 0) blurList.SelectedIndex = 0;
            }

            if (bgStretchList != null)
            {
                foreach (ComboBoxItem item in bgStretchList.Items.Cast<ComboBoxItem>())
                {
                    if (item.Content?.ToString() == _settings.BackgroundImageStretch)
                    {
                        bgStretchList.SelectedItem = item;
                        break;
                    }
                }
                if (bgStretchList.SelectedItem == null && bgStretchList.ItemCount > 0) bgStretchList.SelectedIndex = 3; // UniformToFill
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

            // Bg Image inputs
            var bgPathInput = this.FindControl<TextBox>("BgImagePathInput");
            var bgOpacitySlider = this.FindControl<Slider>("BgImageOpacitySlider");
            var bgStretchList = this.FindControl<ComboBox>("BgImageStretchList");
            var blurList = this.FindControl<ComboBox>("BlurList");

            if (fontSizeInput != null) _settings.FontSize = (double)(fontSizeInput.Value ?? 14);
            if (scrollbackInput != null) _settings.MaxHistory = (int)(scrollbackInput.Value ?? 10000);
            if (opacitySlider != null) _settings.WindowOpacity = opacitySlider.Value;

            if (fontList?.SelectedItem is ComboBoxItem fontItem)
                _settings.FontFamily = fontItem.Content?.ToString() ?? "Consolas";

            if (themeList?.SelectedItem is ComboBoxItem themeItem)
                _settings.ThemeName = themeItem.Content?.ToString() ?? "Default";

            if (blurList?.SelectedItem is ComboBoxItem blurItem)
                _settings.BlurEffect = blurItem.Content?.ToString() ?? "Acrylic";

            if (bgPathInput != null) _settings.BackgroundImagePath = bgPathInput.Text ?? "";
            if (bgOpacitySlider != null) _settings.BackgroundImageOpacity = bgOpacitySlider.Value;
            if (bgStretchList?.SelectedItem is ComboBoxItem stretchItem)
                _settings.BackgroundImageStretch = stretchItem.Content?.ToString() ?? "UniformToFill";

            _settings.Save();
            Close(true); // Return true to indicate saved
        }

        public partial class Helper
        {
            // This method added via partial update manually
        }
    }
}
