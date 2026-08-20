using System.Reflection;
using System.Windows;

namespace SpeechHandler;

internal partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetName();
        var version = name.Version is null ? "1.0.0" : name.Version.ToString(3);
        VersionText.Text = $"Version {version}";

        var company = assembly.GetCustomAttribute<AssemblyCompanyAttribute>()?.Company;
        CompanyText.Text = string.IsNullOrWhiteSpace(company) ? "Desktop transcription for Windows" : company;
    }

    private void Ok_Click(object sender, RoutedEventArgs e) => Close();
}
