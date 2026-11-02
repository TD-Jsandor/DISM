using System.Text.RegularExpressions;

namespace DismGui.Services;

public sealed record ImageEdition(int Index, string Name)
{
    public override string ToString() => $"{Index}  ·  {Name}";
}
public sealed record MountedImage(string MountPath, string ImagePath, int Index, string Status)
{
    public override string ToString() => $"{MountPath}  |  Index {Index}  |  {Status}\n{ImagePath}";
}
public sealed record DismCommand(string[] Arguments)
{
    // Display only. ProcessStartInfo.ArgumentList passes the actual arguments without a shell.
    public override string ToString() => "dism.exe " + string.Join(" ", Arguments.Select(a => a.Any(char.IsWhiteSpace) ? $"\"{a}\"" : a));
}
public sealed record InjectionPlan(string ImagePath, int[] Indexes, string MountPath, string[] DriverFolders);

public static class DismCommands
{
    public static DismCommand Info(string image) => new(["/English", "/Get-WimInfo", $"/WimFile:{image}"]);
    public static DismCommand Mounted() => new(["/English", "/Get-MountedImageInfo"]);
    public static DismCommand Mount(InjectionPlan p, int index) => new(["/English", "/Mount-Image", $"/ImageFile:{p.ImagePath}", $"/Index:{index}", $"/MountDir:{p.MountPath}", "/CheckIntegrity"]);
    public static DismCommand AddDrivers(InjectionPlan p, string folder) => new(["/English", $"/Image:{p.MountPath}", "/Add-Driver", $"/Driver:{folder}", "/Recurse"]);
    public static DismCommand Unmount(string mount, bool commit) => new(["/English", "/Unmount-Image", $"/MountDir:{mount}", commit ? "/Commit" : "/Discard", "/CheckIntegrity"]);
    public static DismCommand Remount(string mount) => new(["/English", "/Remount-Image", $"/MountDir:{mount}"]);
    public static DismCommand Export(string source, int index, string target) => new(["/English", "/Export-Image", $"/SourceImageFile:{source}", $"/SourceIndex:{index}", $"/DestinationImageFile:{target}", "/Compress:max", "/CheckIntegrity"]);
    public static DismCommand Split(string source, string target) => new(["/English", "/Split-Image", $"/ImageFile:{source}", $"/SWMFile:{target}", "/FileSize:3800", "/CheckIntegrity"]);

    public static IEnumerable<DismCommand> Preview(InjectionPlan p)
    {
        foreach (var index in p.Indexes)
        {
            yield return Mount(p, index);
            foreach (var folder in p.DriverFolders) yield return AddDrivers(p, folder);
            yield return Unmount(p.MountPath, true);
        }
    }

    public static List<ImageEdition> ParseEditions(string text)
    {
        var result = new List<ImageEdition>();
        int? index = null;
        foreach (var line in text.Split('\n'))
        {
            var m = Regex.Match(line.Trim(), @"^Index\s*:\s*(\d+)$");
            if (m.Success) index = int.Parse(m.Groups[1].Value);
            else if (index.HasValue && Regex.IsMatch(line.Trim(), @"^Name\s*:"))
            {
                result.Add(new(index.Value, line[(line.IndexOf(':') + 1)..].Trim()));
                index = null;
            }
        }
        return result;
    }

    public static List<MountedImage> ParseMounted(string text)
    {
        var result = new List<MountedImage>();
        string? mount = null, image = null;
        var index = 0;
        var mountRecords = 0;
        foreach (var line in text.Split('\n'))
        {
            var pos = line.IndexOf(':');
            if (pos < 0) continue;
            var key = line[..pos].Trim();
            var value = line[(pos + 1)..].Trim();
            switch (key)
            {
                case "Mount Dir": mountRecords++; mount = value; image = null; index = 0; break;
                case "Image File": image = value; break;
                case "Image Index": int.TryParse(value, out index); break;
                case "Status" when mount != null && image != null && index > 0:
                    result.Add(new(mount, image, index, value)); mount = null; break;
            }
        }
        // Never treat unrecognized output as proof there are no mounts.
        var knownEmpty = text.Contains("No mounted images found", StringComparison.OrdinalIgnoreCase) ||
            (text.Contains("Mounted images:", StringComparison.OrdinalIgnoreCase) && text.Contains("The operation completed successfully", StringComparison.OrdinalIgnoreCase));
        if (mountRecords != result.Count || (result.Count == 0 && !knownEmpty))
            throw new InvalidOperationException("Could not read DISM's mounted-image list. Review the log before continuing.");
        return result;
    }

    public static string FullPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Contains('"') || value.Any(char.IsControl))
            throw new ArgumentException("Choose a valid path without quotes or control characters.");
        if (!Path.IsPathFullyQualified(value)) throw new ArgumentException("Use an absolute path.");
        return Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
    }

    public static bool Overlaps(string a, string b)
    {
        a = FullPath(a); b = FullPath(b);
        return a.Equals(b, StringComparison.OrdinalIgnoreCase) ||
            a.StartsWith(b + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            b.StartsWith(a + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }

    public static void Validate(InjectionPlan p)
    {
        var image = FullPath(p.ImagePath);
        var mount = FullPath(p.MountPath);
        if (!File.Exists(image) || !Path.GetExtension(image).Equals(".wim", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("Choose a WIM file. Export an ESD image to WIM using Image tools first.");
        if ((File.GetAttributes(image) & FileAttributes.ReadOnly) != 0) throw new ArgumentException("The WIM is read-only. Copy the install media to a writable local folder first.");
        if (p.Indexes.Length == 0 || p.Indexes.Any(i => i <= 0) || p.Indexes.Distinct().Count() != p.Indexes.Length)
            throw new ArgumentException("Select at least one valid image edition.");
        if (p.DriverFolders.Length == 0) throw new ArgumentException("Add at least one folder of extracted INF drivers.");
        if (mount.StartsWith(@"\\") || new DriveInfo(Path.GetPathRoot(mount)!).DriveFormat != "NTFS")
            throw new ArgumentException("Choose a mount folder on a local NTFS volume.");
        if (Overlaps(image, mount)) throw new ArgumentException("The image file must be outside the mount folder.");
        foreach (var folder in p.DriverFolders)
        {
            FullPath(folder);
            if (Overlaps(folder, mount)) throw new ArgumentException("Driver folders and the mount folder must be separate.");
            if (!Directory.Exists(folder) || !Directory.EnumerateFiles(folder, "*.inf", SearchOption.AllDirectories).Any())
                throw new ArgumentException($"No INF drivers found in {folder}. Extract vendor installers first.");
        }
        if (Directory.Exists(mount) && (File.GetAttributes(mount) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("The mount folder cannot be a junction or symbolic link.");
        if (Directory.Exists(mount) && Directory.EnumerateFileSystemEntries(mount).Any())
            throw new ArgumentException("The mount folder must be empty. Use Mounted images to inspect existing mounts.");
    }
}
