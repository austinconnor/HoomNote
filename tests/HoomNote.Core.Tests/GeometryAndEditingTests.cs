using System.Numerics;
using HoomNote.Canvas.Geometry;
using HoomNote.Canvas.Interaction;
using HoomNote.Canvas.Spatial;
using HoomNote.Core.Documents;
using HoomNote.Core.Editing;

namespace HoomNote.Core.Tests;

public sealed class GeometryAndEditingTests
{
    [Fact]
    public void OneFingerTouch_PansWithoutChangingZoom()
    {
        var result = TouchViewportMath.Pan(
            1.75,
            new Vector2(12, -8),
            new PointD(100, 200),
            new PointD(145, 172));

        Assert.Equal(1.75, result.Zoom);
        Assert.Equal(57, result.Pan.X, 4);
        Assert.Equal(-36, result.Pan.Y, 4);
    }

    [Theory]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(false, true, true, false, false, true)]
    [InlineData(false, true, false, true, false, true)]
    [InlineData(false, true, false, false, true, true)]
    [InlineData(false, true, false, false, false, false)]
    [InlineData(false, false, false, false, false, false)]
    public void TouchRouting_OnlySendsFingerAndPromotedFingerToNavigation(
        bool reportedAsTouch,
        bool reportedAsMouse,
        bool generated,
        bool nativeTouch,
        bool hasContactArea,
        bool expected)
    {
        Assert.Equal(expected,
            TouchInputPolicy.IsNavigationContact(
                reportedAsTouch, reportedAsMouse, generated, nativeTouch, hasContactArea));
    }

    [Fact]
    public void TwoFingerPinch_IgnoresCentroidTranslationWhenSpreadIsUnchanged()
    {
        var stationary = TouchViewportMath.PinchOnly(
            1.4, 1, new PointD(120, 180),
            new PointD(400, 300), new PointD(400, 300),
            new SizeD(800, 1_000), new SizeD(1_200, 900));
        var translated = TouchViewportMath.PinchOnly(
            1.4, 1, new PointD(120, 180),
            new PointD(400, 300), new PointD(620, 470),
            new SizeD(800, 1_000), new SizeD(1_200, 900));

        Assert.Equal(stationary.Zoom, translated.Zoom);
        Assert.Equal(stationary.Pan, translated.Pan);
    }

    [Fact]
    public void TouchPinch_KeepsPageAnchorBeneathMovingCentroid()
    {
        var result = TouchViewportMath.Pinch(
            startZoom: 1,
            scale: 2,
            pageAnchor: new PointD(220, 180),
            screenAnchor: new PointD(640, 360),
            pageSize: new SizeD(800, 1_000),
            viewportSize: new SizeD(1_200, 900));

        var centeredX = (1_200 - 800 * result.Zoom) / 2d;
        var centeredY = (900 - 1_000 * result.Zoom) / 2d;
        Assert.Equal(2, result.Zoom);
        Assert.Equal(640, 220 * result.Zoom + centeredX + result.Pan.X, 4);
        Assert.Equal(360, 180 * result.Zoom + centeredY + result.Pan.Y, 4);
    }

    [Theory]
    [InlineData(0.001, 0.08)]
    [InlineData(100, 8)]
    public void TouchPinch_ClampsZoom(double scale, double expectedZoom)
    {
        var result = TouchViewportMath.Pinch(1, scale, new PointD(0, 0), new PointD(0, 0),
            new SizeD(800, 1_000), new SizeD(1_200, 900));

        Assert.Equal(expectedZoom, result.Zoom, 4);
    }

    [Fact]
    public void SegmentEraser_SplitsAStrokeWithoutRasterizingIt()
    {
        var stroke = new InkStrokeObject
        {
            Points = Enumerable.Range(0, 21)
                .Select(index => new InkPoint(index * 5, 50, 0.7f, TimestampMicroseconds: index * 1_000))
                .ToList(),
            Style = new InkStyle { Width = 2 }
        };

        var result = SegmentEraser.Erase(stroke, [new PointD(50, 40), new PointD(50, 60)], 5);

        Assert.Equal(2, result.Count);
        Assert.All(result, fragment =>
        {
            Assert.Equal(stroke.Id, fragment.ParentStrokeId);
            Assert.NotEqual(stroke.Id, fragment.Id);
            Assert.All(fragment.Points, point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
        });
        Assert.True(result[0].Points.Max(point => point.X) < result[1].Points.Min(point => point.X));
    }

    [Fact]
    public void SegmentEraser_PreservesUntouchedStrokeIdentity()
    {
        var stroke = new InkStrokeObject { Points = [new InkPoint(0, 0), new InkPoint(20, 20)] };
        var result = SegmentEraser.Erase(stroke, [new PointD(100, 100)], 4);
        Assert.Single(result);
        Assert.Same(stroke, result[0]);
    }

    [Fact]
    public void SelectionTransformer_ProvidesEightResizeHandlesAndRotation()
    {
        var handles = SelectionTransformer.GetHandles(new RectD(10, 20, 200, 100));
        Assert.Equal(9, handles.Count);
        Assert.Equal(new PointD(110, -12), handles[TransformHandle.Rotate]);
        Assert.Equal(TransformHandle.BottomRight,
            SelectionTransformer.HitHandle(new RectD(10, 20, 200, 100), new PointD(210, 120)));
    }

    [Fact]
    public void RotationTransform_KeepsCenterFixedAndFinite()
    {
        var bounds = new RectD(0, 0, 100, 100);
        var transform = SelectionTransformer.CreateTransform(
            TransformHandle.Rotate, bounds, new PointD(50, 0), new PointD(100, 50));
        var center = transform.Apply(bounds.Center);
        Assert.True(transform.IsFinite);
        Assert.Equal(50, center.X, 3);
        Assert.Equal(50, center.Y, 3);
    }

    [Fact]
    public void SpatialIndex_OnlyReturnsObjectsInTheViewport()
    {
        var nearby = new ShapeObject { Bounds = new RectD(10, 10, 40, 40) };
        var distant = new ShapeObject { Bounds = new RectD(2_000, 2_000, 40, 40) };
        var index = new SpatialIndex();
        index.Rebuild([nearby, distant]);

        var result = index.Query(new RectD(0, 0, 200, 200));

        Assert.Contains(nearby, result);
        Assert.DoesNotContain(distant, result);
    }

    [Fact]
    public void SpatialIndex_RemoveImmediatelyExcludesAnErasedObject()
    {
        var stroke = new InkStrokeObject
        {
            Points = [new InkPoint(10, 10), new InkPoint(80, 80)]
        };
        var index = new SpatialIndex();
        index.Add(stroke);

        Assert.True(index.Remove(stroke.Id));

        Assert.Empty(index.Query(new RectD(0, 0, 100, 100)));
        Assert.Equal(0, index.Count);
    }

    [Fact]
    public void CommandHistory_RoundTripsAddUndoAndRedo()
    {
        var document = HoomNoteDocument.Create("Test");
        var page = new NotePage();
        document.Pages.Add(page);
        document.Sections[0].PageIds.Add(page.Id);
        var stroke = new InkStrokeObject { Points = [new InkPoint(1, 2)] };
        var history = new CommandHistory();

        history.Execute(new AddObjectCommand(page.Id, stroke), document);
        Assert.Single(page.Objects);
        Assert.True(history.CanUndo);

        history.Undo(document);
        Assert.Empty(page.Objects);
        Assert.True(history.CanRedo);

        history.Redo(document);
        Assert.Single(page.Objects);
    }

    [Fact]
    public void DeletePageCommand_RemovesAndRestoresPageAndSectionOrder()
    {
        var document = HoomNoteDocument.Create("Test");
        var first = new NotePage { Title = "Page 1" };
        var second = new NotePage { Title = "Page 2" };
        document.Pages.Add(first);
        document.Pages.Add(second);
        document.Sections[0].PageIds.Add(first.Id);
        document.Sections[0].PageIds.Add(second.Id);
        var firstId = first.Id;
        var history = new CommandHistory();

        history.Execute(new DeletePageCommand(firstId), document);

        Assert.Single(document.Pages);
        Assert.DoesNotContain(firstId, document.Sections[0].PageIds);

        history.Undo(document);

        Assert.Equal(firstId, document.Pages[0].Id);
        Assert.Equal(firstId, document.Sections[0].PageIds[0]);
    }

    [Fact]
    public void DeletePageCommand_AllowsZeroPageNotebookAndUndo()
    {
        var document = HoomNoteDocument.Create("Test");
        var page = new NotePage();
        document.Pages.Add(page);
        document.Sections[0].PageIds.Add(page.Id);
        var history = new CommandHistory();

        history.Execute(new DeletePageCommand(page.Id), document);
        Assert.Empty(document.Pages);
        Assert.Empty(document.Sections[0].PageIds);

        history.Undo(document);
        Assert.Single(document.Pages);
        Assert.Equal(page.Id, document.Pages[0].Id);
    }

    [Fact]
    public void Create_PagedNotebookStartsWithNoPages()
    {
        var document = HoomNoteDocument.Create("Empty notebook");

        Assert.Empty(document.Pages);
        Assert.Single(document.Sections);
        Assert.Empty(document.Sections[0].PageIds);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(double.NegativeInfinity)]
    public void InkPointNormalize_RemovesNonFinitePressure(double pressure)
    {
        var normalized = new InkPoint(0, 0, (float)pressure).Normalize();
        Assert.True(float.IsFinite(normalized.Pressure));
        Assert.InRange(normalized.Pressure, 0.01f, 1f);
    }

    [Fact]
    public void InkStyleNormalize_KeepsOnlyHighlighterTranslucent()
    {
        Assert.Equal(1f, new InkStyle { Tool = InkToolKind.Pen, Opacity = 0.3f }.Normalize().Opacity);
        Assert.Equal(1f, new InkStyle { Tool = InkToolKind.Pencil, Opacity = 0.3f }.Normalize().Opacity);
        Assert.Equal(0.3f, new InkStyle { Tool = InkToolKind.Highlighter, Opacity = 0.3f }.Normalize().Opacity);
    }

    [Fact]
    public void ConstantWidthPen_UsesFastRoundedCenterlineAtAnyThickness()
    {
        var style = new InkStyle
        {
            Tool = InkToolKind.Pen,
            Width = 24,
            PressureEnabled = false,
            PressureSensitivity = 0
        };

        Assert.True(StrokeOutlineBuilder.UsesCenterlineStroke(style));
    }

    [Fact]
    public void SourceFidelityCenterline_ReusesImportedPointStorage()
    {
        IReadOnlyList<InkPoint> points = Enumerable.Range(0, 10_000)
            .Select(index => new InkPoint(index, index % 200, 0.5f, TimestampMicroseconds: index * 1_000L))
            .ToList();

        var fitted = StrokeOutlineBuilder.FitCenterline(points, smoothing: 0);

        Assert.Same(points, fitted);
    }

    [Fact]
    public void StrokeOutline_IsAContinuousFiniteVectorShape()
    {
        var points = Enumerable.Range(0, 18)
            .Select(index => new InkPoint(index * 6, 50 + Math.Sin(index * 0.7) * 12,
                0.2f + index / 22f, TimestampMicroseconds: index * 1_000))
            .ToArray();

        var outline = StrokeOutlineBuilder.Build(points,
            new InkStyle { Width = 7, PressureSensitivity = 0.9f, Smoothing = 0.8f });

        Assert.True(outline.Centerline.Count > points.Length);
        Assert.True(outline.Contour.Count > outline.Centerline.Count * 2);
        Assert.Equal(outline.Centerline.Count, outline.Widths.Count);
        Assert.All(outline.Contour, point => Assert.True(double.IsFinite(point.X) && double.IsFinite(point.Y)));
        Assert.All(outline.Widths, width => Assert.True(float.IsFinite(width) && width > 0));
    }

    [Fact]
    public void PressureSensitivity_ControlsWidthVariation()
    {
        var light = new InkPoint(0, 0, 0.12f);
        var firm = new InkPoint(10, 0, 1f);
        var responsive = new InkStyle { Width = 10, PressureSensitivity = 1 };
        var uniform = responsive with { PressureSensitivity = 0 };

        Assert.True(StrokeOutlineBuilder.EffectiveWidth(light, responsive) <
                    StrokeOutlineBuilder.EffectiveWidth(firm, responsive));
        Assert.Equal(StrokeOutlineBuilder.EffectiveWidth(light, uniform),
            StrokeOutlineBuilder.EffectiveWidth(firm, uniform));
    }

    [Fact]
    public void FineInk_UsesContinuousCenterlineRepresentation()
    {
        Assert.True(StrokeOutlineBuilder.UsesCenterlineStroke(new InkStyle { Width = 0.647f }));
        Assert.False(StrokeOutlineBuilder.UsesCenterlineStroke(new InkStyle { Width = 2.4f }));
        Assert.False(StrokeOutlineBuilder.UsesCenterlineStroke(new InkStyle
        {
            Tool = InkToolKind.Highlighter,
            Width = 1
        }));
        Assert.True(StrokeOutlineBuilder.UsesCenterlineStroke(new InkStyle
        {
            Width = 18,
            PreserveSourceGeometry = true
        }));
        Assert.True(StrokeOutlineBuilder.UsesCenterlineStroke(new InkStyle
        {
            Width = 18,
            Smoothing = 0,
            PreserveSourceGeometry = false
        }));
        Assert.False(StrokeOutlineBuilder.UsesCenterlineStroke(new InkStyle
        {
            Tool = InkToolKind.Highlighter,
            Width = 18,
            PreserveSourceGeometry = true
        }));
    }

    [Fact]
    public void FineInk_ScalesStoredWidthWithViewport()
    {
        var style = new InkStyle { Width = 0.647f, PressureEnabled = true };

        Assert.Equal(0.647f, StrokeOutlineBuilder.VisibleCenterlineWidth(style, 0.25), 3);
        Assert.Equal(0.647f, StrokeOutlineBuilder.VisibleCenterlineWidth(style, 1), 3);
        Assert.Equal(0.647f, StrokeOutlineBuilder.VisibleCenterlineWidth(style, 4), 3);
        Assert.Equal(0.647f, style.Width, 3);
    }

    [Fact]
    public void LegacySamsungTimeline_UsesStableCenterlineWithoutNewSchemaFlag()
    {
        var stroke = new InkStrokeObject
        {
            Style = new InkStyle { Width = 12, Smoothing = 0.72f, PreserveSourceGeometry = false },
            Points = Enumerable.Range(0, 20)
                .Select(index => new InkPoint(index, index * 0.5, 0.6f, TimestampMicroseconds: index * 1_000L))
                .ToList()
        };
        var ordinaryStroke = stroke with
        {
            Points = stroke.Points.Select((point, index) => point with
            {
                TimestampMicroseconds = 20_000L + index * 977L
            }).ToList()
        };

        Assert.True(StrokeOutlineBuilder.UsesCenterlineStroke(stroke));
        Assert.False(StrokeOutlineBuilder.UsesCenterlineStroke(ordinaryStroke));
    }

    [Fact]
    public void ImportedFineInk_UsesReferenceDocumentWidthAtEveryZoom()
    {
        var style = new InkStyle
        {
            Width = 0.647f,
            Smoothing = 0,
            PreserveSourceGeometry = true
        };

        Assert.Equal(0.647f, StrokeOutlineBuilder.VisibleCenterlineWidth(style, 0.5), 3);
        Assert.Equal(0.647f, StrokeOutlineBuilder.VisibleCenterlineWidth(style, 4), 3);
        Assert.Equal(0.647f, StrokeOutlineBuilder.VectorCenterlineWidth(style), 3);
        Assert.Equal(0.647f, style.Width, 3);
    }

    [Fact]
    public void StrokeFitting_PreservesEndpoints()
    {
        InkPoint[] points =
        [
            new(4, 8, 0.3f),
            new(20, 25, 0.5f),
            new(45, 9, 0.8f),
            new(70, 30, 0.6f)
        ];

        var fitted = StrokeOutlineBuilder.FitCenterline(points, 0.75f);

        Assert.Equal(points[0].Position, fitted[0].Position);
        Assert.Equal(points[^1].Position, fitted[^1].Position);
        Assert.True(fitted.Count > points.Length);
    }

    [Fact]
    public void ViewportStabilization_RemovesMagnifiedLowZoomPointerNoise()
    {
        var points = Enumerable.Range(0, 41)
            .Select(index => new InkPoint(index * 2, index is 0 or 40 ? 0 : index % 2 == 0 ? 0.8 : -0.8))
            .ToArray();

        var pageFit = StrokeGeometry.StabilizeForViewport(points, viewportZoom: 0.5, smoothing: 0.9f);
        var detailed = StrokeGeometry.StabilizeForViewport(points, viewportZoom: 4, smoothing: 0.9f);

        Assert.Equal(2, pageFit.Count);
        Assert.True(detailed.Count > pageFit.Count);
        Assert.Equal(points[0], pageFit[0]);
        Assert.Equal(points[^1], pageFit[^1]);
    }

    [Fact]
    public void ViewportStabilization_PreservesIntentionalSharpTurn()
    {
        var points = Enumerable.Range(0, 21)
            .Select(index => new InkPoint(index * 2.5, Math.Sin(index) * 0.25))
            .Concat(Enumerable.Range(1, 20)
                .Select(index => new InkPoint(50 + Math.Cos(index) * 0.25, index * 2.5)))
            .ToArray();

        var stabilized = StrokeGeometry.StabilizeForViewport(points, viewportZoom: 0.5, smoothing: 0.9f);

        Assert.Contains(stabilized, point =>
            Math.Abs(point.X - 50) < 1 && Math.Abs(point.Y) < 1);
        Assert.Equal(points[0], stabilized[0]);
        Assert.Equal(points[^1], stabilized[^1]);
        Assert.All(stabilized, point =>
        {
            Assert.True(double.IsFinite(point.X));
            Assert.True(double.IsFinite(point.Y));
        });
    }

    [Fact]
    public void ZeroSmoothing_PreservesImportedCenterlineSamples()
    {
        InkPoint[] points =
        [
            new(1, 2, 0.2f),
            new(4, 9, 0.6f),
            new(8, 3, 0.9f),
            new(13, 11, 0.4f)
        ];

        var fitted = StrokeOutlineBuilder.FitCenterline(points, 0);

        Assert.Equal(points, fitted);
    }

    [Fact]
    public void ZeroSmoothing_DoesNotCollapseDenseSamsungSamples()
    {
        var points = Enumerable.Range(0, 101)
            .Select(index => new InkPoint(index * 0.1, Math.Sin(index * 0.1), 0.65f))
            .ToArray();

        var fitted = StrokeOutlineBuilder.FitCenterline(points, 0);

        Assert.Equal(points.Length, fitted.Count);
        Assert.Equal(points[0].Position, fitted[0].Position);
        Assert.Equal(points[^1].Position, fitted[^1].Position);
    }

    [Fact]
    public void LiveInkDistanceFilter_RetainsDensePathSpanAndEndpoint()
    {
        var points = Enumerable.Range(0, 101)
            .Select(index => new InkPoint(index * 0.1, 0, 0.65f))
            .ToArray();

        var fitted = StrokeOutlineBuilder.FitCenterline(points, 0.5f);

        Assert.True(fitted.Count > 10);
        Assert.Equal(points[0].Position, fitted[0].Position);
        Assert.Equal(points[^1].X, fitted[^1].X, 4);
        Assert.Equal(points[^1].Y, fitted[^1].Y, 4);
    }

    [Fact]
    public void ShapeRecognizer_SnapsHandDrawnBoxToRectangle()
    {
        var points = new List<InkPoint>();
        for (var x = 10; x <= 110; x += 10) points.Add(new InkPoint(x, 10));
        for (var y = 20; y <= 90; y += 10) points.Add(new InkPoint(110, y));
        for (var x = 100; x >= 10; x -= 10) points.Add(new InkPoint(x, 90));
        for (var y = 80; y >= 10; y -= 10) points.Add(new InkPoint(10, y));

        Assert.Equal(ShapeKind.Rectangle, ShapeRecognizer.Recognize(points));
    }

    [Fact]
    public void ShapeRecognizer_LeavesStraightGestureAsInk()
    {
        var points = Enumerable.Range(0, 20)
            .Select(index => new InkPoint(index * 7, 30 + Math.Sin(index) * 0.4))
            .ToArray();

        Assert.Null(ShapeRecognizer.Recognize(points));
    }

    [Fact]
    public void ShapeRecognizer_SnapsHandDrawnCircleToEllipse()
    {
        var points = Enumerable.Range(0, 49)
            .Select(index => index * Math.PI * 2 / 48)
            .Select(angle => new InkPoint(100 + Math.Cos(angle) * 44, 80 + Math.Sin(angle) * 31))
            .ToArray();

        Assert.Equal(ShapeKind.Ellipse, ShapeRecognizer.Recognize(points));
    }

    [Fact]
    public void ShapeRecognizer_LeavesHandwrittenLoopAsInkWithoutDeliberateIntent()
    {
        var handwrittenO = Enumerable.Range(0, 33)
            .Select(index => index * Math.PI * 2 / 32)
            .Select(angle => new InkPoint(50 + Math.Cos(angle) * 24, 50 + Math.Sin(angle) * 28))
            .ToArray();

        Assert.Null(ShapeRecognizer.RecognizeDetailed(handwrittenO, deliberateGesture: false));
        Assert.Equal(ShapeKind.Ellipse,
            ShapeRecognizer.RecognizeDetailed(handwrittenO, deliberateGesture: true)?.Kind);
    }

    [Fact]
    public void ShapeRecognizer_LeavesOneStrokeArrowAsInk()
    {
        var points = Enumerable.Range(0, 11)
            .Select(index => new InkPoint(index * 10, 50))
            .Concat(new[] { new InkPoint(90, 42), new InkPoint(80, 35) })
            .ToArray();

        Assert.Null(ShapeRecognizer.RecognizeDetailed(points));
    }

    [Fact]
    public void CornerResize_CanPreserveImageAspectRatio()
    {
        var bounds = new RectD(0, 0, 200, 100);
        var transform = SelectionTransformer.CreateTransform(TransformHandle.BottomRight, bounds,
            new PointD(200, 100), new PointD(300, 125), preserveAspect: true);

        Assert.Equal(Math.Abs(transform.M11), Math.Abs(transform.M22), 6);
    }
}
