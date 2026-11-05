using System.Xml.Linq;

using ProjectRag.Core.Documents;

namespace ProjectRag.Core.Tests.Documents;

public sealed class MathMlReaderTests
{
    [Fact]
    public void Read_WritesTheScriptsFractionsAndFences()
    {
        Assert.Equal(@"c_{l} = {( \frac{c_{s} + 0.055}{1.055} )}^{2.4}", Read("""
            <mrow><msub><mi>c</mi><mi>l</mi></msub><mo>=</mo>
            <msup><mfenced><mfrac><mrow><msub><mi>c</mi><mi>s</mi></msub><mo>+</mo><mn>0.055</mn></mrow><mn>1.055</mn></mfrac></mfenced><mn>2.4</mn></msup></mrow>
            """));
    }

    [Fact]
    public void Read_WritesTheNamesWordsAndSigns()
    {
        Assert.Equal(@"\mathit{width} \max \mathrm{level} \delta s \text{for a 1D texture,} \lfloor x \rfloor", Read("""
            <mrow><mi mathvariant="italic">width</mi><mo>max</mo><mi>level</mi><mi>δs</mi><mi> for a 1D texture, </mi>
            <mo>⌊</mo><mi>x</mi><mo>⌋</mo><mo>&#x2061;</mo></mrow>
            """));
    }

    [Fact]
    public void Read_WritesATableAndAnAccent()
    {
        Assert.Equal(@"\{ \begin{array}{cc}1 & \mathrm{if} \\ \overline{x} & 0\end{array}", Read("""
            <mrow><mo>{</mo><mtable><mtr><mtd><mn>1</mn></mtd><mtd><mo>if</mo></mtd></mtr>
            <mtr><mtd><mover><mi>x</mi><mo>¯</mo></mover></mtd><mtd><mn>0</mn></mtd></mtr></mtable></mrow>
            """));
    }

    [Fact]
    public void Read_KeepsTheCommandsOfTheFencesApartAndEndsWithoutABackslash()
    {
        Assert.Equal(@"\langle x, y \rangle \lfloor x \rfloor \mathrm{d} x \;", Read("""
            <mrow><mfenced open="⟨" close="⟩"><mi>x</mi><mi>y</mi></mfenced><mfenced open="⌊" close="⌋"><mi>x</mi></mfenced>
            <mi mathvariant="normal">d</mi><mi>x</mi><mspace/></mrow>
            """));
    }

    private static string Read(string inner) =>
        MathMlReader.Read(XElement.Parse($"""<math xmlns="http://www.w3.org/1998/Math/MathML">{inner}</math>"""));
}
