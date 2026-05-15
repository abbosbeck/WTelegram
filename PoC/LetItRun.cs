using System.Diagnostics;

namespace PoC;

public class LetItRun
{
    public async Task Method1()
    {
        var sw = Stopwatch.StartNew();
        await Task.Delay(1000);
        await Task.Delay(1000);
        sw.Stop();

        Console.WriteLine($"Method1: {sw}");
    }

    public async Task Method2()
    {
        var sw = Stopwatch.StartNew();
        var task1 = Task.Delay(1000);
        var task2 = Task.Delay(1000);

        await task1;
        await task2;

        sw.Stop();

        Console.WriteLine($"Method1: {sw}");
    }
}
