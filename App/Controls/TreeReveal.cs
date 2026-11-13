namespace Accel.App.Controls;

using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

/// <summary>
/// Scrolls a <see cref="TreeView"/> so a chosen row - and the header of the folder it sits in - is visible,
/// with the smallest scroll that achieves it.
///
/// <para>Why this exists rather than <c>BringIntoView</c> or just selecting the row: selecting a
/// <see cref="TreeViewItem"/> never scrolls it; WPF only scrolls when the item takes keyboard focus, and a tab
/// click leaves keyboard focus in the tab strip. And <c>BringIntoView</c> goes through
/// <c>RequestBringIntoView</c>, which panel A swallows during rebuilds. Plain scroll arithmetic on the row's
/// header has neither problem, and its result is deterministic enough to unit-test.</para>
///
/// <para>Nothing here expands a folder (a folder the user collapsed stays collapsed) and nothing takes focus.</para>
/// </summary>
public static class TreeReveal
{
    /// <summary>What <see cref="TryReveal"/> found.</summary>
    public enum Outcome
    {
        /// <summary>The view was scrolled (or already showed the row).</summary>
        Done,

        /// <summary>A row on the way down has no container yet - layout has not run since it appeared. Try again
        /// after layout.</summary>
        NeedsLayout,

        /// <summary>No scroll viewer, or an empty chain: nothing to do.</summary>
        Nothing,
    }

    /// <summary>
    /// The vertical offset that shows what was asked for with the least scrolling. All positions are relative
    /// to the top of the visible area (negative = above it).
    ///
    /// <para>The folder header and the row are shown together when both fit in the viewport. When the row is
    /// further below its folder than a viewport is tall, the row wins (the folder header scrolls out of view).
    /// For a root row, pass the same rectangle for both.</para>
    /// </summary>
    public static double ComputeOffset(
        double currentOffset, double viewport, double extent,
        double folderTop, double targetTop, double targetBottom)
    {
        double top = targetBottom - folderTop <= viewport ? folderTop : targetTop;

        double delta = 0;
        if (top < 0)
        {
            delta = top;                       // too high: scroll up until the top is at the edge
        }
        else if (targetBottom > viewport)
        {
            delta = targetBottom - viewport;   // too low: scroll down until the bottom is at the edge
        }

        double maxOffset = Math.Max(0, extent - viewport);
        return Math.Clamp(currentOffset + delta, 0, maxOffset);
    }

    /// <summary>
    /// Walks <paramref name="chain"/> (the data items from a top-level row down to the target) through the
    /// realised containers and scrolls <paramref name="scroller"/>. A collapsed row ends the walk: its header is
    /// what gets revealed, since the row inside it does not exist on screen.
    /// </summary>
    /// <param name="newOffset">The vertical offset the view now has (or already had).</param>
    public static Outcome TryReveal(TreeView tree, ScrollViewer? scroller, IReadOnlyList<object> chain, out double newOffset)
    {
        newOffset = scroller?.VerticalOffset ?? 0;
        if (scroller is null || chain.Count == 0)
        {
            return Outcome.Nothing;
        }

        ItemsControl parent = tree;
        TreeViewItem? folder = null;
        TreeViewItem? target = null;

        for (var i = 0; i < chain.Count; i++)
        {
            if (parent.ItemContainerGenerator.ContainerFromItem(chain[i]) is not TreeViewItem item)
            {
                return Outcome.NeedsLayout;
            }

            folder ??= item;
            target = item;

            if (i < chain.Count - 1 && !item.IsExpanded)
            {
                break; // collapsed: reveal this folder's header and stop
            }

            parent = item;
        }

        var folderHeader = HeaderOf(folder!);
        var targetHeader = HeaderOf(target!);

        // Measure from the scroll content presenter, not the ScrollViewer: the viewport is the presenter's area,
        // and a ScrollViewer with padding or a border places that area a pixel or two inside itself.
        var frame = scroller.Template?.FindName("PART_ScrollContentPresenter", scroller) as FrameworkElement ?? scroller;
        if (!folderHeader.IsDescendantOf(frame) || !targetHeader.IsDescendantOf(frame))
        {
            return Outcome.NeedsLayout; // not laid out into the scroll viewer yet
        }

        double folderTop = TopIn(frame, folderHeader);
        double targetTop = TopIn(frame, targetHeader);
        double targetBottom = targetTop + targetHeader.ActualHeight;

        newOffset = ComputeOffset(
            scroller.VerticalOffset, scroller.ViewportHeight, scroller.ExtentHeight,
            folderTop, targetTop, targetBottom);

        if (Math.Abs(newOffset - scroller.VerticalOffset) > 0.5)
        {
            scroller.ScrollToVerticalOffset(newOffset);
        }

        return Outcome.Done;
    }

    // The part of the row that is its own line: PART_Header in the app's TreeViewItem template (and in WPF's
    // stock one). Using the whole item would include its expanded children, i.e. the entire subtree.
    private static FrameworkElement HeaderOf(TreeViewItem item) =>
        item.Template?.FindName("PART_Header", item) as FrameworkElement ?? item;

    private static double TopIn(FrameworkElement frame, FrameworkElement element) =>
        element.TransformToAncestor(frame).Transform(new Point(0, 0)).Y;
}
