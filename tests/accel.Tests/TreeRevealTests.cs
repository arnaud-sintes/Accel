namespace Accel.Tests;

using System;
using System.Collections.Generic;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using Accel.App.Controls;
using Xunit;

/// <summary>
/// The scroll arithmetic behind "selecting a tab reveals its project folder in panel A": the pure offset
/// computation, and the walk over real <see cref="TreeViewItem"/> containers.
/// </summary>
public class TreeRevealTests
{
    // Viewport 100 tall, content 400 tall, currently scrolled to 150.
    //                                              current viewport extent folderTop targetTop targetBottom => expected
    [Theory]
    [InlineData(150, 100, 400, 10, 30, 50, 150)]    // everything already visible: no scroll
    [InlineData(150, 100, 400, -40, -20, 0, 110)]   // folder header above the top: scroll up to it
    [InlineData(150, 100, 400, 60, 120, 140, 190)]  // row below the bottom, folder fits with it: bottom at the edge
    [InlineData(150, 100, 400, -300, 150, 170, 220)] // row is further than a viewport from its folder: the row wins
    [InlineData(150, 100, 400, -500, -490, -470, 0)] // clamped at the top
    [InlineData(150, 100, 400, 500, 520, 540, 300)] // clamped at the bottom (extent - viewport)
    [InlineData(0, 100, 80, -10, -10, 10, 0)]       // content shorter than the viewport: never scrolls
    public void ComputeOffset_ScrollsTheLeastNeeded(
        double current, double viewport, double extent, double folderTop, double targetTop, double targetBottom, double expected)
    {
        Assert.Equal(expected, TreeReveal.ComputeOffset(current, viewport, extent, folderTop, targetTop, targetBottom), 3);
    }

    [Fact]
    public void ComputeOffset_ARootRow_IsRevealedByItsOwnRectangle()
    {
        Assert.Equal(110, TreeReveal.ComputeOffset(150, 100, 400, -40, -40, -20), 3);
    }

    [Fact]
    public void TryReveal_BringsAFarRowAndItsFolderHeaderIntoView()
    {
        RunOnSta(() =>
        {
            var (tree, scroller, folders) = BuildTree(folderCount: 4, childrenPerFolder: 8);
            var folder = folders[3];
            var row = (TreeViewItem)folder.Items[1];

            var outcome = TreeReveal.TryReveal(tree, scroller, new object[] { folder, row }, out var offset);
            scroller.UpdateLayout();

            Assert.Equal(TreeReveal.Outcome.Done, outcome);
            Assert.True(scroller.VerticalOffset > 0);
            Assert.Equal(scroller.VerticalOffset, offset, 1);
            AssertVisible(scroller, folder);   // the folder header...
            AssertVisible(scroller, row);      // ...and the row, both
        });
    }

    [Fact]
    public void TryReveal_ScrollsBackUpToAFolderAboveTheView()
    {
        RunOnSta(() =>
        {
            var (tree, scroller, folders) = BuildTree(folderCount: 4, childrenPerFolder: 8);
            scroller.ScrollToVerticalOffset(scroller.ScrollableHeight);
            scroller.UpdateLayout();

            var outcome = TreeReveal.TryReveal(tree, scroller, new object[] { folders[0] }, out _);
            scroller.UpdateLayout();

            Assert.Equal(TreeReveal.Outcome.Done, outcome);
            AssertVisible(scroller, folders[0]);
        });
    }

    [Fact]
    public void TryReveal_ACollapsedFolder_IsRevealedByItsHeader_AndStaysCollapsed()
    {
        RunOnSta(() =>
        {
            var (tree, scroller, folders) = BuildTree(folderCount: 4, childrenPerFolder: 8);
            folders[3].IsExpanded = false;
            tree.UpdateLayout();
            var hiddenRow = (TreeViewItem)folders[3].Items[2];

            var outcome = TreeReveal.TryReveal(tree, scroller, new object[] { folders[3], hiddenRow }, out _);
            scroller.UpdateLayout();

            Assert.Equal(TreeReveal.Outcome.Done, outcome);
            Assert.False(folders[3].IsExpanded);
            AssertVisible(scroller, folders[3]);
        });
    }

    [Fact]
    public void TryReveal_ARowWithoutAContainerYet_AsksForALayoutPass()
    {
        RunOnSta(() =>
        {
            var (tree, scroller, _) = BuildTree(folderCount: 2, childrenPerFolder: 2);

            var outcome = TreeReveal.TryReveal(tree, scroller, new object[] { new TreeViewItem() }, out var offset);

            Assert.Equal(TreeReveal.Outcome.NeedsLayout, outcome);
            Assert.Equal(scroller.VerticalOffset, offset);
        });
    }

    [Fact]
    public void TryReveal_WithoutAScrollerOrAChain_DoesNothing()
    {
        RunOnSta(() =>
        {
            var (tree, scroller, folders) = BuildTree(folderCount: 2, childrenPerFolder: 2);

            Assert.Equal(TreeReveal.Outcome.Nothing, TreeReveal.TryReveal(tree, null, new object[] { folders[0] }, out _));
            Assert.Equal(TreeReveal.Outcome.Nothing, TreeReveal.TryReveal(tree, scroller, new List<object>(), out _));
        });
    }

    /// <summary>A tree of expanded folders with simple fixed-height rows, laid out in a small viewport. Nothing is
    /// shown on screen: the tree is measured and arranged directly.</summary>
    private static (TreeView Tree, ScrollViewer Scroller, List<TreeViewItem> Folders) BuildTree(int folderCount, int childrenPerFolder)
    {
        var tree = new TreeView { Width = 240, Height = 120 };
        var folders = new List<TreeViewItem>();
        for (var f = 0; f < folderCount; f++)
        {
            var folder = new TreeViewItem { Header = new TextBlock { Text = "folder " + f, Height = 20 }, IsExpanded = true };
            for (var c = 0; c < childrenPerFolder; c++)
            {
                folder.Items.Add(new TreeViewItem { Header = new TextBlock { Text = $"row {f}.{c}", Height = 20 } });
            }

            tree.Items.Add(folder);
            folders.Add(folder);
        }

        tree.ApplyTemplate();
        tree.Measure(new Size(240, 120));
        tree.Arrange(new Rect(0, 0, 240, 120));
        tree.UpdateLayout();

        var scroller = Assert.IsType<ScrollViewer>(tree.Template.FindName("_tv_scrollviewer_", tree));
        Assert.True(scroller.ScrollableHeight > 0, "the fixture must overflow its viewport");
        return (tree, scroller, folders);
    }

    private static void AssertVisible(ScrollViewer scroller, TreeViewItem item)
    {
        var header = Assert.IsAssignableFrom<FrameworkElement>(item.Template.FindName("PART_Header", item));
        var frame = Assert.IsAssignableFrom<FrameworkElement>(scroller.Template.FindName("PART_ScrollContentPresenter", scroller));
        var top = header.TransformToAncestor(frame).Transform(new Point(0, 0)).Y;
        var bottom = top + header.ActualHeight;

        Assert.True(top >= -0.5, $"header top {top} is above the viewport");
        Assert.True(bottom <= scroller.ViewportHeight + 0.5, $"header bottom {bottom} is below the viewport ({scroller.ViewportHeight})");
    }

    private static void RunOnSta(Action action)
    {
        Exception? captured = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                captured = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (captured is not null)
        {
            throw captured;
        }
    }
}
