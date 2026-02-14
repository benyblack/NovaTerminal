using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Layout;
using NovaTerminal.Controls;
using NovaTerminal.Core;
using Xunit;

namespace NovaTerminal.Tests
{
    public class PaneLayoutModelTests
    {
        [AvaloniaFact]
        public void FromControl_BuildsSplitTree_WithPaneIds()
        {
            var left = new TerminalPane { PaneId = Guid.NewGuid() };
            var right = new TerminalPane { PaneId = Guid.NewGuid() };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

            Grid.SetColumn(left, 0);
            Grid.SetColumn(right, 2);
            grid.Children.Add(left);
            grid.Children.Add(new GridSplitter { Width = 3, ResizeDirection = GridResizeDirection.Columns });
            grid.Children.Add(right);

            var model = PaneLayoutModel.FromControl(grid, left.PaneId, null, false);
            var ids = model.EnumeratePaneIds().ToList();

            Assert.Equal(2, ids.Count);
            Assert.Contains(left.PaneId, ids);
            Assert.Contains(right.PaneId, ids);
            Assert.IsType<PaneSplitNode>(model.Root);
        }

        [AvaloniaFact]
        public void FromControl_NormalizesRatios_ForStarColumns()
        {
            var first = new TerminalPane { PaneId = Guid.NewGuid() };
            var second = new TerminalPane { PaneId = Guid.NewGuid() };
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Star));
            grid.ColumnDefinitions.Add(new ColumnDefinition(3, GridUnitType.Pixel));
            grid.ColumnDefinitions.Add(new ColumnDefinition(1, GridUnitType.Star));

            Grid.SetColumn(first, 0);
            Grid.SetColumn(second, 2);
            grid.Children.Add(first);
            grid.Children.Add(new GridSplitter { Width = 3, ResizeDirection = GridResizeDirection.Columns });
            grid.Children.Add(second);

            var model = PaneLayoutModel.FromControl(grid, first.PaneId, null, false);
            var split = Assert.IsType<PaneSplitNode>(model.Root);

            Assert.Equal(2, split.Ratios.Count);
            Assert.InRange(split.Ratios[0], 0.74, 0.76);
            Assert.InRange(split.Ratios[1], 0.24, 0.26);
        }
    }
}
