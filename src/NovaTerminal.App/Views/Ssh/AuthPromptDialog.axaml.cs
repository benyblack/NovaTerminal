using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Markup.Xaml;
using NovaTerminal.Core.Ssh.Interactions;
using NovaTerminal.ViewModels.Ssh;

namespace NovaTerminal.Views.Ssh;

public partial class AuthPromptDialog : Window
{
    public AuthPromptDialog()
    {
        InitializeComponent();
    }

    public AuthPromptDialog(AuthPromptViewModel viewModel) : this()
    {
        DataContext = viewModel;
        ApplyViewModel(viewModel);
    }

    private AuthPromptViewModel? ViewModel => DataContext as AuthPromptViewModel;

    private void InitializeComponent()
    {
        AvaloniaXamlLoader.Load(this);
    }

    private void ApplyViewModel(AuthPromptViewModel viewModel)
    {
        this.FindControl<TextBlock>("DialogTitle")!.Text = viewModel.Title;
        this.FindControl<TextBlock>("DialogMessage")!.Text = viewModel.Message;

        var host = this.FindControl<StackPanel>("PromptHost")!;
        host.Children.Clear();

        foreach (AuthPromptEntryViewModel prompt in viewModel.Prompts)
        {
            host.Children.Add(new TextBlock { Text = prompt.Prompt });
            var textBox = new TextBox
            {
                PasswordChar = prompt.IsSecret ? '*' : default(char),
                Text = prompt.Value,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            textBox.TextChanged += (_, __) => prompt.Value = textBox.Text ?? string.Empty;
            host.Children.Add(textBox);
        }
    }

    private void OnSubmitClick(object? sender, RoutedEventArgs e)
    {
        Close(ViewModel?.BuildResponse() ?? SshInteractionResponse.Cancel());
    }

    private void OnCancelClick(object? sender, RoutedEventArgs e)
    {
        Close(SshInteractionResponse.Cancel());
    }
}
