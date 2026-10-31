using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Nashi.Core;

namespace Nashi.Windows;

/// Yerleşik öğretici: 13 bölüm, her birinde bir özellik tanıtımı ve etkileşimli deneme.
public sealed partial class TutorialWindow : Window
{
    private readonly TutorialStore store;
    private readonly Window owner;
    private int index = 0;

    private static readonly List<TutorialStep> Steps = new()
    {
        new("welcome","Hoş geldiniz","Nashi’ye hoş geldiniz. Bu öğreticide dosyalarınızı nasıl dönüştüreceğinizi, düzenleyeceğinizi ve yöneteceğinizi adım adım göreceksiniz.",null),
        new("drag-drop","Dosyaları sürükleyip bırakın","Bir dosyayı Nashi penceresine sürükleyin veya sağ üstteki ‘Dosya seç’ düğmesini kullanın. Orijinal dosyanız asla değişmez; yeni bir kopya oluşturulur.",null),
        new("convert-wheel","Dönüşüm tekerleği","Dosya sürüklendiğinde bir tuşa basın (Ctrl=araçlar, Shift=convert, Alt=eylemler) ve fare imlecinizde tekerlek açılın. İstediğiniz biçimi seçin.",null),
        new("convert-format","Biçim seçimi","Dönüşüm tekerleğinde istediğiniz çıktı biçimini seçin (JPG, PNG, PDF, MP4, MP3 vb.). Sonuç yanınıza yeni bir dosya olarak kaydedilir.",null),
        new("edit-image","Görsel düzenleme","Bir görseli seçin ve ‘Düzenle’ye tıklayın. Boyutlandır, kırp, döndür, filtre uygulayın veya not ekleyin.",null),
        new("edit-media","Medya düzenleme","Video ve ses dosyalarını kırpın, birleştirin, hızı değiştirin veya metadata düzenleyin. FFmpeg tabanlı motor şeffaf olarak çalışır.",null),
        new("edit-document","Belge düzenleme","PDF, Word, Excel ve diğer belgeleri birleştirin, böleceğiniz sayfaları seçin veya başka bir biçime dönüştürün.",null),
        new("metadata","Metadata düzenleme","Resim ve video dosyalarının EXIF/IPTC etiketlerini görüntüleyin ve düzenleyin. Türkçe karakterli dosya adları desteklenir.",null),
        new("library","Kütüphane","İşlediğiniz tüm dosyalar Kütüphane’ye kaydedilir. Daha sonra erişmek için arama yapabilir, yeniden açabilir veya silebilirsiniz.",null),
        new("shortcuts","Klavye kısayolları","Ctrl+O dosya seçer, Ctrl+V yapıştırır, Ctrl+Shift+D kopyalanan bağlantıyı indirir. Fare tekerleği ile tekerlek menüsü açılır.",null),
        new("tray","Sistem tepsisi","Nashi kapanmış olsa da sistem tepsisinde çalışmaya devam edebilir. Tepsi simgesine sağ tıklayarak ayarları veya çıkış yapabilirsiniz.",null),
        new("watch-folder","Klasör izleme","Bir klasörü izleme listesine ekleyin; o klasöre yeni dosya düşüştüğünde Nashi otomatik olarak dönüştürür.",null),
        new("finish","Tamamlandı","Öğreticiyi tamamladınız! Nashi’yi kullanmaya başlayabilirsiniz. Ayarlar menüsünden tekrar öğreticiye ulaşabilirsiniz.",null),
    };

    public TutorialWindow(Window owner, TutorialStore store)
    {
        this.owner = owner; this.store = store;
        Owner = owner; Title = "Nashi Öğreticisi"; Width = 820; Height = 560;
        MinWidth = 700; MinHeight = 480; WindowStartupLocation = WindowStartupLocation.CenterOwner;
        Theme.Attach(this);

        var root = new DockPanel(); Content = root;
        var header = new DockPanel { Margin = new(28, 18, 28, 0) };
        DockPanel.SetDock(header, Dock.Top); root.Children.Add(header);
        var title = new TextBlock { Text = "Nashi Öğreticisi", FontSize = 26, FontWeight = FontWeights.Bold };
        var stepNo = new TextBlock { Margin = new(10, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center, Foreground = Theme.Brush("Muted"), FontSize = 14 };
        header.Children.Add(title); header.Children.Add(stepNo);

        var content = new ScrollViewer { VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        var body = new StackPanel { Margin = new(28) };
        var titleText = new TextBlock { FontSize = 18, FontWeight = FontWeights.SemiBold, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 10) };
        var bodyText = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.Wrap, Margin = new(0, 0, 0, 20), Foreground = Theme.Brush("Ink") };
        body.Children.Add(titleText); body.Children.Add(bodyText);
        content.Content = body; root.Children.Add(content);

        var footer = new DockPanel { Margin = new(28, 0, 28, 18) };
        DockPanel.SetDock(footer, Dock.Bottom); root.Children.Add(footer);
        var back = MainWindow.Button("← Önceki", () => Move(-1));
        var next = MainWindow.Button("Sonraki →", () => Move(1));
        back.IsEnabled = false;
        var finish = MainWindow.Button("Bitir", () => Close());
        finish.Background = Theme.Primary; finish.Foreground = new SolidColorBrush(Color.FromRgb(43, 54, 28));
        DockPanel.SetDock(back, Dock.Left); DockPanel.SetDock(finish, Dock.Right); footer.Children.Add(back); footer.Children.Add(next); footer.Children.Add(finish);

        void Render()
        {
            var step = Steps[index];
            titleText.Text = step.Title; bodyText.Text = step.Body;
            stepNo.Text = $"Bölüm {index + 1} / {Steps.Count}";
            back.IsEnabled = index > 0;
            next.IsEnabled = index < Steps.Count - 1;
            finish.Visibility = index == Steps.Count - 1 ? Visibility.Visible : Visibility.Collapsed;
            next.Content = index == Steps.Count - 1 ? "Tamamla" : "Sonraki →";
        }
        Render();

        back.Click += (_, _) => { Move(-1); Render(); };
        next.Click += (_, _) => { Move(1); Render(); };

        void Move(int delta)
        {
            var before = index;
            index = Math.Clamp(index + delta, 0, Steps.Count - 1);
            if (index != before) store.MarkCompleted(Steps[index].Id);
        }
    }
}
