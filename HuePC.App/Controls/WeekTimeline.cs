using System.Globalization;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using HuePC.App.ViewModels;
using HuePC.Core.Models;

namespace HuePC.App.Controls;

/// <summary>
/// Weekly alarm overview: seven day columns with one block per alarm. Blocks can be dragged
/// vertically to change the time (snapped to quarter hours) and horizontally to another day
/// (which replaces the alarm's day list with that single day).
/// </summary>
public sealed class WeekTimeline : FrameworkElement
{
    private const double HeaderHeight = 24;
    private const double HourLabelWidth = 36;
    private const double BlockHeight = 18;

    public static readonly DependencyProperty ItemsProperty = DependencyProperty.Register(
        nameof(Items), typeof(IEnumerable<ScheduleViewModel>), typeof(WeekTimeline),
        new FrameworkPropertyMetadata(null, FrameworkPropertyMetadataOptions.AffectsRender));

    private ScheduleViewModel? _dragItem;
    private TimeOnly _dragTime;
    private int _dragDay;
    private bool _dragging;

    public WeekTimeline()
    {
        Cursor = Cursors.SizeAll;
        ClipToBounds = true;
    }

    public event EventHandler<ScheduleMovedEventArgs>? ItemMoved;

    public IEnumerable<ScheduleViewModel>? Items
    {
        get => (IEnumerable<ScheduleViewModel>?)GetValue(ItemsProperty);
        set => SetValue(ItemsProperty, value);
    }

    protected override void OnRender(DrawingContext drawingContext)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= HourLabelWidth + 40 || height <= HeaderHeight + 60)
        {
            return;
        }

        var plotWidth = width - HourLabelWidth;
        var plotHeight = height - HeaderHeight;
        var columnWidth = plotWidth / 7;

        var background = new SolidColorBrush(Color.FromRgb(0x12, 0x17, 0x1A));
        drawingContext.DrawRoundedRectangle(background,
            new Pen(new SolidColorBrush(Color.FromRgb(0x2A, 0x33, 0x38)), 1),
            new Rect(0.5, 0.5, width - 1, height - 1), 10, 10);

        var typeface = new Typeface("Segoe UI Variable Text, Segoe UI");
        var dpi = VisualTreeHelper.GetDpi(this).PixelsPerDip;
        var gridPen = new Pen(new SolidColorBrush(Color.FromArgb(34, 0x8A, 0x99, 0xA6)), 1);
        var columnPen = new Pen(new SolidColorBrush(Color.FromArgb(48, 0x8A, 0x99, 0xA6)), 1);
        var labelBrush = new SolidColorBrush(Color.FromRgb(0x8F, 0xA9, 0x9A));

        var days = new[] { "Pzt", "Sal", "Çar", "Per", "Cum", "Cmt", "Paz" };
        for (var day = 0; day < 7; day++)
        {
            var header = new FormattedText(days[day], CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 11, labelBrush, dpi);
            drawingContext.DrawText(header, new Point(HourLabelWidth + day * columnWidth + columnWidth / 2 - header.Width / 2, 5));
            if (day > 0)
            {
                var x = HourLabelWidth + day * columnWidth;
                drawingContext.DrawLine(columnPen, new Point(x, HeaderHeight), new Point(x, height - 4));
            }
        }

        for (var hour = 0; hour <= 24; hour += 3)
        {
            var y = HeaderHeight + plotHeight * hour / 24.0;
            drawingContext.DrawLine(gridPen, new Point(HourLabelWidth, y), new Point(width - 6, y));
            if (hour < 24)
            {
                var label = new FormattedText($"{hour:00}:00", CultureInfo.CurrentUICulture, FlowDirection.LeftToRight, typeface, 10, labelBrush, dpi);
                drawingContext.DrawText(label, new Point(6, y - label.Height / 2));
            }
        }

        var nowY = HeaderHeight + plotHeight * (DateTime.Now.TimeOfDay.TotalMinutes / 1440.0);
        if (nowY is > HeaderHeight and < double.MaxValue && nowY < height)
        {
            var nowPen = new Pen(new SolidColorBrush(Color.FromArgb(120, 0xFF, 0x6B, 0x4A)), 1)
            {
                DashStyle = DashStyles.Dash
            };
            drawingContext.DrawLine(nowPen, new Point(HourLabelWidth, nowY), new Point(width - 6, nowY));
        }

        var items = Items?.ToList() ?? [];
        foreach (var schedule in items)
        {
            if (schedule.ParsedTime is not { } time)
            {
                continue;
            }

            for (var day = 0; day < 7; day++)
            {
                if (!HasDay(schedule.CurrentDays, day) || (ReferenceEquals(schedule, _dragItem) && _dragging))
                {
                    continue;
                }

                DrawBlock(drawingContext, typeface, dpi, schedule, time, day, columnWidth, plotHeight, 1.0);
            }
        }

        if (_dragging && _dragItem is not null)
        {
            DrawBlock(drawingContext, typeface, dpi, _dragItem, _dragTime, _dragDay, columnWidth, plotHeight, 0.75);
        }
    }

    private void DrawBlock(
        DrawingContext drawingContext,
        Typeface typeface,
        double dpi,
        ScheduleViewModel schedule,
        TimeOnly time,
        int day,
        double columnWidth,
        double plotHeight,
        double opacity)
    {
        var width = ActualWidth;
        var height = ActualHeight;
        var y = HeaderHeight + plotHeight * ((time.Hour * 60 + time.Minute) / 1440.0);
        var rect = new Rect(HourLabelWidth + day * columnWidth + 4, y - BlockHeight / 2, columnWidth - 8, BlockHeight);
        if (rect.Bottom < HeaderHeight || rect.Top > height)
        {
            return;
        }

        var fill = schedule.Enabled
            ? Color.FromArgb((byte)(220 * opacity), 0xD8, 0x5B, 0x3A)
            : Color.FromArgb((byte)(170 * opacity), 0x55, 0x60, 0x68);
        drawingContext.DrawRoundedRectangle(new SolidColorBrush(fill), null, rect, BlockHeight / 2, BlockHeight / 2);

        var label = new FormattedText(
            $"{time:HH\\:mm} {schedule.Name}",
            CultureInfo.CurrentUICulture,
            FlowDirection.LeftToRight,
            typeface,
            10,
            new SolidColorBrush(Color.FromArgb((byte)(255 * opacity), 0xFF, 0xFF, 0xFF)),
            dpi)
        {
            MaxTextWidth = Math.Max(20, rect.Width - 12),
            MaxLineCount = 1,
            Trimming = TextTrimming.CharacterEllipsis
        };
        drawingContext.DrawText(label, new Point(rect.X + 6, rect.Y + (rect.Height - label.Height) / 2));
    }

    protected override void OnMouseLeftButtonDown(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonDown(e);
        var position = e.GetPosition(this);
        var hit = HitTest(position);
        if (hit is null)
        {
            return;
        }

        _dragItem = hit.Value.Schedule;
        _dragTime = hit.Value.Time;
        _dragDay = hit.Value.Day;
        _dragging = true;
        CaptureMouse();
        InvalidateVisual();
        e.Handled = true;
    }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        if (!_dragging || _dragItem is null)
        {
            return;
        }

        var position = e.GetPosition(this);
        var plotHeight = ActualHeight - HeaderHeight;
        var columnWidth = (ActualWidth - HourLabelWidth) / 7;
        var minutes = Math.Clamp((position.Y - HeaderHeight) / plotHeight * 1440.0, 0, 1439);
        var snapped = (int)Math.Round(minutes / 15.0) * 15;
        _dragTime = new TimeOnly(Math.Clamp(snapped / 60, 0, 23), snapped % 60);
        _dragDay = Math.Clamp((int)Math.Floor((position.X - HourLabelWidth) / columnWidth), 0, 6);
        InvalidateVisual();
    }

    protected override void OnMouseLeftButtonUp(MouseButtonEventArgs e)
    {
        base.OnMouseLeftButtonUp(e);
        if (!_dragging || _dragItem is null)
        {
            return;
        }

        var schedule = _dragItem;
        var time = _dragTime;
        var day = _dragDay;
        _dragging = false;
        _dragItem = null;
        ReleaseMouseCapture();
        InvalidateVisual();
        ItemMoved?.Invoke(this, new ScheduleMovedEventArgs(schedule, time, day));
        e.Handled = true;
    }

    private (ScheduleViewModel Schedule, TimeOnly Time, int Day)? HitTest(Point position)
    {
        var plotHeight = ActualHeight - HeaderHeight;
        var columnWidth = (ActualWidth - HourLabelWidth) / 7;
        foreach (var schedule in (Items ?? []).Reverse())
        {
            if (schedule.ParsedTime is not { } time)
            {
                continue;
            }

            var day = (int)Math.Floor((position.X - HourLabelWidth) / columnWidth);
            if (day is < 0 or > 6 || !HasDay(schedule.CurrentDays, day))
            {
                continue;
            }

            var y = HeaderHeight + plotHeight * ((time.Hour * 60 + time.Minute) / 1440.0);
            var rect = new Rect(HourLabelWidth + day * columnWidth + 4, y - BlockHeight / 2, columnWidth - 8, BlockHeight);
            if (rect.Contains(position))
            {
                return (schedule, time, day);
            }
        }

        return null;
    }

    private static bool HasDay(ScheduleDays days, int index) => index switch
    {
        0 => days.HasFlag(ScheduleDays.Monday),
        1 => days.HasFlag(ScheduleDays.Tuesday),
        2 => days.HasFlag(ScheduleDays.Wednesday),
        3 => days.HasFlag(ScheduleDays.Thursday),
        4 => days.HasFlag(ScheduleDays.Friday),
        5 => days.HasFlag(ScheduleDays.Saturday),
        _ => days.HasFlag(ScheduleDays.Sunday)
    };

    public static ScheduleDays DayAt(int index) => index switch
    {
        0 => ScheduleDays.Monday,
        1 => ScheduleDays.Tuesday,
        2 => ScheduleDays.Wednesday,
        3 => ScheduleDays.Thursday,
        4 => ScheduleDays.Friday,
        5 => ScheduleDays.Saturday,
        _ => ScheduleDays.Sunday
    };
}

public sealed class ScheduleMovedEventArgs : EventArgs
{
    public ScheduleMovedEventArgs(ScheduleViewModel schedule, TimeOnly time, int dayIndex)
    {
        Schedule = schedule;
        Time = time;
        DayIndex = dayIndex;
    }

    public ScheduleViewModel Schedule { get; }
    public TimeOnly Time { get; }
    public int DayIndex { get; }
}
