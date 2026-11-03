namespace Sqloryn.Core;

/// <summary>Prevents a second Run from cancelling or queuing while a batch is in flight.</summary>
public static class RunGuard
{
    public static bool TryEnter(ref int flag)
        => Interlocked.CompareExchange(ref flag, 1, 0) == 0;

    public static void Exit(ref int flag)
        => Interlocked.Exchange(ref flag, 0);

    public static bool IsEntered(int flag) => flag != 0;
}
