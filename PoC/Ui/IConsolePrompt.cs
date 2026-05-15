namespace TelegramDownloader.Ui;

internal interface IConsolePrompt
{
    string Ask(string message);
    string? ReadLineTrimmed();
}

internal sealed class ConsolePrompt : IConsolePrompt
{
    public string Ask(string message)
    {
        Console.Write(message);
        return Console.ReadLine()?.Trim() ?? "";
    }

    public string? ReadLineTrimmed() => Console.ReadLine()?.Trim();
}
