namespace WinIDM.App.E2E.Support;

/// <summary>Main test paths. The target exe is artifacts\WinIDM\WinIDM.App.exe unless WINIDM_EXE is set.</summary>
internal static class RepoPaths
{
    public static string Root { get; } = FindRoot();

    public static string AppExe =>
        Environment.GetEnvironmentVariable("WINIDM_EXE") is { Length: > 0 } custom
            ? custom
            : Path.Combine(Root, "artifacts", "WinIDM", "WinIDM.App.exe");

    /// <summary>Screenshot folder: tests\WinIDM.App.E2E\artifacts.</summary>
    public static string ArtifactsDir => Path.Combine(Root, "tests", "WinIDM.App.E2E", "artifacts");

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "WinIDM.sln")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("WinIDM.sln not found from " + AppContext.BaseDirectory);
    }
}
