import { spawnSync } from 'node:child_process';
import { createHash } from 'node:crypto';
import { existsSync, mkdirSync, readFileSync, writeFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');
const stateDir = path.join(root, '.claude', '.tdd-state');
const testDir = /(^|\/)[^/]*Tests\//i;
const existingTests = /(^|\/)[^/]*Tests\/Existing\//i;
const buildInput = /\.(cs|csproj|json|props|targets|sln)$/i;

function readInput() {
  try {
    return JSON.parse(readFileSync(0, 'utf8') || '{}');
  } catch {
    return {};
  }
}

function output(value) {
  process.stdout.write(JSON.stringify(value));
}

function git(args) {
  const result = spawnSync('git', args, { cwd: root, encoding: 'utf8' });
  return result.status === 0 ? result.stdout : null;
}

function changedFiles() {
  const raw = git(['status', '--porcelain', '-z', '-uall']);
  if (raw === null) return null;
  const entries = raw.split('\0').filter(Boolean);
  const files = [];
  for (let i = 0; i < entries.length; i++) {
    const status = entries[i].slice(0, 2);
    files.push(entries[i].slice(3));
    if (status[0] === 'R' || status[0] === 'C') i++;
  }
  return files;
}

function readState(name) {
  try {
    return readFileSync(path.join(stateDir, name), 'utf8').trim();
  } catch {
    return null;
  }
}

function writeState(name, value) {
  mkdirSync(stateDir, { recursive: true });
  writeFileSync(path.join(stateDir, name), value);
}

function gate(input) {
  const filePath = input.tool_input?.file_path;
  if (!filePath) return;
  const rel = path.relative(root, path.resolve(filePath)).split(path.sep).join('/');
  if (rel.startsWith('..') || path.isAbsolute(rel)) return;
  if (!rel.toLowerCase().endsWith('.cs')) return;

  if (existingTests.test(rel)) {
    output({
      hookSpecificOutput: {
        hookEventName: 'PreToolUse',
        permissionDecision: 'ask',
        permissionDecisionReason:
          `Правка ${rel}: тесты в Existing/ закрепляют поведение выданного кода. ` +
          'Разрешай, только если понятно, зачем их менять.',
      },
    });
    return;
  }

  if (testDir.test(rel)) return;

  const files = changedFiles();
  if (files === null) return;
  if (files.some((f) => f.toLowerCase().endsWith('.cs') && testDir.test(f))) return;

  output({
    hookSpecificOutput: {
      hookEventName: 'PreToolUse',
      permissionDecision: 'deny',
      permissionDecisionReason:
        `TDD: editing ${rel} is blocked — no .cs file under a *Tests/ directory is changed ` +
        'relative to HEAD. Write a test for this behaviour first, run it with ' +
        '`dotnet test --filter ...`, confirm it fails on an assertion, then change the production code.',
    },
  });
}

function fingerprint(files) {
  const hash = createHash('sha1').update((git(['rev-parse', 'HEAD']) || '').trim());
  for (const file of [...files].sort()) {
    const abs = path.join(root, file);
    hash.update(`\0${file}\0`);
    hash.update(existsSync(abs) ? readFileSync(abs) : 'deleted');
  }
  return hash.digest('hex');
}

function summarize(text) {
  const lines = text.split(/\r?\n/);
  const picked = lines.filter(
    (l, i) =>
      /^\s*(Failed |Failed!|Passed!|Total tests|Error Message:)/.test(l) ||
      /\berror [A-Z]+\d+/.test(l) ||
      /^\s*Error Message:/.test(lines[i - 1] ?? ''),
  );
  const unique = [...new Set(picked.map((l) => l.trim()))];
  return (unique.length > 0 ? unique.slice(0, 40) : lines.slice(-40)).join('\n');
}

function stop(input) {
  const files = changedFiles();
  if (files === null) return;
  const relevant = files.filter((f) => !f.startsWith('.claude/') && buildInput.test(f));
  if (relevant.length === 0) return;

  const current = fingerprint(relevant);
  if (readState('green') === current) return;

  if (input.stop_hook_active && readState('red') === current) {
    output({
      systemMessage:
        'TDD: dotnet test красный, а код с прошлого прогона не менялся. Остановка разрешена, ' +
        'но работа не готова: тесты не проходят.',
    });
    return;
  }

  const result = spawnSync('dotnet', ['test', '--nologo'], {
    cwd: root,
    encoding: 'utf8',
    env: { ...process.env, DOTNET_CLI_UI_LANGUAGE: 'en' },
    timeout: 540_000,
    maxBuffer: 64 * 1024 * 1024,
  });

  if (result.error && result.error.code === 'ENOENT') {
    output({ systemMessage: 'TDD: dotnet не найден, проверка тестов пропущена.' });
    return;
  }

  if (result.status === 0) {
    writeState('green', current);
    return;
  }

  writeState('red', current);
  const reason = result.error
    ? `dotnet test did not finish: ${result.error.message}`
    : summarize(`${result.stdout || ''}\n${result.stderr || ''}`);

  output({
    decision: 'block',
    reason:
      'TDD: the code has changed and `dotnet test` is failing. Get the tests green ' +
      '(if it failed connecting to the database, run `docker compose up -d`).\n' +
      'If stopping in the red phase is intentional (the user asked to review tests before the ' +
      'implementation), change nothing, tell the user, and finish the turn again.\n\n' +
      reason,
  });
}

const mode = process.argv[2];
const input = readInput();
if (mode === 'gate') gate(input);
else if (mode === 'stop') stop(input);
