using Avalonia.Controls;
using Avalonia.Interactivity;
using MoonlightHub.Core;

namespace MoonlightHub.UI;

public partial class CredentialsDialog : Window
{
    public string User => (UserBox.Text ?? string.Empty).Trim();
    public string Password => PassBox.Text ?? string.Empty;
    public bool ResetOnSunshine => ResetBox.IsChecked == true;

    /// <summary>True when the user confirmed with "保存" (replaces WPF's <c>ShowDialog() == true</c>); check it after awaiting <c>ShowDialog(owner)</c>.</summary>
    public bool Accepted { get; private set; }

    public CredentialsDialog(InstanceSpec spec, string? currentUser)
    {
        InitializeComponent();
        HeaderText.Text = $"实例 {spec.Id} · {spec.Name} (端口 {spec.WebPort})";
        UserBox.Text = currentUser ?? (spec.Id == 1 ? string.Empty : "hub");
    }

    private async void Ok_Click(object? sender, RoutedEventArgs e)
    {
        if (User.Length == 0 || Password.Length == 0)
        {
            await Dialogs.Info(this, "用户名和密码不能为空。", "凭据", warning: true);
            return;
        }
        Accepted = true;
        Close();
    }

    private void Cancel_Click(object? sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }
}
