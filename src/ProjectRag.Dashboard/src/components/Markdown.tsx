import 'katex/dist/katex.min.css';

import { memo } from 'react';
import ReactMarkdown, { type Components } from 'react-markdown';
import rehypeKatex from 'rehype-katex';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';

// The service marks of the importers are HTML comments. They carry pages and anchors for the index, not text for a reader.
const marks = /<!-- rag:[^>]*-->/g;

/** Removes the service marks of the importers from Markdown. */
export function stripMarks(markdown: string): string {
  return markdown.replace(marks, '');
}

/**
 * Renders the Markdown of a document or a chunk: tables, footnotes and formulas in LaTeX through KaTeX.
 * A formula that KaTeX cannot read shows its source in red, so a wrong import stands out.
 * A link to an anchor of the document goes to onAnchor, which can follow it to the target chunk.
 */
export const Markdown = memo(function Markdown({ text, onAnchor }: { text: string; onAnchor?: (anchor: string) => void }) {
  const components: Components = {
    a: ({ href, children }) => {
      if (href?.startsWith('#') && onAnchor) {
        return (
          <a
            href={href}
            onClick={(event) => {
              event.preventDefault();
              onAnchor(decodeURIComponent(href.slice(1)));
            }}
          >
            {children}
          </a>
        );
      }

      return (
        <a href={href} target="_blank" rel="noreferrer">
          {children}
        </a>
      );
    },
  };

  return (
    <div className="markdown">
      <ReactMarkdown
        remarkPlugins={[remarkGfm, remarkMath]}
        rehypePlugins={[[rehypeKatex, { throwOnError: false, strict: 'ignore', errorColor: 'var(--error)' }]]}
        components={components}
      >
        {stripMarks(text)}
      </ReactMarkdown>
    </div>
  );
});
