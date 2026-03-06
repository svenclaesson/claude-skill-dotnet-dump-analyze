---
description: Expert assistant for generic .NET memory leak analysis using dotnet-dump and SOS retention tracing
argument: Path to dump file (or first dump when comparing multiple dumps)
---

# dotnet-dump Memory Leak Analysis

Analyze .NET dumps to find leak candidates and prove retention paths.

Focus on actionable findings, not exhaustive command catalogs.

## Operating Rules

1. Verify dump path exists before analysis.
2. Prefer reproducible non-interactive command runs first.
3. Prioritize: largest growth candidates -> retention roots -> confidence.
4. Keep output concise; avoid pasting large raw tables unless requested.
5. If only one dump is available, label confidence carefully. Leak confidence is highest when two time-separated dumps show growth.

## Required First Pass

Run this triage first:

```bash
dotnet-dump analyze "$ARGUMENTS" \
  -c "eeversion" \
  -c "clrthreads" \
  -c "eeheap -gc" \
  -c "gcheapstat" \
  -c "dumpheap -stat" \
  -c "dumpheap -stat -min 85000" \
  -c "gchandles" \
  -c "finalizequeue" \
  -c "threadpool" \
  -c "pe -nested" \
  -c "exit"
```

Report:

1. Top types by total bytes and count (bottom of `dumpheap -stat` — sorted ascending, biggest consumers are last)
2. LOH-heavy types (`-min 85000`) — objects ≥85,000 bytes go on the Large Object Heap, collected far less frequently
3. GC heap segment layout from `eeheap -gc` — total committed vs reserved, per-generation sizes
4. Unusual handle/finalizer/threadpool signals
5. Crash/exception context (if present)
6. Workstation vs Server GC mode (from `eeversion` output) — Server GC uses one heap per CPU core

## Investigation Playbooks

Choose playbooks based on first-pass signals.

### 1) Dominant Type Retention

```bash
dumpheap -stat
dumpheap -type <TypeName>
dumpheap -short -type <TypeName>
dumpobj <addr>
gcroot <addr>
```

Use when one type is unexpectedly dominant in size or count.

### 2) Collection/Cache Growth

```bash
dumpheap -type System.Collections.Generic.List
dumpheap -type System.Collections.Generic.Dictionary
dumpheap -type System.Collections.Concurrent.ConcurrentDictionary
dumpobj <collection_addr>
gcroot <collection_or_item_addr>
```

Look for unbounded collections rooted by statics, singletons, or long-lived services.

### 3) Event/Delegate Leaks

```bash
dumpheap -type System.MulticastDelegate
dumpheap -type <SubscriberType>
gcroot <subscriber_addr>
```

Look for long-lived publishers retaining short-lived subscribers.

### 4) Async/Task Retention

```bash
dumpasync -waiting
dumpheap -type System.Threading.Tasks.Task
gcroot <task_or_state_machine_addr>
```

Look for never-completed tasks, stuck continuations, or captured closures.

### 5) String Duplication / Log Buffer Leaks

```bash
dumpheap -strings
dumpheap -type System.String -min 200
gcroot <string_addr>
```

Look for repeated payloads retained by caches, queues, or buffered logging.

### 6) Finalizer Backlog / Dispose Issues

```bash
finalizequeue
threads -special
setthread <finalizer_thread_index>
clrstack
```

Look for large ready/f-reachable counts and blocked or slow finalizer execution.

### 7) LOH / Pinned Object Pressure

```bash
dumpheap -stat -min 85000
gchandles
dumpheap -type System.Byte[]
gcroot <large_object_addr>
```

Look for large arrays/strings and pinned handles preventing effective reclamation.

## Optional Deep Dive Commands

Use only after selecting a concrete suspect:

```bash
gcroot -all <addr>
pathto <root_addr> <target_addr>
objsize <addr>
gcwhere <addr>
syncblk
threadpoolqueue
```

## Multi-Dump Comparison (Recommended)

If two or more dumps exist for the same process at different times:

1. Run identical triage on each dump.
2. Compare type count and total bytes trends.
3. Confirm the same root chain pattern persists across dumps.
4. Prioritize candidates with both growth and stable retention roots.

Do not claim a confirmed leak from one snapshot without a plausible retention chain.

## Source Code Correlation

After identifying retention roots, locate the corresponding source code in the repository:

1. Map the retaining type/field from `gcroot` output to its source file (use Grep/Glob to find the class/field declaration).
2. Identify the exact line(s) where the leak originates — e.g., where objects are added to a collection but never removed, where event handlers are subscribed but never unsubscribed, or where disposables are created but never disposed.
3. If the retention chain involves multiple types, trace back through the chain to find the earliest point in user code that could be fixed.

This is critical — every finding MUST include a source code location.

## Output Contract

For each suspected leak, provide:

1. Finding
2. Evidence (command + key metric/line)
3. Retention root chain summary
4. **Source code location** — file path and line number(s) in the repository where the leak originates, with a brief explanation of why that code causes the retention
5. Confidence (`high`, `medium`, `low`)
6. Next command to increase confidence when confidence is not `high`
