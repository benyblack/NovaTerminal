using System;
using System.Collections.Generic;
using System.Linq;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using NovaTerminal.Controls;

namespace NovaTerminal.Core
{
    public abstract class PaneLayoutNode
    {
    }

    public sealed class PaneLeafNode : PaneLayoutNode
    {
        public required Guid PaneId { get; init; }
    }

    public sealed class PaneSplitNode : PaneLayoutNode
    {
        public required Orientation Orientation { get; init; }
        public required IReadOnlyList<PaneLayoutNode> Children { get; init; }
        public required IReadOnlyList<double> Ratios { get; init; }
    }

    public sealed class PaneLayoutModel
    {
        public PaneLayoutNode? Root { get; init; }
        public Guid? ActivePaneId { get; init; }
        public Guid? ZoomedPaneId { get; init; }
        public bool BroadcastEnabled { get; init; }

        public static PaneLayoutModel FromControl(Control? root, Guid? activePaneId, Guid? zoomedPaneId, bool broadcastEnabled)
        {
            return new PaneLayoutModel
            {
                Root = BuildNode(root),
                ActivePaneId = activePaneId,
                ZoomedPaneId = zoomedPaneId,
                BroadcastEnabled = broadcastEnabled
            };
        }

        public IEnumerable<Guid> EnumeratePaneIds()
        {
            if (Root == null) yield break;

            foreach (var id in EnumeratePaneIds(Root))
            {
                yield return id;
            }
        }

        private static IEnumerable<Guid> EnumeratePaneIds(PaneLayoutNode node)
        {
            if (node is PaneLeafNode leaf)
            {
                yield return leaf.PaneId;
                yield break;
            }

            if (node is PaneSplitNode split)
            {
                foreach (var child in split.Children)
                {
                    foreach (var id in EnumeratePaneIds(child))
                    {
                        yield return id;
                    }
                }
            }
        }

        private static PaneLayoutNode? BuildNode(Control? control)
        {
            if (control == null) return null;

            if (control is TerminalPane pane)
            {
                return new PaneLeafNode { PaneId = pane.PaneId };
            }

            if (control is Grid grid && IsSplitGrid(grid))
            {
                bool horizontal = grid.ColumnDefinitions.Count > 1;
                var orderedChildren = horizontal
                    ? grid.Children
                        .OfType<Control>()
                        .Where(c => c is not GridSplitter)
                        .OrderBy(c => Grid.GetColumn(c))
                        .ToList()
                    : grid.Children
                        .OfType<Control>()
                        .Where(c => c is not GridSplitter)
                        .OrderBy(c => Grid.GetRow(c))
                        .ToList();

                var nodes = new List<PaneLayoutNode>();
                foreach (var child in orderedChildren)
                {
                    var childNode = BuildNode(child);
                    if (childNode != null)
                    {
                        nodes.Add(childNode);
                    }
                }

                if (nodes.Count == 0) return null;

                var ratios = horizontal
                    ? ExtractRatios(grid.ColumnDefinitions
                        .Where((_, i) => !grid.Children.OfType<GridSplitter>().Any(s => Grid.GetColumn(s) == i))
                        .Select(c => c.Width))
                    : ExtractRatios(grid.RowDefinitions
                        .Where((_, i) => !grid.Children.OfType<GridSplitter>().Any(s => Grid.GetRow(s) == i))
                        .Select(r => r.Height));

                return new PaneSplitNode
                {
                    Orientation = horizontal ? Orientation.Horizontal : Orientation.Vertical,
                    Children = nodes,
                    Ratios = ratios
                };
            }

            if (control is ContentControl contentControl && contentControl.Content is Control content)
            {
                return BuildNode(content);
            }

            if (control is Panel panel)
            {
                foreach (var child in panel.Children.OfType<Control>())
                {
                    var childNode = BuildNode(child);
                    if (childNode != null) return childNode;
                }
            }

            return null;
        }

        private static bool IsSplitGrid(Grid grid)
        {
            int childPaneCount = grid.Children.OfType<Control>().Count(c => c is not GridSplitter);
            return childPaneCount >= 2;
        }

        private static IReadOnlyList<double> ExtractRatios(IEnumerable<GridLength> lengths)
        {
            var weights = lengths.Select(length =>
            {
                if (length.IsStar) return Math.Max(0.001, length.Value);
                if (length.IsAbsolute) return Math.Max(0.001, length.Value);
                return 1.0;
            }).ToList();

            double total = weights.Sum();
            if (total <= 0.0001)
            {
                return Enumerable.Repeat(1.0, weights.Count).ToList();
            }

            return weights.Select(w => w / total).ToList();
        }
    }
}
