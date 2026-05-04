using Build;
using System.IO.Compression;
using System.Runtime.InteropServices;

string target = Argument("target", "Default");
string configuration = Argument("Configuration", "Release");

string SourceDirectory = "./src/Src/";
string ArtifactsDirectory = "./artifacts";
string ArtifactsTmpDirectory = "./artifacts/.tmp/";

Setup<BuildData>(ctx =>
{
    GitCommit gitTip = GitLogTip(".");
    GitBranch gitBranchObj = GitBranchCurrent(".");

    string gitCommit = gitTip.Sha;
    string gitBranch = gitBranchObj.CanonicalName;
    string ThisVersion = XmlPeek("src/Directory.Build.props", "//Version/text()");

    return new BuildData(gitCommit, gitBranch, ThisVersion);
});

Task(BuildTarget.RebuildDocumentation)
    .Does<BuildData>((ctx, data) =>
    {
        DotNetRun("./src/Tools/BouncyHsm.DocGenerator/BouncyHsm.DocGenerator.csproj",
            new ProcessArgumentBuilder().Append("Doc/SupportedAlgorithms.md"),
            new DotNetRunSettings()
            {
                Configuration = configuration,
                MSBuildSettings = new DotNetMSBuildSettings()
                {
                    Properties =
                {
                    {"GitCommit", new List<string>() { data.GitCommit } }
                }
                },
                DiagnosticOutput = true
            });
    });

Task(BuildTarget.Clean)
    .Does(() =>
    {
        DeleteDirectories(GetDirectories("./src/Src/**/obj/*"), new DeleteDirectorySettings()
        {
            Recursive = true,
            Force = true
        });

        DeleteDirectories(GetDirectories("./src/Src/**/bin/*"), new DeleteDirectorySettings()
        {
            Recursive = true,
            Force = true
        });

        CleanDirectory(ArtifactsDirectory);
    });

Task(BuildTarget.BuildBouncyHsm)
    .IsDependentOn(BuildTarget.Clean)
    .Does<BuildData>((ctx, data) =>
    {
        string projectFile = $"{SourceDirectory}BouncyHsm/BouncyHsm.csproj";
        string outputDir = $"{ArtifactsTmpDirectory}BouncyHsm";

        DotNetPublishSettings settings = new DotNetPublishSettings()
        {
            Configuration = configuration,
            OutputDirectory = outputDir,
            MSBuildSettings = new DotNetMSBuildSettings(),
        };

        settings.MSBuildSettings.Properties.Add("GitCommit", new List<string>() { data.GitCommit });

        DotNetPublish(projectFile, settings);
    });

Task(BuildTarget.BuildBouncyHsmCli)
    .IsDependentOn(BuildTarget.Clean)
    .Does<BuildData>((ctx, data) =>
    {
        string projectFile = $"{SourceDirectory}BouncyHsm.Cli/BouncyHsm.Cli.csproj";
        string outputDir = $"{ArtifactsTmpDirectory}BouncyHsm.Cli";

        DotNetPublishSettings settings = new DotNetPublishSettings()
        {
            Configuration = configuration,
            OutputDirectory = outputDir,
            MSBuildSettings = new DotNetMSBuildSettings(),
        };

        settings.MSBuildSettings.Properties.Add("GitCommit", new List<string>() { data.GitCommit });

        DotNetPublish(projectFile, settings);
    });

void BuildBouncyHsmPkcs11Lib(PlatformTarget platform)
{
    MSBuildSettings settings = new MSBuildSettings()
    {
        Verbosity = Verbosity.Diagnostic,
        Configuration = configuration,
        PlatformTarget = platform,
        ToolVersion = MSBuildToolVersion.VS2026,
        Targets =
        {
            "clean",
            "build"
        }
    };

    MSBuild($"{SourceDirectory}BouncyHsm.Pkcs11Lib/BouncyHsm.Pkcs11Lib.vcxproj", settings);

    string nativeLib = $"{SourceDirectory}BouncyHsm.Pkcs11Lib/{configuration}/BouncyHsm.Pkcs11Lib.dll";
    string destinationPath = JoinPaths(ArtifactsTmpDirectory, "native", platform == PlatformTarget.x64 ? "Windows-x64" : "Windows-x86");
    CopyFile(nativeLib, $"{destinationPath}/BouncyHsm.Pkcs11Lib.dll");
}

Task(BuildTarget.BuildPkcs11LibWin32)
    .IsDependentOn(BuildTarget.Clean)
    .WithCriteria(IsRunningOnWindows())
    .Does(() =>
    {
        BuildBouncyHsmPkcs11Lib(PlatformTarget.Win32);
    });

Task(BuildTarget.BuildPkcs11LibWin64)
    .IsDependentOn(BuildTarget.Clean)
    .WithCriteria(IsRunningOnWindows())
    .Does(() =>
    {
        BuildBouncyHsmPkcs11Lib(PlatformTarget.x64);
    });

Task(BuildTarget.BuildPkcs11LibLinux32)
    .IsDependentOn(BuildTarget.Clean)
    .WithCriteria(RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X86 && !IsRunningOnWindows())
    .Does(() =>
    {
        string linuxNativeLibx86 = JoinPaths("build_linux", "BouncyHsm.Pkcs11Lib-Linux-x86.so");
        if (FileExists(linuxNativeLibx86))
        {
            CopyFile(linuxNativeLibx86,
                JoinPaths(ArtifactsTmpDirectory, "native", "Linux-x86", "BouncyHsm.Pkcs11Lib.so"));
        }
    });

Task(BuildTarget.BuildPkcs11LibLinux64)
    .IsDependentOn(BuildTarget.Clean)
    .WithCriteria(RuntimeInformation.OSArchitecture == System.Runtime.InteropServices.Architecture.X64 && !IsRunningOnWindows())
    .Does(() =>
    {
        string linuxNativeLibx64 = JoinPaths("build_linux", "BouncyHsm.Pkcs11Lib-Linux-x64.so");
        if (FileExists(linuxNativeLibx64))
        {
            CleanDirectory(JoinPaths(ArtifactsTmpDirectory, "native", "Linux-x64"));
            CopyFile(linuxNativeLibx64,
                JoinPaths(ArtifactsTmpDirectory, "native", "Linux-x64", "BouncyHsm.Pkcs11Lib.so"));
        }
    });


Task(BuildTarget.BuildBouncyHsmClient)
    .IsDependentOn(BuildTarget.Clean)
    .IsDependentOn(BuildTarget.BuildPkcs11LibWin32)
    .IsDependentOn(BuildTarget.BuildPkcs11LibWin64)
    .IsDependentOn(BuildTarget.BuildPkcs11LibLinux32)
    .IsDependentOn(BuildTarget.BuildPkcs11LibLinux64)
    .Does<BuildData>((ctx, data) =>
    {
        string projectFile = $"{SourceDirectory}BouncyHsm.Client/BouncyHsm.Client.csproj";

        DotNetPackSettings settings = new DotNetPackSettings()
        {
            Configuration = configuration,
            OutputDirectory = ArtifactsDirectory,
            MSBuildSettings = new DotNetMSBuildSettings(),
        };

        settings.MSBuildSettings.Properties.Add("RepositoryCommit", new List<string>() { data.GitCommit });
        settings.MSBuildSettings.Properties.Add("RepositoryBranch", new List<string>() { data.GitBranch });
        settings.MSBuildSettings.Properties.Add("IncludeNativeLibs", new List<string>() { "True" });

        DotNetPack(projectFile, settings);
    });

Task(BuildTarget.BuildAll)
    .IsDependentOn(BuildTarget.Clean)
    .IsDependentOn(BuildTarget.BuildPkcs11LibWin32)
    .IsDependentOn(BuildTarget.BuildPkcs11LibWin64)
    .IsDependentOn(BuildTarget.BuildPkcs11LibLinux32)
    .IsDependentOn(BuildTarget.BuildPkcs11LibLinux64)
    .IsDependentOn(BuildTarget.BuildBouncyHsm)
    .IsDependentOn(BuildTarget.BuildBouncyHsmCli)
    .IsDependentOn(BuildTarget.BuildBouncyHsmClient)
    .Does<BuildData>((ctx, data) =>
    {
        // Nah, not the raw so/dll but the ZIP file with info
        // CopyDirectory($"{ArtifactsTmpDirectory}native", $"{ArtifactsTmpDirectory}BouncyHsm/wwwroot/native");

        foreach (var os in new[] { "Windows", "Linux", "RHEL" })
        {
            foreach (var arch in new[] { "x64", "x86" })
            {
                CreateZip(os, arch, data);
            }
        }

        CreateDirectory(JoinPaths(ArtifactsTmpDirectory, "BouncyHsm/data"));
        System.IO.File.WriteAllText(JoinPaths(ArtifactsTmpDirectory, "BouncyHsm/data/keep.txt"), string.Empty);

        DeleteFiles(JoinPaths(ArtifactsTmpDirectory, "BouncyHsm/**/*.pdb"));
        DeleteFiles(JoinPaths(ArtifactsTmpDirectory, "BouncyHsm/**/libman.json"));
        DeleteFiles(JoinPaths(ArtifactsTmpDirectory, "BouncyHsm/**/.gitkeep"));
        DeleteFiles(JoinPaths(ArtifactsTmpDirectory, "BouncyHsm/**/appsettings.Development.json"));

        CopyLicenses(JoinPaths(ArtifactsTmpDirectory, "BouncyHsm"), data);
        CopyLicenses(JoinPaths(ArtifactsTmpDirectory, "BouncyHsm.Cli"), data);

        Zip(JoinPaths(ArtifactsTmpDirectory, "BouncyHsm"), JoinPaths(ArtifactsDirectory, "BouncyHsm.zip"));

        DeleteFiles(JoinPaths(ArtifactsTmpDirectory, "BouncyHsm.Cli/**/*.pdb"));
        DeleteFiles(JoinPaths(ArtifactsTmpDirectory, "BouncyHsm.Cli/**/.gitkeep"));

        Zip(JoinPaths(ArtifactsTmpDirectory, "BouncyHsm.Cli"), JoinPaths(ArtifactsDirectory, "BouncyHsm.Cli.zip"));
    });



void CreateZip(string os, string arch, BuildData buildData)
{
    string ext = os == "Windows" ? "dll" : "so";
    string dllFile = JoinPaths(ArtifactsTmpDirectory, "native", $"{os}-{arch}", $"BouncyHsm.Pkcs11Lib.{ext}");
    string destinationDirectory = JoinPaths(ArtifactsTmpDirectory, "BouncyHsm", "wwwroot", "native");
    string destinationFile = $"{destinationDirectory}/BouncyHsm.Pkcs11Lib-{os}-{arch}.zip";

    if (!FileExists(dllFile))
    {
        Warning("Native lib {0} not found.", dllFile);
        return;
    }

    CreateDirectory(destinationDirectory);
    Debug("Creating ZIP file from dll {0}", dllFile);

    using FileStream fs = new FileStream(destinationFile, FileMode.Create);
    using ZipArchive archive = new ZipArchive(fs, ZipArchiveMode.Create);
    archive.CreateEntryFromFile(dllFile, System.IO.Path.GetFileName(dllFile), CompressionLevel.Optimal);
    using Stream readmeStream = archive.CreateEntry("Readme.txt").Open();

    byte[] content = Encoding.UTF8.GetBytes($"""
        Bouncy Hsm PKCS11 library
        
          Version: {buildData.ThisVersion}
          For platform: {os} {arch}
          Git commit: {buildData.GitCommit}
          
          Project site: https://github.com/harrison314/BouncyHsm
          License: BSD 3 Clausule
        """);

    readmeStream.Write(content);
    readmeStream.Flush();
}

void CopyLicenses(string outFolder, BuildData buildData)
{
    CopyFile("./LICENSE", JoinPaths(outFolder, "License.txt"));
    System.IO.File.WriteAllText(JoinPaths(outFolder, "version.txt"),
        $"""
        Version: {buildData.ThisVersion}
        GIT: {buildData.GitBranch} - {buildData.GitCommit}

        """);
}

string JoinPaths(params string[] parts)
{
    return System.IO.Path.Combine(parts);
}

Task("Default")
    .IsDependentOn(BuildTarget.BuildAll);

RunTarget(target);

