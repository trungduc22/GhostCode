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
            ViewModel.SaveOAuthClient(clientIdBox.Text, clientSecretBox.Password);
        }
    }
}
