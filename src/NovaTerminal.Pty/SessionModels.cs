using System;
using System.Collections.Generic;

namespace NovaTerminal.Core
{
    public class NovaSession
    {
        public int ActiveTabIndex { get; set; } = 0;
        public List<TabSession> Tabs { get; set; } = new();
    }

    public class TabSession
    {
        public string? TabId { get; set; }
        public string Title { get; set; } = "Terminal";
        public string? UserTitle { get; set; }
        public bool IsPinned { get; set; }
        public bool IsProtected { get; set; }
        public PaneNode? Root { get; set; } // The root of the layout tree
        public string? ActivePaneId { get; set; }
        public string? ZoomedPaneId { get; set; }
        public bool BroadcastInputEnabled { get; set; }
    }

    public enum NodeType
    {
        Leaf,
        Split
    }

    public class PaneNode
    {
        public NodeType Type { get; set; }

        // For Splits
        public int SplitOrientation { get; set; } // 0=Horizontal, 1=Vertical
        public List<PaneNode> Children { get; set; } = new();
        public List<string> Sizes { get; set; } = new(); // "1*", "100px" etc.

        // For Leafs
        public string? ProfileId { get; set; }
        public string? SshProfileId { get; set; }
        public string? PaneId { get; set; }

        // Fallbacks for ad-hoc panes (no profile)
        public string? Command { get; set; }
        public string? Arguments { get; set; }
    }

    public class WorkspaceBundlePackage
    {
        public int Version { get; set; } = 1;
        public string WorkspaceName { get; set; } = "workspace";
        public DateTime CreatedUtc { get; set; } = DateTime.UtcNow;
        public string? ExportedBy { get; set; }
        public string PayloadJson { get; set; } = string.Empty;
        public string PayloadHashSha256 { get; set; } = string.Empty;
    }
}
