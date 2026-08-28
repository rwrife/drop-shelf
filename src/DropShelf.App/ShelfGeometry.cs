using DropShelf.Core;

namespace DropShelf.App;

public readonly record struct ShelfBounds(int Left, int Top, int Width, int Height)
{
    public int Right => Left + Width;
    public int Bottom => Top + Height;
}

public static class ShelfGeometry
{
    public const int MinimumReachableSize = 44;
    private const int WorkAreaMargin = 100;

    public static ShelfBounds Recover(
        ShelfBounds previous, ShelfBounds workArea, DockEdge edge, bool collapsed, double renderScaling = 1)
    {
        if (workArea.Width <= 0 || workArea.Height <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(workArea));
        }
        if (!double.IsFinite(renderScaling) || renderScaling <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(renderScaling));
        }

        int minimumReachablePixels = Math.Max(1, (int)Math.Ceiling(MinimumReachableSize * renderScaling));

        int width;
        int height;
        if (collapsed)
        {
            width = Math.Min(minimumReachablePixels, workArea.Width);
            height = Math.Min(minimumReachablePixels, workArea.Height);
        }
        else if (edge is DockEdge.Left or DockEdge.Right)
        {
            width = RecoverThickness(previous.Width, workArea.Width, minimumReachablePixels);
            height = RecoverAlongEdgeLength(previous.Height, workArea.Height, minimumReachablePixels, renderScaling);
        }
        else
        {
            width = RecoverAlongEdgeLength(previous.Width, workArea.Width, minimumReachablePixels, renderScaling);
            height = RecoverThickness(previous.Height, workArea.Height, minimumReachablePixels);
        }

        int left = edge switch
        {
            DockEdge.Left => workArea.Left,
            DockEdge.Right => workArea.Right - width,
            DockEdge.Top or DockEdge.Bottom => Math.Clamp(previous.Left, workArea.Left, workArea.Right - width),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
        int top = edge switch
        {
            DockEdge.Top => workArea.Top,
            DockEdge.Bottom => workArea.Bottom - height,
            DockEdge.Left or DockEdge.Right => Math.Clamp(previous.Top, workArea.Top, workArea.Bottom - height),
            _ => throw new ArgumentOutOfRangeException(nameof(edge)),
        };
        return new(left, top, width, height);
    }

    private static int RecoverAlongEdgeLength(
        int previous, int available, int minimumReachablePixels, double renderScaling)
    {
        int workAreaMarginPixels = (int)Math.Ceiling(WorkAreaMargin * renderScaling);
        int maximum = available > minimumReachablePixels + workAreaMarginPixels
            ? available - workAreaMarginPixels
            : available;
        int minimum = Math.Min(minimumReachablePixels, maximum);
        return Math.Clamp(previous, minimum, maximum);
    }

    private static int RecoverThickness(int previous, int available, int minimumReachablePixels) =>
        Math.Clamp(previous, Math.Min(minimumReachablePixels, available), available);
}
