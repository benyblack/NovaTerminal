using Ntilde.Shell;
using Ntilde.Platform;
using Ntilde.VT;
using Xunit;

namespace Ntilde.Tests
{
    public class PaneEventStreamTests
    {
        [Fact]
        public void Publish_AppendsEventToSnapshot()
        {
            var beforeCount = PaneEventStream.Snapshot().Count;
            var evt = new PaneAuditEvent
            {
                Kind = PaneAuditEventKind.FocusChanged,
                TabId = Guid.NewGuid(),
                PaneId = Guid.NewGuid(),
                Details = "test"
            };

            PaneEventStream.Publish(evt);
            var snapshot = PaneEventStream.Snapshot();

            Assert.True(snapshot.Count >= beforeCount + 1);
            Assert.Contains(snapshot, e => e.TabId == evt.TabId && e.PaneId == evt.PaneId && e.Kind == evt.Kind);
        }
    }
}
