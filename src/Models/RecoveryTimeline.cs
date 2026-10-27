using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SourceGit.Models
{
    public sealed class RecoveryPoint
    {
        public string SHA { get; set; } = string.Empty;
        public string Description { get; set; } = string.Empty;
        public DateTimeOffset Time { get; set; }
        public string Source { get; set; } = string.Empty;
        public string ShortSHA => SHA.Length > 8 ? SHA[..8] : SHA;
        public string LocalTime => Time.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");
    }

    internal static class RecoveryTimeline
    {
        public static async Task CaptureAsync(string repository, string operation)
        {
            var head = await GitQuery.ReadAsync(repository, ["rev-parse", "--verify", "HEAD"]).ConfigureAwait(false);
            if (head.ExitCode != 0)
                return; // An unborn branch has no commit to preserve.
            var name = $"refs/sourcegit/recovery/{DateTimeOffset.UtcNow:yyyyMMddTHHmmssfffZ}-{Guid.NewGuid():N}/{operation}";
            var saved = await GitQuery.ReadAsync(repository, ["update-ref", name, head.Output.Trim()]).ConfigureAwait(false);
            if (saved.ExitCode != 0)
                throw new InvalidOperationException(saved.Error);
        }

        public static async Task<List<RecoveryPoint>> ReadAsync(string repository, CancellationToken token)
        {
            var points = new List<RecoveryPoint>();
            var saved = await GitQuery.ReadAsync(repository,
                ["for-each-ref", "--sort=-refname", "--count=500", "--format=%(refname)%00%(objectname)%00%(subject)", "refs/sourcegit/recovery/"], token).ConfigureAwait(false);
            if (saved.ExitCode != 0)
                throw new InvalidOperationException(saved.Error);
            foreach (var line in saved.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.TrimEnd('\r').Split('\0');
                if (fields.Length != 3)
                    continue;
                var path = fields[0].Split('/');
                if (path.Length != 5 || !DateTimeOffset.TryParseExact(path[3].Split('-')[0],
                    "yyyyMMdd'T'HHmmssfff'Z'", CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var time))
                    continue;
                points.Add(new RecoveryPoint { SHA = fields[1], Time = time, Source = "SourceGit", Description = $"{path[4]} · {fields[2]}" });
            }
            var reflog = await GitQuery.ReadAsync(repository,
                ["reflog", "show", "-n", "300", "--date=iso-strict", "--format=%H%x00%gD%x00%gs", "HEAD"], token).ConfigureAwait(false);
            foreach (var line in reflog.Output.Split('\n', StringSplitOptions.RemoveEmptyEntries))
            {
                var fields = line.TrimEnd('\r').Split('\0');
                if (fields.Length != 3)
                    continue;
                var start = fields[1].IndexOf("@{", StringComparison.Ordinal);
                if (start < 0 || !DateTimeOffset.TryParse(fields[1][(start + 2)..].TrimEnd('}'), out var time))
                    continue;
                points.Add(new RecoveryPoint { SHA = fields[0], Time = time, Source = "Git reflog", Description = fields[2] });
            }
            // shortcut: Show the 500 most recent points; saved refs stay available to Git beyond this view.
            return points.OrderByDescending(p => p.Time).Take(500).ToList();
        }

        public static async Task<string> CreateBranchAsync(string repository, RecoveryPoint point, CancellationToken token)
        {
            if (point == null || !System.Text.RegularExpressions.Regex.IsMatch(point.SHA, "^[0-9a-fA-F]{40,64}$"))
                throw new ArgumentException("Invalid recovery commit.");
            var name = $"recovery/{DateTime.Now:yyyyMMdd-HHmmss}-{Guid.NewGuid():N}";
            var result = await GitQuery.ReadAsync(repository, ["branch", name, point.SHA], token).ConfigureAwait(false);
            if (result.ExitCode != 0)
                throw new InvalidOperationException(result.Error);
            return name;
        }
    }
}
