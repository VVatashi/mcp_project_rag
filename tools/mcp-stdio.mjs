// The bridge connects an MCP client that starts a local command to the HTTP endpoint of the knowledge base.
//
// Some clients, such as Claude Desktop, start an MCP server as a command and talk to it over stdin and stdout.
// The bridge reads the messages of the client from stdin, posts them to /mcp and writes the answers to stdout.
// It needs Node.js 22 or later and no packages.
//
//   node tools/mcp-stdio.mjs [url]
//
// The url comes from the argument, then from PROJECT_RAG_URL, then http://127.0.0.1:5180/mcp.
// PROJECT_RAG_TOKEN gives the bearer token of the server. The bridge writes its own messages to stderr only.
// The bridge needs a server without sessions, such as ProjectRag. Each message then stands alone, and the bridge works on after a restart of the server.

import { createInterface } from 'node:readline';

const url = process.argv[2] || process.env.PROJECT_RAG_URL || 'http://127.0.0.1:5180/mcp';
const token = process.env.PROJECT_RAG_TOKEN || '';
// The most time that the bridge waits for the open calls after the client closes stdin.
const closeMilliseconds = 5000;

let protocolVersion = '';

function log(text) {
  process.stderr.write(`project-rag: ${text}\n`);
}

function write(message) {
  process.stdout.write(`${JSON.stringify(message)}\n`);
}

function headers() {
  const result = { 'Content-Type': 'application/json', Accept: 'application/json, text/event-stream' };
  if (protocolVersion) result['MCP-Protocol-Version'] = protocolVersion;
  if (token) result.Authorization = `Bearer ${token}`;
  return result;
}

// Reads the messages of an answer: one JSON value, a JSON array, or a stream of server-sent events.
async function* readAnswer(response) {
  const type = response.headers.get('content-type') || '';
  if (type.includes('application/json')) {
    const body = await response.json();
    yield* Array.isArray(body) ? body : [body];
    return;
  }

  if (!type.includes('text/event-stream') || !response.body) return;

  const decoder = new TextDecoder();
  let buffer = '';
  let data = [];
  for await (const part of response.body) {
    buffer += decoder.decode(part, { stream: true });
    let end;
    while ((end = buffer.indexOf('\n')) >= 0) {
      const line = buffer.slice(0, end).replace(/\r$/, '');
      buffer = buffer.slice(end + 1);
      if (line.startsWith('data:')) {
        data.push(line.slice(5).replace(/^ /, ''));
      } else if (line === '' && data.length > 0) {
        yield JSON.parse(data.join('\n'));
        data = [];
      }
    }
  }
}

// Posts one message and writes the messages of the answer to stdout. It returns true when the answer to the message itself came back.
async function send(message) {
  const response = await fetch(url, { method: 'POST', headers: headers(), body: JSON.stringify(message) });
  if (!response.ok) {
    const text = (await response.text()).slice(0, 300);
    throw new Error(`the server answered ${response.status} ${response.statusText}${text ? `: ${text}` : ''}`);
  }

  let answered = false;
  try {
    for await (const answer of readAnswer(response)) {
      if (message.method === 'initialize' && answer.result?.protocolVersion) protocolVersion = answer.result.protocolVersion;
      if (answer.id === message.id && !answer.method) answered = true;
      write(answer);
    }
  } catch (error) {
    // The client has its answer, so a broken rest of the stream is only a note.
    if (!answered) throw error;
    log(`the rest of the answer broke: ${error.message}`);
  }

  return answered;
}

async function handle(line) {
  if (line.trim() === '') return;

  let message;
  try {
    message = JSON.parse(line);
  } catch {
    write({ jsonrpc: '2.0', id: null, error: { code: -32700, message: 'The message is not JSON.' } });
    return;
  }

  // A request waits for its answer. A notification and an answer of the client to the server get none.
  const isRequest = message.id !== undefined && Boolean(message.method);
  let reason = '';
  try {
    if (!(await send(message)) && isRequest) reason = `ProjectRag on ${url} closed the answer without a result.`;
  } catch (error) {
    reason = error.cause?.code === 'ECONNREFUSED' ? `ProjectRag does not answer on ${url}. Start the server with "dotnet run --project src/ProjectRag.Server".` : `ProjectRag on ${url} failed: ${error.message}`;
  }

  if (reason) {
    log(reason);
    // The client gets an error instead of silence.
    if (isRequest) write({ jsonrpc: '2.0', id: message.id, error: { code: -32603, message: reason } });
  }
}

const pending = new Set();
const input = createInterface({ input: process.stdin, crlfDelay: Infinity });
input.on('line', (line) => {
  const task = handle(line).finally(() => pending.delete(task));
  pending.add(task);
});
input.on('close', async () => {
  await Promise.race([Promise.allSettled(pending), new Promise((resolve) => setTimeout(resolve, closeMilliseconds))]);
  process.exit(0);
});
