import 'katex/dist/katex.min.css';

import { memo } from 'react';
import ReactMarkdown, { type Components } from 'react-markdown';
import { Link } from 'react-router';
import rehypeKatex from 'rehype-katex';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';

// The service marks of the importers are HTML comments. They carry pages and anchors for the index, not text for a reader.
const marks = /^(?:\s*<!-- rag:[^>]*-->)+\s*$/;

// A link with a scheme leaves the dashboard. A relative link names another document of the source.
const external = /^[a-z][a-z0-9+.-]*:/i;

/** The part of a Markdown tree that the plugin reads. */
interface MarkdownNode {
  type: string;
  value?: string;
  children?: MarkdownNode[];
  position?: { start: { offset?: number }; end: { offset?: number } };
  data?: { hProperties?: Record<string, unknown> };
}

function dropMarks(node: MarkdownNode): void {
  if (!node.children) return;
  node.children = node.children.filter((child) => !(child.type === 'html' && marks.test(child.value ?? '')));
  node.children.forEach(dropMarks);
}

/**
 * Removes the service marks. With an offset the plugin also gives each top block the end of its source in the document,
 * in the attribute data-end, so a page can find the block at an offset.
 */
function remarkMarks({ offset }: { offset: number | undefined }) {
  return (tree: MarkdownNode) => {
    dropMarks(tree);
    if (offset === undefined) return;
    for (const block of tree.children ?? []) {
      const end = block.position?.end.offset;
      if (end === undefined) continue;
      block.data = { ...block.data, hProperties: { ...block.data?.hProperties, 'data-end': offset + end } };
    }
  };
}

/**
 * Renders the Markdown of a document or a chunk: tables, footnotes and formulas in LaTeX through KaTeX.
 * A formula that KaTeX cannot read shows its source in red, so a wrong import stands out.
 * A link to an anchor of the document goes to the address that anchorHref gives.
 * The offset is the place of the text in the document. With it the top blocks carry their ends.
 */
export const Markdown = memo(function Markdown({ text, anchorHref, offset }: { text: string; anchorHref?: (anchor: string) => string; offset?: number }) {
  const components: Components = {
    a: ({ href = '', children }) => {
      if (href.startsWith('#')) {
        const anchor = decodeURIComponent(href.slice(1));
        return anchorHref ? <Link to={anchorHref(anchor)}>{children}</Link> : <span className="markdown__link">{children}</span>;
      }

      if (external.test(href)) {
        return (
          <a href={href} target="_blank" rel="noreferrer">
            {children}
          </a>
        );
      }

      // The page of the chunk resolves the links to other documents in its list of links.
      return (
        <span className="markdown__link" title={href}>
          {children}
        </span>
      );
    },
  };

  return (
    <div className="markdown">
      <ReactMarkdown
        remarkPlugins={[remarkGfm, remarkMath, [remarkMarks, { offset }]]}
        rehypePlugins={[[rehypeKatex, { throwOnError: false, strict: 'ignore', errorColor: 'var(--error)' }]]}
        components={components}
      >
        {text}
      </ReactMarkdown>
    </div>
  );
});
