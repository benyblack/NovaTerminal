using System;
using System.IO;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using NovaTerminal.Core;

namespace NovaTerminal;

internal static class SshAskPassCommand
{
    internal const string ModeFlag = "--ssh-askpass";
    internal const string ModeEnvironmentVariable = "NOVA_SSH_ASKPASS";
    internal const string ProfileIdEnvironmentVariable = "NOVA_SSH_ASKPASS_PROFILE_ID";
    internal const string ProfileNameEnvironmentVariable = "NOVA_SSH_ASKPASS_PROFILE_NAME";
    internal const string ProfileUserEnvironmentVariable = "NOVA_SSH_ASKPASS_PROFILE_USER";
    internal const string ProfileHostEnvironmentVariable = "NOVA_SSH_ASKPASS_PROFILE_HOST";
    internal const string ProfilePortEnvironmentVariable = "NOVA_SSH_ASKPASS_PROFILE_PORT";

    public static bool IsSupportedCliMode(string[] args)
    {
        ArgumentNullException.ThrowIfNull(args);
        return Array.Exists(args, arg => string.Equals(arg, ModeFlag, StringComparison.Ordinal)) ||
               string.Equals(Environment.GetEnvironmentVariable(ModeEnvironmentVariable), "1", StringComparison.Ordinal);
    }

    public static int Execute(string[] args, TextWriter stdout, TextWriter stderr)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(stdout);
        ArgumentNullException.ThrowIfNull(stderr);

        try
        {
            string prompt = GetPrompt(args);
            TerminalProfile profile = CreateProfileFromEnvironment();

            if (IsPasswordPrompt(prompt))
            {
                string? vaultPassword = new VaultService().GetSshPasswordForProfile(profile);
                if (!string.IsNullOrEmpty(vaultPassword))
                {
                    stdout.WriteLine(vaultPassword);
                    return 0;
                }
            }

            var state = new AskPassState(prompt, profile);
            BuildAskPassApp(state).StartWithClassicDesktopLifetime(
                Array.Empty<string>(),
                ShutdownMode.OnExplicitShutdown);

            if (string.IsNullOrEmpty(state.Response))
            {
                return 1;
            }

            stdout.WriteLine(state.Response);
            return 0;
        }
        catch (Exception ex)
        {
            stderr.WriteLine($"NovaTerminal SSH askpass failed: {ex.Message}");
            return 2;
        }
    }

    private static string GetPrompt(string[] args)
    {
        foreach (string arg in args)
        {
            if (!string.Equals(arg, ModeFlag, StringComparison.Ordinal))
            {
                return arg;
            }
        }

        return "SSH authentication required.";
    }

    private static TerminalProfile CreateProfileFromEnvironment()
    {
        var profile = new TerminalProfile
        {
            Type = ConnectionType.SSH,
            Name = Environment.GetEnvironmentVariable(ProfileNameEnvironmentVariable) ?? string.Empty,
            SshUser = Environment.GetEnvironmentVariable(ProfileUserEnvironmentVariable) ?? string.Empty,
            SshHost = Environment.GetEnvironmentVariable(ProfileHostEnvironmentVariable) ?? string.Empty
        };

        if (Guid.TryParse(Environment.GetEnvironmentVariable(ProfileIdEnvironmentVariable), out Guid profileId))
        {
            profile.Id = profileId;
        }

        if (int.TryParse(Environment.GetEnvironmentVariable(ProfilePortEnvironmentVariable), out int port) && port > 0)
        {
            profile.SshPort = port;
        }

        return profile;
    }

    private static AppBuilder BuildAskPassApp(AskPassState state)
    {
        return AppBuilder.Configure(() => new AskPassApplication(state))
            .UsePlatformDetect()
            .WithInterFont()
            .LogToTrace();
    }

    private static bool IsPasswordPrompt(string prompt)
    {
        return prompt.Contains("password", StringComparison.OrdinalIgnoreCase);
    }

    private static bool IsSecretPrompt(string prompt)
    {
        return IsPasswordPrompt(prompt) ||
               prompt.Contains("passphrase", StringComparison.OrdinalIgnoreCase);
    }

    private sealed class AskPassState
    {
        public AskPassState(string prompt, TerminalProfile profile)
        {
            Prompt = string.IsNullOrWhiteSpace(prompt) ? "SSH authentication required." : prompt;
            Profile = profile;
        }

        public string Prompt { get; }
        public TerminalProfile Profile { get; }
        public string? Response { get; set; }
    }

    private sealed class AskPassApplication : Application
    {
        private readonly AskPassState _state;

        public AskPassApplication(AskPassState state)
        {
            _state = state;
        }

        public override void Initialize()
        {
            Styles.Add(new FluentTheme());
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                var window = new AskPassWindow(_state, () => desktop.Shutdown());
                desktop.MainWindow = window;
                window.Show();
            }

            base.OnFrameworkInitializationCompleted();
        }
    }

    private sealed class AskPassWindow : Window
    {
        private readonly AskPassState _state;
        private readonly Action _shutdown;
        private readonly TextBox _input;
        private readonly CheckBox _rememberPassword;

        public AskPassWindow(AskPassState state, Action shutdown)
        {
            _state = state;
            _shutdown = shutdown;

            Title = "NovaTerminal SSH Authentication";
            Width = 520;
            Height = 240;
            CanResize = false;
            WindowStartupLocation = WindowStartupLocation.CenterScreen;

            _input = new TextBox
            {
                PasswordChar = IsSecretPrompt(_state.Prompt) ? '*' : default(char),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            _rememberPassword = new CheckBox
            {
                Content = "Remember password",
                IsVisible = IsPasswordPrompt(_state.Prompt) && _state.Profile.Id != Guid.Empty
            };

            Content = BuildContent();
        }

        private Control BuildContent()
        {
            var cancelButton = new Button { Content = "Cancel", Width = 96 };
            cancelButton.Click += (_, _) => CloseWith(null);

            var submitButton = new Button { Content = "Submit", Width = 96 };
            submitButton.Click += (_, _) => CloseWith(_input.Text);

            return new Border
            {
                Padding = new Thickness(16),
                Child = new StackPanel
                {
                    Spacing = 12,
                    Children =
                    {
                        new TextBlock
                        {
                            Text = "SSH authentication",
                            FontSize = 18,
                            FontWeight = Avalonia.Media.FontWeight.SemiBold
                        },
                        new TextBlock
                        {
                            Text = _state.Prompt,
                            TextWrapping = Avalonia.Media.TextWrapping.Wrap
                        },
                        _input,
                        _rememberPassword,
                        new StackPanel
                        {
                            Orientation = Orientation.Horizontal,
                            HorizontalAlignment = HorizontalAlignment.Right,
                            Spacing = 8,
                            Children = { cancelButton, submitButton }
                        }
                    }
                }
            };
        }

        protected override void OnOpened(EventArgs e)
        {
            base.OnOpened(e);
            _input.Focus();
        }

        private void CloseWith(string? response)
        {
            _state.Response = response;
            if (!string.IsNullOrEmpty(response) && _rememberPassword.IsChecked == true)
            {
                new VaultService().SetSshPasswordForProfile(_state.Profile, response);
            }

            Close();
            Dispatcher.UIThread.Post(_shutdown);
        }
    }
}
