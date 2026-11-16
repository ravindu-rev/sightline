// Sightline's reader for JavaScript and TypeScript. Sightline starts it with Node.js and talks to it in
// JSON lines: each request { id, method, params } on stdin gets one answer { id, result } or { id, error }
// on stdout, sometimes after events { id, event, ... }. Nothing else may go to stdout, so console output
// goes to stderr, which Sightline keeps to explain a failure.
//
//   hello                     -> { node, typescript }
//   open { root, projects }   -> events "progress" and "docs", then { diagnostics }
//   apply { changes }         -> { docs }   new text for files, new files, removed files
//   extract { paths }         -> { docs }   read files again after a declaration they use changed
//
// It never runs code from the repository: the TypeScript compiler only reads it.

import readline from 'node:readline';
import { createRequire } from 'node:module';

const write = (text) => process.stderr.write(text + '\n');
console.log = console.info = console.debug = (...args) => write(args.join(' '));
console.warn = console.error = (...args) => write(args.join(' '));

const require = createRequire(import.meta.url);
const ts = require('./vendor/typescript/lib/typescript.js');
const { Workspace } = await import('./workspace.mjs');

const workspace = new Workspace(ts);

function send(message) {
  process.stdout.write(JSON.stringify(message) + '\n');
}

const methods = {
  hello: () => ({ node: process.version, typescript: ts.version }),
  open: (params, emit) => workspace.open(params, emit),
  apply: (params) => workspace.apply(params),
  extract: (params) => workspace.extract(params),
};

async function handle(line) {
  if (!line.trim()) return;
  let message;
  try {
    message = JSON.parse(line);
  } catch {
    write('Sightline sent a line that is not JSON.');
    return;
  }
  const { id, method, params } = message;
  try {
    const run = methods[method];
    if (!run) throw new Error(`There is no method "${method}".`);
    const result = await run(params ?? {}, (event) => send({ id, ...event }));
    send({ id, result: result ?? {} });
  } catch (err) {
    send({ id, error: String(err?.stack ?? err) });
  }
}

// One request at a time, in the order they came.
let queue = Promise.resolve();
const lines = readline.createInterface({ input: process.stdin, crlfDelay: Infinity });
lines.on('line', (line) => { queue = queue.then(() => handle(line)); });
lines.on('close', () => { queue.then(() => process.exit(0)); });
