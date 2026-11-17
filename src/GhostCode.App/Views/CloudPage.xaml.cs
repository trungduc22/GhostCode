using GhostCode.App.ViewModels;
using GhostCode.Core.Models;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace GhostCode.App.Views;

public sealed partial class CloudPage : Page
{
    public CloudPage()
    {
        ViewModel = App.Services.GetRequiredService<MainViewModel>();
        InitializeComponent();
    }

    public MainViewModel ViewModel { get; }

    private async void ConfigureOAuth_Click(object sender, RoutedEventArgs e)
    {
        bool vi = ViewModel.Config.Language == UiLanguage.Vi;
        var clientIdBox = new TextBox
        {
            Header = "Google Cloud Desktop OAuth Client ID",
            PlaceholderText = "1234567890-xxxx.apps.googleusercontent.com",
            Text = ViewModel.OAuthClientId
        };
        var clientSecretBox = new PasswordBox
        {
            Header = vi ? "Client Secret (tùy chọn cho Desktop PKCE)" : "Client Secret (optional for Desktop PKCE)",
            Password = ViewModel.OAuthClientSecret
        };
        var helpText = new TextBlock
        {
            Text = vi
                ? "Tạo Desktop App OAuth Client ID trong Google Cloud Console (bật Google Drive API). Token được mã hóa cục bộ bằng Windows DPAPI."
                : "Create a Desktop app OAuth Client ID in Google Cloud Console (enable Google Drive API). Tokens are encrypted locally via Windows DPAPI.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(helpText);
        panel.Children.Add(clientIdBox);
        panel.Children.Add(clientSecretBox);

        var dialog = new ContentDialog
        {
            Title = vi ? "Cấu hình Google OAuth 2.0 Client" : "Google OAuth 2.0 Client Setup",
            Content = panel,
            PrimaryButtonText = vi ? "Lưu" : "Save",
            CloseButtonText = vi ? "Hủy" : "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.SaveOAuthClientAsync(clientIdBox.Text, clientSecretBox.Password);
    }

    private async void ConfigureOneDriveOAuth_Click(object sender, RoutedEventArgs e)
    {
        bool vi = ViewModel.Config.Language == UiLanguage.Vi;
        var clientIdBox = new TextBox
        {
            Header = "Microsoft Entra App Client ID",
            PlaceholderText = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
            Text = ViewModel.OneDriveClientId
        };
        var tenantBox = new TextBox
        {
            Header = vi ? "Tenant ('consumers' cho OneDrive cá nhân, hoặc 'common')" : "Tenant ('consumers' for Personal OneDrive, or 'common')",
            PlaceholderText = "consumers",
            Text = ViewModel.OneDriveTenantId
        };
        var helpText = new TextBlock
        {
            Text = vi
                ? "Đăng ký ứng dụng Mobile/Desktop trên Microsoft Entra với redirect URI http://localhost và quyền Files.ReadWrite.AppFolder offline_access User.Read."
                : "Register a Mobile/Desktop app in Microsoft Entra with redirect URI http://localhost and scope Files.ReadWrite.AppFolder offline_access User.Read.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(helpText);
        panel.Children.Add(clientIdBox);
        panel.Children.Add(tenantBox);

        var dialog = new ContentDialog
        {
            Title = vi ? "Cấu hình OneDrive OAuth 2.0 Client" : "OneDrive OAuth 2.0 Client Setup",
            Content = panel,
            PrimaryButtonText = vi ? "Lưu" : "Save",
            CloseButtonText = vi ? "Hủy" : "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };

        if (await dialog.ShowAsync() == ContentDialogResult.Primary)
            await ViewModel.SaveOneDriveClientAsync(clientIdBox.Text, tenantBox.Text);
    }

    private async void MigrateToOneDriveConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmMigrationAsync(googleToOneDrive: true))
            await ViewModel.MigrateToOneDriveCommand.ExecuteAsync(null);
    }

    private async void MigrateToGoogleDriveConfirm_Click(object sender, RoutedEventArgs e)
    {
        if (await ConfirmMigrationAsync(googleToOneDrive: false))
            await ViewModel.MigrateToGoogleDriveCommand.ExecuteAsync(null);
    }

    private async Task<bool> ConfirmMigrationAsync(bool googleToOneDrive)
    {
        bool vi = ViewModel.Config.Language == UiLanguage.Vi;
        var direction = googleToOneDrive ? "Google Drive -> OneDrive" : "OneDrive -> Google Drive";
        var dialog = new ContentDialog
        {
            Title = vi ? $"Xác nhận chuyển dữ liệu ({direction})" : $"Confirm Migration ({direction})",
            Content = vi
                ? "GhostCode sẽ sao chép toàn bộ phiên bản và kiểm tra SHA-256 sang nhà cung cấp đích mà không xóa dữ liệu nguồn."
                : "GhostCode will copy all versions and verify SHA-256 hashes at the destination without deleting source data.",
            PrimaryButtonText = vi ? "Bắt đầu chuyển" : "Start Migration",
            CloseButtonText = vi ? "Hủy" : "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = XamlRoot
        };
        return await dialog.ShowAsync() == ContentDialogResult.Primary;
    }
}
