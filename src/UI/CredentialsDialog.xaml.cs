using System.Windows;
using MoonlightHub.Core;

namespace MoonlightHub.UI;

public partial class CredentialsDialog : Window
{
    public string User => UserBox.Text.Trim();
    public string Password => PassBox.Password;
    public bool ResetOnSunshine => ResetBox.IsChecked == true;

    public CredentialsDialog(InstanceSpec spec, string? currentUser)
    {
        InitializeComponent();
        HeaderText.Text = $"实例 {spec.Id} · {spec.Name} (端口 {spec.WebPort})";
        UserBox.Text = currentUser ?? (spec.Id == 1 ? string.Empty : "hub");
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        if (User.Length == 0 || Password.Length == 0)
        {
            MessageBox.Show(this, "用户名和密码不能为空。", "凭据", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
