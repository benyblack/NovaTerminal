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
        }

        private void CancelTransfer_Click(object? sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: TransferJob job })
            {
                return;
            }

            SftpService.Instance.CancelJob(job.Id);
        }
    }
}
