using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Reflection;
using System.Windows;
using System.Windows.Navigation;
using Kudaki.App.Properties;

namespace Kudaki.App.Views;

// バージョン情報ダイアログ。Kata (C:\Git\ClassDesign) の AboutDialog と同じ構成
// (ロゴ + 版数 + ライセンス / リポジトリ + OSS 一覧) を Kudaki のパレットで作り直したもの。
// VM を立てるほどの状態が無いので code-behind に閉じる (feedback_r3_and_mvvm_purity の
// 「View 固有の配線だけ」に収まる範囲)。
public partial class AboutDialog : Window
{
    public AboutDialog()
    {
        InitializeComponent();

        var version = Assembly.GetExecutingAssembly().GetName().Version;
        var versionText = version is null
            ? "0.0.0"
            : $"{version.Major}.{version.Minor}.{version.Build}";
        VersionText.Text = string.Format(
            CultureInfo.CurrentUICulture, Strings.About_Version_Format, versionText);

        // Hyperlink.NavigateUri は Uri 型なので resx の string を code-behind で Uri 化して注入。
        RepositoryLink.NavigateUri = new Uri(Strings.About_Repository_Url);
        RepositoryLinkText.Text = Strings.About_Repository_Url;

        OssItemsControl.ItemsSource = BuildOssList();
    }

    // Kudaki.App.csproj の PackageReference と手で揃える。
    // パッケージを足す / 上げるときはここも更新すること。
    private static IReadOnlyList<OssComponent> BuildOssList()
    {
        var raw = new (string Name, string Version, string License, string Url)[]
        {
            ("CommunityToolkit.Mvvm", "8.4.2", "MIT", "https://github.com/CommunityToolkit/dotnet"),
            ("ModelContextProtocol.AspNetCore", "2.2.0", "MIT", "https://github.com/modelcontextprotocol/csharp-sdk"),
            ("R3", "1.3.1", "MIT", "https://github.com/Cysharp/R3"),
            ("R3Extensions.WPF", "1.3.1", "MIT", "https://github.com/Cysharp/R3"),
            ("YamlDotNet", "18.1.0", "MIT", "https://github.com/aaubry/YamlDotNet"),
        };

        var list = new List<OssComponent>(raw.Length);
        foreach (var (name, ver, license, url) in raw)
        {
            list.Add(new OssComponent(name, $" v{ver}", $" — {license}", new Uri(url), url));
        }
        return list;
    }

    private void Hyperlink_RequestNavigate(object sender, RequestNavigateEventArgs e)
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = e.Uri.AbsoluteUri,
                UseShellExecute = true,
            });
        }
        catch
        {
            // ブラウザが開けなくてもダイアログは閉じない (URL は画面に出ているので手で開ける)。
        }
        e.Handled = true;
    }

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // VersionText / LicenseText は StringFormat をやめて整形済みで持たせる
    // (WPF の StringFormat は先頭の空白を落とすことがあり、名前と版数がくっつく)。
    private sealed record OssComponent(
        string Name, string VersionText, string LicenseText, Uri Url, string UrlText);
}
