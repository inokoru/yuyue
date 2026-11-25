import { type ChildProcess, spawn, spawnSync } from 'node:child_process';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { ComputerAgent, agentForComputer } from '@midscene/computer';
import { type NodeExecutionContext, defineNode, z } from '@midscene/test';
import { defineFileSetup, defineTestProject } from '@midscene/test/config';
import { createMidsceneNodes } from '@midscene/test/midscene';
import { config as loadEnv } from 'dotenv';

const PROJECT_DIR = path.dirname(fileURLToPath(import.meta.url));
const REPO_ROOT = path.resolve(PROJECT_DIR, '..', '..');
const FIXTURES_BOOKS_DIR = path.join(REPO_ROOT, 'fixtures', 'books');

loadEnv({ path: path.join(PROJECT_DIR, '.env') });

interface FileContext {
  agent: ComputerAgent;
  appProcess: ChildProcess | null;
  libraryDir: string | null;
  ownsLibraryDir: boolean;
}

const getAgent = ({ context }: NodeExecutionContext<unknown, FileContext>) => {
  return context.agent;
};

function sleepWithSignal(ms: number, signal?: AbortSignal): Promise<void> {
  if (ms <= 0) return Promise.resolve();
  return new Promise((resolve, reject) => {
    if (signal?.aborted) {
      reject(signal.reason ?? new Error('Aborted'));
      return;
    }
    const timer = setTimeout(() => {
      signal?.removeEventListener('abort', onAbort);
      resolve();
    }, ms);
    const onAbort = () => {
      clearTimeout(timer);
      reject(signal?.reason ?? new Error('Aborted'));
    };
    signal?.addEventListener('abort', onAbort, { once: true });
  });
}

function findExeRecursively(dir: string): string | null {
  if (!fs.existsSync(dir)) return null;
  const entries = fs.readdirSync(dir, { withFileTypes: true });
  for (const entry of entries) {
    const fullPath = path.join(dir, entry.name);
    if (entry.isFile() && entry.name.toLowerCase() === 'yuyue.exe') {
      return fullPath;
    }
    if (entry.isDirectory()) {
      const found = findExeRecursively(fullPath);
      if (found) return found;
    }
  }
  return null;
}

function resolveYuyueExe(explicitPath?: string): string {
  const candidates = [
    explicitPath,
    process.env.YUYUE_EXE,
    path.join(
      REPO_ROOT,
      'windows',
      'Yuyue',
      'bin',
      'x64',
      'Debug',
      'net8.0-windows10.0.22621.0',
      'win-x64',
      'Yuyue.exe',
    ),
    path.join(
      REPO_ROOT,
      'windows',
      'Yuyue',
      'bin',
      'x64',
      'Release',
      'net8.0-windows10.0.22621.0',
      'win-x64',
      'Yuyue.exe',
    ),
  ].filter((p): p is string => Boolean(p && p.trim()));

  for (const candidate of candidates) {
    const resolved = path.isAbsolute(candidate)
      ? candidate
      : path.resolve(REPO_ROOT, candidate);
    if (fs.existsSync(resolved)) return resolved;
  }

  const discovered = findExeRecursively(
    path.join(REPO_ROOT, 'windows', 'Yuyue', 'bin'),
  );
  if (discovered) return discovered;

  throw new Error(
    '未找到 Yuyue.exe。请先在仓库根目录构建 Windows 端（dotnet build windows/Yuyue/Yuyue.csproj），或在 tests/midscene/.env 中设置 YUYUE_EXE。',
  );
}

function bringProcessWindowToFront(pid: number, timeoutMs = 10_000): boolean {
  if (process.platform !== 'win32') return true;
  const psScript = `
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class WinFocus {
  [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
  [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);
  public static void Focus(IntPtr h) {
    ShowWindow(h, 9);
    SetWindowPos(h, new IntPtr(-1), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
    SetWindowPos(h, new IntPtr(-2), 0, 0, 0, 0, 0x0001 | 0x0002 | 0x0040);
    SetForegroundWindow(h);
  }
}
"@
$deadline = (Get-Date).AddMilliseconds(${timeoutMs})
while ((Get-Date) -lt $deadline) {
  $p = Get-Process -Id ${pid} -ErrorAction SilentlyContinue
  if (-not $p) { exit 2 }
  $p.Refresh()
  if ($p.MainWindowHandle -ne [IntPtr]::Zero) {
    [WinFocus]::Focus($p.MainWindowHandle)
    exit 0
  }
  Start-Sleep -Milliseconds 200
}
exit 1
`;
  const res = spawnSync(
    'powershell.exe',
    ['-NoProfile', '-NonInteractive', '-Command', psScript],
    { stdio: 'ignore', windowsHide: true, timeout: timeoutMs + 5_000 },
  );
  return res.status === 0;
}

function readCrashLogSnippet(libraryDir: string): string {
  const logPath = path.join(libraryDir, 'yuyue.log');
  if (!fs.existsSync(logPath)) return '';
  try {
    const content = fs.readFileSync(logPath, 'utf8').trim();
    return content.length > 800 ? content.slice(-800) : content;
  } catch {
    return '';
  }
}

async function stopYuyueProcess(
  context: FileContext,
  cleanupLibrary: boolean,
): Promise<void> {
  const proc = context.appProcess;
  context.appProcess = null;

  if (proc?.pid && proc.exitCode === null) {
    if (process.platform === 'win32') {
      spawnSync('taskkill', ['/PID', String(proc.pid), '/T', '/F'], {
        stdio: 'ignore',
        windowsHide: true,
      });
    } else {
      try {
        proc.kill('SIGTERM');
      } catch {
        // ignore
      }
    }
    // Wait briefly so WebView2 / SQLite handles release before deleting temp library.
    await sleepWithSignal(400);
  }

  if (cleanupLibrary && context.ownsLibraryDir && context.libraryDir) {
    const dirToDelete = context.libraryDir;
    context.libraryDir = null;
    context.ownsLibraryDir = false;

    for (let attempt = 0; attempt < 3; attempt++) {
      try {
        fs.rmSync(dirToDelete, { recursive: true, force: true });
        break;
      } catch {
        await sleepWithSignal(300);
      }
    }
  }
}

const fileSetup = defineFileSetup<FileContext, undefined>({
  name: 'computer',
  async setup({ env, onTeardown }) {
    const agent = await agentForComputer({
      displayId: env.COMPUTER_DISPLAY_ID || undefined,
    });
    const ctx: FileContext = {
      agent,
      appProcess: null,
      libraryDir: null,
      ownsLibraryDir: false,
    };
    onTeardown(async () => {
      await stopYuyueProcess(ctx, true);
      await agent.destroy();
    });
    return ctx;
  },
});

const yuyueLaunchInputSchema = z.strictObject({
  exePath: z
    .string()
    .optional()
    .describe('Optional override path to Yuyue.exe.'),
  libraryDir: z
    .string()
    .optional()
    .describe(
      'Optional custom library folder. Omit to use an isolated temporary library directory.',
    ),
  reuseLibrary: z
    .boolean()
    .optional()
    .describe(
      'Reuse the previous isolated temporary library directory (useful for restart persistence tests).',
    ),
  seedDemo: z
    .boolean()
    .optional()
    .describe('Pass --seed-demo (Debug build only) to populate demo books.'),
  fixtures: z
    .array(z.string())
    .optional()
    .describe(
      'Fixture file names under fixtures/books/ (e.g. ["beiying.epub"]) or absolute file paths to import & open on launch.',
    ),
  open: z
    .string()
    .optional()
    .describe('Pass --open <query> (Debug build only) to open a matching book.'),
  theme: z
    .enum(['light', 'dark'])
    .optional()
    .describe('Pass --light or --dark (Debug build only) to force theme mode.'),
  tab: z
    .string()
    .optional()
    .describe('Pass --tab <name> to switch the shelf tab on launch.'),
  settings: z
    .union([z.boolean(), z.string()])
    .optional()
    .describe(
      'Pass --settings or --settings <page> to open the Settings window directly.',
    ),
  extraArgs: z
    .array(z.string())
    .optional()
    .describe('Additional raw CLI arguments forwarded to Yuyue.exe.'),
  waitMs: z
    .number()
    .int()
    .min(0)
    .max(30_000)
    .optional()
    .describe('Milliseconds to wait after spawning Yuyue.exe (default: 3000).'),
});

const yuyueLaunch = defineNode<
  typeof yuyueLaunchInputSchema,
  { pid: number; exePath: string; libraryDir: string; args: string[] },
  FileContext
>({
  name: 'yuyue.launch',
  title: 'Launch Yuyue Windows with an isolated library',
  description:
    'Start Yuyue.exe against an isolated temporary --library directory so tests never touch the user book library.',
  inputSchema: yuyueLaunchInputSchema,
  async execute({ input, context, signal }) {
    signal.throwIfAborted();

    const shouldReuse = Boolean(input.reuseLibrary && context.libraryDir);
    await stopYuyueProcess(context, !shouldReuse);

    const exePath = resolveYuyueExe(input.exePath);

    let libraryDir: string;
    if (input.libraryDir) {
      libraryDir = path.resolve(REPO_ROOT, input.libraryDir);
      fs.mkdirSync(libraryDir, { recursive: true });
      context.ownsLibraryDir = false;
    } else if (shouldReuse && context.libraryDir) {
      libraryDir = context.libraryDir;
    } else {
      libraryDir = fs.mkdtempSync(path.join(os.tmpdir(), 'yuyue-midscene-'));
      context.ownsLibraryDir = true;
    }
    context.libraryDir = libraryDir;

    const args: string[] = ['--library', libraryDir];
    if (input.seedDemo) args.push('--seed-demo');
    if (input.theme === 'dark') args.push('--dark');
    if (input.theme === 'light') args.push('--light');
    if (input.tab) args.push('--tab', input.tab);
    if (input.open) args.push('--open', input.open);
    if (input.settings === true) {
      args.push('--settings');
    } else if (typeof input.settings === 'string' && input.settings.trim()) {
      args.push('--settings', input.settings.trim());
    }
    if (input.extraArgs?.length) {
      args.push(...input.extraArgs);
    }
    if (input.fixtures?.length) {
      for (const item of input.fixtures) {
        const candidate = path.isAbsolute(item)
          ? item
          : path.join(FIXTURES_BOOKS_DIR, item);
        if (!fs.existsSync(candidate)) {
          throw new Error(`测试夹具文件不存在: ${candidate}`);
        }
        args.push(candidate);
      }
    }

    const child = spawn(exePath, args, {
      cwd: path.dirname(exePath),
      stdio: 'ignore',
      windowsHide: false,
    });
    if (!child.pid) {
      throw new Error(`启动 Yuyue.exe 失败: ${exePath}`);
    }
    context.appProcess = child;

    const focused = bringProcessWindowToFront(child.pid, 12_000);
    await sleepWithSignal(input.waitMs ?? 2000, signal);
    signal.throwIfAborted();

    if (child.exitCode !== null) {
      const logTail = readCrashLogSnippet(libraryDir);
      throw new Error(
        `Yuyue.exe 启动后提前退出，exitCode=${child.exitCode}${logTail ? `\nyuyue.log:\n${logTail}` : ''}`,
      );
    }

    if (!focused) {
      const logTail = readCrashLogSnippet(libraryDir);
      throw new Error(
        `Yuyue.exe (PID ${child.pid}) 在 12s 内未创建主窗口${logTail ? `\nyuyue.log:\n${logTail}` : ''}`,
      );
    }

    return {
      summary: `Launched Yuyue.exe (PID ${child.pid}) with isolated library ${libraryDir}`,
      data: {
        pid: child.pid,
        exePath,
        libraryDir,
        args,
      },
    };
  },
});

const yuyueCloseInputSchema = z.strictObject({
  keepLibrary: z
    .boolean()
    .optional()
    .describe(
      'Keep the isolated temporary library on disk so a subsequent yuyue.launch({ reuseLibrary: true }) can verify persistence.',
    ),
});

const yuyueClose = defineNode<typeof yuyueCloseInputSchema, void, FileContext>({
  name: 'yuyue.close',
  title: 'Close Yuyue Windows test instance',
  description:
    'Terminate the test-launched Yuyue.exe process and optionally clean up its temporary library folder.',
  inputSchema: yuyueCloseInputSchema,
  async execute({ input, context }) {
    await stopYuyueProcess(context, !input.keepLibrary);
    return {
      summary: input.keepLibrary
        ? 'Closed Yuyue.exe and kept temporary library for relaunch.'
        : 'Closed Yuyue.exe and cleaned up temporary library.',
    };
  },
});

export default defineTestProject<FileContext, undefined>({
  projects: [
    {
      name: 'computer',
      fileSetup,
      maxConcurrentFiles: 1,
      files: { include: ['cases/**/*.{yaml,yml}'] },
    },
  ],
  nodes: [
    yuyueLaunch,
    yuyueClose,
    ...createMidsceneNodes<FileContext>({
      agentClass: ComputerAgent,
      getAgent,
    }),
  ],
  test: { maxConcurrentFiles: 1, testTimeout: 360_000 },
});
