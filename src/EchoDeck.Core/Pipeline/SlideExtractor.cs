using DocumentFormat.OpenXml.Packaging;
using DocumentFormat.OpenXml.Presentation;
using EchoDeck.Core.Models;
using CoreSlide = EchoDeck.Core.Models.Slide;

namespace EchoDeck.Core.Pipeline;

public class SlideExtractor
{
    private const int MaxNotesLength = 4500; // ElevenLabs limit is 5000, leave headroom

    public List<CoreSlide> Extract(Stream pptxStream)
    {
        var slides = new List<CoreSlide>();
        using var doc = PresentationDocument.Open(pptxStream, false);

        var presentationPart = doc.PresentationPart
            ?? throw new InvalidOperationException("The file is not a valid PowerPoint presentation.");

        var slideIdList = presentationPart.Presentation.SlideIdList
            ?? throw new InvalidOperationException("Presentation contains no slides.");

        int index = 0;
        foreach (SlideId slideId in slideIdList.Elements<SlideId>())
        {
            var relationshipId = slideId.RelationshipId?.Value
                ?? throw new InvalidOperationException($"Slide {index} has no relationship ID.");

            var slidePart = (SlidePart)presentationPart.GetPartById(relationshipId);

            var notes = ExtractNotes(slidePart);

            slides.Add(new CoreSlide
            {
                Index = index,
                SpeakerNotes = notes,
            });

            index++;
        }

        return slides;
    }

    private static string ExtractNotes(SlidePart slidePart)
    {
        var notesPart = slidePart.NotesSlidePart;
        if (notesPart == null) return string.Empty;

        // Collect text from all paragraphs in the notes body, skipping the slide number placeholder
        var texts = new List<string>();
        var notesSlide = notesPart.NotesSlide;

        foreach (var sp in notesSlide.Descendants<DocumentFormat.OpenXml.Presentation.Shape>())
        {
            // Skip the slide number placeholder (idx=0 is title, idx=1 is body/notes, idx=2 is slide number)
            var ph = sp.NonVisualShapeProperties?.ApplicationNonVisualDrawingProperties
                ?.GetFirstChild<PlaceholderShape>();
            if (ph?.Index?.Value == 0) continue; // slide title placeholder in notes

            var txBody = sp.TextBody;
            if (txBody == null) continue;

            foreach (var para in txBody.Elements<DocumentFormat.OpenXml.Drawing.Paragraph>())
            {
                var paraText = string.Concat(
                    para.Elements<DocumentFormat.OpenXml.Drawing.Run>()
                        .Select(r => r.Text?.Text ?? string.Empty));
                if (!string.IsNullOrEmpty(paraText))
                    texts.Add(paraText);
            }
        }

        var result = string.Join(" ", texts).Trim();
        return result.Length > MaxNotesLength
            ? result[..MaxNotesLength]
            : result;
    }
}
