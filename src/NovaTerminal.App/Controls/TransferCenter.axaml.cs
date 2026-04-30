using Avalonia.Controls;
using Avalonia.Interactivity;
using NovaTerminal.Core;

namespace NovaTerminal.Controls
{
    public partial class TransferCenter : UserControl
    {
        public TransferCenter()
        {
            InitializeComponent();
            var list = this.FindControl<ListBox>("JobsList");
            if (list != null)
            {
                list.ItemsSource = SftpService.Instance.Jobs;
            }

            var clearFinished = this.FindControl<Button>("BtnClearFinished");
            if (clearFinished != null)
            {
                clearFinished.Click += (_, __) => SftpService.Instance.ClearFinishedJobs();
            }

            var clearFailed = this.FindControl<Button>("BtnClearFailed");
            if (clearFailed != null)
            {
                clearFailed.Click += (_, __) => SftpService.Instance.ClearFailedJobs();
            }

            var clearInactive = this.FindControl<Button>("BtnClearInactive");
            if (clearInactive != null)
            {
                clearInactive.Click += (_, __) => SftpService.Instance.ClearInactiveJobs();
            }
        }

        private void CancelTransfer_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: TransferJob job })
            {
                return;
            }

            SftpService.Instance.CancelJob(job.Id);
        }

        private void RemoveTransfer_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: TransferJob job })
            {
                return;
            }

            if (!job.CanRemove)
            {
                return;
            }

            SftpService.Instance.RemoveJob(job.Id);
        }
    }
}
