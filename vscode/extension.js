// Sightline for VS Code: opens the live map of the solution, shows the method under the cursor on
// the map, and lists Sightline's problems in the Problems panel.
//
// It talks to the same local Sightline server the browser page uses. A map that is already open for
// a workspace folder (started by hand, or beside the app by Co.Revington.Sightline.Hosting) is found
// through the small files Sightline keeps in the user's app-data folder; otherwise one is started.

'use strict';

const vscode = require('vscode');
const cp = require('child_process');
const crypto = require('crypto');
const fs = require('fs');
const http = require('http');
const os = require('os');
const path = require('path');

let output;
let status;
let problems;
let started = null;      // { child, url, token, folder } when this extension started the map
let watching = null;     // { url, token, request } while the Problems panel follows a map

function activate(context) {
  output = vscode.window.createOutputChannel('Sightline');
  problems = vscode.languages.createDiagnosticCollection('sightline');
  status = vscode.window.createStatusBarItem(vscode.StatusBarAlignment.Left, 50);
  status.text = '$(type-hierarchy) Sightline';
  status.tooltip = 'Open the live map of this solution';
  status.command = 'sightline.open';
  status.show();

  context.subscriptions.push(
    output, problems, status,
    vscode.commands.registerCommand('sightline.open', () => run(() => open(''))),
    vscode.commands.registerCommand('sightline.reveal', () => run(reveal)),
    vscode.commands.registerCommand('sightline.stop', () => run(stop)),
  );

  // A map that is already open starts feeding the Problems panel right away.
  map(false).then((found) => { if (found) follow(found); }, () => {});
}

function deactivate() {
  unfollow();
  if (started?.child && !started.child.killed) started.child.kill();
}

async function run(action) {
  try {
    await action();
  } catch (err) {
    vscode.window.showErrorMessage('Sightline: ' + (err.message ?? err));
  }
}

// -------------------------------------------------------------------- finding or starting the map

/** The folder of the file in the active editor, or the first workspace folder. */
function workspaceFolder() {
  const editor = vscode.window.activeTextEditor;
  const folder = editor ? vscode.workspace.getWorkspaceFolder(editor.document.uri) : null;
  return (folder ?? vscode.workspace.workspaceFolders?.[0])?.uri.fsPath ?? null;
}

/** Where Sightline lists the maps that are open on this machine (.NET's LocalApplicationData). */
function instanceFolders() {
  const home = os.homedir();
  if (process.platform === 'win32') return [path.join(process.env.LOCALAPPDATA ?? path.join(home, 'AppData', 'Local'), 'Sightline', 'instances')];
  const xdg = process.env.XDG_DATA_HOME || path.join(home, '.local', 'share');
  const folders = [path.join(xdg, 'Sightline', 'instances')];
  if (process.platform === 'darwin') folders.unshift(path.join(home, 'Library', 'Application Support', 'Sightline', 'instances'));
  return folders;
}

function inside(child, parent) {
  const rel = path.relative(parent, child);
  return rel === '' || (!rel.startsWith('..') && !path.isAbsolute(rel));
}

function request(url, token, method = 'GET', body = null) {
  return new Promise((resolve, reject) => {
    const data = body ? Buffer.from(JSON.stringify(body)) : null;
    const req = http.request(url, {
      method,
      headers: { 'X-Sightline-Token': token, ...(data ? { 'Content-Type': 'application/json', 'Content-Length': data.length } : {}) },
      timeout: 5000,
    }, (res) => {
      const chunks = [];
      res.on('data', (c) => chunks.push(c));
      res.on('end', () => resolve({ status: res.statusCode, text: Buffer.concat(chunks).toString('utf8') }));
    });
    req.on('error', reject);
    req.on('timeout', () => req.destroy(new Error('Sightline did not answer.')));
    if (data) req.write(data);
    req.end();
  });
}

/** A map that is open for the workspace folder, or null. */
async function findOpen(folder) {
  if (started && !started.child.killed && started.folder === folder) return started;
  for (const dir of instanceFolders()) {
    let files = [];
    try { files = fs.readdirSync(dir).filter((f) => f.endsWith('.json')); } catch { continue; }
    for (const file of files) {
      try {
        const info = JSON.parse(fs.readFileSync(path.join(dir, file), 'utf8'));
        const target = info.Target ?? info.target;
        if (!target || !(inside(target, folder) || inside(folder, path.dirname(target)))) continue;
        const map = { url: `http://localhost:${info.Port ?? info.port}`, token: info.Token ?? info.token, folder };
        const ping = await request(map.url + '/api/ping', map.token).catch(() => null);
        if (ping?.status === 204) return map;
      } catch {
        // a file being written, or a map that closed
      }
    }
  }
  return null;
}

/** The command that starts Sightline here: the user's setting, the repository's pinned tool, or dnx. */
function command(folder) {
  // Only the user's own settings count, so a repository's .vscode/settings.json cannot pick the program to run.
  const value = vscode.workspace.getConfiguration('sightline').inspect('command')?.globalValue;
  const setting = typeof value === 'string' ? value.trim() : '';
  if (setting) {
    const parts = setting.match(/"[^"]*"|\S+/g).map((p) => p.replace(/^"|"$/g, ''));
    return { file: parts[0], args: parts.slice(1) };
  }
  for (let dir = folder; dir; dir = path.dirname(dir) === dir ? null : path.dirname(dir)) {
    for (const manifest of [path.join(dir, 'dotnet-tools.json'), path.join(dir, '.config', 'dotnet-tools.json')]) {
      try {
        if (fs.readFileSync(manifest, 'utf8').toLowerCase().includes('co.revington.sightline')) return { file: 'dotnet', args: ['sightline'] };
      } catch {
        // no manifest here
      }
    }
  }
  return { file: 'dotnet', args: ['dnx', 'Co.Revington.Sightline', '--'] };
}

/** Starts a map for the folder and waits until it prints its address. */
function startMap(folder) {
  const token = crypto.randomBytes(16).toString('hex');
  const { file, args } = command(folder);
  const all = [...args, '--no-open', '--token', token];
  output.appendLine(`> ${file} ${all.join(' ')}   (in ${folder})`);

  const child = cp.spawn(file, all, { cwd: folder, shell: false, windowsHide: true, env: { ...process.env, DOTNET_NOLOGO: '1' } });
  return new Promise((resolve, reject) => {
    let text = '';
    const timer = setTimeout(() => reject(new Error('Sightline did not start within 3 minutes. See the Sightline output for details.')), 180000);
    const onData = (chunk) => {
      const s = chunk.toString();
      output.append(s);
      text += s;
      const m = /url\s+(http:\/\/localhost:\d+)/.exec(text);
      if (m) {
        clearTimeout(timer);
        started = { child, url: m[1], token, folder };
        resolve(started);
      }
    };
    child.stdout.on('data', onData);
    child.stderr.on('data', (c) => output.append(c.toString()));
    child.on('error', (err) => { clearTimeout(timer); reject(new Error(`Could not run ${file}: ${err.message}. Is the .NET 10 SDK installed?`)); });
    child.on('exit', (code) => {
      clearTimeout(timer);
      output.appendLine(`Sightline stopped (exit code ${code}).`);
      if (started?.child === child) started = null;
      reject(new Error(`Sightline exited (code ${code}). See the Sightline output for details.`));
    });
  });
}

/** The map for the current folder; starts one when start is true. */
async function map(start = true) {
  const folder = workspaceFolder();
  if (!folder) throw new Error('Open a folder with a .NET solution first.');
  const open = await findOpen(folder);
  if (open || !start) return open;
  return vscode.window.withProgress(
    { location: vscode.ProgressLocation.Notification, title: 'Sightline: reading the solution…' },
    () => startMap(folder));
}

// -------------------------------------------------------------------- commands

async function open(hash) {
  const m = await map();
  follow(m);
  const url = m.url + '/' + (hash ?? '');
  if (vscode.workspace.getConfiguration('sightline').get('openIn') === 'editor') {
    await vscode.commands.executeCommand('simpleBrowser.show', url);
  } else {
    await vscode.env.openExternal(vscode.Uri.parse(url));
  }
}

/** Shows the method, endpoint or type under the cursor on the map. */
async function reveal() {
  const editor = vscode.window.activeTextEditor;
  if (!editor || editor.document.languageId !== 'csharp') throw new Error('Put the cursor in a C# file first.');
  const m = await map();
  follow(m);

  const answer = await request(m.url + '/api/reveal', m.token, 'POST', {
    file: editor.document.uri.fsPath,
    line: editor.selection.active.line + 1,
  });
  if (answer.status === 404) throw new Error('Nothing on the map is declared at this line. Try inside a class, method or endpoint.');
  if (answer.status !== 200) throw new Error(`The map answered ${answer.status}.`);
  const found = JSON.parse(answer.text);

  // Open pages move to it by themselves. Without one, or inside VS Code, open the page at it.
  if (found.viewers === 0 || vscode.workspace.getConfiguration('sightline').get('openIn') === 'editor') await open(found.hash);
  else vscode.window.setStatusBarMessage(`Sightline: showing ${found.name}`, 3000);
}

async function stop() {
  unfollow();
  if (started?.child && !started.child.killed) {
    started.child.kill();
    started = null;
    vscode.window.setStatusBarMessage('Sightline: the map is closed.', 3000);
  } else {
    vscode.window.showInformationMessage('Sightline: no map was started from VS Code. A map started elsewhere closes where it was started.');
  }
}

// -------------------------------------------------------------------- problems panel

/** Keeps the Problems panel in step with the map's problems, through its live event stream. */
function follow(m) {
  if (!m || !vscode.workspace.getConfiguration('sightline').get('problems')) return;
  if (watching && watching.url === m.url) return;
  unfollow();
  watching = { url: m.url, token: m.token, request: null, retry: null };
  refreshProblems(watching);
  listen(watching);
}

function unfollow() {
  if (!watching) return;
  watching.request?.destroy();
  clearTimeout(watching.retry);
  watching = null;
  problems.clear();
}

function listen(w) {
  // client=editor: this stream feeds the Problems panel; the map does not count it as an open page.
  const req = http.get(`${w.url}/api/events?token=${encodeURIComponent(w.token)}&client=editor`, (res) => {
    let buffer = '';
    res.setEncoding('utf8');
    res.on('data', (chunk) => {
      buffer += chunk;
      for (let cut; (cut = buffer.indexOf('\n\n')) >= 0;) {
        const block = buffer.slice(0, cut);
        buffer = buffer.slice(cut + 2);
        const name = /^event: (.*)$/m.exec(block)?.[1];
        const data = /^data: (.*)$/m.exec(block)?.[1];
        if (name === 'reload') refreshProblems(w);
        if (name === 'patch' && data && data.includes('"problems"')) {
          try { show(JSON.parse(data).problems); } catch { refreshProblems(w); }
        }
      }
    });
    res.on('end', () => again(w));
  });
  req.on('error', () => again(w));
  w.request = req;
}

function again(w) {
  if (watching !== w) return;
  w.retry = setTimeout(() => {
    if (watching !== w) return;
    request(w.url + '/api/ping', w.token).then((r) => {
      if (r.status === 204) { refreshProblems(w); listen(w); } else unfollow();
    }, () => unfollow());
  }, 3000);
}

async function refreshProblems(w) {
  try {
    const answer = await request(w.url + '/api/graph', w.token);
    if (answer.status === 200 && watching === w) show(JSON.parse(answer.text).problems ?? []);
  } catch {
    // the map is busy or gone; the event stream decides
  }
}

const SEVERITY = { error: vscode.DiagnosticSeverity.Error, warning: vscode.DiagnosticSeverity.Warning, info: vscode.DiagnosticSeverity.Information };

function show(list) {
  const byFile = new Map();
  for (const p of list ?? []) {
    if (!p.file) continue;
    const line = Math.max(0, (p.line ?? 1) - 1);
    const d = new vscode.Diagnostic(new vscode.Range(line, 0, line, 200), `${p.title}: ${p.message}`, SEVERITY[p.severity] ?? vscode.DiagnosticSeverity.Information);
    d.source = 'Sightline';
    d.code = p.rule;
    if (!byFile.has(p.file)) byFile.set(p.file, []);
    byFile.get(p.file).push(d);
  }
  problems.clear();
  for (const [file, diagnostics] of byFile) problems.set(vscode.Uri.file(file), diagnostics);
}

module.exports = { activate, deactivate };
