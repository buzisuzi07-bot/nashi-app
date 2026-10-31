using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Nashi.Core;

namespace Nashi.Windows;

public sealed class OcrWindow : Window
{
    private readonly OcrService ocrService = new();
    private readonly TextBlock statusText = new() { FontSize = 12, Foreground = Theme.Brush("Muted"), TextWrapping = TextWrapping.Wrap };
    private readonly TextBox resultText = new()
    {
        FontSize = 14,
        TextWrapping = TextWrapping.Wrap,
        AcceptsReturn = true,
        VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        IsReadOnly = true,
        Margin = new(0, 8, 0, 0)
    };
    private readonly ComboBox languageCombo = new()
    {
        ItemsSource = new[] { new { Name = "Otomatik", Code = "auto" }, new { Name = "Türkçe", Code = "tr" }, new { Name = "English", Code = "en" } },
        SelectedIndex = 0,
        DisplayMemberPath = "Name",
        SelectedValuePath = "Code",
        Margin = new(4, 12, 4, 0),
        Width = 120
    };
    private Button selectButton = new();
    private Button copyButton = new();
    private Button saveButton = new();
    private string? currentImagePath;

    public OcrWindow(Window owner)
    {
        Owner = owner;
        Theme.Attach(this);
        Title = "OCR · Nashi";
        Width = 820;
        Height = 680;
        MinWidth = 700;
        MinHeight = 480;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;

        selectButton = new Button { Content = L10n.Text("Resim Seç") };
        selectButton.Click += (_, _) => SelectImage();
        copyButton = new Button { Content = L10n.Text("Kopyala"), IsEnabled = false };
        copyButton.Click += (_, _) => CopyText();
        saveButton = new Button { Content = L10n.Text("Kaydet"), IsEnabled = false };
        saveButton.Click += (_, _) => SaveText();

        var root = new DockPanel { Margin = new(26) };
        Content = root;

        var header = new StackPanel();
        header.Children.Add(L10n.Label(new TextBlock { Text = "OCR", FontSize = 28, FontWeight = FontWeights.Bold }));
        header.Children.Add(new TextBlock { Text = L10n.Text("Bir görüntüdeki metni okur. Windows'ta yerel OCR motoru kullanılır; işlem cihazda kalır."), Margin = new(0, 8, 0, 22), TextWrapping = TextWrapping.Wrap });
        DockPanel.SetDock(header, Dock.Top);
        root.Children.Add(header);

        var body = new Grid();
        body.ColumnDefinitions.Add(new() { Width = new(1, GridUnitType.Star) });
        body.RowDefinitions.Add(new() { Height = GridLength.Auto });
        body.RowDefinitions.Add(new() { Height = new(1, GridUnitType.Star) });

        var topRow = new StackPanel { Margin = new(0, 0, 0, 16) };
        topRow.Children.Add(selectButton);
        topRow.Children.Add(new TextBlock { Text = "  Dil:", VerticalAlignment = VerticalAlignment.Center, Margin = new(12, 0, 4, 0) });
        topRow.Children.Add(languageCombo);
        body.Children.Add(topRow);
        Grid.SetRow(topRow, 0);

        var scroll = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        resultText.ContextMenu = new ContextMenu();
        var copyItem = new MenuItem { Header = "Kopyala" };
        copyItem.Click += (_, _) => CopyText();
        var selectItem = new MenuItem { Header = "Tümünü Seç" };
        selectItem.Click += (_, _) => { resultText.SelectAll(); resultText.Focus(); };
        resultText.ContextMenu.Items.Add(copyItem);
        resultText.ContextMenu.Items.Add(selectItem);
        scroll.Content = resultText;
        body.Children.Add(scroll);
        Grid.SetRow(scroll, 1);

        root.Children.Add(body);

        var footer = new DockPanel { LastChildFill = false, Margin = new(0, 16, 0, 0) };
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var cancel = MainWindow.Button("Kapat", () => DialogResult = false);
        DockPanel.SetDock(cancel, Dock.Left);
        footer.Children.Add(cancel);

        var rightFooter = new StackPanel { Orientation = Orientation.Horizontal };
        saveButton.Margin = new(8, 0, 0, 0);
        copyButton.Margin = new(8, 0, 0, 0);
        rightFooter.Children.Add(saveButton);
        rightFooter.Children.Add(copyButton);
        DockPanel.SetDock(rightFooter, Dock.Right);
        footer.Children.Add(rightFooter);

        statusText.Text = ocrService.IsAvailable ? L10n.Text("OCR hazır.") : L10n.Text("OCR bu Windows sürümünde kullanılamıyor.");
        if (!ocrService.IsAvailable) statusText.Foreground = Theme.Brush("Error");
        statusText.Margin = new(0, 0, 0, 0);
        footer.Children.Add(statusText);

        PreviewKeyDown += (_, e) => { if (e.Key == Key.Escape) { Close(); e.Handled = true; } };
    }

    private void SelectImage()
    {
        var picker = new Microsoft.Win32.OpenFileDialog
        {
            Title = L10n.Text("Resim Seç"),
            Filter = "Görüntü dosyaları (*.png;*.jpg;*.jpeg;*.webp;*.heic;*.tiff;*.bmp;*.svg)|*.png;*.jpg;*.jpeg;*.webp;*.heic;*.tiff;*.bmp;*.svg|Tüm dosyalar (*.*)|*.*"
        };
        if (picker.ShowDialog(this) != true) return;
        currentImagePath = picker.FileName;
        RunOcr(currentImagePath);
    }

    private async void RunOcr(string path)
    {
        copyButton.IsEnabled = false;
        saveButton.IsEnabled = false;
        resultText.Text = string.Empty;
        statusText.Text = L10n.Text("Recognizing...");

        try
        {
            var result = await ocrService.RecognizeAsync(path, (string)languageCombo.SelectedValue);
            if (!result.Success)
            {
                resultText.Text = L10n.Text(result.Error);
                statusText.Text = L10n.Text("Hata: ") + result.Error;
                return;
            }
            resultText.Text = result.Text;
            copyButton.IsEnabled = result.LineCount > 0;
            saveButton.IsEnabled = result.LineCount > 0;
            statusText.Text = $"{result.LineCount} satır okundu.";
        }
        catch (Exception ex)
        {
            resultText.Text = ex.Message;
            statusText.Text = L10n.Text("Hata: ") + ex.Message;
        }
    }

    private void CopyText()
    {
        if (!string.IsNullOrEmpty(resultText.Text))
        {
            try
            {
                Clipboard.SetText(resultText.Text);
                copyButton.Content = L10n.Text("Copied.");
                copyButton.IsEnabled = false;
                _ = Task.Delay(1500).ContinueWith(_ => Dispatcher.Invoke(() => copyButton.Content = L10n.Text("Copy")));
            }
            catch { }
        }
    }

    private void SaveText()
    {
        if (string.IsNullOrEmpty(resultText.Text)) return;
        var picker = new Microsoft.Win32.SaveFileDialog
        {
            Title = L10n.Text("Save"),
            Filter = "Metin dosyası (*.txt)|*.txt|Tüm dosyalar (*.*)|*.*",
            FileName = currentImagePath != null ? System.IO.Path.GetFileNameWithoutExtension(currentImagePath) + "-ocr.txt" : "ocr.txt"
        };
        if (picker.ShowDialog(this) != true) return;
        try
        {
            File.WriteAllText(picker.FileName, resultText.Text);
            statusText.Text = L10n.Text("Kaydedildi.");
        }
        catch (Exception ex)
        {
            statusText.Text = L10n.Text("Hata: ") + ex.Message;
        }
    }
}
