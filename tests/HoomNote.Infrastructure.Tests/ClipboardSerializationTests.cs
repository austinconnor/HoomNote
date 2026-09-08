using System.Text.Json;
using HoomNote.Core.Documents;
using HoomNote.Core.Editing;
using HoomNote.Infrastructure.Serialization;

namespace HoomNote.Infrastructure.Tests;

public sealed class ClipboardSerializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectionRoundTripsThroughClipboardJson(bool singleSelection)
    {
        var ink = new InkStrokeObject { Points = [new(10, 20, 0.3f), new(35, 55, 0.8f)] };
        var image = new ImageObject { AssetHash = "copied-image.png", Transform = Transform2D.Translation(25, 50) };
        CanvasObject[] objects = [ink, image, new ShapeObject(),
            new RichTextObject { Content = RichTextDocument.FromPlainText("Copied text\nSecond line") },
            new GroupObject { ChildIds = [ink.Id, image.Id], Bounds = new(0, 0, 400, 300) }];
        // The UI exposes both its multi-selection List and a single-selection array as IReadOnlyList.
        foreach (var item in singleSelection ? objects : objects.Take(1))
        {
            IReadOnlyList<CanvasObject> selection = singleSelection ? [item] : objects.ToList();
            var json = JsonSerializer.Serialize(selection, HoomNoteJson.Options);
            var pasted = JsonSerializer.Deserialize<List<CanvasObject>>(json, HoomNoteJson.Options);
            Assert.Equal(selection.Count, pasted!.Count);
            for (var index = 0; index < selection.Count; index++)
            {
                Assert.Equal(selection[index].GetType(), pasted[index].GetType());
                Assert.NotSame(selection[index], pasted[index]);
                Assert.Equal(JsonSerializer.Serialize(selection[index], HoomNoteJson.Options),
                    JsonSerializer.Serialize(pasted[index], HoomNoteJson.Options));
            }
        }
    }

    [Fact]
    public void CopyCapturesContentBeforeSourceIsEditedOrCut()
    {
        var ink = new InkStrokeObject { Points = [new(10, 20), new(30, 40)] };
        var selected = new List<CanvasObject> { ink };
        IReadOnlyList<CanvasObject> selection = selected;
        var json = JsonSerializer.Serialize(selection, HoomNoteJson.Options);
        ink.Points.Clear();
        selected.Clear();
        var pasted = JsonSerializer.Deserialize<List<CanvasObject>>(json, HoomNoteJson.Options);
        Assert.Equal(2, Assert.IsType<InkStrokeObject>(Assert.Single(pasted!)).Points.Count);
    }

    [Fact]
    public void WholeSelectionPasteCanBeUndoneAndRedoneOnAnotherPage()
    {
        IReadOnlyList<CanvasObject> selection = [new ImageObject { AssetHash = "shared-image.png" }, new ShapeObject()];
        var json = JsonSerializer.Serialize(selection, HoomNoteJson.Options);
        var pasted = JsonSerializer.Deserialize<List<CanvasObject>>(json, HoomNoteJson.Options)!
            .Select(item => item with { Id = Guid.NewGuid() }).ToArray();
        var document = HoomNoteDocument.Create("Paste target");
        var page = new NotePage();
        document.Pages.Add(page);
        var history = new CommandHistory();
        history.Execute(new ReplaceObjectsCommand(page.Id, [], pasted, "Paste objects"), document);
        Assert.Equal(2, page.Objects.Count);
        Assert.Equal("shared-image.png", Assert.IsType<ImageObject>(page.Objects[0]).AssetHash);
        Assert.DoesNotContain(page.Objects, item => selection.Any(original => original.Id == item.Id));
        history.Undo(document);
        Assert.Empty(page.Objects);
        history.Redo(document);
        Assert.Equal(2, page.Objects.Count);
    }
}
