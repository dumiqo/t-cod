import { spawnSync } from 'node:child_process';
import { existsSync, readFileSync } from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';

const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '..', '..');

let input = {};
try {
  input = JSON.parse(readFileSync(0, 'utf8') || '{}');
} catch {}

const filePath = input.tool_input?.file_path;
if (filePath) {
  const abs = path.resolve(filePath);
  const rel = path.relative(root, abs).split(path.sep).join('/');
  const inside = !rel.startsWith('..') && !path.isAbsolute(rel);

  if (inside && rel.toLowerCase().endsWith('.cs') && existsSync(abs)) {
    const result = spawnSync('dotnet', ['format', 'whitespace', '.', '--folder', '--include', rel], {
      cwd: root,
      encoding: 'utf8',
      env: { ...process.env, DOTNET_CLI_UI_LANGUAGE: 'en' },
      timeout: 60_000,
    });

    if (result.status !== 0) {
      const details = (result.error?.message || `${result.stdout || ''}${result.stderr || ''}`).trim();
      process.stdout.write(
        JSON.stringify({
          hookSpecificOutput: {
            hookEventName: 'PostToolUse',
            additionalContext: `dotnet format whitespace failed for ${rel}: ${details.slice(0, 2000)}`,
          },
        }),
      );
    }
  }
}
