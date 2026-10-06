namespace ChaosToDo.Api;

/// <summary>
/// Per-process count of todo-list database queries, split by read path. The cache demo
/// dashboard reads it from the X-Db-Queries response header to show database load per
/// worker; the counters restart from zero when the process restarts.
/// </summary>
internal static class DemoQueryCounter
{
    static long naive;
    static long fusion;
    static long noCache;

    public static void Naive() => Interlocked.Increment(ref naive);

    public static void Fusion() => Interlocked.Increment(ref fusion);

    public static void NoCache() => Interlocked.Increment(ref noCache);

    public static string HeaderValue =>
        $"naive={Interlocked.Read(ref naive)};fusion={Interlocked.Read(ref fusion)};nocache={Interlocked.Read(ref noCache)}";
}
