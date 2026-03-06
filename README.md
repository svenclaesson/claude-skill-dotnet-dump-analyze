# dotnet-dump Memory Leak Analysis — Claude Code Skill

A Claude Code skill that analyzes .NET memory dumps to find leak candidates, prove retention paths, and correlate findings back to source code.

## What the skill does

Given a `.NET` memory dump, the skill:

1. **Triages** the heap — runs `eeversion`, `clrthreads`, `gcheapstat`, `dumpheap -stat`, `gchandles`, `finalizequeue`, and more in a single non-interactive pass.
2. **Investigates** the top suspects using playbooks for dominant types, collection/cache growth, event/delegate leaks, async retention, string duplication, finalizer backlog, and LOH pressure.
3. **Traces retention roots** with `gcroot` to prove *why* objects stay alive.
4. **Correlates to source code** — every finding includes file path and line numbers showing where the leak originates.
5. **Reports** structured findings with evidence, retention chain, source location, and confidence level.

## Install the skill

Copy the skill file into any repo's `.claude/commands/` directory:

```
.claude/commands/dotnet-dump-analyze.md
```

The skill is then available as a slash command in Claude Code.

## Prerequisites

- `dotnet-dump` CLI tool

```bash
dotnet tool install -g dotnet-dump
```

## Usage

### 1. Collect a dump from a running .NET process

```bash
dotnet-dump collect -p <PID>
```

### 2. Run the skill

In Claude Code:

```
/dotnet-dump-analyze <your_dump_file>
```

### 3. Multi-dump comparison (recommended)

For higher confidence, collect two dumps separated by time and run the skill on each. The skill will compare type counts and byte totals to confirm growth trends.

## Example output

The skill produces structured findings like this:

```
1) Finding: Static byte[] cache growth
   Evidence: dumpheap -stat shows System.Byte[] = 223 MB (77,854 objects);
             LOH holds 199 MB across 1,944 large arrays.
   Retention root: strong handle -> List<Byte[]> (static) -> Byte[][] -> Byte[].
   Source code: Program.cs:52 — private static readonly List<byte[]> Cache = new();
                Program.cs:61 — Cache.Add(new byte[100 * 1024]); adds 100 KB buffers never removed.
   Confidence: high.

2) Finding: Event subscriber retention
   Evidence: LeakySubscriber = 3,661 objects; EventHandler = 3,857 objects.
   Retention root: strong handle -> EventHandler (static) -> Object[] -> LeakySubscriber.
   Source code: Program.cs:72 — public static event EventHandler? OnSomething; (static event as root)
                Program.cs:81 — OnSomething += subscriber.Handle; subscribes but never unsubscribes.
   Confidence: high.

3) Finding: Async tasks never complete
   Evidence: 500 TaskCompletionSource and 500 WaitForeverAsync state machines.
   Retention root: strong handle -> List<Task> (static) -> AsyncStateMachineBox.
   Source code: Program.cs:173 — private static readonly List<Task> Pending = new();
                Program.cs:189-190 — var tcs = new TaskCompletionSource(); await tcs.Task; (never completed)
   Confidence: high.
```

## Demo app (LeakDemo)

This repo includes a small .NET 10 console app that intentionally creates several memory leak patterns for practicing dump analysis.

```bash
dotnet run
```

It produces output showing heap growth:

```
LeakDemo started  PID: 46841
Press Enter to stop...

[21:59:31]  Managed heap ~= 0.4 MB   Cache items: 2   Event subs: 4   Finalizer objects: 400
[21:59:32]  Managed heap ~= 18.1 MB  Cache items: 98  Event subs: 187 Finalizer objects: 19600
```

The demo exercises five leak patterns: static cache growth, event handler retention, async task retention, finalizer backlog pressure, and unbounded string lists.

## Notes

- One dump gives a strong snapshot, but two time-separated dumps give better leak confidence.
- The skill works on any .NET application — LeakDemo is just a convenient test target.
