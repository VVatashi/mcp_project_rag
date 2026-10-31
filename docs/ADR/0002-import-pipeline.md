# 0002. The import pipeline: Markdown, sections, links and formulas

- Status: Accepted
- Date: 2026-10-31
- Refines: [0001](0001-architecture.md)

## Context

The first version read the text of a PDF file in the order of its content stream. It turned the bookmarks into headings and cut the text into chunks of at most 1800 characters at line ends.
The OpenGL specifications showed the limits of that approach:

- A formula fell apart into lines: `∂v` above `∂x` became two lines, and the brackets of `⌊x⌋` came out as the letters `b` and `c`.
- Justified lines glued words together, for example "WhenthevalueofTEXTURE_MIN_FILTER".
- A chunk could cut a table, a listing or a formula in the middle, and the next chunk had no context.
- The 10966 internal links of the core specification were lost. A chunk knew its document, but not its section, its pages or the places it points to.

The request asks for these properties:

1. A chunk follows the sections of the source.
2. A chunk does not break a formula, a table, an algorithm or a listing. An unavoidable break gives the neighbor chunks an overlap.
3. A chunk does not break a word or a sentence.
4. A chunk points to the object of the source in the database, and chunks point to each other through the links of the source.
5. The formulas are checked, for example with MathJax.
6. Markdown is to be considered as the intermediate form of a document.

## Analysis

### Markdown as the intermediate form

Markdown suits the pipeline well. One reader and one chunker serve every format. An agent reads the content of a document as Markdown, and a model reads it better than raw text.
Markdown also carries most of the structure that the request needs: headings, code blocks, pipe tables, formulas in `$…$` and `$$…$$`, footnotes and links.

Markdown lacks three things: the pages, the anchors of tables and formulas, and positions on a page.
The importers add them as service marks in HTML comments, which a Markdown viewer hides:

```
<!-- rag:page 281 -->
<!-- rag:anchor table-8.20 -->
```

A structure reader removes the marks from the text of the chunks and keeps them as metadata. The positions on a page stay inside the importer: it resolves the links before it writes Markdown.

The structure reader is Markdig. Markdig follows CommonMark, gives each block its exact place in the text and reads pipe tables, formulas, footnotes and front matter. A parser of our own would repeat that work with more errors.

### PDF: a layout analysis of the glyphs

PdfPig gives each glyph with its font, size and position, each thin vector line and each link annotation. The importer builds the layout from these:

| Element | How the importer finds it |
| --- | --- |
| Turned pages | The direction of most baselines. A page with its text turned by a quarter turns back, and the lines of other directions, such as a turned running header, go away. A bold line of a turned page is a label of a figure, not a heading |
| Word spaces | Gaps between glyphs, a smaller gap at a change of the font |
| Running headers and footers | Lines at the top or the bottom that repeat on many pages, or capitals with a page number. A header of a chapter repeats on a run of pages. A first or last line with a word of the headers, such as "ECMA-334", is a header too |
| Headings | Bookmarks with their target height, matched to a line by its text. A chapter of LaTeX searches farther. A bookmark without a line becomes a heading of its own. Short bold lines become headings below the outline, such as "Errors" |
| Headings without an outline | The hidden bookmarks of a word processor, such as "OLE_LINK6", do not count, and an outline that reaches fewer than two pages does not count. Then a bold or a much larger line is a heading. The depth of its number, such as "II.22.2", gives the level. A page of contents and a line in a larger regular font give no headings |
| Notes | The label "NOTE" in a column left of a box goes away, so the text of the box stays one paragraph |
| Lists | A line with a bullet stays an item of a list, also when it names identifiers in monospace |
| Paragraphs | Leading, size and the indent of the first line. A full line inside a framed box continues its paragraph |
| Hyphens | A hyphen at a line end goes when the document uses the joined word elsewhere: "compo-nent" becomes "component", "two-dimensional" stays |
| Code | Lines in a monospace font. A prototype with italic parameters still counts as code. Pseudocode sets its variables in math italics and its braces as math symbols. A run of code takes in the lines next to it with some monospace and one word of the text font at most. It also takes in the lines of braces. An empty line inside a listing and a listing that goes on at the top of the next page keep one block |
| Wrapped calls | A line of arguments in math italics, such as "index, count, name);", goes on with the call above it instead of becoming a formula |
| Tables | Lines between wide rules with columns, and the rows right after the last rule that keep the columns |
| Footnotes | Smaller lines at the bottom of a page that start with a number |
| Links | Link annotations with a target page and height. The target block gets an anchor |

### Formulas: from the fonts of TeX to LaTeX

The specifications set formulas in the fonts of Computer Modern. The importer reads the layout of these glyphs:

| Construct | Source in the PDF | LaTeX |
| --- | --- | --- |
| Subscript and superscript | A smaller glyph with a shifted baseline | `x_{i}`, `x^{2}`, `u'` |
| Fraction | Glyphs above and below a short rule | `\frac{a}{b}` |
| Fraction in a table | The rules of the cells of a table touch at one height, so they make no fractions | none |
| Large delimiter | A stack of CMEX pieces that touch. The pieces without Unicode in the private area, such as the top of a brace, map to their delimiters | `\{`, `|` |
| Root | The sign √ and a rule from its top over the radicand | `\sqrt{…}` |
| Script of a tall item | A smaller glyph next to the top of a large parenthesis, or next to a larger letter in a row of small capitals | `(…)^{2}`, `Z^{+}` |
| Cases | A large brace with several lines on its right | `\begin{cases} … \end{cases}` |
| Large operator | A CMEX operator with centered limits or with scripts | `\sum_{i=1}^{N}` |
| Names | Upright letters of CMR, italic letters of CMMI | `\max`, `\mathit{wrap}` |
| Symbols of CMSY | Raw codes without a map to Unicode | `⌊` from "b", `\ne` from "6" over "=" |
| Equation numbers | "(8.3)" at the right edge | `\tag{8.3}` and the anchor `equation-8.3` |

A formula that still has unbalanced braces becomes a fenced block of plain text, so a renderer never fails on it.

### The chunker

The chunker reads the blocks of Markdig, not lines:

1. A chunk stays inside one section and collects whole blocks up to 1800 characters.
   A chunk under 400 characters at the end of a section takes in the next sections of the third level or deeper, its subsections and its siblings, while the chunk stays under the limit.
   The headings of these sections stay in its text. The chunk keeps the section of its start, and a section counts the chunks that overlap its text.
   The Vulkan specification has thousands of sections such as "Revision" or "Contact" with one line each, and a chunk of one line gives the search no context.
2. A chunk with code, a formula or a table may grow to 3000 characters, so such a block stays whole. Code and TeX hold about three characters per token, so 3000 characters fit the slot of 2048 tokens.
3. A caption "Table 8.20:" stays with its table. A lead-in that ends with a colon stays with the formula or the code after it.
4. A paragraph over the limit splits after a sentence. The split skips abbreviations such as "e.g." and never falls inside a code span, a formula or a link. The next chunk repeats the last sentence.
   Without the end of a sentence in reach, the split falls at the last space, and the next chunk repeats the words before it. Only a text without spaces, such as Chinese, splits at the limit, and the split keeps a surrogate pair whole.
5. A block of code, a formula or a table over 3000 characters splits between lines. The next chunk repeats the last lines, the header of the table and the fence of the code or the formula, so each chunk stays valid Markdown.
   Each chunk adds new lines, and a line over the limit splits like text.
6. A list item or a quote with code, a formula or a table gives its parts as blocks of their own kinds, so the code stays whole there too.

### Links and the objects of the source

The database stores the structure of each document:

- `section`: the tree of the sections with the number of each heading, its pages and its range.
- `anchor`: the places that links point at.
- `chunk_link`: the links of each chunk with the path and the anchor of the target.
- A chunk has its section, its first and last page, and the size of its overlap with the previous chunk.

A link resolves at the time of a query: the anchor gives an offset, and the target chunk is the first chunk that ends after it. So a link to a document of the same source works even when that document changes later.
A mention in the text, such as "section 8.14" or "table 8.20", becomes a link when the document has the target. A caption does not mention itself.

### The check of the formulas

KaTeX checks the formulas, not MathJax. The dashboard renders with KaTeX, so the check and the display use one parser. KaTeX also runs in Node without a browser.
The script `npm run check-math` reads the Markdown with the parser of the dashboard and renders each formula. A failed formula is a parse error of the import.
The dashboard shows a chunk next to its page of the original PDF file, drawn with pdf.js, and lists the formulas of a document with their errors.

## Decision

1. Markdown with the service marks `rag:page` and `rag:anchor` is the intermediate form of every document.
2. Markdig reads the Markdown into sections, blocks, anchors, pages, links and mentions.
3. The PDF importer analyzes the layout of the glyphs and writes formulas as LaTeX.
4. The chunker keeps atomic blocks whole, splits text between sentences and overlaps split blocks.
5. The database stores sections, anchors and links. The web API and MCP give a chunk with its links, its backlinks and its neighbors (`read_chunk`).
6. KaTeX checks the formulas, in the dashboard and in the script `check-math`.

## Results

The measurements on the three OpenGL specifications (2122 pages):

| Measure | Core | Compatibility | GLSL |
| --- | --- | --- | --- |
| Sections (bookmarks and bold headings) | 957 | 1081 | 154 |
| Formulas, display and inline | 3448 | 5223 | 0 |
| KaTeX errors | 0 | 0 | 0 |
| Links of the PDF file | 10153 | 12705 | 358 |
| Links that resolve to a chunk | all | all | all |
| Import of the file | 6 s | 7 s | 3 s |

Every chunk has balanced fences of code and formulas. The tests of the chunker check this also for code inside a list item and for code over the larger limit.

## Consequences

- KaTeX reads every formula, but a valid formula may still differ from the original. A matrix becomes rows in `gathered` without brackets.
  Two fractions close one above the other in the rows of `cases` may merge, and a root with an index loses the index.
  The view of a chunk next to its PDF page is the way to check the meaning.
- The importer reads one column. A page with two columns mixes the lines of both columns.
- A table needs wide rules above and below it. A table without rules becomes paragraphs of text, and a table with unclear columns becomes a fenced block of plain text. The text stays searchable, but the columns are lost.
- A figure keeps only its caption and the labels that are text.
- An error of the published file stays in the text, because its glyphs show the error too. The Metal specification shows `sparse_color<Tv>` as `sparse_color-Tv`.
  The replacements of a source fix such errors with regular expressions for the matching files, before the chunks.
- A bookmark or a link that points into a turned page keeps the height of the page before the turn. Its target may land on another block of that page.
- A new version of the importer or the chunker reads every file again. Unchanged chunks keep their vectors by the hash of their text.
