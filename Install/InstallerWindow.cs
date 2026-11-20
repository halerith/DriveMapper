using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using DriveMapper;

namespace Install;

internal sealed class InstallerWindow : Window
{
    private readonly TextBlock state = Text("Checking installation…", 15);
    private readonly TextBlock location = Text("", 13);
    private readonly TextBlock result = Text("", 14);
    private readonly Button primary = new() { Content = "Install", Margin = new Thickness(0, 0, 12, 0), MinWidth = 112 };
    private readonly Button remove = new() { Content = "Uninstall", Margin = new Thickness(0, 0, 12, 0), MinWidth = 104 };
    private readonly Button close = new() { Content = "Close", MinWidth = 88 };
    private readonly CheckBox force = new() { Content = "Remove Halerith DriveMapper settings and logging data on uninstall", Margin = new Thickness(0, 18, 0, 8) };
    private readonly ProgressBar progress = new() { IsIndeterminate = false, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 18, 0, 0) };
    private bool working;

    public InstallerWindow()
    {
        Title = Product.SetupTitle;
        Icon = System.Windows.Media.Imaging.BitmapFrame.Create(new Uri("pack://application:,,,/Install;component/installer.ico"));
        Width = 680; Height = 550; MinWidth = 620; MinHeight = 520;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        var panel = new StackPanel { Margin = new Thickness(30) };
        panel.Children.Add(new TextBlock { Text = Product.DisplayName, FontSize = 28, FontWeight = FontWeights.SemiBold });
        var subtitle = Text("Machine installation", 14);
        subtitle.SetResourceReference(TextBlock.ForegroundProperty, "AppMutedBrush");
        subtitle.Margin = new Thickness(0, 6, 0, 22);
        panel.Children.Add(subtitle);
        var details = new StackPanel();
        details.Children.Add(state);
        location.Margin = new Thickness(0, 10, 0, 0);
        location.SetResourceReference(TextBlock.ForegroundProperty, "AppMutedBrush");
        details.Children.Add(location);
        var card = new Border { CornerRadius = new CornerRadius(8), BorderThickness = new Thickness(1), Padding = new Thickness(20), Child = details };
        card.SetResourceReference(Border.BackgroundProperty, "AppSurfaceBrush");
        card.SetResourceReference(Border.BorderBrushProperty, "AppBorderBrush");
        panel.Children.Add(card);
        force.SetResourceReference(Control.ForegroundProperty, "AppTextBrush");
        panel.Children.Add(force);
        var maps = Text("Drive mappings are left unchanged during uninstall.", 13);
        maps.SetResourceReference(TextBlock.ForegroundProperty, "AppMutedBrush");
        maps.Margin = new Thickness(0, 0, 0, 20);
        panel.Children.Add(maps);
        primary.SetResourceReference(StyleProperty, "AccentButton");
        var buttons = new StackPanel { Orientation = Orientation.Horizontal };
        buttons.Children.Add(primary); buttons.Children.Add(remove); buttons.Children.Add(close);
        panel.Children.Add(buttons);
        progress.SetResourceReference(StyleProperty, "StatusProgressBar");
        panel.Children.Add(progress);
        result.Margin = new Thickness(0, 18, 0, 0);
        panel.Children.Add(result);
        var log = new Button { Content = "Open installer log", HorizontalAlignment = HorizontalAlignment.Left, Margin = new Thickness(0, 14, 0, 0), Padding = new Thickness(10, 5, 10, 5) };
        log.Click += (_, _) =>
        {
            if (File.Exists(InstallLog.LogPath)) Process.Start(new ProcessStartInfo(InstallLog.LogPath) { UseShellExecute = true });
            else result.Text = "No installer log is available.";
        };
        panel.Children.Add(log);
        Content = new ScrollViewer { Content = panel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto };
        primary.Click += async (_, _) => await Run("install");
        remove.Click += async (_, _) => await Run("uninstall");
        close.Click += (_, _) => { if (!working) Close(); };
        Closing += (_, args) => { if (working) args.Cancel = true; };
        RefreshState();
    }

    private static TextBlock Text(string value, double size) => new() { Text = value, FontSize = size, TextWrapping = TextWrapping.Wrap };

    private void RefreshState()
    {
        try
        {
            var installed = InstallRegistration.Read(tolerateInvalidConfig: true);
            var config = installed.Config ?? InstallerConfiguration.ReadPackage(AppContext.BaseDirectory);
            string agent = Path.Combine(AppContext.BaseDirectory, "DriveMapper-Agent.exe");
            string? incoming = File.Exists(agent) ? FileVersionInfo.GetVersionInfo(agent).FileVersion : null;
            state.Text = $"Installed version: {installed.Version ?? (installed.Config == null ? "Not installed" : "Needs repair")}\nPackage version: {incoming ?? "Payload unavailable"}";
            location.Text = config.TargetDirectory;
            primary.Content = installed.Config == null ? "Install" : installed.Version == null || installed.Version == incoming ? "Repair" : "Update";
            primary.IsEnabled = incoming != null && !working;
            remove.IsEnabled = (installed.Config != null || Directory.Exists(config.TargetDirectory)) && !working;
        }
        catch (Exception error)
        {
            state.Text = "Setup could not read installation details.";
            location.Text = error.Message;
            primary.IsEnabled = false;
        }
    }

    private async Task Run(string command)
    {
        if (working) return;
        bool purge = force.IsChecked == true && command == "uninstall";
        working = true;
        primary.IsEnabled = remove.IsEnabled = force.IsEnabled = close.IsEnabled = false;
        result.Text = "Working…";
        progress.Visibility = Visibility.Visible;
        progress.IsIndeterminate = true;
        var updates = new Progress<string>(message => result.Text = message);
        try
        {
            var outcome = await Task.Run(() => Program.Execute(new(command, false, purge), message => ((IProgress<string>)updates).Report(message)));
            Environment.ExitCode = outcome.ExitCode;
            // Drain already-posted progress before presenting the final result.
            await Dispatcher.InvokeAsync(() => { }, System.Windows.Threading.DispatcherPriority.Background);
            result.Text = outcome.Message;
        }
        catch (Exception error) { result.Text = error.Message; Environment.ExitCode = 1603; }
        finally
        {
            working = false;
            progress.IsIndeterminate = false; progress.Visibility = Visibility.Collapsed;
            force.IsEnabled = close.IsEnabled = true;
            RefreshState(); // Result is a separate field and remains visible.
        }
    }
}
