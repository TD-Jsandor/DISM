using DismGui.Services;

var passed = 0;
void Check(bool condition, string name)
{
    if (!condition) throw new Exception("FAILED: " + name);
    Console.WriteLine("PASS: " + name);
    passed++;
}
async Task Reject(Func<Task> action, string name)
{
    try { await action(); } catch (Exception ex) when (ex is ArgumentException or InvalidOperationException) { Check(true, name); return; }
    throw new Exception("FAILED: expected rejection: " + name);
}

var root = Path.Combine(Path.GetTempPath(), "DismStudioChecks-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(root);
try
{
    var image = Path.Combine(root, "install with spaces.wim");
    var drivers = Path.Combine(root, "driver & packages");
    var mount = Path.Combine(root, "mount");
    File.WriteAllText(image, "test fixture, never passed to DISM");
    Directory.CreateDirectory(drivers);
    File.WriteAllText(Path.Combine(drivers, "test.inf"), "[Version]");
    var plan = new InjectionPlan(image, [1, 6], mount, [drivers]);
    Check(DismCommands.ParseEditions("Index : 1\r\nName : Windows Home\r\nIndex : 6\r\nName : Windows Pro\r\n").Select(e => e.Index).SequenceEqual([1, 6]), "parse multiple editions with nonconsecutive indexes");
    Check(DismCommands.ParseMounted("No mounted images found.").Count == 0, "empty mounted-image list");
    Check(DismCommands.ParseMounted("Mounted images:\r\n\r\nThe operation completed successfully.").Count == 0, "empty mounted-image table");
    var mountedOutput = $"Mount Dir : {mount}\nImage File : {image}\nImage Index : 6\nMounted Read/Write : Yes\nStatus : Ok\n";
    Check(DismCommands.ParseMounted(mountedOutput).Single() == new MountedImage(mount, image, 6, "Ok"), "parse mount paths containing drive colons");
    await Reject(() => Task.FromResult(DismCommands.ParseMounted("Unrecognized output")), "fail closed on unrecognized mount output");
    await Reject(() => Task.FromResult(DismCommands.ParseMounted(mountedOutput + "Mount Dir : C:\\incomplete\n")), "reject partially parsed mount tables");
    Check(DismCommands.AddDrivers(plan, drivers).Arguments.Contains($"/Driver:{drivers}"), "preserve spaces and shell characters as a single argument");
    Check(!DismCommands.Preview(plan).Any(c => c.Arguments.Contains("/ForceUnsigned")), "do not force unsigned drivers");
    Check(DismCommands.Preview(plan).Count() == 6, "preview each selected edition");
    await Reject(() => { DismCommands.Validate(plan with { MountPath = drivers }); return Task.CompletedTask; }, "reject mount overlapping driver source");
    await Reject(() => { DismCommands.Validate(plan with { Indexes = [] }); return Task.CompletedTask; }, "reject empty edition selection");
    await Reject(() => Task.FromResult(DismCommands.FullPath("relative\\mount")), "reject relative mount paths");

    var fake = new FakeRunner();
    await new ImageServicing(fake, _ => { }).InjectAsync(plan, () => false);
    Check(fake.Commands.Count == 7 && fake.Commands.Count(c => c.Arguments.Contains("/Commit")) == 2, "service and commit both editions serially");
    fake = new FakeRunner { FailOn = "/Add-Driver" };
    await Reject(() => new ImageServicing(fake, _ => { }).InjectAsync(plan, () => false), "surface failed driver injection");
    Check(!fake.Commands.Any(c => c.Arguments.Contains("/Commit")), "never commit or continue after injection failure");
    fake = new FakeRunner();
    var stop = false;
    await new ImageServicing(fake, s => { if (s.StartsWith("Saved index")) stop = true; }).InjectAsync(plan, () => stop);
    Check(fake.Commands.Count(c => c.Arguments.Contains("/Mount-Image")) == 1 && fake.Commands.Any(c => c.Arguments.Contains("/Commit")), "stop only after saving current edition");
    fake = new FakeRunner { MountedOutput = mountedOutput };
    await Reject(() => new ImageServicing(fake, _ => { }).InjectAsync(plan, () => false), "refuse to service an already mounted image");
    Check(!fake.Commands.Any(c => c.Arguments.Contains("/Mount-Image")), "existing mount is not changed");
    Directory.CreateDirectory(mount);
    File.WriteAllText(Path.Combine(mount, "keep.txt"), "existing data");
    await Reject(() => { DismCommands.Validate(plan); return Task.CompletedTask; }, "reject nonempty mount folder");
    var logs = Path.Combine(root, "logs");
    string logFile;
    using (var log = new SessionLog(logs))
    {
        logFile = log.FilePath;
        log.Write("COMMAND", "dism.exe /English /Get-MountedImageInfo");
        log.Write("ERROR", "failure\nwith details");
        Check(log.ReadAll().Contains("[ERROR] with details"), "flush timestamped multiline errors immediately to disk");
        Parallel.For(0, 50, i => log.Write("DISM", "line " + i));
    }
    var content = File.ReadAllText(logFile);
    Check(content.Contains("[COMMAND]") && content.Contains("Session ended.") && content.Split("[DISM]").Length == 51, "persist commands, concurrent output and session end");
    Console.WriteLine($"All {passed} checks passed. No DISM commands were executed.");
}
finally { Directory.Delete(root, true); }

sealed class FakeRunner : IDismRunner
{
    public List<DismCommand> Commands { get; } = [];
    public string? FailOn { get; init; }
    public string MountedOutput { get; init; } = "No mounted images found.";
    public Task<DismResult> RunAsync(DismCommand command)
    {
        Commands.Add(command);
        if (FailOn != null && command.Arguments.Contains(FailOn)) throw new InvalidOperationException("Simulated DISM failure");
        return Task.FromResult(new DismResult(0, command.Arguments.Contains("/Get-MountedImageInfo") ? MountedOutput : "Success"));
    }
}
