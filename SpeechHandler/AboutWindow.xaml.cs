using System.Reflection;
using System.Windows;

namespace SpeechHandler;

internal partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionText.Text = $"Version {(version is null ? "1.0.0" : version.ToString(3))}";
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
