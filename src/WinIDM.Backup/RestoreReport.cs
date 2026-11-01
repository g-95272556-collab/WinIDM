using System.Text;
using WinIDM.Core.Models;

namespace WinIDM.Backup;

/// <summary>Restore mode: merge (default, safe) or overwrite (replaces the existing data).</summary>
public enum RestoreMode { Merge, Overwrite }

public enum RestoreStatus { Restored, Skipped, Failed }

/// <summary>Result for one restore unit (one file or one group of records).</summary>
public sealed record RestoreOutcome(BackupCategories Category, string Item, RestoreStatus Status, int ItemCount, string Message);

/// <summary>Partial restore report: each unit is recorded separately.</summary>
public sealed class RestoreReport
{
    private readonly List<RestoreOutcome> _outcomes = new();

    public IReadOnlyList<RestoreOutcome> Outcomes => _outcomes;

    public bool IsFullSuccess => !_outcomes.Any(o => o.Status == RestoreStatus.Failed);

    public void Add(RestoreOutcome outcome) => _outcomes.Add(outcome);

    public string ToText()
    {
        var sb = new StringBuilder();
        foreach (var o in _outcomes)
        {
            var status = o.Status switch
            {
                RestoreStatus.Restored => "RESTORED",
                RestoreStatus.Skipped => "SKIPPED",
                _ => "FAILED",
            };
            sb.Append('[').Append(status).Append("] ").Append(o.Item)
              .Append(" (").Append(o.Category).Append("): ").Append(o.Message);
            if (o.Status == RestoreStatus.Restored)
            {
                sb.Append(" [").Append(o.ItemCount).Append(" item]");
            }

            sb.AppendLine();
        }

        return sb.ToString();
    }
}
