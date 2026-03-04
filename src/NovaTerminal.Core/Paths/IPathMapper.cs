using System.Threading;
using System.Threading.Tasks;

namespace NovaTerminal.Core.Paths
{
    public interface IPathMapper
    {
        Task<string> MapAsync(string hostPath, CancellationToken ct = default);
    }

    public class IdentityPathMapper : IPathMapper
    {
        public Task<string> MapAsync(string hostPath, CancellationToken ct = default)
        {
            return Task.FromResult(hostPath);
        }
    }
}
