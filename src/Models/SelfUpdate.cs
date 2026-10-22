using System;
using System.Collections.Generic;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json.Serialization;

namespace SourceGit.Models
{
    public class Version
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("tag_name")]
        public string TagName { get; set; }

        [JsonPropertyName("published_at")]
        public DateTime PublishedAt { get; set; }

        [JsonPropertyName("body")]
        public string Body { get; set; }

        [JsonPropertyName("assets")]
        public List<ReleaseAsset> Assets { get; set; } = [];

        [JsonIgnore]
        public System.Version CurrentVersion { get; }

        [JsonIgnore]
        public string CurrentVersionStr => $"v{CurrentVersion.ToString(CurrentVersion.Revision > 0 ? 4 : 3)}";

        [JsonIgnore]
        public bool IsNewVersion => IsNewer(TagName, CurrentVersion);

        [JsonIgnore]
        public bool IsInstalled { get; set; }

        [JsonIgnore]
        public bool CanAutoUpdate => !IsInstalled && OperatingSystem.IsWindows() &&
            RuntimeInformation.ProcessArchitecture is Architecture.X64 or Architecture.Arm64 &&
            FindAsset(RuntimeInformation.ProcessArchitecture == Architecture.Arm64 ? "win-arm64" : "win-x64") != null;

        [JsonIgnore]
        public string ReleaseDateStr => DateTimeFormat.Format(PublishedAt, true);

        public Version()
        {
            var assembly = Assembly.GetExecutingAssembly().GetName();
            CurrentVersion = assembly.Version ?? new System.Version();
        }

        public ReleaseAsset FindAsset(string runtime)
        {
            if (string.IsNullOrEmpty(TagName) || !TagName.StartsWith('v'))
                return null;

            var name = $"sourcegit_{TagName.Substring(1)}.{runtime}.zip";
            return Assets?.Find(asset => asset.Name == name);
        }

        internal static bool IsNewer(string tag, System.Version current)
        {
            if (string.IsNullOrEmpty(tag) || !tag.StartsWith('v') || !System.Version.TryParse(tag.Substring(1), out var version))
                return false;

            version = new System.Version(version.Major, version.Minor, Math.Max(0, version.Build), Math.Max(0, version.Revision));
            return current.CompareTo(version) < 0;
        }
    }

    public class ReleaseAsset
    {
        [JsonPropertyName("name")]
        public string Name { get; set; }

        [JsonPropertyName("size")]
        public long Size { get; set; }

        [JsonPropertyName("digest")]
        public string Digest { get; set; }
    }

    public class AlreadyUpToDate;

    public class SelfUpdateFailed
    {
        public string Reason
        {
            get;
            private set;
        }

        public SelfUpdateFailed(Exception e)
        {
            if (e.InnerException is { } inner)
                Reason = inner.Message;
            else
                Reason = e.Message;
        }
    }
}
