using SysConsole = System.Console;

namespace ConsoleUI;

internal interface IConsolePrompt
{
    string Ask(string message);
    string? ReadLineTrimmed();
}

internal sealed class ConsolePrompt : IConsolePrompt
{
    public string Ask(string message)
    {
        SysConsole.Write(message);
        return SysConsole.ReadLine()?.Trim() ?? "";
    }

    public string? ReadLineTrimmed() => SysConsole.ReadLine()?.Trim();
}
