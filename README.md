# LeakDemo

LeakDemo is a .NET 10 console app that intentionally creates several memory leak patterns so you can practice dump analysis.

## Prerequisites

- .NET 10 SDK
- `dotnet-dump` CLI tool

Install `dotnet-dump` if needed:

```bash
dotnet tool install -g dotnet-dump
```

## Run LeakDemo

From the project root:

```bash
dotnet run
```

You should see output like:

```text
LeakDemo started  PID: 46841
Press Enter to stop...

[21:59:31]  Managed heap ~= 0.4 MB   Cache items: 2   Event subs: 4   Finalizer objects: 400
[21:59:32]  Managed heap ~= 18.1 MB  Cache items: 98  Event subs: 187 Finalizer objects: 19600
```

Leave this terminal running so memory can grow.

## Grab a memory dump

Open a second terminal.

1. Find the process ID (PID). You can use the PID printed by LeakDemo, or:

```bash
pgrep -f LeakDemo
```

2. Collect a dump:

```bash
dotnet-dump collect -p <PID>
```

3. Note the output file name printed by `dotnet-dump collect` — you will pass it to the analysis skill.

## Use the memory analysis skill

This repo includes a local command/skill at:

- `.claude/commands/dotnet-dump-analyze.md`

In Claude Code, run the skill with the name of your dump file as the argument:

```text
/dotnet-dump-analyze <your_dump_file>
```

The skill runs this first-pass triage:

```bash
dotnet-dump analyze "<your_dump_file>" \
  -c "eeversion" \
  -c "clrthreads" \
  -c "gcheapstat" \
  -c "dumpheap -stat" \
  -c "dumpheap -stat -min 85000" \
  -c "gchandles" \
  -c "finalizequeue" \
  -c "threadpool" \
  -c "pe -nested" \
  -c "exit"
```

## Example output for `core_20260306_221854`

### Triage excerpts

```text
Loading core dump: core_20260306_221854 ...
10.0.125.57005
Workstation mode
ThreadCount:      11

GC Committed Heap Size:    Size: 0xd95c000 (227,917,824) bytes.
Heap     EPH        Gen0       Gen1       Gen2       LOH        POH
Heap0    28156168   6665752    16431148   4797736    199174488  8208

Total 169,046 objects, 227,340,540 bytes
000108a0a5e8 77,854 223,182,288 System.Byte[]
000108a52778 72,247   1,733,928 FinalizerPressure+FinalizableObject
0001086b7598  2,289   1,421,182 System.String
000108a53400  3,857     246,848 System.EventHandler
000108a526e0  3,661      87,864 EventHandlerLeak+LeakySubscriber
000108a4ef40    500      28,000 AsyncLeaks+<WaitForeverAsync>d__2
000108a500f0    500      12,000 System.Threading.Tasks.TaskCompletionSource

LOH: 1,944 objects, 199,112,256 bytes (System.Byte[])

Handles:
    Strong Handles:       20
    Pinned Handles:       1
    Weak Short Handles:   21
    Dependent Handles:    6

Finalizable objects: 20,651 FinalizerPressure+FinalizableObject
Workers Total:    5   Running: 1   Idle: 4
No current managed exception.
```

### Example interpreted findings (skill-style)

```text
1) Finding: Static byte[] cache growth
   Evidence: dumpheap -stat shows System.Byte[] = 223,182,288 bytes (77,854 objects);
             LOH holds 199,112,256 bytes across 1,944 large arrays.
   Retention root: strong handle -> List<System.Byte[]> (static) -> Byte[][] -> Byte[].
   Source code: Program.cs:52 — `private static readonly List<byte[]> Cache = new();`
                Program.cs:61 — `Cache.Add(new byte[100 * 1024]);` adds 100 KB buffers that are never removed.
   Confidence: high.

2) Finding: Event subscriber retention
   Evidence: EventHandlerLeak+LeakySubscriber = 3,661 objects; System.EventHandler = 3,857 objects.
   Retention root: strong handle -> System.EventHandler (static) -> Object[] -> EventHandler -> LeakySubscriber.
   Source code: Program.cs:72 — `public static event EventHandler? OnSomething;` (static event as root)
                Program.cs:81 — `OnSomething += subscriber.Handle;` subscribes but never unsubscribes.
   Confidence: high.

3) Finding: Async tasks never complete
   Evidence: 500 TaskCompletionSource and 500 AsyncLeaks+<WaitForeverAsync>d__2 state machines.
   Retention root: strong handle -> List<Task> (static) -> Task[] -> AsyncStateMachineBox<WaitForeverAsync>.
   Source code: Program.cs:173 — `private static readonly List<Task> Pending = new();` holds all tasks.
                Program.cs:189-190 — `var tcs = new TaskCompletionSource(); await tcs.Task;`
                The TaskCompletionSource is never completed, so state machines stay alive indefinitely.
   Confidence: high.

4) Finding: Finalizer backlog pressure
   Evidence: 20,651 FinalizerPressure+FinalizableObject instances in the finalizer queue.
   Retention root: finalizer queue — objects are GC-unreachable but queued for finalization.
   Source code: Program.cs:145-148 — loop creates 200 FinalizableObject per iteration without throttling.
                Program.cs:161-162 — `~FinalizableObject()` calls `Thread.SpinWait(1_000)`,
                intentionally slowing the finalizer thread and causing backlog.
   Confidence: high.

5) Finding: Repeated large strings retained by static list
   Evidence: List<String> with 1,212 items; System.String = 1,421,182 bytes (2,289 objects).
   Retention root: strong handle -> List<String> (static) -> String[] -> String.
   Source code: Program.cs:108 — `private static readonly List<string> Strings = new();`
                Program.cs:125 — `Strings.Add(string.Concat(t, "-", ...));` appends but never removes.
                Each iteration adds 3 fresh ~540-char strings that accumulate unboundedly.
   Confidence: medium (single dump; growth rate unconfirmed without a second dump).
```

## Notes

- One dump gives a strong snapshot, but two time-separated dumps give better leak confidence.
- This demo is intentionally leaky and not meant for production patterns.
