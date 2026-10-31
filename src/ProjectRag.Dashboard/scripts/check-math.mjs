// Checks the formulas of documents with KaTeX, the renderer of the dashboard.
//
// The script reads Markdown with the same parser as the dashboard (remark with GFM and math) and renders every formula.
// It prints the count of formulas, the count of errors, and the first errors with their place.
//
//   node scripts/check-math.mjs file.md ...            reads Markdown files
//   node scripts/check-math.mjs --api http://127.0.0.1:5180 [--source name]
//                                                       reads the documents of the knowledge base
//
// The exit code is 1 when a formula fails, so the check can run in a pipeline.

import { readFile } from 'node:fs/promises';

import katex from 'katex';
import remarkGfm from 'remark-gfm';
import remarkMath from 'remark-math';
import remarkParse from 'remark-parse';
import { unified } from 'unified';
import { visit } from 'unist-util-visit';

const args = process.argv.slice(2);
const apiIndex = args.indexOf('--api');
const sourceIndex = args.indexOf('--source');
const limitIndex = args.indexOf('--show');
const show = limitIndex >= 0 ? Number(args[limitIndex + 1]) : 15;

async function* documents() {
  if (apiIndex < 0) {
    for (const file of args.filter((arg, index) => !arg.startsWith('--') && args[index - 1] !== '--show')) {
      yield { name: file, text: await readFile(file, 'utf8') };
    }
    return;
  }

  const api = args[apiIndex + 1].replace(/\/$/, '');
  const headers = process.env.PROJECT_RAG_TOKEN ? { Authorization: `Bearer ${process.env.PROJECT_RAG_TOKEN}` } : {};
  const sources = await (await fetch(`${api}/api/sources`, { headers })).json();
  const wanted = sourceIndex >= 0 ? sources.filter((source) => source.name === args[sourceIndex + 1]) : sources;
  for (const source of wanted) {
    const page = await (await fetch(`${api}/api/documents?sourceId=${source.sourceId}&limit=500`, { headers })).json();
    for (const document of page.items) {
      let text = '';
      for (let offset = 0; offset < document.contentLength || offset === 0; ) {
        const part = await (await fetch(`${api}/api/documents/${document.documentId}/content?offset=${offset}&length=100000`, { headers })).json();
        text += part.text;
        if (part.text.length === 0 || part.offset + part.text.length >= part.totalLength) break;
        offset = part.offset + part.text.length;
      }
      yield { name: `${source.name}/${document.path}`, text };
    }
  }
}

const parser = unified().use(remarkParse).use(remarkGfm).use(remarkMath);
let total = 0;
let failed = 0;
const errors = [];
for await (const { name, text } of documents()) {
  let count = 0;
  let bad = 0;
  visit(parser.parse(text), ['math', 'inlineMath'], (node) => {
    count++;
    try {
      katex.renderToString(node.value, { displayMode: node.type === 'math', throwOnError: true, strict: 'ignore' });
    } catch (error) {
      bad++;
      if (errors.length < show) {
        errors.push(`${name}:${node.position?.start.line} ${node.type}: ${error.message.split('\n')[0]}\n    ${node.value.slice(0, 160)}`);
      }
    }
  });
  total += count;
  failed += bad;
  console.log(`${name}: ${count} formulas, ${bad} errors`);
}

console.log(`total: ${total} formulas, ${failed} errors (${total ? ((failed * 100) / total).toFixed(2) : 0}%)`);
for (const error of errors) console.log(error);
process.exit(failed > 0 ? 1 : 0);
