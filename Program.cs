using System.Runtime.CompilerServices;

// ──────────────────────────────────────────────────────────────
// LeakDemo — deliberate memory issues for dotnet-dump analysis
// ──────────────────────────────────────────────────────────────

Console.WriteLine($"LeakDemo started  PID: {Environment.ProcessId}");
Console.WriteLine("Press Enter to stop...\n");

using var cts = new CancellationTokenSource();

// 1. Static collection leak — ~10 MB/sec of byte[] that never gets evicted
var cacheTask = Task.Run(() => StaticCacheLeak.Run(cts.Token));

// 2. Event handler leak — subscribers rooted by a static event
var eventTask = Task.Run(() => EventHandlerLeak.Run(cts.Token));

// 3. String duplication — many identical large strings on the heap
var stringTask = Task.Run(() => StringDuplication.Run(cts.Token));

// 4. Finalizer pressure — objects with ~Finalize queued faster than drained
var finalizerTask = Task.Run(() => FinalizerPressure.Run(cts.Token));

// 5. Async state machines — tasks that never complete
var asyncTasks = AsyncLeaks.StartLeakingTasks(500);

// Status line
var statusTask = Task.Run(async () =>
{
    while (!cts.Token.IsCancellationRequested)
    {
        var mb = GC.GetTotalMemory(forceFullCollection: false) / 1_048_576.0;
        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}]  Managed heap ≈ {mb:N1} MB   " +
            $"Cache items: {StaticCacheLeak.Count}   " +
            $"Event subs: {EventHandlerLeak.SubscriberCount}   " +
            $"Finalizer objects: {FinalizerPressure.Created}");
        await Task.Delay(1_000, cts.Token).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
    }
});

Console.ReadLine();
cts.Cancel();
await Task.WhenAll(cacheTask, eventTask, stringTask, finalizerTask, statusTask);
Console.WriteLine("Stopped.");

// ═══════════════════════════════════════════════════════════════
// 1. Static collection leak
// ═══════════════════════════════════════════════════════════════
static class StaticCacheLeak
{
    private static readonly List<byte[]> Cache = new();

    public static int Count => Cache.Count;

    public static async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            // Allocate a 100 KB buffer and keep it forever
            Cache.Add(new byte[100 * 1024]);
            await Task.Delay(10, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}

// ═══════════════════════════════════════════════════════════════
// 2. Event handler leak
// ═══════════════════════════════════════════════════════════════
static class EventHandlerLeak
{
    public static event EventHandler? OnSomething;
    private static int _subscriberCount;
    public static int SubscriberCount => _subscriberCount;

    public static async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var subscriber = new LeakySubscriber();
            OnSomething += subscriber.Handle;
            Interlocked.Increment(ref _subscriberCount);

            // Fire the event so the subscribers stay "useful"
            OnSomething?.Invoke(null, EventArgs.Empty);

            await Task.Delay(5, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private class LeakySubscriber
    {
        private readonly byte[] _payload = new byte[1024]; // prevent optimizing away

        public void Handle(object? sender, EventArgs e)
        {
            // Touch the payload so it's reachable
            _payload[0] = 1;
        }
    }
}

// ═══════════════════════════════════════════════════════════════
// 3. String duplication
// ═══════════════════════════════════════════════════════════════
static class StringDuplication
{
    private static readonly List<string> Strings = new();

    public static async Task Run(CancellationToken ct)
    {
        // Build the same large strings over and over — not interned
        string[] templates =
        [
            "OrderProcessingService-Handler-" + new string('X', 500),
            "CustomerDataExport-Pipeline-" + new string('Y', 500),
            "InventorySyncWorker-Batch-" + new string('Z', 500),
        ];

        while (!ct.IsCancellationRequested)
        {
            foreach (var t in templates)
            {
                // string.Concat produces a fresh copy each time
                Strings.Add(string.Concat(t, "-", Guid.NewGuid().ToString()[..8]));
            }

            await Task.Delay(50, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }
}

// ═══════════════════════════════════════════════════════════════
// 4. Finalizer pressure
// ═══════════════════════════════════════════════════════════════
static class FinalizerPressure
{
    private static int _created;
    public static int Created => _created;

    public static async Task Run(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            for (var i = 0; i < 200; i++)
            {
                _ = new FinalizableObject();
                Interlocked.Increment(ref _created);
            }

            await Task.Delay(10, ct).ConfigureAwait(ConfigureAwaitOptions.SuppressThrowing);
        }
    }

    private class FinalizableObject
    {
        private readonly byte[] _data = new byte[256];

        ~FinalizableObject()
        {
            // Intentionally slow finalizer to create backlog
            Thread.SpinWait(1_000);
            GC.KeepAlive(_data);
        }
    }
}

// ═══════════════════════════════════════════════════════════════
// 5. Async state machines that never complete
// ═══════════════════════════════════════════════════════════════
static class AsyncLeaks
{
    private static readonly List<Task> Pending = new();

    public static List<Task> StartLeakingTasks(int count)
    {
        for (var i = 0; i < count; i++)
        {
            Pending.Add(WaitForeverAsync($"operation-{i}"));
        }

        return Pending;
    }

    private static async Task WaitForeverAsync(string name)
    {
        // These TaskCompletionSources are never completed, so the
        // async state machines stay alive on the heap indefinitely.
        var tcs = new TaskCompletionSource();
        await tcs.Task;

        // Dead code — keeps 'name' captured in the state machine
        Console.WriteLine(name);
    }
}
