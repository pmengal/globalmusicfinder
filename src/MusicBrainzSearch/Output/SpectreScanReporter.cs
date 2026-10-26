using MusicBrainzSearch.Domain;
using Spectre.Console;

namespace MusicBrainzSearch.Output;

/// <summary>Affiche chaque phase du scanner sous forme de barre de progression Spectre.Console.</summary>
public sealed class SpectreScanReporter(ProgressContext context) : IScanReporter
{
    private readonly List<string> _messages = [];

    public IReadOnlyList<string> Messages => _messages;

    public IScanPhase Begin(string title, int total)
    {
        var task = context.AddTask(Markup.Escape(title), maxValue: Math.Max(total, 1));
        if (total == 0) task.IsIndeterminate = true;
        return new Phase(task, title);
    }

    public void Info(string message) => _messages.Add(message);

    private sealed class Phase(ProgressTask task, string title) : IScanPhase
    {
        public void Advance(string? detail = null)
        {
            if (!task.IsIndeterminate) task.Increment(1);
            if (detail is not null) task.Description = $"{Markup.Escape(title)} [grey]{Markup.Escape(Truncate(detail))}[/]";
        }

        public void AddToTotal(int count) => task.MaxValue += count;

        public void Dispose()
        {
            task.Description = Markup.Escape(title);
            task.IsIndeterminate = false;
            task.Value = task.MaxValue;
            task.StopTask();
        }

        private static string Truncate(string s) => s.Length <= 40 ? s : s[..39] + "…";
    }
}
