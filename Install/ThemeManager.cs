// Adapted from ApplicadAgent SharedUI; follows the Windows app theme preference.
using System;
using System.Windows;
using System.Windows.Media;
using Microsoft.Win32;

namespace Install;

public static class ThemeManager
{
    public static void Apply(Application app)
    {
        var assembly = typeof(ThemeManager).Assembly.GetName().Name;
        app.Resources.MergedDictionaries.Add(new ResourceDictionary { Source = new Uri($"pack://application:,,,/{assembly};component/ThemeResources.xaml") });
        EventManager.RegisterClassHandler(typeof(Window), FrameworkElement.LoadedEvent, new RoutedEventHandler((sender, _) =>
        {
            if (sender is Window window && window.ReadLocalValue(FrameworkElement.StyleProperty) == DependencyProperty.UnsetValue)
                window.SetResourceReference(FrameworkElement.StyleProperty, typeof(Window));
        }));
        Refresh(app);
        UserPreferenceChangedEventHandler handler = (_, _) => app.Dispatcher.BeginInvoke(new Action(() => Refresh(app)));
        SystemEvents.UserPreferenceChanged += handler;
        app.Exit += (_, _) => SystemEvents.UserPreferenceChanged -= handler;
    }

    public static void Refresh(Application app)
    {
        var dark = false;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Themes\Personalize");
            dark = key?.GetValue("AppsUseLightTheme") is int light && light == 0;
        }
        catch (System.Security.SecurityException) { }
        Set("AppWindowBrush", dark ? "#1B1B1D" : "#F4F4F5");
        Set("AppSurfaceBrush", dark ? "#242426" : "#FFFFFF");
        Set("AppControlBrush", dark ? "#2C2C2F" : "#FAFAFA");
        Set("AppTextBrush", dark ? "#F2F2F3" : "#202024");
        Set("AppMutedBrush", dark ? "#B8B8BD" : "#68686F");
        Set("AppBorderBrush", dark ? "#414145" : "#DDDDDF");
        Set("AppHoverBrush", dark ? "#39353F" : "#F0ECF5");
        Set("AppPressedBrush", dark ? "#45404C" : "#E7E0EF");
        Set("AppAccentBrush", dark ? "#7546B7" : "#6338A1");
        Set("AppAccentHoverBrush", dark ? "#8757C7" : "#7549B7");
        Set("AppHeaderBrush", dark ? "#2C2735" : "#ECE8F2");
        Set("AppStatusBrush", dark ? "#27272A" : "#EDEDEF");
        // Drawer and all dynamically built owned dialogs share the same palette.
        app.Resources["ApplicationBackgroundBrush"] = app.Resources["AppSurfaceBrush"];
        void Set(string name, string hex)
        {
            var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex)); brush.Freeze(); app.Resources[name] = brush;
        }
    }
}
