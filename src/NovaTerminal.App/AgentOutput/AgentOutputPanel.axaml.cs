using System;
using System.ComponentModel;
using Avalonia.Controls;
using Avalonia.Input.Platform;
using Avalonia.Interactivity;
using Avalonia.Threading;

namespace NovaTerminal.AgentOutput;

/// <summary>
/// The Agent Output side panel: header, empty state, and the markdown-rendered output region.
/// </summary>
/// <remarks>
/// <para>
/// The view-model is attached with <see cref="SetViewModel"/> rather than DataContext binding:
/// the panel's markdown content cannot be a binding anyway (it is a rebuilt control tree, not a
/// list), and keeping everything in code keeps the compiled-bindings surface to zero for a view
/// that exists to host generated content.
/// </para>
/// <para>
/// <b>Follow tail.</b> While a command streams, the panel keeps the newest content on screen -
/// but only if the user is already at the bottom. A reader who scrolled up to study an earlier
/// section is never yanked back down; the release condition is scrolling back to the end.
/// </para>
/// </remarks>
public partial class AgentOutputPanel : UserControl
{
    private AgentOutputViewModel? _viewModel;
    private bool _pinnedToBottom = true;
    private double _lastOffsetY = -1;

    public AgentOutputPanel()
    {
        InitializeComponent();

        // Pin-state tracking: only an actual offset move counts as the user's intent. Extent
        // growth from streaming content fires ScrollChanged without moving the offset, and that
        // must not unpin a reader sitting at the bottom.
        ContentScroll.ScrollChanged += (_, _) =>
        {
            double offsetY = ContentScroll.Offset.Y;
            bool offsetMoved = Math.Abs(offsetY - _lastOffsetY) > 0.5;
            _lastOffsetY = offsetY;

            if (!offsetMoved)
            {
                return;
            }

            _pinnedToBottom = offsetY + ContentScroll.Viewport.Height >= ContentScroll.Extent.Height - 4;
        };
    }

    /// <summary>Raised when the user asks to close the panel (the ✕ button).</summary>
    public event Action? CloseRequested;

    /// <summary>Attaches the pane's view model. May be called once; the pane owns the lifetime.</summary>
    public void SetViewModel(AgentOutputViewModel viewModel)
    {
        if (_viewModel is not null)
        {
            _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        }

        _viewModel = viewModel;
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;
        Render();
    }

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // Always on the UI thread: the tracker posts its updates through the pane's dispatcher.
        if (e.PropertyName is nameof(AgentOutputViewModel.MarkdownText) or nameof(AgentOutputViewModel.HasContent))
        {
            Render();
        }
        else if (e.PropertyName is nameof(AgentOutputViewModel.StatusText) or nameof(AgentOutputViewModel.IsStreaming))
        {
            UpdateStatus();
        }
    }

    private void Render()
    {
        string markdown = _viewModel?.MarkdownText ?? string.Empty;
        bool hasContent = markdown.Length > 0;

        EmptyState.IsVisible = !hasContent;
        ContentScroll.IsVisible = hasContent;
        BtnCopyAll.IsEnabled = hasContent;

        if (!hasContent)
        {
            MarkdownHost.Children.Clear();
            return;
        }

        // The scroll offset is captured before the swap and restored after: a reader parked mid-
        // document (follow-tail disengaged) stays parked when the content beneath them changes.
        bool wasPinned = _pinnedToBottom;
        bool isStreaming = _viewModel?.IsStreaming ?? false;

        MarkdownHost.Children.Clear();
        Control rendered = MarkdownRenderer.Build(
            markdown,
            this,
            onCopyText: text => CopyToClipboard(text),
            onOpenLink: OpenLink);
        MarkdownHost.Children.Add(rendered);

        if (wasPinned && isStreaming)
        {
            // ContentScroll must be measured before ScrollToEnd means anything; layout runs at
            // the end of this dispatcher cycle.
            Dispatcher.UIThread.Post(() =>
            {
                if (_pinnedToBottom)
                {
                    ContentScroll.ScrollToEnd();
                }
            }, DispatcherPriority.Background);
        }
    }

    private void UpdateStatus()
    {
        string? status = _viewModel?.StatusText;
        StatusText.Text = status;
        StatusText.IsVisible = !string.IsNullOrEmpty(status);
    }

    private void OnCopyAllClick(object? sender, RoutedEventArgs e)
    {
        string markdown = _viewModel?.MarkdownText;
        if (!string.IsNullOrEmpty(markdown))
        {
            CopyToClipboard(markdown);
        }
    }

    private void OnCloseClick(object? sender, RoutedEventArgs e)
        => CloseRequested?.Invoke();

    private async void CopyToClipboard(string text)
    {
        // TopLevel is null in headless tests and before the pane attaches; a copy that silently
        // does nothing there is the correct degradation.
        var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
        if (clipboard is not null)
        {
            await clipboard.SetTextAsync(text);
        }
    }

    private void OpenLink(string url)
    {
        // Same shell-open shape as MainWindow.OpenPathInShell: UseShellExecute resolves the
        // platform handler (browser on https/mailto) without this app linking a launcher.
        try
        {
            _ = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = url,
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AgentOutputPanel] Opening link failed: {ex.Message}");
        }
    }
}
