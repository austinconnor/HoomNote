using HoomNote.Core.Documents;
using HoomNote.Core.Services;
using System.Globalization;
using System.Numerics;
using Windows.Foundation;
using Windows.UI.Input.Inking;
using Windows.UI.Input.Inking.Analysis;

namespace HoomNote_App.Services;

public sealed class WindowsInkRecognitionService : IHandwritingRecognitionService
{
    public async Task<HandwritingRecognitionResult> RecognizeAsync(
        IReadOnlyList<InkStrokeObject> strokes,
        string languageTag,
        CancellationToken cancellationToken = default)
    {
        if (strokes.Count == 0) return HandwritingRecognitionResult.Empty;
        var prepared = strokes.Where(stroke => stroke.Points.Count > 1)
            .Select(stroke =>
            {
                var transform = stroke.Transform.ToMatrix();
                return stroke.Points.Select(point =>
                {
                    var value = Vector2.Transform(new Vector2((float)point.X, (float)point.Y), transform);
                    return new Point(value.X, value.Y);
                }).ToArray();
            })
            .Where(points => points.Length > 1)
            .ToArray();
        if (prepared.Length == 0) return HandwritingRecognitionResult.Empty;

        var minX = prepared.Min(points => points.Min(point => point.X));
        var minY = prepared.Min(points => points.Min(point => point.Y));
        var maxY = prepared.Max(points => points.Max(point => point.Y));
        var sourceHeight = Math.Max(1, maxY - minY);
        // Normalize imported writing to a size at which Windows Ink has reliable character
        // detail, while retaining a reversible map for exact word highlighting.
        var analysisScale = Math.Clamp(42d / sourceHeight, 0.65, 3.5);
        const double padding = 12;
        var analyzer = new InkAnalyzer();
        var builder = new InkStrokeBuilder();
        try
        {
            var previousCulture = CultureInfo.CurrentCulture;
            try
            {
                if (!string.IsNullOrWhiteSpace(languageTag))
                    CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(languageTag);
                foreach (var points in prepared)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var normalized = points.Select(point => new Point(
                        (point.X - minX) * analysisScale + padding,
                        (point.Y - minY) * analysisScale + padding)).ToArray();
                    var inkStroke = builder.CreateStroke(normalized);
                    analyzer.AddDataForStroke(inkStroke);
                    // These batches contain only candidate handwriting. Prevent the analyzer from
                    // discarding headers or small lettering as diagrams/shapes.
                    analyzer.SetStrokeDataKind(inkStroke.Id, InkAnalysisStrokeKind.Writing);
                }
            }
            finally
            {
                CultureInfo.CurrentCulture = previousCulture;
            }

            if (analyzer.AnalysisRoot.Children.Count == 0) return HandwritingRecognitionResult.Empty;
            var result = await analyzer.AnalyzeAsync();
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Status != InkAnalysisStatus.Updated) return HandwritingRecognitionResult.Empty;
            var words = analyzer.AnalysisRoot
                .FindNodes(InkAnalysisNodeKind.InkWord)
                .OfType<InkAnalysisInkWord>()
                .Where(word => !string.IsNullOrWhiteSpace(word.RecognizedText))
                .ToArray();
            return new HandwritingRecognitionResult(
                string.Join(" ", words.Select(word => word.RecognizedText)),
                words.Select(word => new RecognizedTextRegion
                {
                    Text = word.RecognizedText.Trim(),
                    Bounds = new RectD(
                        (word.BoundingRect.X - padding) / analysisScale + minX,
                        (word.BoundingRect.Y - padding) / analysisScale + minY,
                        word.BoundingRect.Width / analysisScale,
                        word.BoundingRect.Height / analysisScale)
                }).ToArray());
        }
        finally
        {
            // InkAnalyzer retains native stroke data after analysis. Explicitly clear each
            // short-lived batch so idle indexing cannot evict the interactive render cache.
            analyzer.ClearDataForAllStrokes();
        }
    }
}
