using Avalonia.Controls;
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
    }
}
