using GhostCode.App.ViewModels;
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
        var clientIdBox = new TextBox
        {
            Header = "Google Cloud Desktop OAuth Client ID",
            PlaceholderText = "1234567890-xxxx.apps.googleusercontent.com",
            Text = ViewModel.OAuthClientId
        };
        var clientSecretBox = new PasswordBox
        {
            Header = "Client Secret (optional for Desktop PKCE)",
            Password = ViewModel.OAuthClientSecret
        };
        var helpText = new TextBlock
        {
            Text = "Create a 'Desktop app' OAuth Client ID in Google Cloud Console (APIs & Services -> Credentials -> Enable Google Drive API). Tokens are encrypted locally via Windows DPAPI.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(helpText);
        panel.Children.Add(clientIdBox);
        panel.Children.Add(clientSecretBox);

        var dialog = new ContentDialog
        {
            Title = "Google OAuth 2.0 Client Setup",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.SaveOAuthClientAsync(clientIdBox.Text, clientSecretBox.Password);
        }
    }

    private async void ConfigureOneDriveOAuth_Click(object sender, RoutedEventArgs e)
    {
        var clientIdBox = new TextBox
        {
            Header = "Microsoft Entra App Registration Client ID",
            PlaceholderText = "xxxxxxxx-xxxx-xxxx-xxxx-xxxxxxxxxxxx",
            Text = ViewModel.OneDriveClientId
        };
        var tenantBox = new TextBox
        {
            Header = "Tenant ('consumers' for Personal OneDrive, or 'common')",
            PlaceholderText = "consumers",
            Text = ViewModel.OneDriveTenantId
        };
        var helpText = new TextBlock
        {
            Text = "Register a Mobile/Desktop app in Microsoft Entra with redirect URI http://localhost and scope Files.ReadWrite.AppFolder offline_access User.Read.",
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12
        };

        var panel = new StackPanel { Spacing = 12 };
        panel.Children.Add(helpText);
        panel.Children.Add(clientIdBox);
        panel.Children.Add(tenantBox);

        var dialog = new ContentDialog
        {
            Title = "OneDrive OAuth 2.0 Client Setup",
            Content = panel,
            PrimaryButtonText = "Save",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
            XamlRoot = this.XamlRoot
        };

        var result = await dialog.ShowAsync();
        if (result == ContentDialogResult.Primary)
        {
            await ViewModel.SaveOneDriveClientAsync(clientIdBox.Text, tenantBox.Text);
        }
    }
}
