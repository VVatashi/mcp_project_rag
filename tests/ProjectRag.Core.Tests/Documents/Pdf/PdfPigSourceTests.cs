using ProjectRag.Core.Documents.Pdf;

namespace ProjectRag.Core.Tests.Documents.Pdf;

public sealed class PdfPigSourceTests
{
    [Fact]
    public void TurnOffSystemFonts_FindsTheHiddenFieldsOfPdfPig()
    {
        // A new version of PdfPig may rename the fields. The search of the fonts of the system then comes back without notice.
        Assert.True(PdfPigSource.TurnOffSystemFonts());
    }
}
