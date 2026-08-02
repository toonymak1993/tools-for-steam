using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media.Animation;

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

    public static readonly DependencyProperty HeadlineTextProperty = DependencyProperty.Register(
        nameof(HeadlineText),
        typeof(string),
        typeof(StartupSplashView),
        new PropertyMetadata("Preparing Steam"));

    public static readonly DependencyProperty ShowRecoveryActionsProperty = DependencyProperty.Register(
        nameof(ShowRecoveryActions),
        typeof(bool),
        typeof(StartupSplashView),
        new PropertyMetadata(false));

    public static readonly DependencyProperty CanRestartSteamProperty = DependencyProperty.Register(
        nameof(CanRestartSteam),
        typeof(bool),
        typeof(StartupSplashView),
        new PropertyMetadata(false));

    public static readonly DependencyProperty ContinueCommandProperty = DependencyProperty.Register(
        nameof(ContinueCommand), typeof(ICommand), typeof(StartupSplashView));

    public static readonly DependencyProperty RestartCommandProperty = DependencyProperty.Register(
        nameof(RestartCommand), typeof(ICommand), typeof(StartupSplashView));

    public static readonly DependencyProperty DesktopCommandProperty = DependencyProperty.Register(
        nameof(DesktopCommand), typeof(ICommand), typeof(StartupSplashView));

    public static readonly DependencyProperty ContinueLabelProperty = DependencyProperty.Register(
        nameof(ContinueLabel), typeof(string), typeof(StartupSplashView), new PropertyMetadata("A  Keep waiting"));

    public static readonly DependencyProperty RestartLabelProperty = DependencyProperty.Register(
        nameof(RestartLabel), typeof(string), typeof(StartupSplashView), new PropertyMetadata("X  Restart Steam"));

    public static readonly DependencyProperty DesktopLabelProperty = DependencyProperty.Register(
        nameof(DesktopLabel), typeof(string), typeof(StartupSplashView), new PropertyMetadata("Y  Open desktop"));

    public static readonly DependencyProperty RecoveryHeadlineTextProperty = DependencyProperty.Register(
        nameof(RecoveryHeadlineText),
        typeof(string),
        typeof(StartupSplashView),
        new PropertyMetadata("Steam is taking longer than usual"));

    public StartupSplashView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (SystemParameters.ClientAreaAnimation)
            {
                return;
            }

            Dispatcher.BeginInvoke(() =>
            {
                foreach (var dot in new[] { Dot1, Dot2, Dot3 })
                {
                    dot.BeginAnimation(OpacityProperty, null);
                    dot.Opacity = 0.65;
                }
            });
        };
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

    public string HeadlineText
    {
        get => (string)GetValue(HeadlineTextProperty);
        set => SetValue(HeadlineTextProperty, value ?? string.Empty);
    }

    public bool ShowRecoveryActions
    {
        get => (bool)GetValue(ShowRecoveryActionsProperty);
        set => SetValue(ShowRecoveryActionsProperty, value);
    }

    public bool CanRestartSteam
    {
        get => (bool)GetValue(CanRestartSteamProperty);
        set => SetValue(CanRestartSteamProperty, value);
    }

    public ICommand? ContinueCommand
    {
        get => (ICommand?)GetValue(ContinueCommandProperty);
        set => SetValue(ContinueCommandProperty, value);
    }

    public ICommand? RestartCommand
    {
        get => (ICommand?)GetValue(RestartCommandProperty);
        set => SetValue(RestartCommandProperty, value);
    }

    public ICommand? DesktopCommand
    {
        get => (ICommand?)GetValue(DesktopCommandProperty);
        set => SetValue(DesktopCommandProperty, value);
    }

    public string ContinueLabel
    {
        get => (string)GetValue(ContinueLabelProperty);
        set => SetValue(ContinueLabelProperty, value ?? string.Empty);
    }

    public string RestartLabel
    {
        get => (string)GetValue(RestartLabelProperty);
        set => SetValue(RestartLabelProperty, value ?? string.Empty);
    }

    public string DesktopLabel
    {
        get => (string)GetValue(DesktopLabelProperty);
        set => SetValue(DesktopLabelProperty, value ?? string.Empty);
    }

    public string RecoveryHeadlineText
    {
        get => (string)GetValue(RecoveryHeadlineTextProperty);
        set => SetValue(RecoveryHeadlineTextProperty, value ?? string.Empty);
    }

}
