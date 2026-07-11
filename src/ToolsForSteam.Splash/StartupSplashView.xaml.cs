using System.Collections;
using System.IO;
using System.Windows;
using System.Windows.Controls;

namespace ToolsForSteam.Splash;

public partial class StartupSplashView : UserControl
{
    private static readonly Uri DefaultIconUri = new(
        "pack://application:,,,/ToolsForSteam.Splash;component/Assets/splash-steam-icon.png",
        UriKind.Absolute);

    public static readonly DependencyProperty GameCoversProperty = DependencyProperty.Register(
        nameof(GameCovers),
        typeof(IEnumerable),
        typeof(StartupSplashView),
        new PropertyMetadata(null));

    public static readonly DependencyProperty WallpaperPathProperty = DependencyProperty.Register(
        nameof(WallpaperPath),
        typeof(string),
        typeof(StartupSplashView),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty IconPathProperty = DependencyProperty.Register(
        nameof(IconPath),
        typeof(string),
        typeof(StartupSplashView),
        new PropertyMetadata(string.Empty, OnIconPathChanged));

    public static readonly DependencyProperty ShowTextProperty = DependencyProperty.Register(
        nameof(ShowText),
        typeof(bool),
        typeof(StartupSplashView),
        new PropertyMetadata(true));

    public static readonly DependencyProperty DetailTextProperty = DependencyProperty.Register(
        nameof(DetailText),
        typeof(string),
        typeof(StartupSplashView),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty StateTextProperty = DependencyProperty.Register(
        nameof(StateText),
        typeof(string),
        typeof(StartupSplashView),
        new PropertyMetadata(string.Empty));

    public static readonly DependencyProperty EffectiveIconSourceProperty = DependencyProperty.Register(
        nameof(EffectiveIconSource),
        typeof(object),
        typeof(StartupSplashView),
        new PropertyMetadata(DefaultIconUri));

    public StartupSplashView()
    {
        InitializeComponent();
    }

    public IEnumerable? GameCovers
    {
        get => (IEnumerable?)GetValue(GameCoversProperty);
        set => SetValue(GameCoversProperty, value);
    }

    public string WallpaperPath
    {
        get => (string)GetValue(WallpaperPathProperty);
        set => SetValue(WallpaperPathProperty, value ?? string.Empty);
    }

    public string IconPath
    {
        get => (string)GetValue(IconPathProperty);
        set => SetValue(IconPathProperty, value ?? string.Empty);
    }

    public bool ShowText
    {
        get => (bool)GetValue(ShowTextProperty);
        set => SetValue(ShowTextProperty, value);
    }

    public string DetailText
    {
        get => (string)GetValue(DetailTextProperty);
        set => SetValue(DetailTextProperty, value ?? string.Empty);
    }

    public string StateText
    {
        get => (string)GetValue(StateTextProperty);
        set => SetValue(StateTextProperty, value ?? string.Empty);
    }

    public object EffectiveIconSource
    {
        get => GetValue(EffectiveIconSourceProperty);
        private set => SetValue(EffectiveIconSourceProperty, value);
    }

    private static void OnIconPathChanged(DependencyObject dependencyObject, DependencyPropertyChangedEventArgs args)
    {
        var view = (StartupSplashView)dependencyObject;
        var path = args.NewValue as string;
        view.EffectiveIconSource = !string.IsNullOrWhiteSpace(path) && File.Exists(path)
            ? new Uri(path!, UriKind.Absolute)
            : DefaultIconUri;
    }
}
