﻿///////////////////////////////////////////////////////////////////////////////
// ARGUMENTS
///////////////////////////////////////////////////////////////////////////////

var target = Argument<string>("target", "Default");
var configuration = Argument<string>("configuration", "Release");

//////////////////////////////////////////////////////////////////////
// EXTERNAL NUGET TOOLS
//////////////////////////////////////////////////////////////////////

#Tool "xunit.runner.console"
#Tool "GitVersion.CommandLine"
#Tool "Brutal.Dev.StrongNameSigner"

//////////////////////////////////////////////////////////////////////
// EXTERNAL NUGET LIBRARIES
//////////////////////////////////////////////////////////////////////

#addin "Cake.FileHelpers"
#addin "System.Text.Json"
using System.Text.Json;

///////////////////////////////////////////////////////////////////////////////
// GLOBAL VARIABLES
///////////////////////////////////////////////////////////////////////////////

var projectName = "Polly.Caching.Serialization.Json";
var keyName = "Polly.snk";

var solutions = GetFiles("./**/*.sln");
var solutionPaths = solutions.Select(solution => solution.GetDirectory());

var srcDir = Directory("./src");
var buildDir = Directory("./build");
var artifactsDir = Directory("./artifacts");
var testResultsDir = artifactsDir + Directory("test-results");

// NuGet
var nuspecFilename = projectName + ".nuspec";
var nuspecSrcFile = srcDir + File(nuspecFilename);
var nuspecDestFile = buildDir + File(nuspecFilename);
var nupkgDestDir = artifactsDir + Directory("nuget-package");
var snkFile = srcDir + File(keyName);

var projectToNugetFolderMap = new Dictionary<string, string[]>() {
    { "NetStandard11", new [] {"netstandard1.1"} },
    { "NetStandard20", new [] {"netstandard2.0"} },
};

// Gitversion
var gitVersionPath = ToolsExePath("GitVersion.exe");
Dictionary<string, object> gitVersionOutput;

// StrongNameSigner
var strongNameSignerPath = ToolsExePath("StrongNameSigner.Console.exe");


///////////////////////////////////////////////////////////////////////////////
// SETUP / TEARDOWN
///////////////////////////////////////////////////////////////////////////////

Setup(_ =>
{
    Information(@"");
    Information(@" ____   __   __    __    _  _   ___   __    ___  _  _  __  __ _   ___     ____  ____  ____  __   __   __    __  ____   __  ____  __  __   __ _       __  ____   __   __ _ ");
    Information(@"(  _ \ /  \ (  )  (  )  ( \/ ) / __) / _\  / __)/ )( \(  )(  ( \ / __)   / ___)(  __)(  _ \(  ) / _\ (  )  (  )(__  ) / _\(_  _)(  )/  \ (  ( \    _(  )/ ___) /  \ (  ( \");
    Information(@" ) __/(  O )/ (_/\/ (_/\ )  /_( (__ /    \( (__ ) __ ( )( /    /( (_ \ _ \___ \ ) _)  )   / )( /    \/ (_/\ )(  / _/ /    \ )(   )((  O )/    / _ / \) \\___ \(  O )/    /");
    Information(@"(__)   \__/ \____/\____/(__/(_)\___)\_/\_/ \___)\_)(_/(__)\_)__) \___/(_)(____/(____)(__\_)(__)\_/\_/\____/(__)(____)\_/\_/(__) (__)\__/ \_)__)(_)\____/(____/ \__/ \_)__)");
    Information(@"");
});

Teardown(_ =>
{
    Information("Finished running tasks.");
});

//////////////////////////////////////////////////////////////////////
// PRIVATE TASKS
//////////////////////////////////////////////////////////////////////

Task("__Clean")
    .Does(() =>
{
    DirectoryPath[] cleanDirectories = new DirectoryPath[] {
        buildDir,
        testResultsDir,
        nupkgDestDir,
        artifactsDir
  	};

    CleanDirectories(cleanDirectories);

    foreach(var path in cleanDirectories) { EnsureDirectoryExists(path); }

    foreach(var path in solutionPaths)
    {
        Information("Cleaning {0}", path);
        CleanDirectories(path + "/**/bin/" + configuration);
        CleanDirectories(path + "/**/obj/" + configuration);
    }
});

Task("__RestoreNugetPackages")
    .Does(() =>
{
    foreach(var solution in solutions)
    {
        Information("Restoring NuGet Packages for {0}", solution);
        NuGetRestore(solution);
    }
});

Task("__UpdateAssemblyVersionInformation")
    .Does(() =>
{
    var gitVersionSettings = new ProcessSettings()
        .SetRedirectStandardOutput(true);

    IEnumerable<string> outputLines;
    StartProcess(gitVersionPath, gitVersionSettings, out outputLines);

    var output = string.Join("\n", outputLines);
    gitVersionOutput = new JsonParser().Parse<Dictionary<string, object>>(output);

    Information("Updated GlobalAssemblyInfo");
    Information("AssemblyVersion -> {0}", gitVersionOutput["AssemblySemVer"]);
    Information("AssemblyFileVersion -> {0}", gitVersionOutput["MajorMinorPatch"]);
    Information("AssemblyInformationalVersion -> {0}", gitVersionOutput["InformationalVersion"]);
});

Task("__UpdateDotNetStandardAssemblyVersionNumber")
    .Does(() =>
{
    // NOTE: TEMPORARY fix only, while GitVersionTask does not support .Net Standard assemblies.  See https://github.com/App-vNext/Polly/issues/176.  
    // This build Task can be removed when GitVersionTask supports .Net Standard assemblies.
    var assemblySemVer = gitVersionOutput["AssemblySemVer"].ToString();
    Information("Updating NetStandard1.1 AssemblyVersion to {0}", assemblySemVer);
    var replacedFiles = ReplaceRegexInFiles("./src/Polly.Caching.Serialization.Json.NetStandard11/Properties/AssemblyInfo.cs", "AssemblyVersion[(]\".*\"[)]", "AssemblyVersion(\"" + assemblySemVer +"\")");
    if (!replacedFiles.Any())
    {
        Information("NetStandard1.1 AssemblyVersion could not be updated.");
    }
});

Task("__UpdateAppVeyorBuildNumber")
    .WithCriteria(() => AppVeyor.IsRunningOnAppVeyor)
    .Does(() =>
{
    var fullSemVer = gitVersionOutput["FullSemVer"].ToString();
    AppVeyor.UpdateBuildVersion(fullSemVer);
});

Task("__BuildSolutions")
    .Does(() =>
{
    foreach(var solution in solutions)
    {
        Information("Building {0}", solution);

        MSBuild(solution, settings =>
            settings
                .SetConfiguration(configuration)
                .WithProperty("TreatWarningsAsErrors", "true")
                .UseToolVersion(MSBuildToolVersion.VS2017)
                .SetVerbosity(Verbosity.Minimal)
                .SetNodeReuse(false));
    }
});

Task("__RunDotnetTests")
    .Does(() =>
{
    foreach(var specsProj in GetFiles("./src/**/*.Specs.csproj")) {
        DotNetCoreTest(specsProj.FullPath, new DotNetCoreTestSettings {
            Configuration = configuration,
            NoBuild = true
        });
    }
});

Task("__CopyOutputToNugetFolder")
    .Does(() =>
{
    foreach(var project in projectToNugetFolderMap.Keys) {
        var sourceDir = srcDir + Directory(projectName + "." + project) + Directory("bin") + Directory(configuration);

        foreach(var targetFolder in projectToNugetFolderMap[project]) {
            var destDir = buildDir + Directory("lib");

            Information("Copying {0} -> {1}.", sourceDir, destDir);
            CopyDirectory(sourceDir, destDir);
       }
    }

    CopyFile(nuspecSrcFile, nuspecDestFile);
});

Task("__CreateNugetPackage")
    .Does(() =>
{
    var nugetVersion = gitVersionOutput["NuGetVersion"].ToString();
    var packageName = projectName;

    Information("Building {0}.{1}.nupkg", packageName, nugetVersion);

    var nuGetPackSettings = new NuGetPackSettings {
        Id = packageName,
        Title = packageName,
        Version = nugetVersion,
        OutputDirectory = nupkgDestDir
    };

    NuGetPack(nuspecDestFile, nuGetPackSettings);
});

Task("__StronglySignAssemblies")
    .Does(() =>
{
    //see: https://github.com/brutaldev/StrongNameSigner
    var strongNameSignerSettings = new ProcessSettings()
        .WithArguments(args => args
            .Append("-in")
            .AppendQuoted(buildDir)
            .Append("-k")
            .AppendQuoted(snkFile)
            .Append("-l")
            .AppendQuoted("Changes"));

    StartProcess(strongNameSignerPath, strongNameSignerSettings);
});

Task("__CreateSignedNugetPackage")
    .Does(() =>
{
    var nugetVersion = gitVersionOutput["NuGetVersion"].ToString();
    var packageName = projectName + "-Signed";

    Information("Building {0}.{1}.nupkg", packageName, nugetVersion);

    var nuGetPackSettings = new NuGetPackSettings {
        Id = packageName,
        Title = packageName,
        Version = nugetVersion,
        OutputDirectory = nupkgDestDir
    };

    NuGetPack(nuspecDestFile, nuGetPackSettings);
});


//////////////////////////////////////////////////////////////////////
// BUILD TASKS
//////////////////////////////////////////////////////////////////////

Task("Build")
    .IsDependentOn("__Clean")
    .IsDependentOn("__RestoreNugetPackages")
    .IsDependentOn("__UpdateAssemblyVersionInformation")
    .IsDependentOn("__UpdateDotNetStandardAssemblyVersionNumber")
    .IsDependentOn("__UpdateAppVeyorBuildNumber")
    .IsDependentOn("__BuildSolutions")
    .IsDependentOn("__RunDotnetTests")
    .IsDependentOn("__CopyOutputToNugetFolder")
    .IsDependentOn("__CreateNugetPackage")
    .IsDependentOn("__StronglySignAssemblies")
    .IsDependentOn("__CreateSignedNugetPackage");

///////////////////////////////////////////////////////////////////////////////
// PRIMARY TARGETS
///////////////////////////////////////////////////////////////////////////////

Task("Default")
    .IsDependentOn("Build");

///////////////////////////////////////////////////////////////////////////////
// EXECUTION
///////////////////////////////////////////////////////////////////////////////

RunTarget(target);

//////////////////////////////////////////////////////////////////////
// HELPER FUNCTIONS
//////////////////////////////////////////////////////////////////////

string ToolsExePath(string exeFileName) {
    var exePath = System.IO.Directory.GetFiles(@".\Tools", exeFileName, SearchOption.AllDirectories).FirstOrDefault();
    return exePath;
}
