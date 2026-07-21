using System.Collections;
using System.Windows;
using System.Windows.Controls;

namespace ToolsForSteam.Splash;

public partial class StartupSplashView : UserControl
{
    public static readonly DependencyProperty GameCoversProperty = DependencyProperty.Register(
        nameof(GameCovers),
        typeof(IEnumerable),
        typeof(StartupSplashView),
        new PropertyMetadata(null));

    public static readonly DependencyProperty CustomImagePathProperty = DependencyProperty.Register(
        nameof(CustomImagePath),
        typeof(string),
        typeof(StartupSplashView),
        new PropertyMetadata(string.Empty));

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

    public StartupSplashView()
    {
        InitializeComponent();
    }

    public IEnumerable? GameCovers
    {
        get => (IEnumerable?)GetValue(GameCoversProperty);
        set => SetValue(GameCoversProperty, value);
    }

    public string CustomImagePath
    {
        get => (string)GetValue(CustomImagePathProperty);
        set => SetValue(CustomImagePathProperty, value ?? string.Empty);
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

}
