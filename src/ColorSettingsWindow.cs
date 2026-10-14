using System;
using System.Globalization;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Media;

namespace CodexQuota
{
    public sealed class ColorSettingsWindow : Window
    {
        string backgroundColor;
        string textColor;
        string progressColor;
        readonly Border preview;
        readonly Border previewTrack;
        readonly Border previewFill;
        readonly TextBlock previewLabel;
        readonly TextBlock previewText;
        readonly TextBlock backgroundValue;
        readonly TextBlock textValue;
        readonly TextBlock progressValue;
        readonly Border backgroundSwatch;
        readonly Border textSwatch;
        readonly Border progressSwatch;
        readonly TextBlock contrastNotice;

        public string SelectedBackgroundColor { get { return backgroundColor; } }
        public string SelectedTextColor { get { return textColor; } }
        public string SelectedProgressColor { get { return progressColor; } }

        public ColorSettingsWindow(UsageBarSettings settings)
        {
            if (settings == null) throw new ArgumentNullException("settings");
            backgroundColor = settings.BackgroundColor;
            textColor = settings.TextColor;
            progressColor = settings.ProgressColor;

            Title = "自定义配色";
            Width = 410;
            Height = 445;
            ResizeMode = ResizeMode.NoResize;
            WindowStartupLocation = WindowStartupLocation.CenterOwner;
            ShowInTaskbar = false;
            Background = Brush("#F5F6F7");
            FontFamily = new FontFamily("Segoe UI, Microsoft YaHei UI");
            FontSize = 13;

            var root = new StackPanel { Margin = new Thickness(22) };
            root.Children.Add(new TextBlock { Text = "自定义悬浮栏颜色", FontSize = 20, FontWeight = FontWeights.SemiBold,
                Foreground = Brush("#202124"), Margin = new Thickness(0, 0, 0, 4) });
            root.Children.Add(new TextBlock { Text = "选择颜色后可查看预览；应用后会保存到本机设置。", Foreground = Brush("#5F6368"),
                TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 0, 0, 14) });

            previewText = new TextBlock { Text = "5 小时        58%", FontSize = 16, FontWeight = FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center };
            previewFill = new Border { Width = 76, Height = 4, CornerRadius = new CornerRadius(2), HorizontalAlignment = HorizontalAlignment.Left };
            previewTrack = new Border { Width = 140, Height = 4, CornerRadius = new CornerRadius(2), Child = previewFill,
                HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 8, 0, 0) };
            var previewStack = new StackPanel();
            previewLabel = new TextBlock { Text = "Codex 剩余用量", FontSize = 11, Foreground = Brush("#CFD1D5"), Margin = new Thickness(0, 0, 0, 3) };
            previewStack.Children.Add(previewLabel);
            previewStack.Children.Add(previewText);
            previewStack.Children.Add(previewTrack);
            preview = new Border { Padding = new Thickness(14, 11, 14, 11), CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Child = previewStack };
            root.Children.Add(preview);

            root.Children.Add(ColorRow("卡片背景", "选择卡片的底色", out backgroundSwatch, out backgroundValue, ChooseBackground));
            root.Children.Add(ColorRow("主文字", "影响用量数字", out textSwatch, out textValue, ChooseText));
            root.Children.Add(ColorRow("正常进度条", "用量高于 20% 时使用", out progressSwatch, out progressValue, ChooseProgress));

            contrastNotice = new TextBlock { Foreground = Brush("#9A5D00"), TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 8, 0, 0), MinHeight = 18 };
            root.Children.Add(contrastNotice);
            root.Children.Add(new TextBlock { Text = "剩余用量不超过 20% 时仍使用红色或黄色提示。", FontSize = 11,
                Foreground = Brush("#5F6368"), TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 3, 0, 0) });

            var footer = new DockPanel { LastChildFill = false, Margin = new Thickness(0, 18, 0, 0) };
            var reset = new Button { Content = "恢复默认", MinWidth = 92, MinHeight = 34, Padding = new Thickness(8, 4, 8, 4),
                Margin = new Thickness(0, 0, 8, 0) };
            AutomationProperties.SetName(reset, "恢复默认配色");
            reset.Click += delegate { RestoreDefaults(); };
            DockPanel.SetDock(reset, Dock.Left);
            footer.Children.Add(reset);
            var cancel = new Button { Content = "取消", MinWidth = 74, MinHeight = 34, Padding = new Thickness(8, 4, 8, 4),
                IsCancel = true, Margin = new Thickness(0, 0, 8, 0) };
            DockPanel.SetDock(cancel, Dock.Right);
            footer.Children.Add(cancel);
            var apply = new Button { Content = "应用", MinWidth = 74, MinHeight = 34, Padding = new Thickness(8, 4, 8, 4), IsDefault = true };
            apply.Click += delegate { DialogResult = true; };
            DockPanel.SetDock(apply, Dock.Right);
            footer.Children.Add(apply);
            root.Children.Add(footer);

            Content = root;
            RefreshPreview();
        }

        UIElement ColorRow(string label, string description, out Border swatch, out TextBlock value, Action choose)
        {
            var row = new Grid { Margin = new Thickness(0, 12, 0, 0) };
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var labelStack = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
            labelStack.Children.Add(new TextBlock { Text = label, FontWeight = FontWeights.SemiBold, Foreground = Brush("#202124") });
            labelStack.Children.Add(new TextBlock { Text = description, FontSize = 11, Foreground = Brush("#5F6368") });
            row.Children.Add(labelStack);

            swatch = new Border { Width = 18, Height = 18, BorderBrush = Brush("#777777"), BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(3), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(0, 0, 8, 0) };
            value = new TextBlock { VerticalAlignment = VerticalAlignment.Center, FontFamily = new FontFamily("Consolas"), FontSize = 12 };
            var choice = new StackPanel { Orientation = Orientation.Horizontal };
            choice.Children.Add(swatch);
            choice.Children.Add(value);
            var button = new Button { Content = choice, MinWidth = 126, MinHeight = 34, Padding = new Thickness(8, 4, 8, 4),
                VerticalAlignment = VerticalAlignment.Center };
            AutomationProperties.SetName(button, "选择" + label + "颜色");
            button.Click += delegate { choose(); };
            Grid.SetColumn(button, 1);
            row.Children.Add(button);
            return row;
        }

        void ChooseBackground() { ChooseColor(backgroundColor, delegate(string value) { backgroundColor = value; RefreshPreview(); }); }
        void ChooseText() { ChooseColor(textColor, delegate(string value) { textColor = value; RefreshPreview(); }); }
        void ChooseProgress() { ChooseColor(progressColor, delegate(string value) { progressColor = value; RefreshPreview(); }); }

        void ChooseColor(string current, Action<string> selected)
        {
            using (var dialog = new System.Windows.Forms.ColorDialog())
            {
                dialog.Color = System.Drawing.ColorTranslator.FromHtml(current);
                dialog.FullOpen = true;
                dialog.SolidColorOnly = true;
                if (dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK)
                    selected(String.Format(CultureInfo.InvariantCulture, "#{0:X2}{1:X2}{2:X2}", dialog.Color.R, dialog.Color.G, dialog.Color.B));
            }
        }

        void RestoreDefaults()
        {
            backgroundColor = UsageBarSettings.DefaultBackgroundColor;
            textColor = UsageBarSettings.DefaultTextColor;
            progressColor = UsageBarSettings.DefaultProgressColor;
            RefreshPreview();
        }

        void RefreshPreview()
        {
            preview.Background = Brush(backgroundColor);
            preview.BorderBrush = Brush(UsageBarPalette.BorderColor(backgroundColor));
            previewLabel.Foreground = Brush(UsageBarPalette.SecondaryTextColor(backgroundColor));
            previewText.Foreground = Brush(textColor);
            previewTrack.Background = Brush(UsageBarPalette.TrackColor(backgroundColor));
            previewFill.Background = Brush(progressColor);
            UpdateColorChoice(backgroundSwatch, backgroundValue, backgroundColor);
            UpdateColorChoice(textSwatch, textValue, textColor);
            UpdateColorChoice(progressSwatch, progressValue, progressColor);
            contrastNotice.Text = ContrastRatio(backgroundColor, textColor) < 4.5
                ? "背景与文字对比度较低，可能不易辨认。" : String.Empty;
        }

        static void UpdateColorChoice(Border swatch, TextBlock value, string color)
        {
            swatch.Background = Brush(color);
            value.Text = color;
        }

        static double ContrastRatio(string background, string foreground)
        {
            double first = Luminance(ParseColor(background));
            double second = Luminance(ParseColor(foreground));
            double light = Math.Max(first, second), dark = Math.Min(first, second);
            return (light + 0.05) / (dark + 0.05);
        }

        static double Luminance(Color color)
        {
            return 0.2126 * Linear(color.R / 255.0) + 0.7152 * Linear(color.G / 255.0) + 0.0722 * Linear(color.B / 255.0);
        }

        static double Linear(double channel)
        {
            return channel <= 0.04045 ? channel / 12.92 : Math.Pow((channel + 0.055) / 1.055, 2.4);
        }

        static Color ParseColor(string value) { return (Color)ColorConverter.ConvertFromString(value); }
        static Brush Brush(string value)
        {
            var brush = (SolidColorBrush)new BrushConverter().ConvertFromString(value);
            brush.Freeze();
            return brush;
        }
    }
}
